# 交付报告

日期：2026-11-08。首轮：把仓库从"文档基线"推进到可构建、可测试、可运行的首期实现。
第二轮（同日，复核修订）：修复复核确认的九项问题，新增回归；真实媒体仍未完成，阻碍见文末。
第三轮（同日，第二轮复核修订）：修复第二轮复核确认的八项问题，新增回归；真实媒体仍未完成。
第四轮（同日，第三轮复核修订）：修复第三轮复核确认的五项缺口，新增回归；真实媒体仍未完成。
第五轮（同日，第四轮复核修订）：修复第四轮复核确认的四项缺口，新增回归；真实媒体仍未完成。

## 第九轮：三项复核修复（2026-11-08）

本轮修复复核确认的三项问题：数据库暂时不可用时的故障收尾丢失、旧信令队列失败回调误关替换后的媒体、信令有效期未覆盖重试与在途发送。三项均直接修改源码，并完成宿主回归与隔离 PostgreSQL 真实拓扑回归。提交、推送与正式部署未执行。

### 1. 数据库暂时不可用时故障收尾不丢失

根因：媒体/信令故障处理在数据库异常时只记录日志，故障回调已经被消费，通话在库中保持 `active/ready`，数据库恢复后没有执行者继续收尾。

修复：

- `WatchMediaAsync` / `HandleSignalingFailureAsync` 在通话锁内核对账号代际与当前实例后，先停止该通话的信令队列、媒体会话、代理进程、音频任务，再执行数据库收尾。数据库不可用不影响本地资源释放。
- 收尾失败保留为内存待办（`_pendingFaults`），绑定服务器、账号、登录代际、`call_id`、具体媒体会话与信令绑定。新增 `CallFaultFinalizer` 每 1 秒扫描，按 1/2/4/…/30 秒退避重试；`CallMaintenanceWorker` 每轮也处理一次。
- 终态事务前写 `reconcile_state=pending`；终态与 `media_closed` 由同一状态版本事务产生。事务提交后撤销媒体令牌、停止并释放媒体、调用后端 `discard_call`；任一步失败后重试都从已提交状态继续，`media_closed` 只由实际应用的状态版本发出，重复执行不会重复终态或事件。
- 后端 `discard_call` 暂时失败继续写 `pending_end_reason=failed` 与对账退避，由维护任务与启动对账继续释放；400/404 视为线路已释放。媒体令牌删除与 discard 均为幂等操作。
- 待办条目每次重试前重新核对账号代际与当前注册实例；账号退出、登录代际变化或媒体/信令实例被替换时直接丢弃，旧任务不会影响新会话或替换后的媒体。
- 恢复边界：内存待办不跨进程重启；进程重启由启动对账结束上一实例遗留的通话（`ended/closed/restart`），不恢复旧媒体。实现没有引入 JSON 文件、备用数据库或整库快照降级存储。

真实拓扑回归（隔离适配器 PostgreSQL 56412，停止集群并保留 `pause` 防止 supervisor 自动重启；仅强杀适配器 A 的 `milky_media_agent`）：

| 场景 | 结果 |
|---|---|
| ready 且双向音频后停库、仅杀媒体代理 | 通过：适配器 A 进程与 HTTP 存活、`get_login_info` 恢复 100000、代理进程消失、音频样本停止增长、日志出现 `fault finalization ... deferred`；恢复前数据库一直不可用 |
| 数据库恢复后不调用任何通话 API | 通过：自动收敛为 `failed/closed/media_agent_failed`，`reconcile_state` 与 `pending_end_reason` 为空，媒体令牌 0，后端线路 `ended`；调用方消费者收到 `media_closed`，双方各 1 个终态事件、调用方 1 个 `media_closed` |
| 收尾期间数据库不可用并强杀适配器进程 | 通过：数据库恢复后 supervisor 重启适配器，启动对账把旧通话结束为 `ended/closed/restart`，无待办标记，线路 `ended`，令牌 0，重放消费者收到 `media_closed` |
| 恢复后双方重拨 | 通过：多次重拨，A 发 440 Hz 只收 660 Hz、B 发 660 Hz 只收 440 Hz，RMS 与窄带能量比均达标 |
| 宿主故障注入：媒体故障/信令故障期间数据库不可用、终态提交后凭据撤销失败 | 通过：`Round6RegressionTests` 4 项，含待办重试、自动收敛与幂等重试 |

### 2. 旧信令队列失败回调不得关闭替换后的媒体

修复：

- `SignalingBinding` 增加不可混淆的 `InstanceId`；`BeginSignaling` 的回调捕获具体 binding，`OnSignalingFailure` / `HandleSignalingFailureAsync` 全程携带该实例。
- 取得通话锁后重新核对账号、登录代际、服务器域（`IsContextCurrent`）与当前注册 binding（`IsBindingCurrent`）；`Session` 尚未赋值的启动阶段仍视为当前实例。已替换、已停止或已退出账号的回调直接结束，不再调用 `FailLocallyAsync`。
- `StopSignalingAsync(callId, expected)` 使用精确键值移除，不误删同一 `call_id` 上新安装的 binding；`StopBindingMediaAsync` 在 `Session` 为空时只停止该 `call_id` 当前引擎实例。
- 第一项的内存待办同样保存媒体会话与 binding，重试前复核实例；即使故障回调与收尾重试并发，旧实例也不能关闭替换后的媒体。
- 回归使用可控锁与延迟回调触发竞态：旧队列失败回调等待通话锁时安装新 binding 与新媒体，释放旧回调后新媒体保持 `active/ready`；当前 binding 故障仍能结束通话；替换实例不能再发送信令或触发收尾；挂断、退出、替换与失败通知并发时只有一个有效终态与一个 `media_closed`。

### 3. 信令有效期覆盖全部重试与发送过程

修复：

