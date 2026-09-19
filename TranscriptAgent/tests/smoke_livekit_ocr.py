"""Opt-in paid inference smoke test for photo text recognition and translation.

Run from TranscriptAgent: .venv/Scripts/python.exe tests/smoke_livekit_ocr.py
Draws synthetic Chinese and English signs locally; loads credentials from .env without printing them.
"""
import asyncio
import base64
import io
import json
import sys
import time
from pathlib import Path
from dotenv import load_dotenv
from PIL import Image, ImageDraw, ImageFont

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / "src"))
from collaboration import ModelClient, ModelSettings, CollaborationService

SIGNS = {"zh": ["請先關閉電源", "再更換濾網"], "en": ["CAUTION: Hot surface", "Do not touch"]}


def sign(lines: list[str]) -> bytes:
    font = ImageFont.truetype("C:/Windows/Fonts/msjh.ttc", 40)
    image = Image.new("RGB", (720, 70 * len(lines) + 60), "white")
    draw = ImageDraw.Draw(image)
    for i, line in enumerate(lines):
        draw.text((30, 30 + 70 * i), line, fill="black", font=font)
    buffer = io.BytesIO()
    image.save(buffer, "JPEG", quality=85)
    return buffer.getvalue()


async def main():
    load_dotenv(root / ".env")
    model = ModelClient(ModelSettings(enabled=True, provider="livekit",
        model="google/gemini-3.1-flash-lite"))
    for language, lines in SIGNS.items():
        packets = []
        async def send(packet, destination):
            assert destination == "smoke-viewer"
            packets.append(packet)
        service = CollaborationService(send, model)
        started = time.monotonic()
        try:
            await service.handle_image(sign(lines), {"request_id": "smoke-" + language}, "smoke-viewer")
            await asyncio.wait_for(service.task, 75)
            assert packets[-1]["type"] == "result_complete", packets[-1].get("message")
            data = b"".join(base64.b64decode(p["data"]) for p in packets if p["type"] == "result_chunk")
            result = json.loads(data)
            assert result["source_language"] == language, result
            print(json.dumps({"elapsed_seconds": round(time.monotonic() - started, 2),
                "display_text": result["display_text"]}, ensure_ascii=False))
        finally:
            await service.close()

if __name__ == "__main__":
    asyncio.run(main())
