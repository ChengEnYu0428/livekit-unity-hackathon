import asyncio
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace


SOURCE_ROOT = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SOURCE_ROOT))

from yating_asr import CHUNK_BYTES, YatingStreamingSTT  # noqa: E402


class _FakeSession:
    closed = False

    async def close(self) -> None:
        self.closed = True


class _FakeWebSocket:
    def __init__(self) -> None:
        self.closed = False
        self.sent: list[bytes] = []

    def __aiter__(self):
        async def messages():
            await asyncio.Future()
            yield None

        return messages()

    async def send_bytes(self, value: bytes) -> None:
        self.sent.append(value)

    async def close(self) -> None:
        self.closed = True

    def exception(self):
        return None


class YatingMessageTests(unittest.TestCase):
    def test_only_returns_final_sentence(self) -> None:
        self.assertEqual(
            YatingStreamingSTT.final_text(
                {"pipe": {"asr_final": True, "asr_sentence": "測試完成"}}
            ),
            "測試完成",
        )
        self.assertEqual(
            YatingStreamingSTT.final_text(
                {"pipe": {"asr_final": False, "asr_sentence": "測試"}}
            ),
            "",
        )


class YatingAudioTests(unittest.IsolatedAsyncioTestCase):
    async def test_sends_two_thousand_byte_pcm_chunks(self) -> None:
        websocket = _FakeWebSocket()
        recognizer = YatingStreamingSTT(_FakeSession(), websocket)
        frame = SimpleNamespace(
            data=memoryview(b"\x01\x00" * 1_000),
            num_channels=1,
            sample_rate=16_000,
        )

        await recognizer.push_frame(frame)

        self.assertEqual(websocket.sent, [b"\x01\x00" * 1_000])
        self.assertEqual(len(websocket.sent[0]), CHUNK_BYTES)
        await recognizer.close()


if __name__ == "__main__":
    unittest.main()
