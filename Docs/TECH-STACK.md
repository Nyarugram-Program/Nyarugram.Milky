# 技术选型与实现结构

本文记录实际落地的技术决定、理由和被否决的替代方案。版本以 `config/locks.json` 与各 `csproj` 为准。

## 决定

| 项目 | 选择 | 理由 |
|---|---|---|
| 运行时 | .NET 8（`net8.0`，SDK 8.0.204，`rollForward=latestFeature`） | 与 Nyarugram 后端同为 .NET，可直接复用 Google.Protobuf 与 Npgsql 生态，避免跨语言序列化开销 |
| Web | ASP.NET Core 最小 API + Kestrel | 单一进程同时提供 Milky HTTP、SSE/WebSocket 与媒体 WebSocket |
| 序列化（后端） | Google.Protobuf 3.28.3 + Grpc.Tools 2.68.1 | 后端强制 Protobuf-only，构建期从固定 proto 生成代码 |
| 序列化（对外） | System.Text.Json，显式 snake_case 特性 | 零运行时反射配置，字段名与 Milky 1.3 一致；未知消息段以 `JsonElement` 原样保留 |
| 存储 | PostgreSQL + Npgsql 8.0.5，手写 SQL 与版本化迁移 | 迁移内容可审计，schema 与查询同源；避免 ORM 隐式行为 |
| 加密 | AES-256-GCM（会话令牌）+ OS CSPRNG（随机数） | 会话令牌落库前加密，密钥在数据库之外 |
| 呼叫密钥协商 | `System.Numerics.BigInteger` 实现 DH | 无额外原生依赖，可用已知向量在宿主测试中验证 |
| 音频编解码 | Concentus 2.2.2（纯 C# Opus） | 无原生库；仅用于媒体管线内部的 PCM↔Opus，不构成电话互通证据 |
| 测试 | xUnit 2.9.2 + Xunit.SkippableFact + 进程内 Kestrel 假后端 | 无 PostgreSQL 时跳过而非假通过 |

## 项目结构

```
src/
  Nyarugram.Milky.Protocol/   固定 proto 生成的 C# 类型（构建期代码生成）
  Nyarugram.Milky.Backend/    /api/v1/auth、/api/v1/account、/api/v1/rpc 与事件提示流客户端
  Nyarugram.Milky.Contracts/  Milky 1.3 对外 JSON 契约（实体、消息段、扩展、事件）
  Nyarugram.Milky.Storage/    PostgreSQL schema、迁移、加密凭据、幂等与投递队列
  Nyarugram.Milky.Core/       会话、身份映射、消息映射、差分同步、呼叫控制
  Nyarugram.Milky.Media/      DH、PCM 帧、Opus、loopback 引擎、tgcalls 引擎与媒体代理控制协议
  Nyarugram.Milky.Host/       CLI、HTTP/事件/媒体端点、组合根
tests/
  Nyarugram.Milky.Tests/      单元测试 + 进程内假后端 + 真实 PostgreSQL 集成测试
```

依赖方向单向：`Host → Core → Storage/Backend/Contracts → Protocol`，`Host → Media → Core`。Core 不引用 Media，通过 `IMediaEngine`、`IDiffieHellman` 反转依赖。

## 协议固定

- `protocol/botgram_rpc.proto`：来自 Botgram.Backend 提交 `4597614`，SHA-256 `0d2d4a4c…c1be6`。构建时只读本仓库副本，不读取同级目录。
- `config/milky-1.3.0.openapi.json`：Milky 官方 OpenAPI，版本 1.3.0，来源提交 `151dd90`，SHA-256 `8ccb6118…27614`。启动时校验该文件哈希，不匹配即拒绝启动。
- 两者都记录在 `config/locks.json`，`nyarugram_get_capabilities` 原样对外公布。

## 被否决的方案

| 方案 | 否决原因 |
|---|---|
| SQLite / JSON 文件库 | 违反「服务端业务持久化强制 PostgreSQL-only」 |
| EF Core | 迁移与 SQL 不可直接审计，且本项目 schema 很简单，收益低于成本 |
| JSON-TL / 已弃用 REST 回退 | 违反 Protobuf-only 约束；后端对这些入口返回 410 |
| 在 C# 中重写 tgcalls 媒体协议 | 工作量与验证成本不可控：加密 SCTP/mtproto 信令与 WebRTC 栈无法自行重写。改为在 Linux 上复用 Android 子树的 tgcalls/WebRTC 源码构建独立媒体代理 |
| 用 loopback 引擎宣称语音可用 | loopback 无远端、无信令传输，回送的帧不构成真实通话证据 |

## 媒体现状

`IMediaEngine` 是唯一接入点。当前构建注册三种实现：

