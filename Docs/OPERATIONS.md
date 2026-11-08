# 运行与配置

## 前置条件

- .NET 8 运行时（构建需要 SDK 8.0.204 或更高 feature band）。
- PostgreSQL 16（14+ 应可用，未验证）。适配服务只使用一个独立 schema：`nyarugram_milky`。
- 可访问的 Nyarugram 后端，`/api/v1/auth`、`/api/v1/account`、`/api/v1/rpc`、`/api/ws` 可用。

### 与 PostgreSQL 权威后端联调

验证呼叫和存储时，不能让后端停在 `LegacyOnly`。隔离后端必须显式使用 PostgreSQL 权威存储：

```json
{
  "Storage": {
    "Mode": "NewStoragePrimary",
    "ValidationBackend": "RelationalBackend",
    "PrimaryAcknowledged": true,
    "PrimaryConnectionString": "Host=<host>;Port=<port>;Database=<dedicated-db>;Username=<user>"
  },
  "Privacy": { "KeyDirectory": "<key-dir>" },
  "Protocol": {
    "LegacyJsonRpcEnabled": false,
    "LegacyJsonWssEnabled": false,
    "ProtobufRolloutPercent": 100,
    "ServerDomain": "<lowercase-dns-domain>"
  }
}
```

- 新库用 `Storage.Admin init <private-config.json>` 建立后端 schema；`inspect` 应显示 schema 版本 1..12。
- 后端启动后 `/health` 必须包含 `storage=postgresql`；返回 `storage=legacy-file` 表示仍在 LegacyOnly，应停止验证并修正配置，不能把该结果记作数据库验收。
- 适配器状态库与后端业务库使用不同数据库和连接串，分别记录数据库名、健康状态和重启证据。适配器的 `/health` 不检查后端存储模式，需单独检查后端 `/health`。

## 命令

```bash
# 生成 config/local.json（含随机 access_token 与凭据保护密钥）
nyarugram-milky init

# 交互登录：密码与验证码通过隐藏输入读取
nyarugram-milky login

# 前台运行服务
nyarugram-milky serve

# 查看账号、会话代际、同步游标与待投递事件
nyarugram-milky status

# 退出并撤销本地会话
nyarugram-milky logout

# 打印版本与协议哈希
nyarugram-milky version
```

配置从 `config/local.json` 读取，可用环境变量 `NYARUGRAM_*` 覆盖（如 `NYARUGRAM_milky__access_token`）。内容根目录默认取当前工作目录，可用 `NYARUGRAM_CONTENT_ROOT` 指定。

## 配置项

| 键 | 必需 | 说明 |
|---|---|---|
| `backend.base_address` | 是 | 后端地址，必须 `https://`；仅 `allow_plaintext=true` 时允许明文（回环测试用） |
| `backend.server_domain` | 是 | 后端协议身份域名，需与后端 `Protocol:ServerDomain` 一致 |
| `backend.certificate_sha256_pin` | 否 | 叶证书 SHA-256 指纹。设置后仍走证书链校验，指纹用于替代 IP 直连的主机名匹配 |
| `backend.request_timeout_seconds` | 否 | 默认 30 |
| `storage.connection_string` | 是 | PostgreSQL 连接串，建议独立数据库 |
| `storage.secret_key` | 是 | base64 的 32 字节密钥，用于 AES-256-GCM 加密会话令牌；必须存放在数据库之外 |
| `milky.listen` | 否 | 默认 `http://127.0.0.1:8080` |
| `milky.access_token` | 是 | 应用访问凭据，至少 16 字符，恒定时间比较 |
| `media.engine` | 否 | `none`（默认）、`loopback`（仅测试）或 `tgcalls`（真实语音） |
| `media.agent_command` | tgcalls 必填 | 媒体代理命令行，例如 `<包目录>/media/milky_media_agent --agent` |
| `media.agent_startup_seconds` | 否 | 代理启动就绪等待，默认 20 |
| `media.agent_log` | 否 | 是否保留代理自身日志，默认 false |
| `runtime.sync_interval_seconds` | 否 | 轮询补偿间隔，默认 5 |
| `runtime.outbox_retention_hours` | 否 | 未投递事件的保留窗口，默认 6；超期转 `expired` 并计入 `/health` |
| `runtime.media_token_lifetime_minutes` | 否 | 媒体凭据有效期，默认 10 |
| `runtime.profile_cache_ttl_minutes` | 否 | 资料缓存 TTL，默认 10；缓存按服务器+账号+代际隔离 |
| `runtime.operation_lease_seconds` | 否 | 幂等操作租约，默认 30，用于串行化并发同键请求 |
| `runtime.operation_wait_seconds` | 否 | 并发同键请求等待既有操作完成的上限，默认 10 |
| `runtime.reconcile_interval_seconds` | 否 | 发送/呼叫对账间隔，默认 10 |
| `runtime.replay_batch_size` | 否 | 单次投递给消费者的最大事件数，默认 200 |
| `runtime.background_sync` | 否 | 后台差分轮询，默认 true；仅自动化测试会关闭 |
| `runtime.background_send_reconcile` | 否 | 后台发送对账，默认 true；仅自动化测试会关闭 |
| `runtime.background_call_maintenance` | 否 | 后台呼叫对账与媒体重试，默认 true |
| `runtime.signaling_queue_capacity` | 否 | 每个媒体实例的出站信令队列容量，默认 64，范围 1..1024；溢出会结束该通话 |
| `runtime.signaling_max_attempts` | 否 | 单条信令的最大投递次数，默认 5，范围 1..20；耗尽会结束该通话 |
| `runtime.signaling_retry_base_seconds` | 否 | 重试退避基数，默认 1，上限 8 秒 |
| `runtime.signaling_max_age_seconds` | 否 | 信令在队列中的最长有效期，默认 60；超期按失败处理 |

