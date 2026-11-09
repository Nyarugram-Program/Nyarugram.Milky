"""TTS rendering and the versioned Milky PCM WebSocket playback path."""

import asyncio
import struct
import sys
from urllib.parse import urlsplit, urlunsplit
from typing import Awaitable, Callable

Api = Callable[[str, dict], Awaitable[dict]]
FRAME_BYTES = 1920
FRAME_SECONDS = 0.02
MAX_PCM_BYTES = 48000 * 2 * 180


class CallAudioError(Exception):
    pass


async def render_tts(text: str, voice: str) -> bytes:
    try:
        import edge_tts
        import miniaudio
    except ImportError:
        raise CallAudioError("TTS 依赖未安装，请安装 edge-tts、miniaudio 和 websockets。") from None

    async def synthesize() -> bytes:
        audio = bytearray()
        speech = f"{text}，重复，{text}"
        async for chunk in edge_tts.Communicate(speech, voice).stream():
            if chunk["type"] == "audio":
                audio.extend(chunk["data"])
                if len(audio) > 8 * 1024 * 1024:
                    raise CallAudioError("合成音频过长，请缩短播报文本。")
        if not audio:
            raise CallAudioError("TTS 服务未返回音频，请稍后再试。")
        return bytes(audio)

    try:
        mp3 = await asyncio.wait_for(synthesize(), timeout=60)
        decoded = await asyncio.to_thread(
            miniaudio.decode, mp3, output_format=miniaudio.SampleFormat.SIGNED16,
            nchannels=1, sample_rate=48000,
        )
        if sys.byteorder != "little":
            decoded.samples.byteswap()
        pcm = decoded.samples.tobytes()
    except CallAudioError:
        raise
    except Exception as error:
        # Provider exceptions may include the text or temporary service credentials.
        if getattr(error, "status", None) == 429:
            raise CallAudioError("Edge TTS 服务当前限流，请稍后重试；本次尚未拨号。") from None
        raise CallAudioError("TTS 合成或音频解码失败，请检查网络与 TTS 依赖。") from None
    if not pcm or len(pcm) % 2 or len(pcm) > MAX_PCM_BYTES:
        raise CallAudioError("合成音频为空或超过三分钟，请缩短播报文本。")
    return pcm


async def wait_for_media(api: Api, call_id: str, timeout: float = 90) -> None:
    deadline = asyncio.get_running_loop().time() + timeout
    answered = False
    while asyncio.get_running_loop().time() < deadline:
        result = await api("nyarugram_get_call", {"call_id": call_id})
        call = result.get("call")
        if not isinstance(call, dict):
            raise CallAudioError("呼叫状态响应格式错误。")
        if call.get("state") in {"ended", "failed"}:
            raise CallAudioError("对方已拒接、挂断或呼叫失败，播报已结束。")
        if not answered and call.get("state") in {"accepted", "negotiating", "active"}:
            answered = True
            deadline = asyncio.get_running_loop().time() + 60
        if call.get("media_state") == "ready" and call.get("state") == "active":
            return
        await asyncio.sleep(0.5)
    raise CallAudioError("等待接听或媒体连接超时，已进入挂断流程。")


def media_address(base_url: str, call_id: str, opened: dict) -> tuple[str, str]:
    expected = f"/nyarugram/calls/{call_id}/media"
    if opened.get("url_path") != expected or not isinstance(opened.get("token"), str) or not opened["token"]:
        raise CallAudioError("媒体接口返回了无效路径或令牌。")
    base = urlsplit(base_url)
    path = base.path.rstrip("/") + expected
    address = urlunsplit(("wss" if base.scheme == "https" else "ws", base.netloc, path, "", ""))
    return address, opened["token"]


async def send_pcm(socket, pcm: bytes) -> None:
    loop = asyncio.get_running_loop()
    deadline = loop.time()
    # One second of trailing silence lets the paced audio queue drain before hangup.
    pcm += bytes(48000 * 2)
    for sequence, offset in enumerate(range(0, len(pcm), FRAME_BYTES)):
        payload = pcm[offset:offset + FRAME_BYTES].ljust(FRAME_BYTES, b"\0")
        header = struct.pack(">2sBBIQ", b"NM", 1, 1, sequence, sequence * 20)
        await asyncio.wait_for(socket.send(header + payload), timeout=5)
        deadline = max(deadline + FRAME_SECONDS, loop.time())
        await asyncio.sleep(max(0, deadline - loop.time()))


async def play_audio(api: Api, base_url: str, call_id: str, pcm: bytes) -> None:
    try:
        from websockets.asyncio.client import connect
    except ImportError:
        raise CallAudioError("请安装 websockets==15.0.1 后再使用 TTS。") from None
    await wait_for_media(api, call_id)
    opened = await api("nyarugram_open_call_media", {"call_id": call_id, "input": True, "output": False})
    address, token = media_address(base_url, call_id, opened)
    try:
        async with connect(
            address, additional_headers={"Authorization": "Bearer " + token},
            open_timeout=10, close_timeout=3, max_size=1936, max_queue=4,
            write_limit=8192, proxy=None,
        ) as socket:
            async def drain() -> None:
                # Keep receiving to detect hangup and avoid backpressure; discard peer audio.
                async for _ in socket:
                    pass

            sender = asyncio.create_task(asyncio.wait_for(
                send_pcm(socket, pcm), timeout=len(pcm) / 96000 + 31,
            ))
            receiver = asyncio.create_task(drain())
            try:
                done, _ = await asyncio.wait({sender, receiver}, return_when=asyncio.FIRST_COMPLETED)
                if sender in done:
                    await sender
                else:
                    raise CallAudioError("媒体连接已关闭，播报中止。")
            finally:
                sender.cancel()
                receiver.cancel()
                await asyncio.gather(sender, receiver, return_exceptions=True)
    except CallAudioError:
        raise
    except Exception:
        raise CallAudioError("媒体连接失败或已断开，播报中止。") from None


async def end_call(api: Api, call_id: str, request_id: str) -> None:
    for attempt in range(3):
        try:
            await asyncio.wait_for(api("nyarugram_end_call", {
                "call_id": call_id, "reason": "hangup", "request_id": request_id,
            }), timeout=5)
            return
        except Exception:
            if attempt == 2:
                raise CallAudioError(f"自动挂断结果未确认，请在 WebUI 检查呼叫 {call_id}。") from None
            await asyncio.sleep(0.5)