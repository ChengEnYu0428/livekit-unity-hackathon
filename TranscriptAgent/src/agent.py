import asyncio
import json
import logging
import os
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

try:
    from dotenv import load_dotenv

    load_dotenv(Path(__file__).resolve().parents[1] / ".env")
except ImportError:
    # LiveKit Cloud injects environment variables directly; dotenv is only
    # needed for local development with TranscriptAgent/.env.
    pass

from livekit import agents, rtc
from livekit.agents import AgentServer, AutoSubscribe
from recording import (
    PcmTimelineRecorder,
    RecordingResult,
    iter_base64_chunks,
    recording_chunk_count,
)
from yating_asr import YatingStreamingSTT
from collaboration import (CollaborationService, CONTROL_TOPIC as AI_CONTROL_TOPIC,
    EVENT_TOPIC as AI_EVENT_TOPIC, IMAGE_TOPIC as AI_IMAGE_TOPIC, MAX_IMAGE_BYTES)


CONTROL_TOPIC = "jorjin.transcript.control.v1"
EVENT_TOPIC = "jorjin.transcript.event.v1"
ROLE_TOPIC = "jorjin.meeting.role.v1"
ROLE_LABELS = {"field": "Field", "expert": "Expert"}
AGENT_IDENTITY_PREFIX = "agent-"

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("jorjin-transcript-agent")

server = AgentServer()


def env_bool(name: str, default: bool) -> bool:
    raw_value = os.getenv(name)
    if raw_value is None:
        return default
    return raw_value.strip().lower() in {"1", "true", "yes", "on"}


def env_int(
    name: str,
    default: int,
    *,
    minimum: int,
    maximum: int,
) -> int:
    raw_value = os.getenv(name, "").strip()
    try:
        parsed = int(raw_value) if raw_value else default
    except ValueError:
        logger.warning("%s is not an integer; using %d", name, default)
        parsed = default
    return max(minimum, min(maximum, parsed))


RECORDING_ENABLED = env_bool("TRANSCRIPT_RECORDING_ENABLED", True)
RECORDING_MAX_MINUTES = env_int(
    "TRANSCRIPT_RECORDING_MAX_MINUTES",
    45,
    minimum=1,
    maximum=180,
)
RECORDING_BITRATE_KBPS = env_int(
    "TRANSCRIPT_RECORDING_BITRATE_KBPS",
    48,
    minimum=16,
    maximum=128,
)
# 9,000 raw bytes become roughly 12 KB after base64 encoding, leaving room
# below LiveKit's 15 KiB reliable data-packet payload limit.
RECORDING_CHUNK_BYTES = env_int(
    "TRANSCRIPT_RECORDING_CHUNK_BYTES",
    9_000,
    minimum=1_024,
    maximum=9_000,
)
RECORDING_MAX_TRANSFER_BYTES = (
    env_int(
        "TRANSCRIPT_RECORDING_MAX_TRANSFER_MB",
        32,
        minimum=1,
        maximum=256,
    )
    * 1024
    * 1024
)
RECORDING_CHUNK_DELAY_MS = env_int(
    "TRANSCRIPT_RECORDING_CHUNK_DELAY_MS",
    2,
    minimum=0,
    maximum=1_000,
)
RECORDING_FFMPEG_TIMEOUT_SECONDS = env_int(
    "TRANSCRIPT_RECORDING_FFMPEG_TIMEOUT_SECONDS",
    180,
    minimum=10,
    maximum=900,
)
RECORDING_FFMPEG_BINARY = (
    os.getenv("TRANSCRIPT_RECORDING_FFMPEG_BINARY", "").strip() or "ffmpeg"
)
RECORDING_WORK_DIR = (
    os.getenv("TRANSCRIPT_RECORDING_DIR", "").strip() or None
)
YATING_API_KEY = os.getenv("YATING_API_KEY", "").strip()
YATING_PIPELINE = (
    os.getenv("YATING_PIPELINE", "asr-zh-en-std").strip()
    or "asr-zh-en-std"
)
YATING_CUSTOM_MODEL_ID = os.getenv("YATING_CUSTOM_MODEL_ID", "").strip()