- `SignalingRelay.Entry` 增加绝对到期时间，队列使用 `Stopwatch` 单调时钟（可注入测试时钟），不受系统时间调整影响。
- 每次发送尝试前检查剩余有效期；重试等待被截断到剩余有效期，等待结束时已过期则直接报告 `signaling_expired`，不再进行第二次发送。
- 在途发送由链接取消令牌约束，并用 `Task.WhenAny(send, remaining)` 限时；超过剩余有效期即取消发送任务并报告过期。
- 明确区分主动停止/账号失效（静默返回，不结束新通话）、条目过期、永久失败、重试耗尽；失败通知幂等且每次队列最多一次；有界容量与顺序保持不变。
- 已发出的请求可能在服务端已生效，本地取消不保证撤销提交；仍按可恢复的至少一次语义处理，不宣称恰好一次。

回归：`SignalingRelayTests` 12/12，覆盖出队前过期、首次暂时失败后在重试等待期间过期不二次发送、在途发送超时被取消并报告过期、有效期内暂时失败后按序恢复且负载一致、过期/替换/Dispose/重试耗尽并发只通知一次、旧队列停止后不再发送。用户复现口径（有效期 20 ms、重试延迟 100 ms）由 `Expiry_during_retry_wait_does_not_send_a_second_time` 覆盖，只发送一次并报告 `signaling_expired`。

### 回归与验证汇总

- `dotnet build -c Release`：0 警告、0 错误。
- `dotnet test -c Release`（含真实 `milky_media_agent`）：119 通过、0 失败、0 跳过。
- 定向：`SignalingRelayTests` 12/12；`Round6RegressionTests` 12/12（含本轮新增 4 项数据库中断、凭据撤销重试与旧回调隔离）。
- 真实拓扑：`Database outage + restart e2e` 28/28；`media-fault-e2e` 11/11（仅杀代理、重拨双向音频、故障与挂断并发只产生 1 个终态和 1 个 `media_closed`、线路可复用）。
- 拓扑：后端 `NewStoragePrimary` + `RelationalBackend`，Protobuf-only；适配器与后端各用独立 PostgreSQL 16.15 集群（56412/56413）；`/health` 为 `storage=postgresql`；账号 100000/100001。未访问或修改正式服务及正式数据库。

### 部分失败恢复策略

- 终态提交前失败：本地资源已停止，持久化收尾留在内存待办并按退避重试；进程在此期间重启则由启动对账接管。
- 终态提交后凭据撤销失败：令牌行保留，重试从已提交终态继续，最终删除令牌；不会重复终态或 `media_closed`。
- 后端线路释放失败：写 `pending_end_reason=failed` 与 `reconcile_state=pending`，维护任务与重启对账继续 `discard_call`；400/404 直接视为已释放。
- 媒体已停止而数据库不可用：不会重建旧媒体；恢复后只补齐持久化收尾。客户端媒体 WebSocket 由 `media_closed` 事件驱动关闭，本轮验证的是引擎代理控制 socket 与音频任务已停止，不是由服务端强制关闭客户端连接。

### 发布包（本轮重新打包）

- 目录：`D:\BotgramBuild\milky-release\nyarugram-milky-0.1.0-linux-x64`（44 个文件，39 MB）
- 归档：`D:\BotgramBuild\milky-release\nyarugram-milky-0.1.0-linux-x64.tar.gz`（16,136,532 字节）
- 归档 SHA-256：`f1b159b0cb22fe4296aa9bbd3eb2a514c8de76dcb3e550552e19af7372c7c149`
- 包内 `SHA256SUMS` 43 项，`sha256sum -c` 43/43 通过；`nyarugram-milky version` 输出 milky 1.3.0、backend protocol major 1；包内代理自测 `{"ok":true,"established":true,"caller_frames":500,"callee_frames":500,"caller_rms":726.88,"callee_rms":635.61}`。
- 本轮只更新托管程序集；媒体代理二进制未变（SHA-256 `dc0cf5d6…72fa`），`DEPENDENCIES.txt`、`VERSION` 与许可证材料未变。

### 未验证事项

- 真实后端注入出站信令过期/耗尽/永久失败：仅有宿主可控故障注入，未在真实后端制造。
- TURN 中继、Android 真机双向音频、视频、弱网与断网恢复：环境或设备未具备，未验证。
- 正式部署与正式数据迁移：未执行。

## 第八轮：四项复核修复（2026-11-08）

本轮修复复核发现的四项问题：真实联调切到 PostgreSQL 权威后端、媒体运行期故障通知、出站信令可靠投递、原生媒体代理源码与可复现构建。提交与正式部署未执行。

### 1. 真实联调切换到 PostgreSQL 权威后端

隔离拓扑（不接触正式数据库或正式实例）：

| 项 | 值 |
|---|---|
| 后端 | 源码 `W:\Work\Botgram.Backend` commit `60b5adc89e741b92ee836279aac4647f1f28d5ce`；`Storage:Mode=NewStoragePrimary`、`ValidationBackend=RelationalBackend`、`PrimaryAcknowledged=true`、Protobuf-only |
| 后端数据库 | PostgreSQL 16.15，独立集群端口 `56413`，数据库 `botgram_milky_pg`，schema `botgram_backend_v1`，版本 1..12 |
| 后端健康 | `http://127.0.0.1:8523/health`：`status=ok`、`storage=postgresql`、`outbox_worker=running`；`Storage.Admin inspect` 通过 |
| 适配器状态库 | 另一 PostgreSQL 16.15 集群端口 `56412`，数据库 `milky_pg_a` / `milky_pg_b`（会话测试另用 `milky_pg_c`），schema `nyarugram_milky` 版本 1..5 |
| 适配器源码 | 本仓库工作树（尚无 git 提交）；linux-x64 Release 发布，`nyarugram-milky version` 输出 milky 1.3.0、backend protocol major 1 |
| 账号 | 两个专用账号经正式 Protobuf `register` API 创建，TL ID 100000/100001；另建 100003 用于会话测试。未直接写库创建账号 |

LegacyOnly 的第七轮结果保留为历史证据，不再代表当前验收。后端未发现需要修改的源码缺陷；本轮未修改 `Botgram.Backend` 源码（`packages.lock.json` 的 `linux-x64` 段是发布还原时由 NuGet 生成的附带变化）。

在该拓扑下重跑：

