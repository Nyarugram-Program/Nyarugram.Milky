# 原生媒体代理构建与运行（Ubuntu 24.04 / linux-x64）

本文说明如何从固定上游源码重建 `milky_media_agent`。构建输入、补丁和脚本全部在本仓库 `media/` 下；发布包只携带构建产物和许可证材料。

## 固定输入

| 组件 | 来源 | 固定版本 |
|---|---|---|
| Botgram Android `TMessagesProj/jni/voip`、`openssl`、跟踪的 `third_party` | Botgram 仓库 | commit `2fab5dcf4a574c0aae7a60ed3fb1e959e2062cb5` |
| tgcalls | TelegramMessenger/tgcalls | commit `1398c1648f52666b3249111ec3c5b531946e41e4` |
| BoringSSL | google/boringssl | `2b44a3701a4788e1ef866ddc7f143060a3d196c9` |
| abseil-cpp | abseil/abseil-cpp | `54fac219c4ef0bc379dfffb0b8098725d77ac81b` |
| libopus | xiph/opus | `22244de5a79bd1d6d623c32e72bf1954b56235be` |
| libyuv | libyuv/libyuv | `28ce69c2744a6aafdb58564e7b884aec3f66be5f` |
| libvpx | webmproject/libvpx | `1024874c5919305883187e2953de8fcb4c3d7fa6`（v1.16.0） |
| OpenH264 | cisco/openh264 | `652bdb7719f30b52b08e506645a7322ff1b2cc6f`（仅头文件） |

完整清单、许可证和目录映射见 `media/upstream.json` 与 `media/LICENSES/THIRD-PARTY-NOTICES.md`。`media/sources.txt` 是实际编译的 1,266 个源文件清单；`media/agent/` 是适配层源码，`media/patches/0001-linux-build.patch` 是 Linux 适配补丁。

## 准备

Ubuntu 24.04（WSL2 或原生）安装：

```bash
sudo apt-get update
sudo apt-get install -y build-essential cmake ninja-build git python3 zlib1g-dev libevent-dev
```

准备源码。`--botgram` 必须是包含固定 commit 的 git 仓库；脚本用 `git archive` 提取提交中的精确 blob，而不是读取工作区。`--native` 是按 `<组件>-<commit>` 命名的依赖目录，可放在 `D:\BotgramBuild\native`（WSL 路径 `/mnt/d/BotgramBuild/native`）。

```bash
./media/prepare-source.sh \
  --botgram /mnt/w/Work/Botgram \
  --native  /mnt/d/BotgramBuild/native \
  --dest    /mnt/d/BotgramBuild/milky-media-clean
```

脚本会：

1. 校验 Botgram commit 存在，且每个依赖目录与固定 commit 对应；
2. 只复制代理编译/包含所需的子集（不依赖同级 Android 工作区的构建产物）；
3. 应用 `patches/0001-linux-build.patch`，任一 hunk 失败即退出；
4. 对补丁前后两棵树分别计算 SHA-256 清单并与 `media/upstream.json` 比对，不一致即拒绝继续。

首次记录新上游时才使用 `--allow-unverified`，随后必须把输出的两个哈希写回 `upstream.json`。

## 构建

```bash
./media/build-agent.sh --work /mnt/d/BotgramBuild/milky-media-clean --jobs 8
```

步骤：

1. 若 `<work>/out/vpx/lib/libvpx.a` 不存在，在 `<work>/src/third_party/libvpx` 内按固定参数构建静态 libvpx：`--target=x86_64-linux-gcc --disable-examples --disable-tools --disable-docs --disable-unit-tests --disable-webm-io --enable-vp8 --enable-vp9 --disable-shared --enable-static`；
2. CMake Release 构建 `milky_media_agent`，使用 `-DMILKY_MEDIA_SRC=<work>/src -DMILKY_MEDIA_OUT=<work>/out`；
3. 产物复制到 `<work>/artifacts/milky_media_agent` 并打印 SHA-256；
4. 运行 `milky_media_agent --selftest-p2p 5`（两个真实引擎实例、回环 P2P、440 Hz 输入，失败即退出）。

