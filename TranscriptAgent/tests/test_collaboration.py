import asyncio
import base64
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from actions import TaskBoard, tasks_display_text
from collaboration import (CollaborationService, KnowledgeIndex, ModelClient,
                           ModelSettings, clean_result)


class KnowledgeTests(unittest.TestCase):
    def test_chinese_retrieval_and_only_approved_extensions(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "camera.md").write_text("遠端沒有影像時，先確認現場相機預覽，再確認發布影像。", encoding="utf-8")
            (root / "audio.txt").write_text("麥克風靜音時無法傳送語音。", encoding="utf-8")
            (root / "secret.env").write_text("影像 API_KEY=not-a-real-key", encoding="utf-8")
            index = KnowledgeIndex(root)
            results = index.search("遠端沒有影像 相機預覽")
            self.assertEqual(results[0]["source"], "camera.md")
            self.assertTrue(all(d["source"] != "secret.env" for d in index.chunks))
            self.assertEqual(index.search("unrelatedword"), [])

    def test_citations_are_restricted_to_retrieved_documents(self):
        raw = {"answer": "請確認預覽", "next_steps": [], "sources": ["invented", "a"]}
        result = clean_result("answer", raw, [{"id": "a"}])
        self.assertEqual(result["sources"], [{"id": "a"}])

    def test_malformed_summary_is_rejected(self):
        with self.assertRaises(ValueError):
            clean_result("summary", {"current_status": "ok"}, [])


class ServiceTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.sent = []
        async def send(packet, destination):
            self.sent.append((packet, destination))
        self.model = AsyncMock()
        self.model.settings = ModelSettings(True, "", "test-model")
        self.model.generate.return_value = {"answer": "請確認預覽。" * 600,
            "next_steps": ["回報預覽是否有畫面"], "sources": []}
        self.tmp = tempfile.TemporaryDirectory()
        self.service = CollaborationService(send, self.model, KnowledgeIndex(Path(self.tmp.name)))
        await self.service.begin("session-a", "owner")
        self.service.add_segment("session-a", "現場", "遠端看不到我的影像")

    async def asyncTearDown(self):
        await self.service.close()
        self.tmp.cleanup()

    async def ask(self, rid="r1", sender="owner", sid="session-a"):
        await self.service.handle(dict(action="ask", request_id=rid, session_id=sid, question="怎麼辦"), sender)
        if self.service.task:
            await self.service.task

    async def test_result_chunks_roundtrip_utf8_hash_and_destination(self):
        await self.ask()
        packets = [p for p, _ in self.sent if p["type"] == "result_chunk"]
        self.assertGreater(len(packets), 1)
        data = b"".join(base64.b64decode(p["data"]) for p in packets)
        self.assertEqual(hashlib.sha256(data).hexdigest(), packets[0]["sha256"])
        self.assertIn("請確認預覽", json.loads(data)["display_text"])
        self.assertTrue(all(d == "owner" for _, d in self.sent))
        self.assertTrue(all(len(json.dumps(p, ensure_ascii=False).encode()) < 15000 for p, _ in self.sent))

    async def summary(self, rid="s1", sender="owner", sid="session-a"):
        await self.service.handle(dict(action="summary", request_id=rid, session_id=sid), sender)
        if self.service.task:
            await self.service.task

    async def test_wrong_controller_and_stale_session_cannot_summarize(self):
        await self.summary(sender="other")
        await self.summary("s2", sid="old-session")
        self.model.generate.assert_not_awaited()
        self.assertEqual([p["type"] for p, _ in self.sent], ["error", "error"])

    async def test_other_participant_question_is_direct_without_transcript(self):
        await self.ask(sender="other")
        task = self.model.generate.call_args.args[0]
        self.assertEqual(task["mode"], "direct")
        self.assertEqual(task["transcript"], [])
        self.assertTrue(all(d == "other" for _, d in self.sent))
        self.assertEqual(self.sent[-1][0]["type"], "result_complete")

    async def test_direct_question_without_recording_keeps_own_follow_up_history(self):
        await self.service.begin("", "")
        await self.ask("d1", sender="viewer", sid="")
        self.service.last_request = 0
        await self.ask("d2", sender="viewer", sid="")
        self.assertEqual(len(self.model.generate.call_args.args[0]["earlier_ai_dialogue"]), 1)
        self.service.last_request = 0
        await self.ask("d3", sender="someone-else", sid="")
        self.assertEqual(self.model.generate.call_args.args[0]["earlier_ai_dialogue"], [])
        self.assertFalse(self.service.history)

    async def test_direct_question_must_not_be_blank(self):
        await self.service.handle(dict(action="ask", request_id="d1", session_id="", question="  "), "viewer")
        self.model.generate.assert_not_awaited()
        self.assertEqual(self.sent[-1][0]["message"], "請輸入問題。")

    async def test_duplicate_request_is_not_billed_twice(self):
        await self.ask()
        await self.ask()
        self.assertEqual(self.model.generate.await_count, 1)

    async def test_history_and_new_session_isolation(self):
        await self.ask()
        self.service.last_request = 0
        await self.ask("r2")
        task = self.model.generate.call_args.args[0]
        self.assertEqual(len(task["earlier_ai_dialogue"]), 1)
        await self.service.begin("session-b", "new-owner")
        self.assertFalse(self.service.history)
        self.assertFalse(self.service.segments)
        self.service.add_segment("session-a", "old", "不得洩漏")
        self.assertFalse(self.service.segments)

    async def test_context_bound_reports_omissions(self):
        for _ in range(550):
            self.service.add_segment("session-a", "speaker", "測試" * 900)
        context, omitted = self.service.context()
        self.assertLessEqual(sum(len(s["text"]) for s in context), 22000)
        self.assertGreater(omitted, 500)

    async def test_new_session_cancels_inflight_generation(self):
        entered = asyncio.Event()
        async def block(_):
            entered.set()
            await asyncio.Event().wait()
        self.model.generate.side_effect = block
        await self.service.handle(dict(action="ask", request_id="r", session_id="session-a"), "owner")
        await entered.wait()
        await self.service.begin("new", "new-owner")
        self.assertFalse(any(p["type"] == "result_chunk" for p, _ in self.sent))

    async def test_model_errors_do_not_expose_details(self):
        self.model.generate.side_effect = RuntimeError("secret=private-provider-payload")
        await self.ask()
        packet = self.sent[-1][0]
        self.assertEqual(packet["type"], "error")
        self.assertNotIn("private", packet["message"])

    async def test_stop_summary_uses_final_transcript(self):
        self.model.generate.return_value = {"problem_summary": ["沒有畫面"],
            "performed_actions": ["已檢查預覽"], "current_status": "等待確認",
            "action_items": ["回報遠端結果"], "next_steps": ["確認影像發布"]}
        self.service.add_segment("session-a", "現場", "我已檢查預覽有畫面")
        self.service.summarize_on_stop()
        await self.service.task
        summary, actions = [call.args[0] for call in self.model.generate.call_args_list]
        self.assertEqual(summary["task"], "summary")
        self.assertIn("已檢查", summary["transcript"][-1]["text"])
        # Stopping also looks for action items in the transcript.
        self.assertEqual(actions["task"], "actions")
        self.assertEqual(len(actions["transcript"]), 2)

    async def test_unconfigured_model_is_explicit_error(self):
        self.service.model = ModelClient(ModelSettings())
        await self.ask()
        self.assertIn("尚未設定", self.sent[-1][0]["message"])

    async def test_model_timeout_is_explicit_error(self):
        self.model.generate.side_effect = asyncio.TimeoutError()
        await self.ask()
        self.assertEqual(self.sent[-1][0]["type"], "error")
        self.assertIn("timed out", self.sent[-1][0]["message"])

    async def test_livekit_provider_does_not_require_external_api_url(self):
        model = ModelClient(ModelSettings(enabled=True, model="google/gemini-3.1-flash-lite", provider="livekit"))
        with patch.object(model, "generate_livekit", new_callable=AsyncMock) as generate:
            generate.return_value = {"current_status": "ready"}
            self.assertEqual(await model.generate({"task": "summary"}), {"current_status": "ready"})
            generate.assert_awaited_once()

    async def test_summary_does_not_load_document_index(self):
        self.service.knowledge = None
        self.model.generate.return_value = dict(problem_summary=[], performed_actions=[],
            current_status="Awaiting confirmation", action_items=[], next_steps=[])
        with patch("collaboration.KnowledgeIndex", side_effect=AssertionError("Unexpected document load")):
            await self.service.handle(dict(action="summary", request_id="summary1", session_id="session-a"), "owner")
            await self.service.task
        self.assertEqual(self.sent[-1][0]["type"], "result_complete")