| 场景 | 结果 |
|---|---|
| 登录、会话恢复、后端令牌失效、适配器退出、重新登录 | 通过：失效令牌导致 API `-403`；CLI 退出后 `session none`；重新登录后重启 serve 恢复 |
| 私聊/群聊、提及、回复、历史、消息查询、已读 | 通过：基础套件 14/14；群套件 11/11（群由正式 Protobuf `create_group` 创建） |
| 幂等重试、同键冲突、差分与事件恢复 | 通过：14/14 含幂等/冲突/实时事件；epoch ACK/resume 与后端重启后的重连通过。适配器事件连接断开后的差分补偿由既有同步代码承担，本轮未单独制造该断点 |
| 呼叫发起、接听、拒接、取消、忙线、超时、双方重拨 | 通过：14/14 |
| 两个真实媒体代理的双向已知音频 | 通过：A 发送 440 Hz、B 发送 660 Hz；A 只收到 660 Hz（能量比 6.4e5），B 只收到 440 Hz（能量比 2.1e6），排除静音、串音与本地回环 |
| 适配器重启 | 通过：强杀适配器 A 后重启，复用持久会话，被叫收到 `ended/disconnect`，重拨后双向音频恢复 |
| 后端重启 | 通过：恢复后消息与双向音频均成功 |
| 适配器状态 PostgreSQL 重启 | 通过：恢复后消息与双向音频均成功 |
| 后端业务 PostgreSQL 重启 | 通过：恢复后消息与双向音频均成功 |
| epoch ACK/resume、账号隔离 | 通过：旧/缺失 epoch 被拒，数据库位点前进；跨账号读取呼叫 `-404` |

证据保存在 `D:\BotgramBuild\milky-pg\evidence\topology.txt`，包含两库 schema 版本、服务端版本、健康输出、呼叫终态与账号列表。

### 2. 媒体运行期故障

- `IMediaSession.Completion` 新增一次完成通知：主动关闭为 `null`，代理退出/控制连接 EOF/读写失败/引擎 `failed` 为 `MediaFault`。`TgcallsSession.Fail` 幂等发布故障、取消传输、结束音频通道并终止进程树，不等待读循环或 owner Dispose。
- `CallService` 在媒体 ready 后启动 watcher；故障时先停止该通话的信令队列，再以 `failed` 终态在同一事务写入 `media_state=closed` 和 `media_closed`，随后释放会话、进程、socket、令牌与任务；`engine.Find(call_id) == faulted session` 保证旧实例迟到退出不影响替换后的媒体。
- 失败后 `discard_call` 暂时失败会写 `pending_end_reason=failed` 与对账退避，维护任务继续释放；适配器不退出，双方可重拨。
- 回归：新增 4 项宿主集成测试（故障后状态/事件、迟到旧实例、故障与挂断并发、discard 失败后自动对账）全部通过；真实代理场景 `media-fault-e2e` 10/10：仅强杀媒体代理后适配器存活、DB `failed/closed/closed/media_agent_failed`、消费者收到 `media_closed`、双方重拨双向音频、并发仅 1 个终态与 1 个 `media_closed`、线路可复用。

### 3. 出站信令可靠投递

- 新增 `SignalingRelay`：每媒体实例一个有界 Channel（容量可配）、单 worker 保序；暂时失败（未知结果、可重试、HTTP 0/408/429/5xx、IO/Socket/超时）指数退避重试；受尝试次数与信号有效期约束；永久失败、溢出、过期、耗尽统一结束通话并释放线路。入队不阻塞 PCM 读循环。
- 队列绑定账号、登录代际、call_id、信令绑定与具体媒体实例；挂断、退出、账号切换、媒体替换和对账释放都会停止旧队列；旧实例回调因绑定校验失败被丢弃。
- 后端 `send_signaling_data` 无幂等键；响应丢失时以相同负载重发，属显式至少一次边界，不宣称恰好一次。内存队列的进程重启边界由启动对账结束旧通话。
- 回归：`SignalingRelayTests` 7/7（顺序、暂时失败重试、响应丢失重复、永久失败、重试耗尽、容量溢出、过期、Dispose 取消、替换实例不再投递）；`Round6RegressionTests` 中新增 3 项真实 CallService 集成用例（瞬时 500 顺序交付、响应丢失重复、重试耗尽失败释放）全部通过。

### 4. 原生媒体代理源码与可复现构建

- 仓库新增 `media/`：`CMakeLists.txt`、`sources.txt`（1,266 个编译源文件）、`agent/`（5 个代理源码）、`patches/0001-linux-build.patch`、`upstream.json`、`prepare-source.sh`、`build-agent.sh`、`LICENSES/`。
- `prepare-source.sh` 用 `git archive` 提取 Botgram `2fab5dcf…` 的精确 blob，校验 tgcalls/BoringSSL/abseil/opus/libyuv/libvpx/OpenH264 固定提交，应用补丁前后分别校验树哈希；失败即中止。`build-agent.sh` 先构建静态 libvpx（v1.16.0），再构建 Release 代理并运行 P2P 自测。
- 从新建干净目录 `D:\BotgramBuild\milky-media-build2` 构建成功：Ubuntu 24.04.3 LTS、GCC/G++ 13.3.0、ld 2.42、CMake 3.28.3、Ninja 1.11.1；补丁前树哈希 `4214a4470c48d507a012992c6dbd73c10ff0f7b80e2b9600c9a66b186208b481`，补丁后 `d5608efea5c38425261164defff873db7d508b1397a4a78ab7063018eeff88e8`；产物 SHA-256 `dc0cf5d6fa6547eb7add222ea93d998e0d24f40655b84142baaad52ad6d872fa`（35,414,216 字节）；自测 `caller_frames=504 callee_frames=502 RMS=726/635`。
- 许可证和重链接材料：`media/LICENSES/` 含 tgcalls LGPL-3.0、WebRTC/BoringSSL/opus/libyuv/libvpx/libsrtp/usrsctp/rnnoise/PFFFT/OpenH264/json11/libevent 文本与 `THIRD-PARTY-NOTICES.md`。打包后 43 项 SHA-256 全部校验通过，包内代理自测再次通过。

### 发布包

