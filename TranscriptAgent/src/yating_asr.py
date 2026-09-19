"""Streaming Yating ASR client for LiveKit audio tracks."""

from __future__ import annotations

import asyncio
import audioop
import json
import logging
from collections.abc import AsyncIterator
from typing import Any
from urllib.parse import urlencode

import aiohttp


logger = logging.getLogger("yating-asr")

TOKEN_URL = "https://asr.api.yating.tw/v1/token"
WEBSOCKET_URL = "wss://asr.api.yating.tw/ws/v1/"
TARGET_SAMPLE_RATE = 16_000
CHUNK_BYTES = 2_000


class YatingAsrError(RuntimeError):
    """Raised when Yating authentication or streaming fails."""


class YatingStreamingSTT:
    """One Yating WebSocket recognition stream for one LiveKit audio track."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        websocket: aiohttp.ClientWebSocketResponse,
    ) -> None:
        self._session = session
        self._websocket = websocket
        self._buffer = bytearray()
        self._rate_state = None
        self._queue: asyncio.Queue[str | None] = asyncio.Queue()
        self._ready = asyncio.Event()
        self._eof = asyncio.Event()
        self._receive_error: Exception | None = None
        self._closed = False
        self._receiver_task = asyncio.create_task(
            self._receive_messages(),
            name="yating-asr-receiver",
        )

    @classmethod
    async def connect(
        cls,
        api_key: str,
        pipeline: str,
        *,
        custom_model_id: str = "",
        connect_timeout_seconds: float = 20.0,
    ) -> "YatingStreamingSTT":
        if not api_key.strip():
            raise YatingAsrError(
                "YATING_API_KEY is empty. Configure it as a LiveKit Cloud "
                "Agent secret before starting the worker."
            )

        timeout = aiohttp.ClientTimeout(total=connect_timeout_seconds)
        session = aiohttp.ClientSession(timeout=timeout)
        try:
            body: dict[str, Any] = {"pipeline": pipeline}
            if custom_model_id.strip():
                body["options"] = {"s3CusModelKey": custom_model_id.strip()}

            async with session.post(
                TOKEN_URL,
                headers={
                    "key": api_key.strip(),
                    "Content-Type": "application/json",
                },
                json=body,
            ) as response:
                response_text = await response.text()
                try:
                    token_response = json.loads(response_text)
                except json.JSONDecodeError as exception:
                    raise YatingAsrError(
                        "Yating token endpoint returned a non-JSON response "
                        f"(HTTP {response.status})."
                    ) from exception

                auth_token = str(token_response.get("auth_token", "")).strip()
                if response.status != 201 or not auth_token:
                    detail = str(
                        token_response.get("message")
                        or token_response.get("detail")
                        or "authentication failed"
                    )
                    raise YatingAsrError(
                        f"Yating token request failed (HTTP {response.status}): "
                        f"{detail}"
                    )

            websocket = await session.ws_connect(
                f"{WEBSOCKET_URL}?{urlencode({'token': auth_token})}",
                heartbeat=20,
                autoclose=True,
            )
            recognizer = cls(session, websocket)
            try:
                await asyncio.wait_for(
                    recognizer._ready.wait(),
                    timeout=connect_timeout_seconds,
                )
            except Exception:
                await recognizer.close()
                raise
            if recognizer._receive_error is not None:
                receive_error = recognizer._receive_error
                await recognizer.close()
                raise receive_error
            return recognizer
        except Exception:
            if not session.closed:
                await session.close()
            raise

    @staticmethod
    def final_text(message: dict[str, Any]) -> str:
        pipe = message.get("pipe")
        if not isinstance(pipe, dict) or pipe.get("asr_final") is not True:
            return ""
        return str(pipe.get("asr_sentence", "")).strip()

    async def _receive_messages(self) -> None:
        try:
            async for message in self._websocket:
                if message.type == aiohttp.WSMsgType.TEXT:
                    try:
                        payload = json.loads(message.data)
                    except (TypeError, json.JSONDecodeError):
                        logger.warning("Ignoring malformed Yating ASR message")
                        continue

                    status = str(payload.get("status", "")).lower()
                    if status == "ok":
                        self._ready.set()
                    elif status == "error":
                        detail = str(payload.get("detail", "unknown error"))
                        raise YatingAsrError(f"Yating ASR stream error: {detail}")

                    pipe = payload.get("pipe")
                    if isinstance(pipe, dict) and pipe.get("asr_eof"):
                        self._eof.set()

                    text = self.final_text(payload)
                    if text:
                        await self._queue.put(text)
                elif message.type == aiohttp.WSMsgType.ERROR:
                    raise YatingAsrError(
                        f"Yating WebSocket error: {self._websocket.exception()}"
                    )
        except asyncio.CancelledError:
            raise
        except Exception as exception:
            self._receive_error = exception
            logger.exception("Yating ASR receiver stopped unexpectedly")
        finally:
            self._ready.set()
            self._eof.set()
            await self._queue.put(None)

    def _to_mono_16k_pcm(self, frame) -> bytes:
        pcm = frame.data.tobytes()
        channels = int(frame.num_channels)
        if channels > 1:
            pcm = audioop.tomono(pcm, 2, 0.5, 0.5)
        sample_rate = int(frame.sample_rate)
        if sample_rate != TARGET_SAMPLE_RATE:
            pcm, self._rate_state = audioop.ratecv(
                pcm,
                2,
                1,
                sample_rate,
                TARGET_SAMPLE_RATE,
                self._rate_state,
            )
        return pcm

    async def push_frame(self, frame) -> None:
        if self._closed:
            return
        if self._receive_error is not None:
            raise self._receive_error

        self._buffer.extend(self._to_mono_16k_pcm(frame))
        while len(self._buffer) >= CHUNK_BYTES:
            chunk = bytes(self._buffer[:CHUNK_BYTES])
            del self._buffer[:CHUNK_BYTES]
            await self._websocket.send_bytes(chunk)

    async def final_transcripts(self) -> AsyncIterator[str]:
        while True:
            text = await self._queue.get()
            if text is None:
                return
            yield text

    async def finish_input(self, timeout_seconds: float = 8.0) -> None:
        if self._closed or self._websocket.closed:
            return
        if self._buffer:
            padded = bytes(self._buffer).ljust(CHUNK_BYTES, b"\x00")
            self._buffer.clear()
            await self._websocket.send_bytes(padded)
        await self._websocket.send_bytes(b"")
        try:
            await asyncio.wait_for(self._eof.wait(), timeout=timeout_seconds)
        except asyncio.TimeoutError:
            logger.warning("Timed out waiting for Yating ASR EOF")

    async def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        if not self._websocket.closed:
            await self._websocket.close()
        if not self._receiver_task.done():
            self._receiver_task.cancel()
        await asyncio.gather(self._receiver_task, return_exceptions=True)
        if not self._session.closed:
            await self._session.close()
