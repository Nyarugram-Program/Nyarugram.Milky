from time import time

from nonebot import on_command, on_message
from nonebot.adapters.milky import Bot, Message, MessageEvent

# Plain message send/receive: replies to every "echo:" message.
echo = on_message(priority=1, block=False)


@echo.handle()
async def handle_echo(bot: Bot, event: MessageEvent) -> None:
    text = event.get_plaintext()
    if text.startswith("echo:"):
        await bot.send(event, Message(f"echoed:{text[5:]}"))


# Call control through the nyarugram_ extension APIs. The adapter HTTP client
# returns the unwrapped "data" object of every Milky response.
call = on_command("call", priority=1, block=True)


@call.handle()
async def handle_call(bot: Bot, event: MessageEvent) -> None:
    numbers = [part for part in event.get_plaintext().replace("/", " ").split() if part.isdigit()]
    if not numbers:
        await bot.send(event, Message("usage: /call <uin>"))
        return
    result = await bot.adapter.call_http(
        bot.info, "nyarugram_start_call",
        {"user_id": int(numbers[-1]), "request_id": f"demo-call-{int(time())}"})
    info = result["call"]
    await bot.send(event, Message(f"call:{info['call_id']}:{info['state']}"))


accept = on_command("accept", priority=1, block=True)


@accept.handle()
async def handle_accept(bot: Bot, event: MessageEvent) -> None:
    tokens = [token for token in event.get_plaintext().replace("/", " ").split()
              if len(token) == 36 and token.count("-") == 4]
    if not tokens:
        await bot.send(event, Message("usage: /accept <incoming call_id>"))
        return
    result = await bot.adapter.call_http(
        bot.info, "nyarugram_accept_call",
        {"call_id": tokens[-1], "request_id": f"demo-accept-{int(time())}"})
    await bot.send(event, Message(f"accepted:{result['call']['state']}"))


end = on_command("end", priority=1, block=True)


@end.handle()
async def handle_end(bot: Bot, event: MessageEvent) -> None:
    tokens = [token for token in event.get_plaintext().replace("/", " ").split()
              if len(token) == 36 and token.count("-") == 4]
    if not tokens:
        await bot.send(event, Message("usage: /end <call_id>"))
        return
    result = await bot.adapter.call_http(
        bot.info, "nyarugram_end_call",
        {"call_id": tokens[-1], "reason": "hangup", "request_id": f"demo-end-{int(time())}"})
    await bot.send(event, Message(f"ended:{result['call']['state']}"))