- 目录：`D:\BotgramBuild\milky-release\nyarugram-milky-0.1.0-linux-x64`
- 归档：`D:\BotgramBuild\milky-release\nyarugram-milky-0.1.0-linux-x64.tar.gz`（16 MB）
- 归档 SHA-256：`304e0c8a6c1b916be43f15a94fb87737e85d3e8cc524bdb96229580bec9a9fc6`
- 包内 `SHA256SUMS` 43 项，`sha256sum -c` 通过；`media/SOURCES.txt`、`media/BUILD.md`、`media/upstream.json`、`DEPENDENCIES.txt` 已更新。

### 验证汇总

- .NET：110 项通过、0 失败、0 跳过（含真实代理 `TgcallsEngineTests`，使用本轮干净构建的 `milky_media_agent`）。
- 真实 PostgreSQL 后端联调：基础 14/14、群 11/11、媒体故障 10/10、恢复 4/4、会话 5/5、epoch 通过、重启重拨与双向已知音频通过。
- 未验证：TURN 中继（环境未配置 ICE）、Android 真机双向音频（无设备）、真实后端注入出站信令故障（仅有宿主故障注入）、视频。

## 第七轮：真实媒体、真实后端、NoneBot 与发布（2026-11-08）

本轮完成四项任务中的真实媒体、真实后端联调、机器人框架互操作与独立发布准备。历史各轮结论保留在下方，不再代表当前状态。

### 1. 真实媒体（tgcalls）

- 核对 Android `VoIPService` 实际引擎：`tgcalls::InstanceV2Impl`，`Meta::Versions()` 返回 `7.0.0/8.0.0/9.0.0/12.0.0/13.0.0`，生产端使用 `13.0.0`；协议层 65..92；信令为加密 SCTP/mtproto，不能自行重写。
- 在 WSL2 Ubuntu 24.04（12 核/11GB，可联网与 sudo）用 CMake/Ninja/GCC 从 Android 源码子树（commit `2fab5dcf`）构建 tgcalls + WebRTC 静态库与 `milky_media_agent`（约 1300 源文件）。为 Linux 适配：排除 Android/JNI/WASM/音频采集模块，接入 `FakeAudioDeviceModule`，修补缺失头文件与 Android 专用日志，连接超时 20→45 秒。
- `TgcallsMediaEngine` 以子进程方式接入 `IMediaEngine`：启动、`READY` 握手、共享密钥/端点/协议参数、双向信令、PCM 输入输出、异常退出与资源释放均已实现。`media_ready` 仅在信令 `established` 且收到首帧对端音频后发出。
- 证据：
  - 引擎自测（进程内两实例，P2P）：`caller_frames=499 callee_frames=498`，RMS/过零率符合 440/880 Hz。
  - 双进程桥接：全双工 RMS 1033/635。
  - `TgcallsEngineTests`（两个独立代理进程 + 两个 `TgcallsMediaEngine`）：13.0.0，全双工 440/660，RMS>100。
  - 真实隔离后端 + 两个 Linux 适配实例 A↔B：呼叫 08:30:11 起，08:30:17 双 ready，RMS 850/537，4 秒内 25 万采样/方向。
  - 通话中强杀主叫适配进程：被叫收到 `ended/disconnect`；重启复用持久会话、无需重新登录；对端重拨后双向音频恢复（RMS 314/423）。
- 局限：TURN 未配置、未验证；Android 真机双向音频未验证（`adb devices` 为空）。实例间互通不能替代真机验收。

### 2. 真实 Nyarugram 后端联调

隔离环境：后端 `LegacyOnly`（JSON 数据文件独立目录），PostgreSQL 16.2 独立集群，专用账号 `milkybot_/milkypeer_`，两个 Linux 适配实例。发现并修复的真实契约差异：

| 差异 | 修复 |
|---|---|
| `UserRef` 缺 `server_domain` 导致后端 501 `BOTGRAM_REMOTE_DOMAIN_UNSUPPORTED` | 身份映射补齐域名 |
| `confirm_call` 的生效状态只发给被叫，主叫若忽略响应会停在 `negotiating` | 使用 confirm 响应并保存快照，置 `active` |
| 媒体重试读旧快照导致 `CALL_FINGERPRINT_MISSING` | 缓存已验证密钥，容忍快照指纹为 0 |
| `get_difference` 返回 `too_long` 后等待 1 分钟，消息滞后 | 立即重置游标并重新基线 |
| 过期信令写已关闭 socket 使整页同步失败、丢事件 | 单条信令失败不中止整页；关闭的会话移除 |
| 媒体启动在同步循环内等待，58 秒才 ready | 改为后台等待，ready 约 5 秒 |
| 媒体代理 30 秒无控制帧自杀 | 空闲阈值改为 10 分钟 |
| `get_group_list` 请求 `limit=500` 被后端拒绝 | 按后端上限 100 分页枚举 |
| 入站 `mention` 仅有 `@name` 文本 | 增加 `resolve_username` 反向解析并缓存，无法解析时保留纯文本 |

验证结果（全部针对真实后端，不是假后端）：

| 套件 | 结果 |
|---|---|
| 私聊/群聊文本、提及、回复、历史、查询、已读 | 11/11（群经官方 `create_group` RPC 建立） |
| 幂等发送、同键冲突、事件、历史、查询、已读、拒接重拨、取消、忙线、超时、账号隔离 | 14/14 |
| 真实双向 PCM | 通过 |
| 崩溃重启对账 + 重拨 | 通过 |
| PostgreSQL 重启（`pg_ctl restart -m fast`） | 通过：两实例持续服务，连接池自动恢复，无需重启进程；重启后新消息送达并可查历史 |
| epoch ACK/resume | 通过；旧/缺失 epoch 被拒，数据库 `last_seq`/`epoch` 证据齐全 |

### 3. NoneBot 互操作

NoneBot 2.5.0 + nonebot-adapter-milky 1.3.0（Python 3.12 独立 venv）真实连接适配实例：

