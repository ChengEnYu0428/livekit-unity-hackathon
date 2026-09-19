"""Opt-in paid inference smoke test using synthetic dialogue only.

Run from TranscriptAgent: .venv/Scripts/python.exe tests/smoke_livekit_summary.py
Loads credentials from the local .env without printing them.
"""
import asyncio
import json
import base64
import hashlib
import sys
import time
from pathlib import Path
from dotenv import load_dotenv

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / "src"))
from collaboration import ModelClient, ModelSettings, CollaborationService

async def main():
    load_dotenv(root / ".env")
    model = ModelClient(ModelSettings(enabled=True, provider="livekit",
        model="google/gemini-3.1-flash-lite"))
    task = {"task": "summary", "schema": {
        "problem_summary": ["string"], "performed_actions": ["string"],
        "current_status": "string", "action_items": ["string"], "next_steps": ["string"]},
        "transcript": [
            {"speaker": "現場人員", "text": "眼鏡相機預覽正常，但遠端看不到畫面。"},
            {"speaker": "工程師", "text": "已確認相機權限正常，還沒檢查影像發布設定。"},
            {"speaker": "現場人員", "text": "會後由小林檢查發布設定，明天下午三點回報。"}],
        "technical_documents": [], "earlier_ai_dialogue": []}
    started = time.monotonic()
    packets = []
    async def send(packet, destination):
        assert destination == "smoke-owner"
        packets.append(packet)
    service = CollaborationService(send, model)
    await service.begin("smoke-session", "smoke-owner")
    for segment in task["transcript"]:
        service.add_segment("smoke-session", segment["speaker"], segment["text"])
    try:
        await service.handle(dict(action="summary", request_id="smoke-summary",
            session_id="smoke-session"), "smoke-owner")
        await asyncio.wait_for(service.task, 75)
        assert packets[-1]["type"] == "result_complete", packets[-1].get("message")
        chunks = sorted((p for p in packets if p["type"] == "result_chunk"), key=lambda p:p["index"])
        data = b"".join(base64.b64decode(p["data"]) for p in chunks)
        assert hashlib.sha256(data).hexdigest() == packets[-1]["sha256"]
        result = json.loads(data)
        assert result["kind"] == "summary" and result["context_segments"] == 3
        print(json.dumps({"elapsed_seconds": round(time.monotonic()-started, 2),
            "model": model.settings.model, "result": result}, ensure_ascii=False))
    finally:
        await service.close()

if __name__ == "__main__":
    asyncio.run(main())