数据库不存在时，`serve`/`login`/`status` 会从维护库自动创建它，然后执行迁移；不需要外部 `psql`。

## 事件重放

- 应用可命名消费者：`GET /event?nyarugram_consumer=<id>`（或 `X-Nyarugram-Consumer` 头）。
- 服务端按该消费者的持久位点重放；SSE 同时支持标准 `Last-Event-ID`，也接受 `nyarugram_cursor=<seq>`。
- 未命名消费者从当前末尾开始，只收实时事件。
- 命名消费者首次连接即持久化其起始位点；即使一条都未确认就断线，重连也会从同一位置恢复，而不是被当作全新消费者跳到末尾。
- 投递使用最低**已发送**位点，确认位点用于投递计量。一个迟迟不确认的消费者不会卡住其他消费者接收新事件。发送真正阻塞并导致队列溢出时，订阅立即退出分发计算，并取消或中止传输。重连从已确认位点恢复。
- 位点早于保留窗口时，服务端先发 `nyarugram_events_gap`，给出 `requested_cursor` 与 `oldest_available`，应用需重新同步状态。
- 无消费者时事件保持 `pending`；超过保留窗口转 `expired`。`/health` 暴露 `outbox_pending`、`outbox_expired`、`outbox_oldest_pending` 与 `event_consumers`。

## WebSocket 确认与恢复（`nyarugram_ws_ack=1`）

当 URL 带 `nyarugram_ws_ack=1`（或 `X-Nyarugram-Ws-Ack: 1`）时，事件改走版本化 Nyarugram 扩展帧，区分"已发送"与"已确认"两个位点：

- 服务端发送 `nyarugram_event` 帧，携带 `extension_version=1`、`epoch`、`seq`、`event_id`、`event_type` 与标准 Milky `event` 载荷。
- 客户端发送 `{"type":"nyarugram_ack","extension_version":1,"epoch":E,"seq":N}`，其中 E 必须来自当前事件帧。有效的显式确认推进持久位点，断线后按该位置重放，确认丢失时允许重复投递。
- 非法超前确认（`seq` 大于已发送位点）返回 `nyarugram_ack_rejected`，不会被应用；重复确认幂等。
- 显式恢复使用 `{"type":"nyarugram_resume","extension_version":1,"epoch":E,"cursor":N}`，把持久位点**降低**到已发送范围内的 `N` 并**递增该消费者的游标代际**（`epoch`），同时分页重放 `(N, sent]` 的全部事件。恢复成功时返回携带新 `epoch` 的 `nyarugram_resumed`，后续 ACK 和 resume 使用新值。超出已发送范围的回退返回 `nyarugram_resume_rejected`。恢复区间超过一批时逐页补发，不会截断。
- 游标代际同时约束扩展帧和数据库写入。缺少 epoch、旧 epoch 或超前 epoch 的 ACK/resume 会被拒绝，持久位点保持原值。扩展客户端需要回传 epoch。普通 Milky SSE 和未启用确认扩展的 WebSocket 继续使用标准事件载荷。
- 不带 `nyarugram_ws_ack=1` 时保持标准 Milky 事件 JSON，发送即确认（至少一次、可重放）。

标准 Milky 事件 JSON 与扩展帧互不污染：普通应用端只看到标准载荷，扩展应用端拿到带 `seq` 的帧。

## 发送幂等

发送接口接受可选 `nyarugram_idempotency_key`：