- 私聊消息事件到达插件并由 `send_private_message` 回复（`nb-ack:hello-nonebot`）。
- `/caps` 通过适配器 HTTP 客户端调用扩展，返回 `caps:1.3:voice=true:engine=tgcalls-13.0.0`。
- `/call <uin>` 发起真实呼叫，对端接听后进入 `active`，`/end` 结束；呼叫事件经过适配器透传。
- 适配器重启后 NoneBot 自动重连并继续收到事件；扩展事件被适配器标记 `received unsupported event` 但原样透传，不中断连接。这是当前框架层的已知差异：NoneBot 原生不识别 Nyarugram 扩展事件，需要专用插件处理器。
- 示例见 `examples/nonebot` 与 `examples/media/pcm-bridge.mjs`；后者已在实际呼叫中双向录制，RMS 762/540。

### 4. 构建、测试与发布

- 完整构建 0 错误、0 警告；完整测试 **90 通过、0 失败、0 跳过**（含真实媒体引擎测试，设置 `NYARUGRAM_MEDIA_AGENT_COMMAND`，使用最终 Release 媒体代理）。
- 发布包：`D:\BotgramBuild\milky-release\nyarugram-milky-0.1.0-linux-x64`（39 MB，34 个文件）；归档 `nyarugram-milky-0.1.0-linux-x64.tar.gz`（16 MB，SHA-256 `d386bfab19d886ede759c7fffa40995c9cfd92725cd71306608828554efbc3a2`）：
  - 适配程序（Release，framework-dependent，需 .NET 8 + ASP.NET Core 8 运行时）
  - `media/milky_media_agent`（Release，35.4 MB；Debug 版 220 MB 未放入包）
  - `protocol/botgram_rpc.proto`、`config/locks.json`、`config/milky-1.3.0.openapi.json`
  - `config/local.json.template`（无真实服务器地址与凭据）
  - `LICENSES/`（tgcalls LGPL-3.0、BoringSSL/abseil Apache-2.0、opus/libyuv/libvpx/libevent BSD）与 `NOTICE.txt`
  - `DEPENDENCIES.txt`、`media/SOURCES.txt`、`VERSION`、`SHA256SUMS`（34 项，`sha256sum -c` 全部通过）
- 干净目录验证（2026-11-08 18:0x）：把发布包解压到 Linux ext4 目录 `~/milky-clean`，使用全新 PostgreSQL 数据库 `milky_release`：
  ```bash
  sha256sum -c SHA256SUMS        # 34/34 OK
  ./nyarugram-milky version      # 0.1.0 / milky 1.3.0 / proto sha256 0d2d4a4c…
  ./nyarugram-milky status       # 迁移 1..5 执行，随后会话恢复为 uin 100000
  ./nyarugram-milky serve        # /health: status=ok, media_engine=tgcalls-13.0.0, media_available=true
  ```
  用包内实例与对端实例完成：消息投递成功；对端呼入、包内实例接听；双方媒体 ready 后双向 PCM 3 秒，包内实例 RMS 958、对端 RMS 336，采样 278,400/299,520。
- 上游会话恢复与升级路径：全新数据库 + 旧库 `account`/`session` 数据导入后直接可用，无需重新登录；数据库迁移按版本顺序执行。
- 构建注意：在 WSL 的 `/mnt/d` 挂载盘上直接运行升级后的程序集时，9p 缓存可能向新进程提供旧程序集。验证时应复制到 ext4 路径（如 `~/milky-app`）后再启动；此现象已在运行手册中记录。

### 5. 明确未完成

- Android 真机双向音频（机器人呼手机、手机呼机器人、P2P/TURN、弱网与断网恢复）。
- TURN 中继验证。
- 正式 Nyarugram 生产后端与正式数据迁移/部署（需用户明确授权）。
- 仓库尚无首次提交（`master` 无 commit，远端 `origin` 已配置）；未执行提交或推送。
- 视频、录音、自动接听、文件类消息与群管理。

---


## 第六轮复核修复（2026-11-08）

本轮修复 WebSocket 确认代际及阻塞订阅隔离。

- 扩展事件及恢复响应携带 epoch，ACK 和 resume 必须回传匹配值。旧、缺失或超前 epoch 不推进持久位点。
- 数据帧与控制帧串行发送，恢复响应与新代际数据有明确顺序。扩展模式仅由已验证的 ACK 持久化确认位置，避免退出快照把旧位点配上新 epoch。
- 溢出订阅立即从总线移除，并取消传输任务。WebSocket 的阻塞写入同时中止，SSE 的发送使用订阅取消令牌。接收端断线也会清理空闲订阅。
- 更新原有扩展客户端回归，使其使用服务端提供的 epoch，新增六项回归，覆盖旧/缺失/超前 epoch、恢复后有效确认、跨批投递、阻塞传输取消、空闲连接退出、普通 Milky 载荷及两种队列溢出路径。

完整构建为 0 错误、0 警告。最终源码的完整测试连续两次均为 79 通过、0 失败、0 跳过。测试使用新建的独立 PostgreSQL 16.2 集群、进程内 Protobuf 假后端和宿主 HTTP/WebSocket 端点，集群在验证结束后停止。阻塞发送由暂停消费的订阅及可取消传输任务模拟。

启用 nyarugram_ws_ack 的客户端需要更新 ACK/resume 请求以携带 epoch。真实后端、NoneBot 互操作及真实双向语音均未执行，本轮未进行正式部署。历史轮次记录保留如下。

---

## 第五轮：第四轮复核修订（2026-11-08）

第四轮复核提出的四项缺口已逐项修复，并由新增回归覆盖。测试基线从 69 项扩到 **73 项，全部通过，无跳过**（真实 PostgreSQL 16.2 + 进程内 Protobuf 假后端）。

| # | 问题 | 修复位置 | 回归用例 |
|---|---|---|---|
| 1 | 一个未确认事件的消费者阻塞其他消费者 | `EventBus.MinSentCursor`、`SyncService.DrainAsync`（投递按最低**已发送**位点推进，而非最低已确认位点） | `A_consumer_that_never_acknowledges_does_not_stall_its_peers` |
| 2 | 旧连接仍能覆盖 resume 回退位置 | `event_cursor.epoch` 迁移、`OutboxStore`（`SetConsumerCursorAsync`/`RewindConsumerCursorAsync` 按代际条件更新，`Rewind` 递增代际并返回新值）、`EventSubscription.Epoch/AdoptEpoch`、`MilkyEndpoints` 贯穿代际 | `A_stale_connection_cannot_overwrite_a_rewind` |
| 3 | 启动清理旧呼叫首次失败后不持续重试 | `CallStore.ListPendingReconcileAsync`、`CallService.MaintainAsync`（周期维护纳入 `reconcile_state='pending'` 的呼叫）、成功路径 `ClearReconcilingAsync` | `A_failed_startup_cleanup_keeps_retrying_in_maintenance` |
| 4 | 挂断后仍显示媒体 ready、缺少媒体关闭事件 | `CallStore.TransitionWithEventAsync`（终态原子设置 `media_state='closed'`、`media_version+1` 并同事务发 media_closed 事件）、`CallService.TransitionAsync` | `Hanging_up_closes_media_and_emits_a_media_closed_event` |

