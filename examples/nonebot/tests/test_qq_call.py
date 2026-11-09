import json
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

import httpx
import nonebot
from nonebot.adapters.onebot.v11 import Adapter, Bot, Message, PrivateMessageEvent

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "plugins"))
nonebot.init(
    driver="~fastapi",
    superusers={"123"},
    command_start={"/"},
    log_level="WARNING",
    nyarugram_milky_url="http://127.0.0.1:58080",
    nyarugram_milky_token="isolated-test-token",
)
nonebot.get_driver().register_adapter(Adapter)
loaded = nonebot.load_plugin("nyarugram_qq_call")
assert loaded is not None
plugin = loaded.module


class QqCallTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.requests = []

        def respond(request):
            payload = json.loads(request.content)
            self.requests.append((request.url.path, payload))
            self.assertEqual(request.headers["Authorization"], "Bearer isolated-test-token")
            data = (
                {"user_id": 100002, "username": "alice"}
                if request.url.path.endswith("resolve_username")
                else {"call": {"call_id": "isolated-call-id", "state": "outgoing"}}
            )
            return httpx.Response(200, json={"status": "ok", "retcode": 0, "data": data})

        plugin.client = httpx.AsyncClient(
            base_url="http://127.0.0.1:58080/",
            headers={"Authorization": "Bearer isolated-test-token"},
            transport=httpx.MockTransport(respond),
        )
        self.bot = Bot(nonebot.get_adapter(Adapter), "999")
        self.send_patch = patch.object(Bot, "send", new_callable=AsyncMock)
        self.send = self.send_patch.start()

    async def asyncTearDown(self):
        self.send_patch.stop()
        await plugin.shutdown()

    async def dispatch(self, text, user_id=123, message_id=42):
        event = PrivateMessageEvent(
            time=1, self_id=999, post_type="message", message_type="private",
            sub_type="friend", user_id=user_id, message_id=message_id,
            message=Message(text), raw_message=text, font=0,
            sender={"user_id": user_id, "nickname": "Isolated tester"},
        )
        await self.bot.handle_event(event)

    async def test_real_onebot_command_dispatch_and_stable_request_id(self):
        await self.dispatch("/call @alice")
        await self.dispatch("/call @alice")
        self.assertEqual(len(self.requests), 4)
        self.assertEqual(self.requests[0], ("/api/nyarugram_resolve_username", {"username": "alice"}))
        self.assertEqual(self.requests[1][0], "/api/nyarugram_start_call")
        payload = self.requests[1][1]
        self.assertEqual(payload["user_id"], 100002)
        self.assertFalse(payload["video"])
        self.assertEqual(payload["request_id"], self.requests[3][1]["request_id"])
        self.assertLessEqual(len(payload["request_id"]), 64)
        self.assertIn("isolated-call-id", str(self.send.call_args.kwargs["message"]))

    async def test_non_superuser_cannot_call(self):
        await self.dispatch("/call alice", user_id=456)
        self.assertEqual(self.requests, [])
        self.send.assert_not_called()

    async def test_invalid_command_replies_with_usage(self):
        await self.dispatch("/call alice bob")
        self.assertEqual(self.requests, [])
        self.assertIn("用法", str(self.send.call_args.kwargs["message"]))

    async def test_missing_username_replies_with_usage(self):
        await self.dispatch("/call")
        self.assertEqual(self.requests, [])
        self.assertIn("用法", str(self.send.call_args.kwargs["message"]))

    async def test_media_unavailable_is_reported(self):
        await plugin.client.aclose()
        plugin.client = httpx.AsyncClient(base_url="http://127.0.0.1/", transport=httpx.MockTransport(
            lambda request: httpx.Response(200, json={"status": "failed", "retcode": -501})
        ))
        with self.assertRaisesRegex(plugin.MilkyError, "媒体引擎"):
            await plugin.call_api("nyarugram_start_call", {})

    async def test_old_milky_missing_api_is_reported(self):
        await plugin.client.aclose()
        plugin.client = httpx.AsyncClient(base_url="http://127.0.0.1/", transport=httpx.MockTransport(
            lambda request: httpx.Response(404)
        ))
        with self.assertRaisesRegex(plugin.MilkyError, "nyarugram_resolve_username"):
            await plugin.call_api("nyarugram_resolve_username", {})

    async def test_invalid_access_token_is_reported(self):
        await plugin.client.aclose()
        plugin.client = httpx.AsyncClient(base_url="http://127.0.0.1/", transport=httpx.MockTransport(
            lambda request: httpx.Response(401)
        ))
        with self.assertRaisesRegex(plugin.MilkyError, "访问令牌校验失败"):
            await plugin.call_api("nyarugram_start_call", {})

    async def test_transport_failure_is_not_retried(self):
        await plugin.client.aclose()
        attempts = []

        def timeout(request):
            attempts.append(request)
            raise httpx.ReadTimeout("isolated timeout", request=request)

        plugin.client = httpx.AsyncClient(base_url="http://127.0.0.1/", transport=httpx.MockTransport(timeout))
        with self.assertRaisesRegex(plugin.MilkyError, "无法确认请求结果"):
            await plugin.call_api("nyarugram_start_call", {})
        self.assertEqual(len(attempts), 1)

    async def test_malformed_retcode_is_handled(self):
        await plugin.client.aclose()
        plugin.client = httpx.AsyncClient(base_url="http://127.0.0.1/", transport=httpx.MockTransport(
            lambda request: httpx.Response(200, json={"status": "failed", "retcode": {}})
        ))
        with self.assertRaisesRegex(plugin.MilkyError, "有效错误码"):
            await plugin.call_api("nyarugram_start_call", {})


if __name__ == "__main__":
    unittest.main()