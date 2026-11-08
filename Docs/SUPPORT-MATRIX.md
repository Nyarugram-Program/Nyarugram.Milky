# 支持矩阵

对照 Milky 1.3.0 与本仓库实现。状态含义：

- **已实现**：有源码、有宿主测试证据。
- **受限**：接口可用，但字段或语义与 QQ 原生实现不同，差异在下表说明。
- **未实现**：请求返回明确错误或 HTTP 404，不会静默降级。

## 标准 API

| API | 状态 | 数据来源 | 说明 |
|---|---|---|---|
| `get_login_info` | 已实现 | `account_me`(206) | `uin` 为后端 TL 用户 ID，非 QQ 号 |
| `get_impl_info` | 已实现 | 适配服务自身 | `qq_protocol_type` 固定 `android_phone`，仅描述底层客户端平台族，不代表已连接 QQ |
| `get_user_profile` | 受限 | `get_users`(111)、`get_full_user`(117) | `age`、`sex`、`level`、`country`、`city`、`school`、`qid` 无后端来源，返回 schema 合法空值 |
| `get_friend_list` | 已实现 | `get_contacts`(112) | 无好友分组，`category_id=0` |
| `get_friend_info` | 已实现 | `get_users` / `resolve_identities` | 同上 |
| `get_group_list` | 已实现 | `get_dialogs`(102) | 仅当前账号可见的群 |
| `get_group_info` | 已实现 | `get_groups`(119) | `max_member_count` 无来源，返回 0 |
| `get_group_member_list` | 受限 | `get_full_group`(118) | `role` 恒为 `member`；后端不提供角色、禁言、群等级、专属头衔 |
| `get_group_member_info` | 受限 | 同上 | 同上 |
| `send_private_message` | 已实现 | `send_message`(107) | 支持 `text`、`mention`、`reply`；正文上限 16000 字符；支持可选幂等键 `nyarugram_idempotency_key` |
| `send_group_message` | 已实现 | 同上 | 同上 |
| `get_message` | 已实现 | `get_messages`(106) | 会校验消息确实属于请求的会话 |
| `get_history_messages` | 已实现 | `get_history`(105) | `limit` 上限 30，按 `message_seq` 升序返回 |
| `mark_message_as_read` | 已实现 | `read_history`(108) | 单调推进已读水位 |
| 其余标准 API | 未实现 | — | 返回 HTTP 404（API 未暴露） |

## Nyarugram 扩展

| API | 状态 | 说明 |
|---|---|---|
| `nyarugram_get_capabilities` | 已实现 | 公布接口、事件、消息段、呼叫与媒体能力、协议哈希 |
| `nyarugram_start_call` | 已实现 | `request_id` 幂等键；随机 ID、DH 材料与阶段先落库；`video=true` 在能力未声明前直接拒绝 |
| `nyarugram_accept_call` | 已实现 | 仅来电方向；生成并持久化 g_b |
| `nyarugram_end_call` | 已实现 | `hangup`/`missed`/`busy`/`disconnect`/`cancel`/`timeout`/`failed`，终态幂等 |
| `nyarugram_get_call` | 已实现 | 返回方向、对端、状态、时间、结束原因、媒体状态、状态版本 |
| `nyarugram_open_call_media` | 已实现 | 仅在媒体引擎可用且 `media_state=ready` 时签发短期凭据 |

## 扩展字段

| 字段 | 适用接口 | 语义 |
|---|---|---|
| `nyarugram_idempotency_key` | `send_private_message`、`send_group_message` | 可选，1..128 字符。同键同内容复用原操作并返回原结果；同键不同内容返回 `-409`；并发同键只驱动一次真实发送 |
| `nyarugram_consumer`（query/`X-Nyarugram-Consumer`） | `GET /event` | 消费者标识。提供后服务端按该消费者的持久位点重放 |
| `nyarugram_cursor`（query） | `GET /event` | 显式起始序号；SSE 也接受标准 `Last-Event-ID` |
| `nyarugram_ws_ack`（query/`X-Nyarugram-Ws-Ack`） | `GET /event`（WebSocket） | 启用版本化确认帧，事件和恢复响应提供 `epoch`，ACK/resume 必须回传匹配 epoch；溢出订阅立即取消传输并退出分发计算 |
| `nyarugram_events_gap`（事件） | `GET /event` | 位点早于保留窗口时发出，说明丢失区间与需重新同步 |

未提供 `nyarugram_consumer` 时，连接从当前末尾开始，只接收实时事件；这是明确的能力边界，不是静默重放。

## 事件

| 事件 | 状态 | 说明 |
|---|---|---|
| `message_receive` | 已实现 | 仅推送对方发来的消息；自己发送的消息不产生事件 |
| `nyarugram_call_incoming` | 已实现 | 有效来电，含 `event_id`、`call_id`、`state_version` |
| `nyarugram_call_state_changed` | 已实现 | 响铃、接听、协商、接通、结束 |
| `nyarugram_call_media_ready` | 已实现 | 信令 `established` 且收到首帧对端音频后才发送 |
| `nyarugram_call_media_closed` | 已实现 | 含明确原因，包括引擎不可用 |
| 其余 Milky 事件 | 未实现 | 撤回、置顶、入群请求等不在首期范围 |