def utc_timestamp() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


class MeetingTranscript:
    def __init__(self, room: rtc.Room) -> None:
        self.room = room
        self.active = False
        self.stopping = False
        self.session_id = ""
        self.controller_identity = ""
        self.segment_count = 0
        self.recorder: PcmTimelineRecorder | None = None
        self.track_tasks: dict[str, asyncio.Task[None]] = {}
        self.state_lock = asyncio.Lock()
        self.yating_lock = asyncio.Lock()
        self.yating_streams: dict[
            str,
            tuple[YatingStreamingSTT, asyncio.Task[None], str],
        ] = {}
        # Per-speaker reconnect state: a failed stream is retried with backoff instead
        # of silencing that person for the rest of the meeting.
        self.yating_connecting: set[str] = set()
        self.roles: dict[str, str] = {}
        self.yating_retry: dict[str, tuple[float, int]] = {}
        self.collaboration = CollaborationService(self.publish_collaboration)

    async def publish_collaboration(self, payload: dict, destination: str) -> None:
        await self.room.local_participant.publish_data(
            json.dumps(payload, ensure_ascii=False), reliable=True,
            destination_identities=[destination], topic=AI_EVENT_TOPIC,
        )

    async def publish(
        self,
        packet_type: str,
        *,
        destinations: list[str] | None = None,
        **values: Any,
    ) -> None:
        payload = {
            "type": packet_type,
            "room": self.room.name,
            "timestamp": utc_timestamp(),
            **values,
        }
        await self.room.local_participant.publish_data(
            json.dumps(payload, ensure_ascii=False),
            reliable=True,
            destination_identities=destinations or [],
            topic=EVENT_TOPIC,
        )

    async def publish_ready(self, destination: str | None = None) -> None:
        await self.publish(
            "ready",
            destinations=[destination] if destination else None,
            state=(
                "recording"
                if self.active
                else ("processing" if self.stopping else "ready")
            ),
            session_id=self.session_id,
            message="Transcript Agent is ready.",
        )

    async def receive_image(self, reader: rtc.ByteStreamReader, sender: str) -> None:
        if sender.startswith(AGENT_IDENTITY_PREFIX):
            return
        if reader.info.size and reader.info.size > MAX_IMAGE_BYTES:
            await self.collaboration.handle_image(b"", reader.info.attributes, sender)
            return
        data = bytearray()
        async for chunk in reader:
            data.extend(chunk)
            if len(data) > MAX_IMAGE_BYTES:
                await self.collaboration.handle_image(b"", reader.info.attributes, sender)
                return
        await self.collaboration.handle_image(bytes(data), reader.info.attributes, sender)

    def speaker_label(self, identity: str, display_name: str) -> str:
        """Speaker name with the participant's chosen role, e.g. Amy (Field)."""
        name = display_name or identity
        role = ROLE_LABELS.get(self.roles.get(identity, ""))
        return f"{name} ({role})" if role else name

    async def handle_control(self, packet: rtc.DataPacket) -> None:
        if packet.topic == ROLE_TOPIC and packet.participant is not None:
            try:
                role = json.loads(packet.data.decode("utf-8")).get("role")
            except (UnicodeDecodeError, json.JSONDecodeError, AttributeError):
                return
            if role in ROLE_LABELS:
                self.roles[packet.participant.identity] = role
            return
        if packet.topic == AI_CONTROL_TOPIC and packet.participant is not None:
            if len(packet.data) > 10_000:
                return
            try:
                request = json.loads(packet.data.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                return
            if isinstance(request, dict):
                await self.collaboration.handle(request, packet.participant.identity)
            return
        if packet.topic != CONTROL_TOPIC or packet.participant is None:
            return

        try:
            request = json.loads(packet.data.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return

        if not isinstance(request, dict):
            return
        action = str(request.get("action", "")).lower()
        requested_session_id = str(request.get("session_id", "")).strip()
        sender = packet.participant.identity

        if action == "start":
            recorder: PcmTimelineRecorder | None = None
            async with self.state_lock:
                if self.active:
                    active_session_id = self.session_id
                    active_controller = self.controller_identity
                    is_stopping = False
                elif self.stopping:
                    active_session_id = self.session_id
                    active_controller = self.controller_identity
                    is_stopping = True
                else:
                    active_session_id = ""
                    active_controller = ""
                    is_stopping = False

            if active_session_id:
                await self.publish(
                    "status",
                    state="processing" if is_stopping else "recording",
                    session_id=active_session_id,
                    controller_identity=active_controller,
                    recording_enabled=self.recorder is not None,
                    message=(
                        "Meeting transcription is stopping. Please wait."
                        if is_stopping
                        else "Meeting transcription is already running."
                    ),
                )
                return

            session_id = requested_session_id or uuid.uuid4().hex
            recording_error = ""
            if RECORDING_ENABLED:
                try:
                    recorder = PcmTimelineRecorder(
                        session_id,
                        max_minutes=RECORDING_MAX_MINUTES,
                        bitrate_kbps=RECORDING_BITRATE_KBPS,
                        work_root=RECORDING_WORK_DIR,
                        ffmpeg_binary=RECORDING_FFMPEG_BINARY,
                    )
                except Exception as exception:
                    recording_error = str(exception)
                    logger.exception(
                        "unable to start recording room=%s session=%s",
                        self.room.name,
                        session_id,
                    )

            async with self.state_lock:
                self.active = True
                self.session_id = session_id
                self.controller_identity = sender
                self.segment_count = 0
                self.recorder = recorder
                self.yating_connecting.clear()
                self.yating_retry.clear()

            await self.collaboration.begin(session_id, sender)

            await self.publish(
                "status",
                state="recording",
                session_id=self.session_id,
                controller_identity=sender,
                recording_enabled=recorder is not None,
                message=(
                    "Meeting transcription and MP3 recording started."
                    if recorder is not None
                    else "Meeting transcription started without MP3 recording."
                ),
            )
            if recording_error:
                await self.publish(
                    "recording_error",
                    destinations=[sender],
                    session_id=self.session_id,
                    message=recording_error,
                )
            logger.info(
                "transcription started room=%s session=%s controller=%s recording=%s",
                self.room.name,
                self.session_id,
                sender,
                recorder is not None,
            )
            return

        if action == "stop":
            async with self.state_lock:
                if self.stopping:
                    completed_session_id = self.session_id
                    completed_segments = self.segment_count
                    recorder = None
                    was_active = False
                    already_stopping = True
                elif not self.active:
                    completed_session_id = requested_session_id or self.session_id
                    completed_segments = self.segment_count
                    recorder = None
                    was_active = False
                    already_stopping = False
                else:
                    completed_session_id = self.session_id
                    completed_segments = self.segment_count
                    recorder = self.recorder
                    self.recorder = None
                    self.active = False
                    self.stopping = True
                    was_active = True
                    already_stopping = False

            if already_stopping:
                await self.publish(
                    "status",
                    state="processing",
                    session_id=completed_session_id,
                    segment_count=completed_segments,
                    message="Meeting transcription is already stopping.",
                )
                return

            if not was_active:
                await self.publish(
                    "complete",
                    state="ready",
                    session_id=completed_session_id,
                    segment_count=completed_segments,
                    message="Transcription was already stopped.",
                )
                return

            await self._finish_all_yating_streams()
            self.collaboration.summarize_on_stop()
            async with self.state_lock:
                completed_segments = self.segment_count

            recording_saved = False
            if recorder is not None:
                await self.publish(
                    "status",
                    state="processing",
                    session_id=completed_session_id,
                    controller_identity=sender,
                    recording_enabled=True,
                    message="Encoding and transferring meeting MP3.",
                )
                try:
                    result = await recorder.finalize(
                        RECORDING_FFMPEG_TIMEOUT_SECONDS
                    )
                    recording_saved = await self.publish_recording(
                        result,
                        completed_session_id,
                        sender,
                    )
                except Exception as exception:
                    logger.exception(
                        "recording finalization failed room=%s session=%s",
                        self.room.name,
                        completed_session_id,
                    )
                    await self.publish(
                        "recording_error",
                        destinations=[sender],
                        session_id=completed_session_id,
                        message=f"MP3 recording failed: {exception}",
                    )
                finally:
                    recorder.cleanup()

            await self.publish(
                "complete",
                state="ready",
                session_id=completed_session_id,
                segment_count=completed_segments,
                recording_saved=recording_saved,
                message=(
                    "Meeting transcription and MP3 recording completed."
                    if recording_saved
                    else "Meeting transcription completed."
                ),
            )
            logger.info(
                "transcription stopped room=%s session=%s segments=%d",
                self.room.name,
                completed_session_id,
                completed_segments,
            )
            async with self.state_lock:
                self.stopping = False

    async def publish_recording(
        self,
        result: RecordingResult,
        session_id: str,
        destination: str,
    ) -> bool:
        if result.total_bytes > RECORDING_MAX_TRANSFER_BYTES:
            await self.publish(
                "recording_error",
                destinations=[destination],
                session_id=session_id,
                recording_file_name=result.file_name,
                recording_bytes=result.total_bytes,
                message=(
                    "MP3 recording is larger than the configured transfer "
                    f"limit ({RECORDING_MAX_TRANSFER_BYTES} bytes)."
                ),
            )
            return False

        chunk_count = recording_chunk_count(
            result.total_bytes,
            RECORDING_CHUNK_BYTES,
        )
        metadata = {
            "session_id": session_id,
            "recording_file_name": result.file_name,
            "recording_bytes": result.total_bytes,
            "recording_chunk_count": chunk_count,
            "recording_sha256": result.sha256,
            "recording_duration_seconds": round(result.duration_seconds, 3),
            "recording_truncated": result.truncated,
        }
        await self.publish(
            "recording_start",
            destinations=[destination],
            **metadata,
        )

        for chunk_index, encoded_data in iter_base64_chunks(
            result.path,
            RECORDING_CHUNK_BYTES,
        ):
            await self.publish(
                "recording_chunk",
                destinations=[destination],
                session_id=session_id,
                recording_chunk_index=chunk_index,
                recording_chunk_count=chunk_count,
                recording_data=encoded_data,
            )
            if RECORDING_CHUNK_DELAY_MS:
                await asyncio.sleep(RECORDING_CHUNK_DELAY_MS / 1_000)

        await self.publish(
            "recording_complete",
            destinations=[destination],
            **metadata,
        )
        logger.info(
            "recording transferred room=%s session=%s bytes=%d chunks=%d",
            self.room.name,
            session_id,
            result.total_bytes,
            chunk_count,
        )
        return True

    def attach_track(
        self,
        track: rtc.Track,
        publication: rtc.RemoteTrackPublication,
        participant: rtc.RemoteParticipant,
    ) -> None:
        if track.kind != rtc.TrackKind.KIND_AUDIO:
            return
        if participant.identity.startswith(AGENT_IDENTITY_PREFIX):
            return

        track_sid = publication.sid or track.sid
        previous = self.track_tasks.get(track_sid)
        if previous is not None and not previous.done():
            return

        task = asyncio.create_task(
            self.transcribe_track(track, track_sid, participant),
            name=f"transcribe-{participant.identity}-{track_sid}",
        )
        self.track_tasks[track_sid] = task
        task.add_done_callback(lambda _: self.track_tasks.pop(track_sid, None))

    async def transcribe_track(
        self,
        track: rtc.Track,
        track_sid: str,
        participant: rtc.RemoteParticipant,
    ) -> None:
        identity = participant.identity
        display_name = participant.name or identity
        logger.info("subscribed audio participant=%s track=%s", identity, track_sid)

        return await self._transcribe_track_yating(
            track,
            track_sid,
            participant,
        )

    async def _transcribe_track_yating(
        self,
        track: rtc.Track,
        track_sid: str,
        participant: rtc.RemoteParticipant,
    ) -> None:
        identity = participant.identity
        display_name = participant.name or identity
        audio_stream = rtc.AudioStream(track)
        try:
            async for audio_event in audio_stream:
                async with self.state_lock:
                    active = self.active
                    recorder = self.recorder if self.active else None
                if recorder is not None:
                    try:
                        recorder.append_frame(
                            track_sid,
                            audio_event.frame.data.tobytes(),
                            audio_event.frame.samples_per_channel,
                        )
                    except Exception:
                        logger.exception(
                            "audio recording failed participant=%s track=%s",
                            identity,
                            track_sid,
                        )
                if active:
                    await self._push_yating_frame(
                        track_sid,
                        identity,
                        display_name,
                        audio_event.frame,
                    )
        except asyncio.CancelledError:
            raise
        except Exception:
            logger.exception(
                "audio stream failed participant=%s track=%s",
                identity,
                track_sid,
            )
        finally:
            await self._finish_yating_track(track_sid)
            await audio_stream.aclose()

    async def _push_yating_frame(
        self,
        track_sid: str,
        identity: str,
        display_name: str,
        frame,
    ) -> None:
        async with self.state_lock:
            if not self.active:
                return
            session_id = self.session_id

        entry = self.yating_streams.get(track_sid)
        if entry is None or entry[2] != session_id:
            entry = await self._open_yating_stream(
                track_sid, identity, display_name, session_id
            )
            if entry is None:
                return
        recognizer = entry[0]
        try:
            # No shared lock here: one slow speaker must not stall the others.
            await recognizer.push_frame(frame)
        except Exception as exception:
            logger.warning(
                "Yating ASR stream dropped participant=%s track=%s error=%s",
                identity,
                track_sid,
                exception,
            )
            await self._drop_yating_stream(track_sid, entry, identity, display_name)

    async def _open_yating_stream(
        self,
        track_sid: str,
        identity: str,
        display_name: str,
        session_id: str,
    ):
        loop = asyncio.get_running_loop()
        async with self.yating_lock:
            if track_sid in self.yating_connecting:
                return None
            retry_at, failures = self.yating_retry.get(track_sid, (0.0, 0))
            if loop.time() < retry_at:
                return None
            self.yating_connecting.add(track_sid)
        try:
            recognizer = await YatingStreamingSTT.connect(
                YATING_API_KEY,
                YATING_PIPELINE,
                custom_model_id=YATING_CUSTOM_MODEL_ID,
            )
        except Exception as exception:
            failures += 1
            delay = min(30.0, 2.0 ** failures)
            async with self.yating_lock:
                self.yating_connecting.discard(track_sid)
                self.yating_retry[track_sid] = (loop.time() + delay, failures)
            logger.warning(
                "Yating ASR connection failed participant=%s track=%s "
                "attempt=%d retry_in=%.0fs error=%s",
                identity,
                track_sid,
                failures,
                delay,
                exception,
            )
            if failures == 1:
                # A status (not "error") keeps Unity recording; the Agent retries.
                await self.publish(
                    "status",
                    state="recording",
                    session_id=session_id,
                    participant_identity=identity,
                    message=(
                        f"Speech recognition for {display_name or identity} "
                        f"is reconnecting: {exception}"
                    ),
                )
            return None

        async with self.state_lock:
            still_active = self.active and self.session_id == session_id
        if not still_active:
            async with self.yating_lock:
                self.yating_connecting.discard(track_sid)
            await recognizer.close()
            return None

        receiver_task = asyncio.create_task(
            self._receive_yating_transcripts(
                recognizer,
                session_id,
                track_sid,
                identity,
                display_name,
            ),
            name=f"yating-transcript-{identity}-{track_sid}",
        )
        entry = (recognizer, receiver_task, session_id)
        async with self.yating_lock:
            self.yating_connecting.discard(track_sid)
            self.yating_retry.pop(track_sid, None)
            self.yating_streams[track_sid] = entry
        logger.info(
            "Yating ASR started participant=%s track=%s pipeline=%s",
            identity,
            track_sid,
            YATING_PIPELINE,
        )
        return entry

    async def _drop_yating_stream(
        self,
        track_sid: str,
        entry,
        identity: str,
        display_name: str,
    ) -> None:
        loop = asyncio.get_running_loop()
        async with self.yating_lock:
            if self.yating_streams.get(track_sid) is entry:
                self.yating_streams.pop(track_sid, None)
            # Reconnect on the next audio frame after a short pause.
            self.yating_retry[track_sid] = (loop.time() + 1.0, 0)
        recognizer, receiver_task, _ = entry
        await recognizer.close()
        receiver_task.cancel()
        await asyncio.gather(receiver_task, return_exceptions=True)

    async def _receive_yating_transcripts(
        self,
        recognizer: YatingStreamingSTT,
        session_id: str,
        track_sid: str,
        identity: str,
        display_name: str,
    ) -> None:
        async for text in recognizer.final_transcripts():
            async with self.state_lock:
                if session_id != self.session_id:
                    continue
                self.segment_count += 1
                sequence = self.segment_count
                state = "recording" if self.active else "processing"

            speaker = self.speaker_label(identity, display_name)
            self.collaboration.add_segment(session_id, speaker, text)

            await self.publish(
                "segment",
                state=state,
                session_id=session_id,
                sequence=sequence,
                participant_identity=identity,
                participant_name=speaker,
                track_sid=track_sid,
                text=text,
            )

    async def _finish_yating_entry(
        self,
        track_sid: str,
        entry: tuple[YatingStreamingSTT, asyncio.Task[None], str],
    ) -> None:
        recognizer, receiver_task, _ = entry
        try:
            await recognizer.finish_input()
        except Exception:
            logger.exception("Yating ASR finish failed track=%s", track_sid)
        finally:
            await recognizer.close()
        try:
            await asyncio.wait_for(receiver_task, timeout=5)
        except asyncio.TimeoutError:
            receiver_task.cancel()
            await asyncio.gather(receiver_task, return_exceptions=True)

    async def _finish_yating_track(self, track_sid: str) -> None:
        async with self.yating_lock:
            entry = self.yating_streams.pop(track_sid, None)
            self.yating_connecting.discard(track_sid)
            self.yating_retry.pop(track_sid, None)
        if entry is not None:
            await self._finish_yating_entry(track_sid, entry)

    async def _finish_all_yating_streams(self) -> None:
        async with self.yating_lock:
            entries = list(self.yating_streams.items())
            self.yating_streams.clear()
            self.yating_connecting.clear()
            self.yating_retry.clear()
        if entries:
            await asyncio.gather(
                *(
                    self._finish_yating_entry(track_sid, entry)
                    for track_sid, entry in entries
                ),
                return_exceptions=True,
            )

    async def close(self) -> None:
        await self.collaboration.close()
        async with self.state_lock:
            self.active = False
            self.stopping = False
            recorder = self.recorder
            self.recorder = None
        if recorder is not None:
            recorder.abort()

        await self._finish_all_yating_streams()

        tasks = list(self.track_tasks.values())
        for task in tasks:
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
        self.track_tasks.clear()


@server.rtc_session()
async def transcript_agent(ctx: agents.JobContext) -> None:
    transcript = MeetingTranscript(ctx.room)
    disconnected = asyncio.Event()

    @ctx.room.on("track_subscribed")
    def on_track_subscribed(
        track: rtc.Track,
        publication: rtc.RemoteTrackPublication,
        participant: rtc.RemoteParticipant,
    ) -> None:
        transcript.attach_track(track, publication, participant)

    @ctx.room.on("data_received")
    def on_data_received(packet: rtc.DataPacket) -> None:
        asyncio.create_task(transcript.handle_control(packet))

    def on_image_stream(reader: rtc.ByteStreamReader, participant_identity: str) -> None:
        asyncio.create_task(transcript.receive_image(reader, participant_identity))

    ctx.room.register_byte_stream_handler(AI_IMAGE_TOPIC, on_image_stream)

    @ctx.room.on("participant_connected")
    def on_participant_connected(participant: rtc.RemoteParticipant) -> None:
        if not participant.identity.startswith(AGENT_IDENTITY_PREFIX):
            asyncio.create_task(transcript.publish_ready(participant.identity))

    @ctx.room.on("disconnected")
    def on_disconnected(*_: Any) -> None:
        disconnected.set()

    await ctx.connect(auto_subscribe=AutoSubscribe.AUDIO_ONLY)
    await transcript.publish_ready()

    try:
        await disconnected.wait()
    finally:
        await transcript.close()


if __name__ == "__main__":
    agents.cli.run_app(server)