- 同键同内容：返回原结果，不产生第二条消息。
- 同键不同内容：`-409`。
- 响应丢失：返回 `-500` 且消息包含该键；用同键重试或等待后台对账即可恢复。
- 不提供键时，每次 HTTP 请求都是新的逻辑发送；服务端不会用文本哈希合并两次有意重复的消息。

## 排障查询

```sql
-- 被永久跳过、未投射为事件的消息
SELECT message_id, reason, created_at FROM nyarugram_milky.message_skip ORDER BY created_at DESC LIMIT 50;
-- 未完成的发送/呼叫操作
SELECT scope, idempotency_key, state, attempts, last_error FROM nyarugram_milky.idempotency
WHERE state IN ('pending','submitted','unknown');
SELECT idempotency_key, stage, last_error FROM nyarugram_milky.call_operation WHERE stage <> 'linked';
```

## 端点

| 方法 | 路径 | 鉴权 |
|---|---|---|
| POST | `/api/{action}` | `Authorization: Bearer <access_token>` |
| GET | `/event` | 同上，或 `?access_token=`；按 `Upgrade` 头区分 SSE/WebSocket |
| GET | `/nyarugram/calls/{call_id}/media` | 该呼叫的短期媒体令牌（`Authorization` 或 `access_token`），并校验与主凭据不同源 |
| GET | `/health` | 无 |

`/health` 返回 `status`（`ok` / `degraded` / `unhealthy`）、存储类型、登录状态、uin、会话代际、实例标识、媒体引擎、事件消费者，以及 outbox 的 pending/expired 计数与最老待投递时间。未登录时为 `degraded`，存储不可达时为 503。

## 安全边界

- `access_token`、后端 Bearer 令牌、媒体令牌三者独立校验，互不复用。
- 日志与错误响应只输出符号化错误码，不包含令牌、密码、DH 私钥或音频内容。
- 媒体令牌只存 SHA-256 哈希；呼叫结束即删除。
- `config/local.json` 含凭据保护密钥，应限制文件权限，并与 PostgreSQL 备份分开保管。
- 默认只监听回环地址。对外暴露时必须自行配置 TLS 终止与访问控制。

## 独立发布包

发布包目录（`linux-x64`，目标平台 Ubuntu 24.04 / glibc 2.39）：

```
nyarugram-milky-<version>-linux-x64/
  nyarugram-milky              # apphost（也可 dotnet nyarugram-milky.dll）
  *.dll, *.json                # 固定依赖，无同级源码依赖
  media/milky_media_agent      # tgcalls 媒体代理
  media/SOURCES.txt            # 源码提交、构建方式与产物哈希
  media/BUILD.md               # Ubuntu 24.04 准备/构建/运行/自测说明
  media/upstream.json          # 固定提交、组件映射与树哈希
  protocol/botgram_rpc.proto   # 后端协议固定副本
  config/locks.json            # Milky / 后端协议版本与校验值
  config/milky-1.3.0.openapi.json
  config/local.json.template   # 不含任何真实凭据
  LICENSES/THIRD-PARTY-NOTICES.md
  LICENSES/*-LICENSE / *-COPYING
  DEPENDENCIES.txt
  VERSION
  SHA256SUMS                   # 除自身外所有文件的 SHA-256
```

媒体代理动态依赖：`libz.so.1`、`libevent-2.1.so.7`、`libstdc++.so.6`、`libgcc_s.so.1`、`libm.so.6`、glibc（Ubuntu 24.04 包名 `libevent-2.1-7t64`、`zlib1g`）。不在该发行版上部署时必须在目标发行版重新构建媒体代理。

部署步骤：

```bash
# 1. 解压到独立目录并校验
sha256sum -c SHA256SUMS
chmod +x nyarugram-milky media/milky_media_agent
# 2. 生成带随机密钥与令牌的配置，然后按 config/local.json.template 补齐
./nyarugram-milky init
#    编辑 config/local.json：backend.base_address / server_domain /
#    storage.connection_string / milky.access_token / media.*
./nyarugram-milky login
./nyarugram-milky serve
curl -fsS http://127.0.0.1:8080/health
```

`init` 生成的 `config/local.json` 含凭据保护密钥，权限应为 `0600`，并与数据库备份分开存放。PostgreSQL 数据库不存在时首次 `serve`/`login` 会自动创建并迁移。

systemd 单元示例（`/etc/systemd/system/nyarugram-milky.service`）：

```ini
[Unit]
Description=Nyarugram Milky adapter
After=network-online.target postgresql.service

[Service]
Type=simple
WorkingDirectory=/opt/nyarugram-milky
ExecStart=/opt/nyarugram-milky/nyarugram-milky serve
Restart=on-failure
RestartSec=5
Environment=NYARUGRAM_CONTENT_ROOT=/opt/nyarugram-milky

[Install]
WantedBy=multi-user.target
```

