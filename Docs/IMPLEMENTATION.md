# 基础能力与实现契约

本文定义首期实现范围和验收方式。当前为文档基线，所有支持状态需要在实现后根据测试证据更新。

## 架构和协议边界

服务由 Milky 入口、Nyarugram Protobuf 客户端、同步与投递、PostgreSQL 持久化、通话控制和媒体模块组成。首期一个实例登录一个账号。

- Milky 入口提供 POST /api/{action} 和 /event 的事件连接，校验独立 access_token。
- /event 至少实现固定 Milky 版本要求的 SSE 和正向 WebSocket。实现 WebHook 时保持同版本事件及鉴权语义，支持失败重试和明确的投递边界。
- 后端认证使用 /api/v1/auth，账号管理使用 /api/v1/account，业务使用 /api/v1/rpc。事件连接使用 /api/ws 和 botgram.event-hint.v1.protobuf 子协议。
- 身份反查、通话状态和消息都通过 Protobuf 接口取得。禁止直接操作后端数据库表或调用已弃用 JSON API。
- 独立构建使用固定 proto 副本或固定版本协议包。记录其源提交、SHA-256、生成工具和运行依赖版本，执行兼容性检查后再升级。

Milky 的路径、响应和鉴权按锁定规范实现，业务错误及未支持接口保持明确可识别。[通信规范](https://milky.ntqqrev.org/guide/communication)

## Milky 基础子集

| 对外接口或事件 | Nyarugram Protobuf 来源 | 首期要求 |
|---|---|---|
| get_login_info | account_me（206）及账号身份 | 当前账号 ID 和昵称 |
| get_impl_info | 适配服务自身构建信息及协议锁文件 | 版本及兼容范围真实可查 |
| get_user_profile | get_users（111）、get_full_user（117） | 提供已有资料，未知字段按 schema 处理 |
| get_friend_list / get_friend_info | get_contacts（112）及用户资料 | 联系人映射、查询和权限 |
| get_group_list / get_group_info | get_dialogs（102）、get_groups（119）、get_full_group（118） | 当前账号可访问的群 |
| get_group_member_list / get_group_member_info | get_full_group（118）及关联用户 | 当前可取得的成员和角色 |
| send_private_message / send_group_message | send_message（107） | 文字、可表达的提及及回复 |
| get_message | get_messages（106） | 通过会话及消息映射验证访问权限 |
| get_history_messages | get_history（105） | 按 Milky 序号、上限和排序返回真实历史 |
| mark_message_as_read | read_history（108） | 单调推进该账号已读状态 |
| message_receive 事件 | get_difference（101）中的消息更新 | 转换为固定版本 IncomingMessage |
| 呼叫扩展及事件 | request_call 至 get_dh_config（128–136）及差分 | 具体契约见 CALLS.md |

上述编号用于定位 2026-11-08 核对的源码。实现时核对实际 proto，字段含义以固定协议副本为准。

新增的编辑、撤回、转发等能力，根据当前后端真实实现增补支持矩阵。首期可用消息能力必须通过实际收发和重启恢复验收。

Milky 的 API 字段和消息段结构从对应版本定义生成或严格校验。[系统 API](https://milky.ntqqrev.org/api/system) · [消息 API](https://milky.ntqqrev.org/api/message)

## QQ 专属字段的兼容边界

Milky 的原始模型包含 uin、QQ 客户端类型、QQ 等级等字段。本项目需要建立明确的 Nyarugram 兼容配置。

- uin、user_id、group_id 映射到账号作用域内稳定的 Nyarugram 数字身份，文档说明其来源。
- QQ 等级、年龄、性别等无对应资料时，仅使用固定 schema 允许的空值或 unknown，不推断或编造用户资料。
- qq_protocol_type 等枚举没有真实 Nyarugram 对应值时，必须在实现前验证目标 SDK 的约束，记录兼容差异并提供必要的应用端适配。禁止自行填入非法枚举后宣称可直接兼容全部 Milky 应用。
- 兼容配置通过独立的 nyarugram_get_capabilities 扩展提供 platform、profile_version、支持接口/消息段、呼叫及媒体能力、实际后端协议版本。
- 若需要扩展 SDK 类型，随本项目提供明确的适配说明和真实互操作测试。基础子集兼容声明限定到已验证的版本及应用端。

## 身份与消息标识

持久映射的作用域包含服务器身份和账号 UUID。用户名、昵称和 Gram ID 变更不能改变映射。

Milky 使用 message_scene、peer_id、message_seq 共同定位消息。首期 friend 对应普通私聊，group 对应群聊，temp 只有在后端有对应语义时才声明支持。

- 用户及群使用已有稳定 TL 身份，或在确有需要时分配并持久化数字别名。不能对 UUID 做无碰撞保证的截断哈希。
- 首期 message_seq 可以复用后端稳定消息 ID，始终与会话作用域共同查询，允许序号缺口。若目标应用要求原生会话内序号，需通过 typed 字段取得或建立经过历史回填验证的持久映射。
- 相同消息通过实时更新、历史、回复、重放和重启后取得时，必须得到同一标识。
- 正确处理 int64、时间单位和文本实体的 UTF-16 偏移。禁止通过浮点转换损失身份或随机 ID 精度。
- 群消息发送者保留其用户身份，禁止把群 ID 当作发送者 ID。

## 登录、凭据和配置

实现交互登录、会话状态、退出及重新登录流程。密码和验证器输入使用受保护的交互方式，成功后保存加密会话令牌，运行中避免持续保存账号密码。

| 必需配置 | 要求 |
|---|---|
| Nyarugram 服务器地址 | 部署者显式填写，测试示例可用 https://192.168.1.20:8521 |
| 协议身份域名 | 部署者显式填写，与实际后端身份一致 |
| PostgreSQL 连接 | 独立数据库或独立 schema，固定迁移版本 |
| 凭据保护密钥 | 数据库之外的受限配置或系统设施 |
| Milky 监听与 access_token | 显式监听范围和独立随机令牌 |
| 事件投递与容量 | 消费者、队列上限、重试、保留和过期策略 |
| 呼叫及媒体 | 默认自动接听策略、协商能力、媒体端点权限和资源限制 |

服务器证书必须通过信任链及主机名或 IP SAN 验证。示例中的地址和域名需要部署者替换，不提供默认公共服务。

## 同步、投递和幂等

1. 首次绑定明确初始化同步起点，区分历史补载和新消息事件。记录初始化期间到达的新更新。
2. WebSocket 提示只作为唤醒信号，以 get_difference 的实际差分为同步依据，定期补偿遗漏唤醒。
3. 在 PostgreSQL 事务中保存消息及呼叫映射、待投递事件和后续同步水位，提交后才向应用发送事件。
4. 事件提供稳定的 nyarugram 扩展事件 ID。断线后可能重复投递，应用可以去重。SSE、WebSocket 和 WebHook 的重放及确认约定分别写清。当前实现：SSE/WebSocket 支持 `nyarugram_consumer` 持久位点与 `Last-Event-ID` 重放；无消费者时事件保持 pending，超过保留窗口转 expired 并计入健康检查；WebHook 仍未实现。
5. 有界队列处理慢消费者，达到容量后明确背压或断开并记录可恢复位置，避免静默丢消息。
6. 发送前持久化操作记录和随机 ID。网络结果未知时复用同一操作对账，不能新建随机 ID 重发。
7. 为应用提供可选的幂等请求扩展键。缺少该键时准确记录 HTTP 响应丢失后的边界，不把两次相同文字强行合并。
8. 后端响应的原消息 pts、当前同步 pts、事件游标分别使用，避免旧消息重试推进同步水位。
9. 在线状态由显式前台心跳维护：`serve` 期间每 30 秒发送一次带 `foreground=true` 的 `get_state`，账号对其他客户端显示在线；差分轮询不带该标志，不延长在线窗口，也不影响消息同步与投递。

## 加密聊天和未支持能力

首期普通消息路径不自动加入端到端加密。遇到密文时识别其类型，遵守未开启方占位语义，不向应用伪造解密结果。

若后续提供加密消息能力，需要实现独立账号密钥库、PIN 策略、成熟协议实现及客户端身份确认，按单独能力验收。现有单方旧消息永久不可恢复的产品语义继续生效。

图片、文件、语音消息、贴纸、复杂按钮及群管理等超出首期子集的请求，明确返回未支持，保持能力矩阵准确。真实电话音频通过 CALLS.md 定义的媒体模块提供。

## 实施与验收顺序

| 阶段 | 必须交付的结果 |
|---|---|
| 契约固定 | Milky schema、后端 proto、兼容配置及支持矩阵 |
| 媒体技术验证 | 与现有 Android 引擎完成一对一最小双向音频互通 |
| 基础服务 | 初始化、登录、会话恢复、PostgreSQL 迁移、鉴权和就绪检查 |
| 消息可用 | 私聊/群聊收发、查询、已读、原生事件及真实 Milky 应用互操作 |
| 通话可用 | 呼叫扩展、状态恢复、双向媒体及拒接后双方重拨 |
| 完整交付 | 独立构建与运行说明、可执行产物、实际测试证据和明确限制 |

每阶段保持可验证结果，并持续完成全部首期范围。测试至少包含无权限、账号切换、会话失效、重启、重复差分、发送结果未知、慢消费者和 PostgreSQL 不可达。

当前文档不代表上述阶段已经完成。交付报告逐项记录实现、测试及实际部署状态。