- `UnavailableMediaEngine`（`media.engine=none`，默认）：任何呼叫在发起前返回 `media_unavailable`，并附明确原因。
- `LoopbackMediaEngine`（`media.engine=loopback`）：接受应用 PCM 并原样回送，用于验证媒体 WebSocket 契约与音频帧管线。它不连接任何对端，`Capability.Audio=false`。
- `TgcallsMediaEngine`（`media.engine=tgcalls`）：与 Android `VoIPService` 相同的 `tgcalls::InstanceV2Impl`，注册版本 `13.0.0`/`12.0.0`，`Capability.Audio=true`。

`MediaSessionRequest` 携带共享密钥、`get_call_config` 返回的引擎配置 JSON、`phoneConnection` 端点列表与 P2P 策略；`IMediaSession.SendSignalingAsync` 与 `StartAsync(onSignaling)` 构成双向信令；`IApplicationAudioChannel` 是应用音频的唯一入口。

### tgcalls 引擎的构建与运行

- 构建输入已在仓库固定：`media/upstream.json` 记录 Botgram `2fab5dcf…`、tgcalls `1398c164…` 及 BoringSSL/abseil/opus/libyuv/libvpx/OpenH264 的完整提交；`media/sources.txt` 是 1,266 个编译源文件清单；`media/agent/` 是代理适配源码；`media/patches/0001-linux-build.patch` 是 Linux 适配补丁。
- `media/prepare-source.sh` 使用 `git archive` 从指定 Botgram 仓库提取固定提交的精确 blob，并从 `--native` 固定子模块目录复制依赖，应用补丁前后分别校验 `upstream.json` 中的树哈希；失败即中止。构建因此不依赖同级 Android 工作区、个人路径或历史构建缓存。
- `media/build-agent.sh` 先按固定参数构建静态 libvpx（v1.16.0，VP8+VP9），再用仓库内 CMake/Ninja 构建 Release 代理并运行 `--selftest-p2p`。
- 干净构建环境：Ubuntu 24.04.3 LTS x86-64，GCC/G++ 13.3.0，ld 2.42，CMake 3.28.3，Ninja 1.11.1。补丁前树哈希 `4214a447…b481`，补丁后 `d5608efe…88e8`；产物 `milky_media_agent` SHA-256 `dc0cf5d6…72fa`（35,414,216 字节）。自测 `caller_frames=504 callee_frames=502 RMS=726/635`。跨机器二进制哈希不保证相同，需同时记录工具链、输入哈希和产物哈希。
- 运行方式：每个通话一个 `milky_media_agent` 子进程，控制协议为 4 字节大端长度 + 1 字节类型（JSON 控制、上行 PCM、下行 PCM），引擎就绪后输出 `READY <port>`；适配服务通过回环 TCP 连接。代理在控制连接断开后自行退出。
- 应用音频：48 kHz 单声道 S16LE，采集 10 ms、收发 20 ms；引擎内部编解码由 tgcalls 自身协商。
- 发布：`media/milky_media_agent` 与适配服务同包分发；`media/BUILD.md`、`media/upstream.json` 与 `LICENSES/` 随包提供许可证和源码获取/重链接说明。

### 运行期故障与出站信令

- `IMediaSession.Completion` 提供一次性的终止通知：主动关闭为 `null`，代理退出、控制连接 EOF、读写失败或引擎 `failed` 为 `MediaFault`。`CallService` 在媒体就绪后启动 watcher，故障时结束通话并以同一事务写入 `media_closed`。已就绪会话不原地重启，因为子进程已不可恢复。
- watcher 绑定具体媒体实例：`engine.Find(call_id) == faulted session` 时才处理，旧实例的迟到退出不会关闭替换后的媒体。故障回调只发布状态，不等待所有者 Dispose，避免双向等待。
- 出站信令使用进程内 `SignalingRelay`：每媒体实例一个容量受限、单 worker 保序队列，入队不阻塞 PCM 读循环；暂时失败指数退避、限次、限有效期，永久失败/溢出/耗尽统一结束通话。
- 该队列不持久化：进程重启后旧通话由启动对账结束或失败，旧媒体实例的信令不会被投递给新实例。响应丢失时按至少一次重发相同负载，后端没有幂等键，因此不宣称恰好一次。
- 适配器仍坚持 Protobuf-only 访问后端、PostgreSQL-only 业务持久化。真实联调要求后端自身运行在 `NewStoragePrimary` + `RelationalBackend`，并以 `/health` 的 `storage=postgresql` 为证据；适配器的 PostgreSQL 测试不能替代后端存储实现。

### 未完成与限制

- Android 真机互通未验证（当前环境 `adb devices` 为空），`Capability.Audio=true` 仅代表引擎已接入并通过实例间双向音频，不代表已与手机通话。
- TURN 未配置、未验证；隔离后端只下发 P2P 端点。
- 视频、录音、自动接听仍未实现或未验证。