### 第五轮测试环境与结果

```bash
dotnet build Nyarugram.Milky.sln          # 0 error, 0 warning
dotnet test tests/Nyarugram.Milky.Tests   # 73 passed, 0 failed, 0 skipped
```

环境：Windows 10.0.26100，.NET SDK 8.0.204/9.0.320，PostgreSQL 16.2（隔离集群，端口 56411，数据库 `nyarugram_milky_test` 由测试夹具重建）。

### 第五轮明确未完成

**真实双向语音**（同前四轮）。真实媒体引擎与设备互通仍是产品未完成目标，`calls.voice=false` 保持不变。真实后端联调、NoneBot 互操作、真机通话同样未执行，原因是没有可用环境。

---

## 第四轮：第三轮复核修订（2026-11-08）

第三轮复核提出的五项缺口已逐项修复，并由新增回归覆盖。测试基线从 63 项扩到 **69 项，全部通过，无跳过**（真实 PostgreSQL 16.2 + 进程内 Protobuf 假后端）。

| # | 问题 | 修复位置 | 回归用例 |
|---|---|---|---|
| 1 | 接听丢响应后已保存私钥未被恢复流程使用 | `CallService.GetSecretAsync`（通用字段读不到时回退读 `accept_secret_*`） | `Lost_accept_response_still_derives_the_key_from_persisted_material` |
| 2 | 密钥失败后本地失败但后端仍占线 | `CallService.FailCallAsync`（本地失败后 `discard_call`，暂时失败转待对账）、`ReconcileOneAsync`（终态但待释放仍执行释放）、`CallStore.SetPendingEndAsync/ListPendingEndAsync`（去掉 `ended_at IS NULL` 约束）、`ClearPendingEndAsync` | `Key_failure_releases_the_backend_line_so_the_peer_can_redial`、`Key_failure_with_transient_discard_is_reconciled_by_maintenance` |
| 3 | 零游标消费者重连漏掉未确认事件 | `OutboxStore.EnsureConsumerAsync`（命名消费者首次连接注册初始位点）、`MilkyEndpoints.HandleEventAsync` | `Zero_cursor_consumer_reconnect_replays_unacknowledged_events` |
| 4 | resume 回退仅改内存未持久化 | `OutboxStore.RewindConsumerCursorAsync`（`LEAST` 允许降低位点）、`MilkyEndpoints` resume 分支改用回退语义 | `Explicit_resume_persists_the_rewind_durably` |
| 5 | 超过一批的恢复区间被截断 | `MilkyEndpoints` resume 分支分页循环（`ReplayBatchSize` 逐页读完）、`EventSubscription.Replay` | `Resume_over_a_range_wider_than_one_page_replays_everything` |

### 第四轮测试环境与结果

```bash
dotnet build Nyarugram.Milky.sln          # 0 error, 0 warning
dotnet test tests/Nyarugram.Milky.Tests   # 69 passed, 0 failed, 0 skipped
```

环境：Windows 10.0.26100，.NET SDK 8.0.204/9.0.320，PostgreSQL 16.2（隔离集群，端口 56411，数据库 `nyarugram_milky_test` 由测试夹具重建）。

此外修正了测试替身的一处语义：`FakeBackend.GetDifference` 现在在满页时返回 `Slice` 而非 `Complete`，让同步循环按真实后端的差分语义逐页消费积压事件，避免长跑测试时差分游标被单页 100 条截断而永远追不上。

### 第四轮明确未完成

**真实双向语音**（同前三轮）。真实媒体引擎与设备互通仍是产品未完成目标，`calls.voice=false` 保持不变。真实后端联调、NoneBot 互操作、真机通话同样未执行，原因是没有可用环境。

---

## 第三轮：第二轮复核修订（2026-11-08）

第二轮复核提出的八项问题已逐项修复，并由新增回归覆盖。测试基线从 45 项扩到 **63 项，全部通过，无跳过**（真实 PostgreSQL 16.2 + 进程内 Protobuf 假后端）。

| # | 问题 | 修复位置 | 回归用例 |
|---|---|---|---|
| 1 | 非法 DH 公钥导致未受控失败 | `Core/DhValidation.cs`（新增，参数/生成元/公钥安全范围校验）、`CallService.ConfirmAsync/AcceptAsync`、`SyncService`（同步循环不再被单条坏数据打断） | `Invalid_peer_public_value_fails_only_that_call`（0/1/p-1/p/长度错误/全 FF 六种输入） |
| 2 | 被叫未验证 g_a_hash 与最终指纹 | `CallService.ApplyAsync`（持久承诺、SHA-256 校验、指纹必填且匹配、跨呼叫材料替换） | `Callee_rejects_ga_that_does_not_match_the_original_commitment`、`Callee_requires_a_matching_fingerprint_before_media_ready`、`Callee_reaches_media_ready_only_after_hash_and_fingerprint_checks` |
| 3 | 接听未先持久化密钥 | `CallService.AcceptAsync`（`ClaimAcceptAsync` 先落私钥/g_b/协议/阶段） | `Accept_persists_key_before_the_backend_and_reuses_it`、`Concurrent_accepts_share_one_key_material` |
| 4 | 媒体恢复未独立于信令 | `CallService.EnsureMediaAsync`（媒体阶段/重试与信令解耦、有界退避、就绪判定纳入引擎契约） | `Media_start_retries_after_a_transient_config_failure` |
| 5 | 挂断待确认无后台执行者 | `CallService.MaintainAsync` + `CallMaintenanceWorker`（后台对账、有界退避、错误码区分） | `Unknown_hangup_is_reconciled_without_manual_retry` |
| 6 | 事件连接未绑定账号代际 | `EventBus`/`EventSubscription`（订阅绑定 server/account/generation、`CancelScope`）、`SessionManager`、媒体连接代际校验 | `Event_connection_of_an_old_generation_stops_after_relogin` |
| 7 | 租约无所有者隔离 | `IdentityStore`/`CallStore`（`lease_token` 条件更新/续租/释放/完成，禁止降级已完成） | `An_expired_lease_holder_cannot_release_or_complete_the_new_owners_operation`、`Call_operation_lease_is_owner_scoped` |
| 8 | WS 无客户端确认与恢复位置 | `MilkyEndpoints.RunWebSocketAsync/ReceiveAcksAsync`（版本化帧、已发送/已确认位点分离、`nyarugram_ack`/`nyarugram_resume`）、`EventSubscription.Replay` | `WebSocket_ack_mode_replays_until_the_client_confirms`、`WebSocket_resume_moves_the_cursor_back_within_the_sent_range` |

