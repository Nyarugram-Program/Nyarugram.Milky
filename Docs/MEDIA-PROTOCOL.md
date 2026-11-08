# 媒体 WebSocket 契约（版本 1）

本文是 `Docs/CALLS.md` 中「应用音频接口」的落地版本化契约。实现见
`src/Nyarugram.Milky.Media/PcmAudio.cs` 与 `MilkyEndpoints.HandleMediaAsync`。

## 连接

```
GET /nyarugram/calls/{call_id}/media
Authorization: Bearer <open_call_media 返回的 token>
Upgrade: websocket
```

也接受 `?access_token=<token>`。校验项：

1. 令牌哈希存在且未过期。
2. 令牌绑定的 `call_id` 与路径一致。
3. 令牌绑定账号与当前登录账号一致。
4. 呼叫未处于终态。
5. 该呼叫有正在运行的媒体引擎；否则返回 409 与 `{"error":"media_unavailable","reason":...}`。

媒体令牌与 Milky 主 `access_token` 不同源，不能互换使用。

## 帧格式

二进制消息，单帧即一条音频帧，头部 16 字节大端：

| 偏移 | 长度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | 1 | magic | `0x4E` (`N`) |
| 1 | 1 | magic | `0x4D` (`M`) |
| 2 | 1 | version | 固定 `1` |
| 3 | 1 | flags | bit0=1 表示应用上行音频 |
| 4 | 4 | sequence | uint32，发送方递增 |
| 8 | 8 | timestamp_ms | uint64，毫秒 |
| 16 | ≤1920 | payload | PCM S16LE |

- 采样率 48000 Hz、单声道、16 bit，20 ms 一帧 = 960 采样 = 1920 字节。
- 单帧超过 1936 字节、payload 非整数采样、magic 或版本错误时，服务端以 `1007 invalid payload` 关闭连接，不做猜测性解释。
- 首帧不额外握手：格式由本文与 `nyarugram_call_media_ready` 事件中的 `media` 字段共同确定，避免两处定义漂移。

## 方向

- 应用 → 服务端：机器人播放的音频，`flags` bit0 = 1。
- 服务端 → 应用：对端音频，`flags` bit0 = 0。

## 缓冲与背压

- 输出通道容量 50 帧（约 1 秒），满时丢弃最旧帧，不无限积压。
- 单次发送超时或连接断开即释放该呼叫的媒体会话。

## 运行期关闭

- 媒体代理在就绪后退出、控制连接 EOF、读写失败或引擎报告 `failed` 时，会话以 `MediaFault` 结束；通话被置为 `failed`，并在同一事务写入 `media_state=closed` 与 `nyarugram_call_media_closed`。
- 媒体 WebSocket 会随会话关闭而结束。之后 `nyarugram_open_call_media` 返回 409 `media_unavailable`（通话已终态时返回冲突/未找到），不会为旧会话重新签发凭据。
- 主动挂断与故障并发时只产生一个终态事务；旧媒体实例的迟到故障不会影响替换后的实例。

## 引擎接入与就绪

`media` 配置 `engine=tgcalls` 时，适配服务为每个通话启动一个 `milky_media_agent` 子进程并绑定：引擎版本、DH 共享密钥（256 字节 authKey）、后端下发的 RTC 端点、P2P 开关和自定义参数。代理通过本机回环控制连接返回引擎状态和双向 PCM；控制连接断开时代理自行退出。

`nyarugram_call_media_ready` 只在以下条件同时满足后发出：

1. tgcalls 信令状态为 `established`；
2. 已收到至少一帧对端 PCM（证明媒体路径已通，而不是仅信令完成）；
3. 应用媒体令牌此时可换取。

P2P 与 TURN 由后端 `get_call_config` 的端点列表决定。当前隔离联调环境未配置 `Rtc:IceServers`，因此只验证了 P2P；TURN 路径需要在部署环境配置后单独验收。

## 当前状态

- `loopback` 引擎只用于回归测试：它把上行帧回送，没有远端对端，`Capability.Audio=false`，**不能作为语音可用证据**。
- `tgcalls` 引擎为当前语音实现，注册版本 `13.0.0`/`12.0.0`。应用侧契约保持本文不变，引擎替换不改变帧格式。
- 已验证两个独立适配实例之间的双向 PCM、进程重启后的媒体释放与重拨、接通后单独强杀媒体代理后的 `media_closed`/失败状态与双方重拨；Android 真机与 TURN 未验证。
- 2026-11-08 的 PostgreSQL 权威后端拓扑下，双向音频使用 440/660 Hz 不同输入，A 只收到 B 的 660 Hz、B 只收到 A 的 440 Hz，能量比 > 4 个数量级，排除静音、串音和本地回环。
