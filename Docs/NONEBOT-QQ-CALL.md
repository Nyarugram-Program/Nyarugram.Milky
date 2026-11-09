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

## 接听后播报 TTS 并自动挂断

```text
/call bot -tts 今天天气真好
```

插件使用 `zh-CN-XiaoxiaoNeural`（晓晓）合成“今天天气真好，重复，今天天气真好”，等待对方接听且音频媒体就绪后播放，结束后挂断。普通 `/call bot` 继续作为原有呼叫指令。

### 在 RHEL 上的 NoneBot 环境安装

在运行 NoneBot 的同一个 Python 环境执行。

```bash
python -m pip install edge-tts==7.3.1 miniaudio==1.61 websockets==15.0.1
```

可选音色配置如下。

```dotenv
NYARUGRAM_TTS_VOICE=zh-CN-XiaoxiaoNeural
```

更新时复制整个 `nyarugram_qq_call` 插件目录，包括新增的 `call_audio.py`，然后重启 NoneBot。Python 音频库负责将合成的 MP3 解码为 48 kHz、单声道、PCM S16LE，按 20 ms 帧发送给 Milky。

TTS 模式需要 Milky 使用 `media.engine=tgcalls`，并已配置 RHEL 主机上可运行的原生媒体代理。`ring_only` 模式适用于来电提醒，插件在该模式下会拒绝 TTS 请求。媒体代理的二进制需与 RHEL 的架构、glibc 和动态库兼容，应在对应发行版中构建。NoneBot 与 Milky 可部署在同一台 RHEL 主机，也可以通过已配置的 HTTP/WSS 地址连接。

Edge TTS 是微软在线服务，播报文本会发送给该服务。插件在拨号前完成合成，因此合成失败或服务限流时会直接提示。文本限制 300 字符，合成音频最长三分钟。播报文本和媒体令牌不写入插件日志。

呼叫拒接、超时、媒体断开或播放异常时执行挂断；挂断暂时失败会以相同操作标识重试。插件正常关闭时取消并清理正在处理的 TTS 呼叫。重复投递的同一 QQ 指令在当前进程中复用正在处理的任务。播报末尾发送一秒静音，为已发送音频留出排空时间。

## 验证

接口测试使用隔离后端与数据库，覆盖用户名解析、发起/重放/结束呼叫、未知用户名和非法输入。Python 测试使用真实 NoneBot / OneBot V11 插件加载与模拟 HTTP 响应。实际 NapCat 消息、手机响铃及双向音频需要部署后验证。
2026-11-09 开发环境验证：22 项 Python 回归通过，覆盖真实 NoneBot/OneBot V11 指令分发、TTS 文本组合、实际本机 WebSocket PCM 传输、重复指令和关闭清理。Edge TTS 7.3.1 晓晓在线合成及 miniaudio 解码通过。实际 RHEL 部署、NapCat 到手机通话、对端听感和自动挂断尚未端到端验证。
