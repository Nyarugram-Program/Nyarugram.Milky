"""OneBot V11 / NapCat command bridge to a separately running Nyarugram.Milky."""

import hashlib
import re
from urllib.parse import urlsplit

import httpx
from nonebot import get_driver, get_plugin_config, logger, on_command
from nonebot.adapters.onebot.v11 import Bot, Message, MessageEvent, MessageSegment
from nonebot.params import CommandArg
from nonebot.permission import SUPERUSER
from nonebot.plugin import PluginMetadata
from pydantic import BaseModel, SecretStr


class Config(BaseModel):
    nyarugram_milky_url: str
    nyarugram_milky_token: SecretStr


__plugin_meta__ = PluginMetadata(
    name="Nyarugram QQ 呼叫",
    description="通过 NapCat 的 QQ 指令发起 Nyarugram 语音呼叫",
    usage="/call <nyarugram username>（仅 SUPERUSERS）",
    config=Config,
    supported_adapters={"~onebot.v11"},
)

config = get_plugin_config(Config)
endpoint = config.nyarugram_milky_url.rstrip("/")
parsed_endpoint = urlsplit(endpoint)
if (
    parsed_endpoint.scheme not in {"http", "https"}
    or not parsed_endpoint.hostname
    or parsed_endpoint.username is not None
    or parsed_endpoint.password is not None
    or parsed_endpoint.query
    or parsed_endpoint.fragment
):
    raise ValueError("nyarugram_milky_url must be an HTTP(S) base URL without credentials, query or fragment")
if not config.nyarugram_milky_token.get_secret_value():
    raise ValueError("nyarugram_milky_token is required")

client: httpx.AsyncClient | None = None


@get_driver().on_startup
async def startup() -> None:
    global client
    client = httpx.AsyncClient(
        base_url=endpoint + "/",
        headers={"Authorization": "Bearer " + config.nyarugram_milky_token.get_secret_value()},
        timeout=httpx.Timeout(30.0, connect=10.0),
        follow_redirects=False,
    )


@get_driver().on_shutdown
async def shutdown() -> None:
    global client
    if client is not None:
        await client.aclose()
        client = None


class MilkyError(Exception):
    pass


async def call_api(action: str, payload: dict) -> dict:
    if client is None:
        raise MilkyError("插件的 HTTP 客户端尚未启动。")
    try:
        response = await client.post("api/" + action, json=payload)
    except httpx.RequestError as error:
        # HTTP errors can include credential-bearing URLs. Log only the class.
        logger.warning("Nyarugram Milky transport failed ({})", type(error).__name__)
        raise MilkyError("无法确认请求结果，请检查 Milky 服务；请勿连续重复发送呼叫指令。") from None
    if response.status_code == 401:
        raise MilkyError("Milky 访问令牌校验失败，请检查 NYARUGRAM_MILKY_TOKEN。")
    if response.status_code == 404:
        raise MilkyError(f"Milky 未提供 {action} 接口，请更新并重启 Milky，同时核对业务 HTTP 地址。")
    try:
        result = response.json()
    except ValueError:
        raise MilkyError(f"Milky 返回了非 JSON 响应（HTTP {response.status_code}），请检查业务 HTTP 地址。") from None
    if not isinstance(result, dict):
        raise MilkyError("Milky 响应格式错误。")
    if response.is_success and result.get("status") == "ok" and result.get("retcode") == 0:
        data = result.get("data")
        if isinstance(data, dict):
            return data
        raise MilkyError("Milky 响应缺少 data 对象。")
    messages = {
        -400: "请求参数被拒绝。",
        -403: "Milky 尚未登录，或当前账号没有呼叫权限。",
        -404: "未找到该用户名或扩展接口，请确认 Milky 已更新。",
        -409: "当前账号忙线，或该指令与已有呼叫操作冲突。",
        -501: "当前功能或语音媒体引擎不可用，请检查 Milky 的 media 配置。",
        -429: "请求过于频繁，请稍后再试。",
    }
    code = result.get("retcode")
    if not isinstance(code, int) or isinstance(code, bool):
        raise MilkyError("Milky 响应缺少有效错误码。")
    raise MilkyError(messages.get(code, f"Milky 请求失败（HTTP {response.status_code}）。"))


async def start_call(username: str, request_id: str) -> dict:
    resolved = await call_api("nyarugram_resolve_username", {"username": username})
    user_id = resolved.get("user_id")
    if not isinstance(user_id, int) or isinstance(user_id, bool) or user_id <= 0:
        raise MilkyError("用户名查询没有返回有效用户 ID。")
    result = await call_api("nyarugram_start_call", {
        "user_id": user_id,
        "video": False,
        "request_id": request_id,
    })
    call = result.get("call")
    if not isinstance(call, dict) or not isinstance(call.get("call_id"), str) or not call["call_id"]:
        raise MilkyError("呼叫请求结果缺少 call_id，请在 WebUI 查询，避免重复拨号。")
    return call


call = on_command("call", permission=SUPERUSER, priority=10, block=True, force_whitespace=True)


@call.handle()
async def handle_call(bot: Bot, event: MessageEvent, args: Message = CommandArg()) -> None:
    username = args.extract_plain_text().strip().removeprefix("@")
    if re.fullmatch(r"[A-Za-z0-9_]{1,64}", username) is None:
        await call.finish(MessageSegment.text("用法：/call <nyarugram username>，例如 /call alice"))
    # Redelivered QQ events reuse their idempotency key, including across bot restarts.
    identity = f"{bot.self_id}:{event.get_session_id()}:{event.message_id}"
    request_id = "qq-call-" + hashlib.sha256(identity.encode()).hexdigest()[:48]
    try:
        info = await start_call(username, request_id)
    except MilkyError as error:
        await call.finish(MessageSegment.text(str(error)))
    await call.finish(MessageSegment.text(
        f"呼叫请求已提交给 @{username}\n呼叫 ID：{info['call_id']}\n状态：{info.get('state', 'unknown')}"
    ))