事件投递语义：至少一次；同一逻辑事件使用稳定 `event_id`（消息事件为 `msg:{message_id}`，呼叫事件含事件类型与状态/媒体版本）。无消费者时事件保持 `pending`，超过保留窗口转 `expired` 并计入 `/health`，不会以"已交付"名义退休。

## 消息段

| 段 | 入站 | 出站 | 说明 |
|---|---|---|---|
| `text` | 是 | 是 | — |
| `mention` | 是 | 是 | 入站通过 `resolve_username`(110) 把 `@name` 还原为 `user_id` 并缓存；无法解析时保留为纯文本。出站需要目标用户有 username |
| `mention_all` | 否 | 否 | 后端无对应实体，出站返回 `-501` |
| `reply` | 是 | 是 | 入站会回查被引用消息；`sender_name` 仅在合并转发中存在，本实现留空 |
| 其余（image/record/video/file/forward/light_app/xml/markdown/market_face/face） | 否 | 否 | 超出首期范围，出站返回 `-501` |
| 加密消息（E2EE） | 否 | 否 | 不尝试解密；入站记录到 `message_skip`（原因 `unsupported:e2ee…`），不产生事件 |

## 会话场景

| 场景 | 状态 |
|---|---|
| `friend` | 已实现 |
| `group` | 已实现 |
| `temp` | 未声明。后端无临时会话概念，请求返回 `-501` |

## 身份与标识

- `user_id` = 后端 `tl_user_id`（序列从 100000 起，落在 Milky 要求的 `[10001, 4294967295]`）。
- `group_id` = 后端 `tl_chat_id`。
- `message_seq` = 后端 `message_id`，允许缺口；始终与会话作用域共同使用。
- 映射按 `server_domain + account_uuid` 隔离；映射变更即报错，不会静默重指向。
- 不使用 UUID 的截断或哈希生成数字身份。

## 明确不支持

- 端到端加密消息：识别为密文后拒绝，不伪造解密结果。
- 视频通话、录音、自动接听（配置项存在但未接线，能力声明为 false）。
- 图片、文件、语音消息、贴纸、按钮、群管理。
- WebHook 事件推送（SSE 与 WebSocket 已实现）。

## 真实互操作验证

2026-11-08 最终拓扑：隔离 Nyarugram 后端使用 `NewStoragePrimary` + `RelationalBackend`（PostgreSQL 16.15，独立集群与数据库 `botgram_milky_pg`，`/health` 返回 `storage=postgresql`，schema 1..12）；两个 Linux 适配实例的状态库使用另外的 PostgreSQL 数据库 `milky_pg_a` / `milky_pg_b`。历史 LegacyOnly 结果保留在交付报告，不再代表当前验收。

| 范围 | 结果 |
|---|---|
| 真实后端登录、会话恢复、后端令牌失效、适配器退出、重新登录 | 通过（失效会话返回 `-403`；退出后 `session none`） |
| 私聊与群聊文本、提及、回复、历史、查询、已读 | 通过（群由后端 `create_group` RPC 创建，11/11） |
| 幂等发送、同键冲突、拒接重拨、取消、忙线、未接超时、账号隔离 | 通过（14/14）；事件连接断开后的差分补偿未在本轮单独断开验证，沿用既有同步实现 |
| 呼叫 + 真实双向 PCM（440/660 Hz 不同已知输入） | 通过：A 的 660/440 能量比约 6.4e5，B 的 440/660 约 2.1e6 |
| 接通后仅强杀媒体代理 | 通过：适配器存活，`failed/closed`，收到 `media_closed`，双方重拨后音频恢复 |
| 代理退出与挂断并发 | 通过：1 个终态事件、1 个 `media_closed` 事件 |
| 出站信令瞬时 500、响应丢失、顺序、重试耗尽、队列容量/过期/取消 | 宿主回归通过；真实后端未注入信令故障，属于验证边界 |
| 后端进程重启、适配器状态 PostgreSQL 重启、后端业务 PostgreSQL 重启 | 通过：每次恢复后完成消息与双向音频通话 |
| 适配器崩溃重启对账与重拨 | 通过 |
| epoch ACK/resume 契约（旧代际不推进位点） | 通过（含数据库位点证据） |
| NoneBot 2.5.0 + nonebot-adapter-milky 1.3.0 | 消息、能力、呼叫发起/接听/挂断通过；扩展事件被框架标记 `unsupported event` 并原样透传 |
| TURN 中继 | 未验证（环境未配置 ICE 服务器） |
| Android 真机双向音频 | 未验证（无连接设备） |
