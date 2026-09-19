import base64
import sys
import tempfile
import unittest
from array import array
from pathlib import Path


SOURCE_ROOT = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SOURCE_ROOT))

from recording import (  # noqa: E402
    PcmTimelineRecorder,
    iter_base64_chunks,
    recording_chunk_count,
)


class MutableClock:
    def __init__(self, value: float) -> None:
        self.value = value

    def __call__(self) -> float:
        return self.value


class RecordingTests(unittest.TestCase):
    def test_two_tracks_are_mixed_on_the_same_timeline(self) -> None:
        with tempfile.TemporaryDirectory() as work_root:
            clock = MutableClock(100.0)
            recorder = PcmTimelineRecorder(
                "session",
                max_minutes=1,
                work_root=work_root,
                clock=clock,
            )
            clock.value = 100.02
            sample_count = 960
            recorder.append_frame(
                "track-a",
                array("h", [1_000] * sample_count).tobytes(),
                sample_count,
            )
            recorder.append_frame(
                "track-b",
                array("h", [2_000] * sample_count).tobytes(),
                sample_count,
            )
            recorder.stop_capture()

            mixed = array("h")
            mixed.frombytes(recorder.pcm_path.read_bytes())
            self.assertEqual(sample_count, len(mixed))
            self.assertTrue(all(value == 3_000 for value in mixed))
            recorder.cleanup()

    def test_chunks_round_trip_and_count_matches(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source_path = Path(directory) / "recording.mp3"
            original = bytes(index % 251 for index in range(25_123))
            source_path.write_bytes(original)

            chunks = list(iter_base64_chunks(source_path, 9_000))
            rebuilt = b"".join(
                base64.b64decode(encoded) for _, encoded in chunks
            )

            self.assertEqual(original, rebuilt)
            self.assertEqual(
                recording_chunk_count(len(original), 9_000),
                len(chunks),
            )
            self.assertEqual(list(range(len(chunks))), [i for i, _ in chunks])

    def test_ffmpeg_command_encodes_mono_mp3(self) -> None:
        with tempfile.TemporaryDirectory() as work_root:
            recorder = PcmTimelineRecorder(
                "session",
                max_minutes=1,
                bitrate_kbps=48,
                work_root=work_root,
                ffmpeg_binary="custom-ffmpeg",
            )
            command = recorder.ffmpeg_command()

            self.assertEqual("custom-ffmpeg", command[0])
            self.assertIn("libmp3lame", command)
            self.assertIn("48k", command)
            self.assertIn(str(recorder.pcm_path), command)
            audio_filter = command[command.index("-af") + 1]
            self.assertIn("dynaudnorm", audio_filter)
            self.assertIn("alimiter", audio_filter)
            self.assertEqual(str(recorder.mp3_path), command[-1])
            recorder.abort()


if __name__ == "__main__":
    unittest.main()
