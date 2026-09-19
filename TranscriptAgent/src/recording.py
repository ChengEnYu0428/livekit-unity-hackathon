import asyncio
import base64
import hashlib
import math
import os
import re
import shutil
import sys
import tempfile
import time
import uuid
from array import array
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterator


BYTES_PER_SAMPLE = 2


class RecordingEncodingError(RuntimeError):
    pass


@dataclass(frozen=True)
class RecordingResult:
    path: Path
    file_name: str
    total_bytes: int
    duration_seconds: float
    sha256: str
    truncated: bool


class PcmTimelineRecorder:
    """Mixes normalized mono PCM frames on a wall-clock timeline."""

    def __init__(
        self,
        session_id: str,
        *,
        sample_rate: int = 48_000,
        channels: int = 1,
        max_minutes: int = 45,
        bitrate_kbps: int = 48,
        work_root: str | os.PathLike[str] | None = None,
        ffmpeg_binary: str = "ffmpeg",
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        if sample_rate <= 0:
            raise ValueError("sample_rate must be positive")
        if channels != 1:
            raise ValueError("PcmTimelineRecorder currently requires mono PCM")
        if max_minutes <= 0:
            raise ValueError("max_minutes must be positive")

        safe_session = re.sub(r"[^A-Za-z0-9_-]+", "_", session_id).strip("_")
        self.session_id = safe_session or uuid.uuid4().hex
        self.sample_rate = sample_rate
        self.channels = channels
        self.max_samples = int(sample_rate * max_minutes * 60)
        self.bitrate_kbps = max(16, bitrate_kbps)
        self.ffmpeg_binary = ffmpeg_binary
        self.clock = clock
        self.started_at = clock()
        self.track_next_sample: dict[str, int] = {}
        self.highest_sample = 0
        self.truncated = False
        self.accepting = True
        self._closed = False

        root = Path(work_root) if work_root else Path(tempfile.gettempdir())
        root.mkdir(parents=True, exist_ok=True)
        self.work_dir = root / f"meeting-{self.session_id}-{uuid.uuid4().hex[:8]}"
        self.work_dir.mkdir(parents=True, exist_ok=False)
        self.pcm_path = self.work_dir / "mixed-mono-s16le.pcm"
        self.mp3_path = self.work_dir / f"MeetingRecording_{self.session_id}.mp3"
        self._pcm_file = self.pcm_path.open("w+b")

    def append_frame(
        self,
        track_id: str,
        pcm_s16le: bytes,
        samples_per_channel: int,
    ) -> None:
        if not self.accepting or self._closed or samples_per_channel <= 0:
            return

        expected_bytes = samples_per_channel * BYTES_PER_SAMPLE
        data = pcm_s16le[:expected_bytes]
        if len(data) < expected_bytes:
            data += b"\0" * (expected_bytes - len(data))

        frame_duration = samples_per_channel / self.sample_rate
        wall_start = max(
            0,
            int(
                (
                    self.clock()
                    - self.started_at
                    - frame_duration
                )
                * self.sample_rate
            ),
        )
        previous_end = self.track_next_sample.get(track_id)
        gap_threshold = self.sample_rate // 4
        if previous_end is None:
            start_sample = wall_start
        elif wall_start > previous_end + gap_threshold:
            start_sample = wall_start
        else:
            start_sample = previous_end

        if start_sample >= self.max_samples:
            self.truncated = True
            return

        writable_samples = min(samples_per_channel, self.max_samples - start_sample)
        if writable_samples < samples_per_channel:
            self.truncated = True
            data = data[: writable_samples * BYTES_PER_SAMPLE]

        offset = start_sample * BYTES_PER_SAMPLE
        self._pcm_file.seek(offset)
        existing = self._pcm_file.read(len(data))
        if len(existing) < len(data):
            existing += b"\0" * (len(data) - len(existing))

        mixed = self._mix_int16(existing, data)
        self._pcm_file.seek(offset)
        self._pcm_file.write(mixed)

        end_sample = start_sample + writable_samples
        self.track_next_sample[track_id] = end_sample
        self.highest_sample = max(self.highest_sample, end_sample)

    def stop_capture(self) -> None:
        if self._closed:
            return
        self.accepting = False

        elapsed_samples = max(
            1,
            int((self.clock() - self.started_at) * self.sample_rate),
        )
        final_samples = min(elapsed_samples, self.max_samples)
        if elapsed_samples > self.max_samples:
            self.truncated = True
        self.highest_sample = max(self.highest_sample, final_samples)

        final_bytes = self.highest_sample * BYTES_PER_SAMPLE
        self._pcm_file.truncate(final_bytes)
        self._pcm_file.flush()
        self._pcm_file.close()
        self._closed = True

    async def finalize(self, timeout_seconds: int = 180) -> RecordingResult:
        self.stop_capture()
        command = self.ffmpeg_command()
        try:
            process = await asyncio.create_subprocess_exec(
                *command,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
        except FileNotFoundError as exception:
            raise RecordingEncodingError(
                f"FFmpeg was not found: {self.ffmpeg_binary}"
            ) from exception

        try:
            _, stderr = await asyncio.wait_for(
                process.communicate(),
                timeout=max(10, timeout_seconds),
            )
        except asyncio.TimeoutError as exception:
            process.kill()
            await process.communicate()
            raise RecordingEncodingError("FFmpeg MP3 encoding timed out") from exception

        if process.returncode != 0 or not self.mp3_path.is_file():
            details = stderr.decode("utf-8", errors="replace").strip()
            raise RecordingEncodingError(
                "FFmpeg MP3 encoding failed"
                + (f": {details[-800:]}" if details else "")
            )

        total_bytes = self.mp3_path.stat().st_size
        duration = self.highest_sample / self.sample_rate
        return RecordingResult(
            path=self.mp3_path,
            file_name=self.mp3_path.name,
            total_bytes=total_bytes,
            duration_seconds=duration,
            sha256=sha256_file(self.mp3_path),
            truncated=self.truncated,
        )

    def ffmpeg_command(self) -> list[str]:
        return [
            self.ffmpeg_binary,
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-f",
            "s16le",
            "-ar",
            str(self.sample_rate),
            "-ac",
            str(self.channels),
            "-i",
            str(self.pcm_path),
            "-vn",
            "-codec:a",
            "libmp3lame",
            "-b:a",
            f"{self.bitrate_kbps}k",
            "-af",
            # Normalize each short section independently so far-field laptop /
            # webcam microphones remain audible.  The threshold prevents true
            # digital silence from being amplified, while the limiter protects
            # against clipping when a participant suddenly speaks loudly.
            (
                "dynaudnorm="
                "f=250:g=15:p=0.90:m=100:r=0.06:t=0.00005:o=0.5,"
                "alimiter=limit=0.95"
            ),
            str(self.mp3_path),
        ]

    def abort(self) -> None:
        self.accepting = False
        if not self._closed:
            self._pcm_file.close()
            self._closed = True
        self.cleanup()

    def cleanup(self) -> None:
        shutil.rmtree(self.work_dir, ignore_errors=True)

    @staticmethod
    def _mix_int16(existing: bytes, incoming: bytes) -> bytes:
        current_samples = array("h")
        current_samples.frombytes(existing)
        new_samples = array("h")
        new_samples.frombytes(incoming)

        if sys.byteorder != "little":
            current_samples.byteswap()
            new_samples.byteswap()

        for index, sample in enumerate(new_samples):
            value = current_samples[index] + sample
            current_samples[index] = max(-32768, min(32767, value))

        if sys.byteorder != "little":
            current_samples.byteswap()
        return current_samples.tobytes()


def sha256_file(path: str | os.PathLike[str]) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def recording_chunk_count(total_bytes: int, chunk_bytes: int) -> int:
    if chunk_bytes <= 0:
        raise ValueError("chunk_bytes must be positive")
    return int(math.ceil(total_bytes / chunk_bytes)) if total_bytes else 0


def iter_base64_chunks(
    path: str | os.PathLike[str],
    chunk_bytes: int,
) -> Iterator[tuple[int, str]]:
    if chunk_bytes <= 0:
        raise ValueError("chunk_bytes must be positive")
    with Path(path).open("rb") as source:
        index = 0
        while True:
            data = source.read(chunk_bytes)
            if not data:
                return
            yield index, base64.b64encode(data).decode("ascii")
            index += 1
