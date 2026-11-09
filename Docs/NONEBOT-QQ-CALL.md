# NapCat / OneBot V11 的 Nyarugram 呼叫插件

在现有 NoneBot 2 + NapCat 实例中使用 `/call alice` 或 `/call @alice`，通过独立运行的 Nyarugram.Milky 账号呼叫对应 Nyarugram 用户。QQ 使用者需要列入 NoneBot 的 `SUPERUSERS`，私聊和群聊均可使用。

## 前提

- Python 3.10+，NoneBot 2.5.0，OneBot V11 适配器 2.4.6，httpx 0.28.1。插件使用当前 NoneBot 的驱动，NapCat 连接方式沿用实例现有配置。
- 更新并重新编译 Milky，包含 `nyarugram_resolve_username` 扩展。该接口以 Protobuf 查询后端用户名，返回稳定数字用户 ID。
- Milky 已登录呼叫方账号，`serve` 常驻。仅需手机来电提醒时设置 `media.engine=ring_only`，直接发起呼叫邀请，使用者可在手机上挂断；误接听时适配服务会主动结束邀请。该模式的 `media_available=false` 表示音频能力不可用，无需原生媒体代理。
- 需要真实语音时配置 tgcalls 媒体引擎及原生媒体代理。`media.engine=none` 会拒绝发起呼叫，loopback 仅用于宿主测试。

## 安装

在现有 NoneBot 项目使用的 Python 环境中安装 HTTP 依赖。完整验证版本记录在 `examples/nonebot/requirements-qq-call.txt`。

```bash
python -m pip install httpx==0.28.1
```

将 `examples/nonebot/plugins/nyarugram_qq_call` 整个目录复制到 NoneBot 项目的 `plugins/` 下，并在原有 `bot.py` 的 `nonebot.run()` 前加载。

```python
nonebot.load_plugin("plugins.nyarugram_qq_call")
```

已通过 `nonebot.load_plugins("plugins")` 加载目录的实例会自动发现该插件。若项目使用其他插件目录，将加载路径调整为实际 Python 包路径。

在当前生效的 `.env` 或 `.env.prod` 中合并配置。

```dotenv
NYARUGRAM_MILKY_URL=http://127.0.0.1:8080
NYARUGRAM_MILKY_TOKEN=replace-with-milky-access-token
SUPERUSERS=["123456789"]
```

`NYARUGRAM_MILKY_URL` 填 Milky 的业务 HTTP 基础地址。令牌取自 Milky `config/local.json` 的 `milky.access_token`。QQ 号填写实际允许操作的用户，已有 `SUPERUSERS` 时把用户加入现有列表。确保 NoneBot 的 `COMMAND_START` 包含 `/`，其默认值已包含该前缀。

重启 NoneBot 后，从已授权的 QQ 私聊机器人，或在机器人所在群中发送。

```text
/call alice
```

成功时回复呼叫 ID 和当前状态。请求使用 QQ 消息身份生成稳定 `request_id`，同一事件重复投递会复用该键；网络超时后提示结果待确认，并保留服务端的幂等约束。

用户名查询与发起呼叫均使用 Milky 的 Bearer 业务令牌，证书由 httpx 正常验证。呼叫方是 Milky 当前登录的 Nyarugram 账号。音频输入输出由 Milky 媒体端点提供，QQ 指令负责呼叫控制。

## 验证

接口测试使用隔离后端与数据库，覆盖用户名解析、发起/重放/结束呼叫、未知用户名和非法输入。Python 测试使用真实 NoneBot / OneBot V11 插件加载与模拟 HTTP 响应。实际 NapCat 消息、手机响铃及双向音频需要部署后验证。