class TaskBoardTests(unittest.TestCase):
    def test_create_blocks_duplicates_and_builds_calendar(self):
        board = TaskBoard()
        op = {"op": "create", "title": "做簡報", "owner": "小美", "deadline": "星期三以前",
              "deadline_date": "2026-09-23"}
        first = board.apply([op])
        again = board.apply([dict(op, title="做簡報。")])
        self.assertEqual([a["type"] for a in first], ["create_task", "calendar_created"])
        self.assertEqual(again[0]["status"], "already_exists")
        self.assertEqual(len(board.tasks), 1)
        self.assertEqual(board.calendar()[0]["date"], "2026-09-23")

    def test_update_moves_deadline_and_invalid_dates_are_dropped(self):
        board = TaskBoard()
        board.apply([{"op": "create", "title": "報價單", "owner": "阿明", "deadline": "明天",
                      "deadline_date": "2026-09-20"}])
        task_id = board.tasks[0]["id"]
        actions = board.apply([{"op": "update", "task_id": task_id, "title": "", "owner": "",
                                "deadline": "下星期一", "deadline_date": "2026-09-28"}])
        self.assertEqual([a["type"] for a in actions], ["update_task", "calendar_updated"])
        self.assertEqual(board.tasks[0]["deadline_date"], "2026-09-28")
        board.apply([{"op": "update", "task_id": task_id, "deadline": "未指定", "deadline_date": "not-a-date"}])
        self.assertEqual(board.calendar(), [])
        missing = board.apply([{"op": "update", "task_id": "nope", "title": "x"}])
        self.assertEqual(missing[0]["status"], "not_found")

    def test_malformed_operations_are_rejected(self):
        with self.assertRaises(ValueError):
            TaskBoard().apply("create everything")

    def test_display_lists_tasks_calendar_and_changes(self):
        board = TaskBoard()
        actions = board.apply([{"op": "create", "title": "做簡報", "owner": "小美",
                                "deadline": "星期三以前", "deadline_date": "2026-09-23"}])
        text = tasks_display_text(dict(tasks=board.snapshot(), calendar_events=board.calendar(),
                                       actions=actions, message="已建立一筆待辦。"))
        self.assertIn("• 做簡報｜負責人：小美｜期限：星期三以前（2026-09-23）", text)
        self.assertIn("2026-09-23（星期三）  做簡報 — 小美", text)
        self.assertIn("新增：做簡報", text)


class TaskRequestTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.sent = []
        async def send(packet, destination):
            self.sent.append((packet, destination))
        self.model = AsyncMock()
        self.model.settings = ModelSettings(True, "", "test-model")
        self.model.generate.return_value = {"operations": [{"op": "create", "title": "做簡報",
            "owner": "小美", "deadline": "星期三以前", "deadline_date": "2026-09-23"}],
            "message": "已建立一筆待辦。"}
        self.service = CollaborationService(send, self.model)

    async def asyncTearDown(self):
        await self.service.close()

    async def request(self, question="", sender="viewer", sid="", rid="t1"):
        self.service.last_request = 0
        await self.service.handle(dict(action="tasks", request_id=rid, session_id=sid,
                                       question=question), sender)
        if self.service.task:
            await self.service.task
        data = b"".join(base64.b64decode(p["data"]) for p, _ in self.sent
                        if p["type"] == "result_chunk" and p["request_id"] == rid)
        return json.loads(data) if data else None

    async def test_typed_text_creates_task_without_recording(self):
        result = await self.request("小美負責做簡報，星期三以前完成")
        task = self.model.generate.call_args.args[0]
        self.assertEqual(task["task"], "actions")
        self.assertIn("today", task)
        self.assertEqual(result["kind"], "tasks")
        self.assertEqual(result["tasks"][0]["owner"], "小美")
        self.assertEqual(result["calendar_events"][0]["date"], "2026-09-23")

    async def test_blank_request_outside_recording_only_lists(self):
        await self.request("小美負責做簡報", rid="t1")
        result = await self.request("", sender="someone-else", rid="t2")
        self.assertEqual(self.model.generate.await_count, 1)
        self.assertEqual(len(result["tasks"]), 1)

    async def test_recording_initiator_blank_request_reads_only_new_transcript(self):
        await self.service.begin("session-a", "owner")
        self.service.add_segment("session-a", "主管", "小美負責做簡報，星期三以前完成")
        await self.request("", sender="owner", sid="session-a", rid="t1")
        self.assertEqual(len(self.model.generate.call_args.args[0]["transcript"]), 1)
        await self.request("", sender="owner", sid="session-a", rid="t2")
        self.assertEqual(self.model.generate.await_count, 1)
        self.assertIn("尚未有新的逐字稿", self.sent[-1][0]["message"])


JPEG = b"\xff\xd8\xff\xe0" + b"\x00" * 64


class OcrTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.sent = []
        async def send(packet, destination):
            self.sent.append((packet, destination))
        self.model = AsyncMock()
        self.model.settings = ModelSettings(True, "", "test-model")
        self.model.generate.return_value = {"source_language": "zh",
            "original_text": "請先關閉電源", "translated_text": "Turn off the power first"}
        self.service = CollaborationService(send, self.model)

    async def asyncTearDown(self):
        await self.service.close()

    async def photo(self, data=JPEG, rid="p1", sender="viewer"):
        await self.service.handle_image(data, {"request_id": rid}, sender)
        if self.service.task:
            await self.service.task

    def result(self):
        data = b"".join(base64.b64decode(p["data"]) for p, _ in self.sent if p["type"] == "result_chunk")
        return json.loads(data)

    async def test_photo_works_without_recording_session_and_returns_translation(self):
        await self.photo()
        task, image = self.model.generate.call_args.args
        self.assertEqual(task["task"], "ocr")
        self.assertTrue(image.startswith("data:image/jpeg;base64,"))
        result = self.result()
        self.assertEqual((result["kind"], result["target_language"]), ("ocr", "en"))
        self.assertIn("Translation (English)\nTurn off the power first", result["display_text"])
        self.assertTrue(all(d == "viewer" for _, d in self.sent))

    async def test_english_photo_translates_to_chinese(self):
        self.model.generate.return_value = {"source_language": "en",
            "original_text": "Replace filter", "translated_text": "更換濾網"}
        await self.photo()
        self.assertEqual(self.result()["target_language"], "zh")

    async def test_invalid_images_never_call_model(self):
        await self.photo(b"not an image", "p1")
        await self.photo(b"", "p2")
        await self.photo(JPEG, "bad id!")
        self.model.generate.assert_not_awaited()
        self.assertEqual([p["type"] for p, _ in self.sent], ["error", "error"])

    async def test_photo_without_text_is_explained(self):
        self.model.generate.return_value = {"source_language": "none",
            "original_text": "", "translated_text": ""}
        await self.photo()
        self.assertEqual(self.sent[-1][0]["type"], "error")
        self.assertIn("沒有可辨識的文字", self.sent[-1][0]["message"])

    async def test_duplicate_photo_is_not_billed_twice(self):
        await self.photo()
        self.service.last_request = 0
        await self.photo()
        self.assertEqual(self.model.generate.await_count, 1)


if __name__ == "__main__":
    unittest.main()