### 第三轮测试环境与结果

```bash
dotnet build Nyarugram.Milky.sln          # 0 error, 0 warning
dotnet test tests/Nyarugram.Milky.Tests   # 63 passed, 0 failed, 0 skipped
```

环境：Windows 10.0.26100，.NET SDK 8.0.204/9.0.320，PostgreSQL 16.2（隔离集群，端口 56411，数据库 `nyarugram_milky_test` 由测试夹具重建）。

### 第三轮明确未完成

**真实双向语音**（同前两轮）。真实媒体引擎与设备互通仍是产品未完成目标，`calls.voice=false` 保持不变。真实后端联调、NoneBot 互操作、真机通话同样未执行，原因是没有可用环境。

---

## 第二轮复核修订（2026-11-08）

复核提出的九项问题已逐项修复，并由新增回归覆盖。测试基线从 23 项扩到 **45 项，全部通过，无跳过**（真实 PostgreSQL 16.2 + 进程内 Protobuf 假后端）。

| # | 问题 | 修复位置 | 回归用例 |
|---|---|---|---|
| 1 | `AccountMe` 走错入口 | `Core/AccountApi.cs`（`/api/v1/account`）、`MilkyActions.LoginInfoAsync`；假后端 `/api/v1/rpc` 对账号操作返回 501 | `Account_operations_are_only_served_on_the_account_endpoint`、`Login_info_fails_when_the_session_changes_during_the_request` |
| 2 | 发送幂等只存在于内存 | `Core/SendService.cs`、`Storage/IdentityStore.cs`、`idempotency.request_json/lease_until/state` 迁移 | `Repeating_an_idempotency_key_replays_the_original_send`、`Reusing_..._conflicts`、`Lost_response_...`、`Reconciler_completes_...` |
| 3 | 同步异常后仍推进游标 | `Core/SyncService.cs`（暂时/永久分类）、`message_skip` 表 | `Encrypted_message_is_recorded_as_skipped_...`、`Transient_backend_failure_does_not_advance_the_cursor` |
| 4 | 事件仅内存投递、无重放 | `Core/EventBus.cs`、`OutboxDispatcher`、`MilkyEndpoints`（consumer/cursor/Last-Event-ID）、`event_cursor` 表 | `Events_are_not_retired_when_no_consumer_is_connected`、`Consumer_replays_events_missed_while_disconnected`、`Health_reports_pending_...` |
| 5 | 缺少 received/confirm 与出站信令 | `CallService.AcceptAsync/ConfirmAsync/ApplyAsync`、`IMediaEngine` 出站回调、媒体 WS 脱耦合 | `Outgoing_call_confirms_the_key_and_reaches_media_ready` |
| 6 | 发起呼叫幂等不可恢复 | `call_operation` 表、`CallService.StartAsync`（先落随机 ID/DH 材料/阶段） | `Call_start_is_idempotent_and_rejects_a_different_payload` |
| 7 | 重启清理与挂断未知结果 | `call.instance_id`、`CallService.ReconcileAsync`、`EndAsync` 未知结果保留 | `Restart_releases_calls_left_by_a_previous_process`、`Hangup_with_unknown_backend_result_is_not_reported_as_released` |
| 8 | 资料缓存未按账号隔离 | `SessionContext.Scope`、`ProfileCache` 作用域、`SessionManager.RestoreAsync` 按域名选账号 | `CacheScopeTests`（作用域构成、跨账号/跨代际不共享、失效、过期） |
| 9 | 呼叫事件 ID 冲突 | `CallService.BuildEvent`（类型 + 状态/媒体版本）、状态与事件同事务 | `Call_state_and_media_events_have_distinct_stable_ids` |

### 本轮明确未完成

**真实双向语音**。`IMediaEngine` 的接入边界已按真实引擎需要补齐（协议提案、`get_call_config`、端点列表、出站信令、g_a/g_b 校验、指纹校验、会话句柄释放），但本机无法构建 tgcalls：仓库中的引擎树仅提供 Bazel + WebRTC 的 Linux/macOS/Android 目标（`TMessagesProj/jni/tgcalls/Dockerfile` 为 Ubuntu + Bazel），本机无 Docker、无 WSL、无 Linux 工具链；即便构建成功，也没有真实后端与 Android 客户端做双向音频验收。因此 `calls.voice=false` 保持不变，`GetDhConfig`/协议协商只到控制面。

其余外部验收（真实后端联调、NoneBot 互操作、真机通话）同样未执行，原因是没有可用环境，不是实现取舍。

---

## 首轮实现记录

证据分级：**设计**（文档/接口定义） < **源码**（已实现但未运行） < **宿主测试**（本机自动化测试） < **真实服务**（对接真实 Nyarugram 后端） < **设备测试**（真实客户端/手机）。

## 总体结论