构建不读取 `W:\Work\Botgram`、个人绝对路径或既有 `obj/`；所有新下载/缓存/输出都在 `--work` 指定的 D 盘目录。不同机器的工具链与库版本可能产生不同二进制哈希，报告需同时记录工具链、输入树哈希和产物哈希。

## 干净构建记录（2026-11-08）

- 源目录：`D:\BotgramBuild\milky-media-build2`（首次准备时使用 `--allow-unverified` 记录，随后从该目录重新运行 `prepare-source.sh` 验证通过）。
- 工具链：Ubuntu 24.04.3 LTS x86_64、GCC/G++ 13.3.0、GNU ld 2.42、CMake 3.28.3、Ninja 1.11.1。
- 树哈希：补丁前 `4214a4470c48d507a012992c6dbd73c10ff0f7b80e2b9600c9a66b186208b481`；补丁后 `d5608efea5c38425261164defff873db7d508b1397a4a78ab7063018eeff88e8`。
- 产物：`artifacts/milky_media_agent`，35,414,216 字节，SHA-256 `dc0cf5d6fa6547eb7add222ea93d998e0d24f40655b84142baaad52ad6d872fa`。
- 自测：`{"ok":true,"established":true,"caller_frames":504,"callee_frames":502,"caller_rms":726.43,"callee_rms":634.65}`。
- 发布包：`D:\BotgramBuild\milky-release\nyarugram-milky-0.1.0-linux-x64`；第九轮重新打包后归档 `nyarugram-milky-0.1.0-linux-x64.tar.gz`（16,136,532 字节）SHA-256 `f1b159b0cb22fe4296aa9bbd3eb2a514c8de76dcb3e550552e19af7372c7c149`，包内 43 项校验值通过，包内代理再次通过自测。

不同机器、不同 GCC/binutils 或 libc 版本可能产生不同二进制哈希；复现时应记录工具链、树哈希、libvpx 配置参数与产物哈希，而不能只比较二进制哈希。

## 运行

代理由适配服务按通话启动，不需要常驻：

```json
{
  "media": {
    "engine": "tgcalls",
    "agent_command": "/opt/nyarugram-milky/media/milky_media_agent --agent",
    "agent_startup_seconds": 20
  }
}
```

- 代理绑定 `127.0.0.1:0` 并打印 `READY <port>`；适配服务用回环 TCP 连接，随后发送 JSON `start`、`signaling`、`stop` 控制帧和 PCM 帧。
- 每个通话一个进程；代理在控制连接断开后退出。控制连接断开/进程退出会被适配服务识别为运行期媒体故障。
- 无参数运行代理会列出编译进二进制的 tgcalls 协议版本；`--selftest-p2p <秒>` 只用于构建验收，不接后端。

运行依赖：Ubuntu 24.04 的 `libstdc++6`、`libgcc-s1`、`libevent-2.1-7t64`、`zlib1g`；其余库静态链接。发布包 `DEPENDENCIES.txt` 记录实际动态依赖。

## 许可证与源码获取

- tgcalls 为 LGPL-3.0；代理静态链接该库，`media/prepare-source.sh` 和 `media/build-agent.sh` 提供替换库并重新链接的完整方法。
- WebRTC、BoringSSL、opus、libyuv、libvpx、libsrtp、usrsctp、rnnoise、PFFFT、OpenH264 头文件为 BSD/Apache/ISC 类许可证，文本在 `media/LICENSES/`。
- 多数组件可从上游公开仓库按固定 commit 获取；Botgram 子树来自部署方的 Botgram 仓库同一 commit。发布包附带本目录、`upstream.json`、补丁和许可证文本，满足重新获取对应源码的需要。
