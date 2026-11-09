import asyncio
import struct
import unittest
from unittest.mock import AsyncMock, patch

import test_qq_call as base
from nyarugram_qq_call import call_audio

plugin = base.plugin


class TtsCommandTests(unittest.IsolatedAsyncioTestCase):
    asyncSetUp = base.QqCallTests.asyncSetUp
    asyncTearDown = base.QqCallTests.asyncTearDown
    dispatch = base.QqCallTests.dispatch

    def api_mock(self, voice=True):
        async def respond(action, payload):
            if action == "nyarugram_get_capabilities":
                return {"calls": {"voice": voice}}
            if action == "nyarugram_resolve_username":
                return {"user_id": 100002}
            if action == "nyarugram_start_call":
                return {"call": {"call_id": "tts-test-call", "state": "outgoing"}}
            if action == "nyarugram_end_call":
                return {"call": {"call_id": "tts-test-call", "state": "ended"}}
            raise AssertionError(action)
        return AsyncMock(side_effect=respond)

    async def test_tts_command_plays_then_hangs_up(self):
        api = self.api_mock()
        with patch.object(plugin, "call_api", api), patch.object(plugin, "render_tts", new_callable=AsyncMock) as render, patch.object(plugin, "play_audio", new_callable=AsyncMock) as play:
            render.return_value = b"\0\0"
            await self.dispatch("/call bot -tts 今天天气真好")
            render.assert_awaited_once_with("今天天气真好", "zh-CN-XiaoxiaoNeural")
            play.assert_awaited_once_with(api, plugin.endpoint, "tts-test-call", b"\0\0")
            self.assertEqual(api.await_args_list[-1].args[0], "nyarugram_end_call")
            self.assertIn("已挂断", str(self.send.call_args.kwargs["message"]))
            self.assertEqual(plugin.tts_jobs, {})

    async def test_ring_only_rejects_tts_before_calling(self):
        api = self.api_mock(voice=False)
        with patch.object(plugin, "call_api", api), patch.object(plugin, "render_tts", new_callable=AsyncMock) as render:
            await self.dispatch("/call bot -tts 提醒")
            render.assert_not_awaited()
            self.assertEqual([args.args[0] for args in api.await_args_list], ["nyarugram_get_capabilities"])
            self.assertIn("tgcalls", str(self.send.call_args.kwargs["message"]))

    async def test_playback_failure_still_hangs_up(self):
        api = self.api_mock()
        with patch.object(plugin, "call_api", api), patch.object(plugin, "render_tts", new_callable=AsyncMock) as render, patch.object(plugin, "play_audio", new_callable=AsyncMock) as play:
            render.return_value = b"\0\0"
            play.side_effect = call_audio.CallAudioError("媒体连接中断")
            await self.dispatch("/call bot -tts 提醒")
            self.assertEqual(api.await_args_list[-1].args[0], "nyarugram_end_call")
            self.assertIn("媒体连接中断", str(self.send.call_args.kwargs["message"]))

    async def test_synthesis_failure_never_places_call(self):
        api = self.api_mock()
        with patch.object(plugin, "call_api", api), patch.object(plugin, "render_tts", new_callable=AsyncMock) as render:
            render.side_effect = call_audio.CallAudioError("合成失败")
            await self.dispatch("/call bot -tts 提醒")
            self.assertEqual([args.args[0] for args in api.await_args_list], ["nyarugram_get_capabilities"])

    async def test_shutdown_cancels_tts_task_and_hangs_up(self):
        api = self.api_mock()
        playing = asyncio.Event()

        async def pending_audio(*args):
            playing.set()
            await asyncio.Future()

        with patch.object(plugin, "call_api", api), patch.object(plugin, "render_tts", new_callable=AsyncMock) as render, patch.object(plugin, "play_audio", side_effect=pending_audio):
            render.return_value = b"\0\0"
            task = asyncio.create_task(self.dispatch("/call bot -tts 提醒"))
            await asyncio.wait_for(playing.wait(), 5)
            await asyncio.wait_for(plugin.shutdown(), 5)
            await asyncio.wait_for(asyncio.gather(task, return_exceptions=True), 5)
            self.assertEqual(api.await_args_list[-1].args[0], "nyarugram_end_call")
            self.assertEqual(plugin.tts_jobs, {})

    async def test_duplicate_active_tts_command_does_not_call_twice(self):
        api = self.api_mock()
        playing = asyncio.Event()
        release = asyncio.Event()

        async def pending_audio(*args):
            playing.set()
            await release.wait()

        with patch.object(plugin, "call_api", api), patch.object(plugin, "render_tts", new_callable=AsyncMock) as render, patch.object(plugin, "play_audio", side_effect=pending_audio):
            render.return_value = b"\0\0"
            task = asyncio.create_task(self.dispatch("/call bot -tts 提醒"))
            try:
                await asyncio.wait_for(playing.wait(), 5)
                await self.dispatch("/call bot -tts 提醒")
                starts = [args for args in api.await_args_list if args.args[0] == "nyarugram_start_call"]
                self.assertEqual(len(starts), 1)
            finally:
                release.set()
                await task

    async def test_tts_empty_and_excessive_text_are_rejected(self):
        for value in ["bot -tts", "bot -tts   ", "bot -tts " + "字" * 301]:
            with self.assertRaises(plugin.MilkyError):
                plugin.parse_arguments(value)
        self.assertEqual(plugin.parse_arguments("@bot -tts 今天天气真好"), ("bot", "今天天气真好"))
        self.assertEqual(plugin.parse_arguments("bot"), ("bot", None))


class AudioProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_synthesis_repeats_text_with_spoken_repeat_marker(self):
        import edge_tts
        import miniaudio
        from array import array
        from types import SimpleNamespace

        class Speech:
            async def stream(self):
                yield {"type": "audio", "data": b"fake-mp3"}

        with patch.object(edge_tts, "Communicate", return_value=Speech()) as speech, patch.object(miniaudio, "decode", return_value=SimpleNamespace(samples=array("h", [1, 2]))) as decode:
            pcm = await call_audio.render_tts("今天天气真好", "zh-CN-XiaoxiaoNeural")
            speech.assert_called_once_with("今天天气真好，重复，今天天气真好", "zh-CN-XiaoxiaoNeural")
            self.assertEqual(decode.call_args.kwargs["sample_rate"], 48000)
            self.assertEqual(decode.call_args.kwargs["nchannels"], 1)
            self.assertEqual(len(pcm), 4)

    async def test_media_ready_gate_and_remote_hangup(self):
        api = AsyncMock(side_effect=[
            {"call": {"state": "ringing", "media_state": "none"}},
            {"call": {"state": "active", "media_state": "ready"}},
        ])
        with patch.object(call_audio.asyncio, "sleep", new_callable=AsyncMock):
            await call_audio.wait_for_media(api, "call")
        self.assertEqual(api.await_count, 2)
        with self.assertRaises(call_audio.CallAudioError):
            await call_audio.wait_for_media(AsyncMock(return_value={"call": {"state": "ended"}}), "call")

    async def test_pcm_frames_and_trailing_silence(self):
        socket = AsyncMock()
        with patch.object(call_audio.asyncio, "sleep", new_callable=AsyncMock):
            await call_audio.send_pcm(socket, b"\x01\x00" * 960)
        frames = [args.args[0] for args in socket.send.await_args_list]
        self.assertEqual(len(frames), 51)
        for sequence, frame in enumerate(frames):
            self.assertEqual(len(frame), 1936)
            self.assertEqual(struct.unpack(">2sBBIQ", frame[:16]), (b"NM", 1, 1, sequence, sequence * 20))
        self.assertEqual(frames[-1][16:], bytes(1920))

    async def test_media_tokens_stay_on_configured_origin(self):
        address, token = call_audio.media_address("https://127.0.0.1:8091/proxy", "call", {
            "url_path": "/nyarugram/calls/call/media", "token": "temporary-token",
        })
        self.assertEqual(address, "wss://127.0.0.1:8091/proxy/nyarugram/calls/call/media")
        self.assertNotIn(token, address)
        with self.assertRaises(call_audio.CallAudioError):
            call_audio.media_address("https://127.0.0.1", "call", {"url_path": "https://other.example/media", "token": token})

    async def test_real_websocket_receives_paced_pcm_and_media_token(self):
        from websockets.asyncio.server import serve
        frames = []
        received_headers = []

        async def receive(socket):
            received_headers.append(socket.request.headers["Authorization"])
            async for frame in socket:
                frames.append(frame)

        async with serve(receive, "127.0.0.1", 0) as server:
            port = server.sockets[0].getsockname()[1]
            api = AsyncMock(side_effect=[
                {"call": {"state": "active", "media_state": "ready"}},
                {"url_path": "/nyarugram/calls/live-call/media", "token": "temporary-media-token"},
            ])
            await call_audio.play_audio(api, f"http://127.0.0.1:{port}", "live-call", b"\x01\x00" * 960)
        self.assertEqual(received_headers, ["Bearer temporary-media-token"])
        self.assertEqual(len(frames), 51)
        self.assertEqual(frames[0][16:], b"\x01\x00" * 960)
        self.assertEqual(frames[-1][16:], bytes(1920))

    async def test_hangup_retries_same_operation(self):
        api = AsyncMock(side_effect=[RuntimeError("transient"), {}])
        with patch.object(call_audio.asyncio, "sleep", new_callable=AsyncMock):
            await call_audio.end_call(api, "call", "stable-end-id")
        self.assertEqual(api.await_args_list[0], api.await_args_list[1])


if __name__ == "__main__":
    unittest.main()