| 能力 | 设计 | 源码 | 宿主测试 | 真实服务 | 设备 |
|---|---|---|---|---|---|
| 登录与会话（密码 + 验证器） | 是 | 是 | 是（假后端） | 否 | — |
| 会话加密持久化与代际隔离 | 是 | 是 | 是 | 否 | — |
| 差分同步与游标推进 | 是 | 是 | 是 | 否 | — |
| 事件投递（SSE / WebSocket，含重放） | 是 | 是 | 是（SSE，含断线重放） | 否 | — |
| 账号/好友/群/成员查询 | 是 | 是 | 是 | 否 | — |
| 私聊/群聊文本、提及、回复 | 是 | 是 | 是 | 否 | — |
| 消息查询、历史、已读 | 是 | 是 | 是 | 否 | — |
| 呼叫控制（发起/接听/确认/挂断/查询/重启对账） | 是 | 是 | 是 | 否 | — |
| 呼叫扩展事件 | 是 | 是 | 部分 | 否 | — |
| 真实双向语音 | 是 | **否** | 否 | 否 | 否 |
| 视频通话 | 否 | 否 | — | — | — |

"宿主测试：是（假后端）"表示使用本仓库的进程内 Protobuf 假后端与真实 PostgreSQL 验证；**不代表已对接真实 Nyarugram 后端**。

## 已实现（有代码与测试证据）

1. **契约固定**：`protocol/botgram_rpc.proto`（SHA-256 `0d2d4a4c…c1be6`）与 `config/milky-1.3.0.openapi.json`（SHA-256 `8ccb6118…27614`）入库锁定，启动时校验 schema 哈希。
2. **认证面**：`/api/v1/auth` 登录、`/api/v1/account` 会话校验与退出；Bearer 令牌 AES-256-GCM 加密落库；密码不落盘。
3. **代际隔离**：每次登录递增 generation，所有请求捕获上下文，旧代际的结果在返回前被丢弃。
4. **同步**：`get_difference` 驱动，WebSocket 提示仅作唤醒；消息投影、待投递事件、游标在同一 PostgreSQL 事务提交；游标只增不减；`too_long` 触发重基线。
5. **投递**：outbox + 稳定 `event_id`，至少一次语义；慢消费者有界队列并断开；无消费者超宽限期后回收。
6. **Milky 子集**：14 个标准 API + 6 个扩展 API，详见 `Docs/SUPPORT-MATRIX.md`。
7. **身份映射**：`tl_user_id` / `tl_chat_id` 作为 uin / group_id，按服务器+账号隔离持久化，映射冲突直接报错。
8. **发送幂等**：随机 ID 复用重试（最多 3 次），结果仍未知时返回明确错误而非新建 ID 重发。
9. **呼叫控制**：DH 协商、`request_call`/`accept_call`/`discard_call`、状态机（带版本号 CAS）、终态幂等、启动时把旧代际未完成呼叫标记为失败。
10. **媒体契约**：16 字节帧头 + PCM S16LE 48 kHz/20 ms 的 WebSocket 端点，令牌绑定呼叫与账号。

## 未实现 / 未验证

| 项 | 状态 | 说明 |
|---|---|---|
| 真实 tgcalls 兼容引擎 | 未实现 | `IMediaEngine` 只有 `Unavailable` 与 `Loopback` 两种实现。真实语音不可用，`calls.voice=false` |
| 视频通话 | 未实现 | 参数在发起前被拒绝 |
| 图片/文件/语音/贴纸/转发/群管理 | 未实现 | 出站返回 `-501`，入站不产生这些段 |
| `mention` 入站反向解析 | 未实现 | 后端只给 `@name` 文本，当前按纯文本保留 |
| 群成员角色/禁言/等级 | 未实现 | 后端 `get_full_group` 不提供角色 |
| 撤回/置顶/已读回执事件 | 未实现 | 超出首期范围 |
| 真实后端联调 | 未执行 | 本机无可用的 Nyarugram 后端与专用账号 |
| 与 NoneBot 等应用端互操作 | 未执行 | 未安装 Milky 应用端 |
| 真实手机双向音频 | 未执行 | 无接入引擎，也无设备 |

## 测试证据

`dotnet test`：23 项全部通过（0 失败、0 跳过），覆盖：

- PCM 帧构造/解析/错误路径、DH 双方共享密钥一致、指纹算法、消息段组装与拒绝路径。
- 端到端：真实 PostgreSQL + 进程内 Protobuf 假后端 + 真实 HTTP 端点。
  - `get_login_info` 返回后端 uin 与昵称。
  - `get_impl_info` 声明 Milky 1.3。
  - 能力声明中 `voice=false`、引擎 `loopback`。
  - 好友列表映射到数字身份；群列表与成员来自 `get_dialogs` + `get_full_group`。
  - 发送私聊返回后端消息序号；不支持的消息段返回 `-501`。
  - 未知接口 HTTP 404；缺令牌 HTTP 401。
  - 注入一条来信后，SSE 收到完整 `message_receive` 事件（场景、对端、发送者、文本段）。
  - 呼叫发起→挂断→重复挂断保持终态。

进程级验证：`serve` 启动后 `/health` 返回 200（未登录为 `degraded`，含 outbox 计数、消费者与实例标识），`get_impl_info`、`nyarugram_get_capabilities` 正常响应，无令牌请求返回 401。

第二轮执行命令与结果：

```
dotnet build Nyarugram.Milky.sln          # 0 error, 0 warning
dotnet test tests/Nyarugram.Milky.Tests   # 45 passed, 0 failed, 0 skipped
```

环境：Windows 10.0.26100，.NET SDK 8.0.204/9.0.320，PostgreSQL 16.2（隔离集群，端口 56411，数据库 `nyarugram_milky_test` 由测试夹具重建）。数据库不可达时用例会 `Skip`，本轮没有跳过。

## 下一步建议

1. 在有真实后端的隔离环境完成登录、同步、收发、重启恢复联调。
2. 接入 NoneBot Milky 适配器，验证事件与接口的真实解析行为。
3. 媒体引擎选型：与 Android `VoIPService` 使用的引擎版本对齐后通过 `IMediaEngine` 接入；接入前不要打开 `calls.voice`。
4. 补齐撤回、编辑、置顶等后端已具备的操作。
5. 增加 WebHook 投递与事件重放游标（当前 Milky 无游标参数，重连期间的实时事件不重放）。
