import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch


SOURCE_ROOT = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SOURCE_ROOT))

import agent  # noqa: E402


class _FakeRecognizer:
    def __init__(self) -> None:
        self.frames = []

    async def push_frame(self, frame) -> None:
        self.frames.append(frame)

    async def final_transcripts(self):
        if False:
            yield ""

    async def finish_input(self) -> None:
        return None

    async def close(self) -> None:
        return None


class YatingStartStopGateTests(unittest.IsolatedAsyncioTestCase):
    async def test_room_join_does_not_connect_before_start(self) -> None:
        transcript = agent.MeetingTranscript(SimpleNamespace(name="room"))
        connect = AsyncMock()

        with patch.object(agent.YatingStreamingSTT, "connect", connect):
            await transcript._push_yating_frame(
                "track",
                "user",
                "User",
                SimpleNamespace(),
            )

        connect.assert_not_awaited()

    async def test_active_session_connects_and_sends_audio(self) -> None:
        transcript = agent.MeetingTranscript(SimpleNamespace(name="room"))
        transcript.active = True
        transcript.session_id = "session"
        recognizer = _FakeRecognizer()
        frame = SimpleNamespace()

        with patch.object(
            agent.YatingStreamingSTT,
            "connect",
            AsyncMock(return_value=recognizer),
        ) as connect:
            await transcript._push_yating_frame(
                "track",
                "user",
                "User",
                frame,
            )

        connect.assert_awaited_once()
        self.assertEqual(recognizer.frames, [frame])
        await transcript._finish_all_yating_streams()

    def _active(self):
        transcript = agent.MeetingTranscript(SimpleNamespace(name="room"))
        transcript.active = True
        transcript.session_id = "session"
        transcript.publish = AsyncMock()
        return transcript

    async def test_dropped_stream_reconnects_instead_of_silencing_speaker(self) -> None:
        transcript = self._active()
        broken, healthy = _FakeRecognizer(), _FakeRecognizer()
        broken.push_frame = AsyncMock(side_effect=ConnectionResetError("closed"))
        connect = AsyncMock(side_effect=[broken, healthy])
        with patch.object(agent.YatingStreamingSTT, "connect", connect):
            await transcript._push_yating_frame("track", "user", "User", "f1")
            self.assertNotIn("track", transcript.yating_streams)
            transcript.yating_retry["track"] = (0.0, 0)  # skip the 1 s pause
            await transcript._push_yating_frame("track", "user", "User", "f2")
        self.assertEqual(connect.await_count, 2)
        self.assertEqual(healthy.frames, ["f2"])
        await transcript._finish_all_yating_streams()

    async def test_one_speaker_failing_does_not_block_another(self) -> None:
        transcript = self._active()
        other = _FakeRecognizer()
        connect = AsyncMock(side_effect=[RuntimeError("limit reached"), other])
        with patch.object(agent.YatingStreamingSTT, "connect", connect):
            await transcript._push_yating_frame("track-a", "a", "A", "a1")
            await transcript._push_yating_frame("track-b", "b", "B", "b1")
            # Speaker A is in back-off, so this does not try to reconnect yet.
            await transcript._push_yating_frame("track-a", "a", "A", "a2")
        self.assertEqual(other.frames, ["b1"])
        self.assertEqual(connect.await_count, 2)
        packet = transcript.publish.await_args
        self.assertEqual(packet.args[0], "status")
        self.assertEqual(packet.kwargs["state"], "recording")
        await transcript._finish_all_yating_streams()


class RoleTests(unittest.IsolatedAsyncioTestCase):
    async def test_role_packets_label_transcript_speakers(self) -> None:
        transcript = agent.MeetingTranscript(SimpleNamespace(name="room"))
        packet = SimpleNamespace(topic=agent.ROLE_TOPIC, data='{"role":"expert"}'.encode(),
                                 participant=SimpleNamespace(identity="amy"))
        await transcript.handle_control(packet)
        self.assertEqual(transcript.speaker_label("amy", "Amy"), "Amy（專家端）")
        self.assertEqual(transcript.speaker_label("bob", ""), "bob")
        packet.data = b'{"role":"admin"}'
        await transcript.handle_control(packet)
        self.assertEqual(transcript.roles["amy"], "expert")

if __name__ == "__main__":
    unittest.main()
