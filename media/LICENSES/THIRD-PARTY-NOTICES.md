# Native media agent third-party notices

`milky_media_agent` is built from the pinned upstream trees recorded in
`media/upstream.json`. The build is reproducible with
`media/prepare-source.sh` and `media/build-agent.sh`; the complete source for
every component below is obtained from the recorded commit, and the agent can
be relinked against a modified copy of any component by rerunning the build
script with the modified prepared tree.

| Component | Upstream | Pinned version | License | Compiled/linked |
| --- | --- | --- | --- | --- |
| tgcalls | https://github.com/TelegramMessenger/tgcalls | `1398c1648f52666b3249111ec3c5b531946e41e4` | LGPL-3.0 | yes (static) |
| WebRTC (`TMessagesProj/jni/voip/webrtc`) | Botgram Android tree / WebRTC | Botgram `2fab5dcf4a574c0aae7a60ed3fb1e959e2062cb5` | BSD-3-Clause | yes (static) |
| BoringSSL | https://boringssl.googlesource.com/boringssl | `2b44a3701a4788e1ef866ddc7f143060a3d196c9` | OpenSSL/ISC mixed (see LICENSE) | yes (static libssl/libcrypto) |
| Abseil | https://github.com/abseil/abseil-cpp | `54fac219c4ef0bc379dfffb0b8098725d77ac81b` | Apache-2.0 | yes (static) |
| libopus | https://github.com/xiph/opus | `22244de5a79bd1d6d623c32e72bf1954b56235be` | BSD-3-Clause | yes (static) |
| libyuv | https://chromium.googlesource.com/libyuv/libyuv | `28ce69c2744a6aafdb58564e7b884aec3f66be5f` | BSD-3-Clause | yes (static) |
| libvpx | https://github.com/webmproject/libvpx | `1024874c5919305883187e2953de8fcb4c3d7fa6` | BSD-3-Clause | yes (static) |
| libsrtp | Botgram `jni/third_party/libsrtp` | Botgram `2fab5dcf4a574c0aae7a60ed3fb1e959e2062cb5` | BSD-3-Clause | yes (static) |
| usrsctp | Botgram `jni/third_party/usrsctplib` | Botgram `2fab5dcf4a574c0aae7a60ed3fb1e959e2062cb5` | BSD-3-Clause | yes (static) |
| rnnoise (WebRTC copy) | `voip/rnnoise` | Botgram `2fab5dcf4a574c0aae7a60ed3fb1e959e2062cb5` | BSD-3-Clause | yes (static) |
| rnnoise (tgcalls copy) | `third_party/rnnoise` | Botgram `2fab5dcf4a574c0aae7a60ed3fb1e959e2062cb5` | BSD-3-Clause | yes (static) |
| PFFFT | Botgram `jni/third_party/pffft` | Botgram `2fab5dcf4a574c0aae7a60ed3fb1e959e2062cb5` | FFTPACK/BSD-style (see LICENSE) | yes (static) |
| OpenH264 headers | https://github.com/cisco/openh264 | `652bdb7719f30b52b08e506645a7322ff1b2cc6f` | BSD-3-Clause | headers only |
| json11 | bundled in tgcalls `third-party/json11.cpp` | tgcalls commit above | MIT | yes |
| zlib | Ubuntu 24.04 `zlib1g-dev` | distro version | zlib | dynamic |
| libevent | Ubuntu 24.04 `libevent-dev` | distro version | BSD-3-Clause | dynamic |
| GNU C++ runtime / pthread / libdl / librt | Ubuntu 24.04 toolchain | distro version | GPL-3.0-with-GCC-exception / LGPL | dynamic |

The OpenSSL-derived `openssl/aes_ige.c` copied by the Android tree is not part
of the compiled source list; BoringSSL supplies the crypto symbols linked into
the agent.

License texts are in `media/LICENSES/`. tgcalls is LGPL-3.0: the agent is
statically linked, and the unmodified upstream source plus the build scripts
are provided so the library can be replaced and the agent relinked. No
modification of tgcalls itself is included in `patches/0001-linux-build.patch`
except the ICE timeout constant and Linux build adaptations to WebRTC files.