升级：停止服务、备份 `config/local.json` 与数据库、解压新版本覆盖程序文件（保留 `config/`）、启动。迁移在启动时按版本顺序执行且幂等；旧版本程序集不再使用。回滚时恢复旧版本程序目录并保留同一数据库（迁移只向前，回滚前先在隔离库验证）。

在 WSL 的 `/mnt/*` 挂载盘上运行时，升级后需把程序重新复制到 Linux 文件系统（如 `~/milky-app`）再启动，避免 9p 缓存把旧程序集提供给新进程。

## 应用示例

仓库 `examples/` 提供最小可运行组合：

```bash
# NoneBot 2.5.0 + nonebot-adapter-milky 1.3.0
cd examples/nonebot
cp .env.example .env      # 填入适配服务地址与 access_token
python bot.py             # 私聊 echo:、/call、/accept、/end

# 框架外的音频组件：为某个已接通的呼叫输入/录制 PCM
MILKY_BASE=http://127.0.0.1:8080 MILKY_TOKEN=... CALL_ID=... \
  node examples/media/pcm-bridge.mjs
```

NoneBot 插件只能承载消息与控制面；双向音频需要 `pcm-bridge.mjs` 这类辅助进程。两者使用同一适配服务与同一呼叫，组合即为完整的消息、呼叫与音频示例。

## 故障处理

| 现象 | 处理 |
|---|---|
| 启动即报 schema 哈希不匹配 | `config/milky-1.3.0.openapi.json` 被改动。恢复锁定副本或同步更新 `config/locks.json` 后重新验证 |
| `-403 未登录` | 执行 `login`；`status` 可查看会话是否已过期 |
| 事件长期收不到 | 确认 `/event` 已连接；`/health` 的 `event_subscribers` 为 0 表示没有消费者，事件会在宽限期后被回收 |
| 进程重启后无法拨号 | 启动时会把上一代会话未结束的呼叫标记为 `failed`（原因 `restart`），随后可重拨 |
| PostgreSQL 不可达 | 服务返回 503；恢复后无需重启，连接池自动重建 |
| `nyarugram_open_call_media` 返回 409 `media_unavailable` | 检查 `/health` 的 `media_engine`、`media.agent_command` 路径与 `media.agent_startup_seconds`；媒体代理未就绪时呼叫不会进入 ready |
| 两个客户端只响铃不接通 | 查看呼叫状态是否停在 `negotiating`：通常是 45 秒内未通过后端中继完成信令，检查双方版本是否在 `13.0.0`/`12.0.0` 且 layer 65..92 |
| 呼叫只有单向音频 | 确认双方 `nyarugram_open_call_media` 都声明 `input=true`；只开输入不读取时对端仍会收到静音。P2P 不通且后端未配置 TURN 时 ICE 只能依赖直连 |
| 接通后媒体代理进程退出 | 适配服务不退出；若数据库可用，该呼叫在数秒内变为 `failed`、`media_state=closed`，并发出 `nyarugram_call_media_closed`。`reason=media_agent_failed`；若数据库暂时不可用，收尾任务自动重试，后端暂时不可达则 `pending_end_reason=failed` 由维护任务继续释放，之后可重拨 |
| 呼叫因 `signaling_queue_overflow` / `signaling_retry_exhausted` 失败 | 说明出站信令积压或后端持续失败。检查后端可达性、`runtime.signaling_*` 限制和双方网络；失败路径已释放媒体与占线，可直接重拨 |
| `pending_end_reason` 长时间不清除 | 查看后端 `/health` 与适配日志中的 `discard_call` 错误；维护任务按退避重试，后端恢复后自动清除 |
| 日志出现 `fault finalization ... deferred` | 故障媒体/信令已停止，但数据库暂时不可用；收尾任务在 1 秒起指数退避重试。数据库恢复后无需重启适配器，检查通话是否变为 `failed/closed` 且 `pending_end_reason` 被清除 |
| 数据库中断期间仍有通话显示 `active/ready` | 这是数据库中的旧快照，不是仍在运行的媒体；媒体资源已停止。数据库恢复后 `CallFaultFinalizer` 会自动完成终态。若进程在此期间重启，启动对账会结束上一实例的通话 |
| 信令日志出现 `signaling_expired` | 信号在有效期内未能完成投递（含在途超时）。该通话会被失败并释放；已发出的请求可能已在后端生效，重试语义至少一次 |

数据库故障留下的内存收尾任务在重新登录后继续保留。同一服务器、同一账号重新登录时，任务使用当前会话继续完成终态、媒体令牌撤销与后端线路释放。退出或切换到其他账号/服务器时暂停，原账号回来后重试。进程重启后由启动对账接管。
