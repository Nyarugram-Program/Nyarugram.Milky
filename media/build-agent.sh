#!/usr/bin/env bash
# Builds the Release milky_media_agent from a prepared source tree.
#
# Usage (Ubuntu 24.04 / WSL2, from the repository root):
#   ./media/build-agent.sh --work /mnt/d/BotgramBuild/milky-media-clean [--jobs 8]
#
# The prepared tree must come from prepare-source.sh. libvpx is built first into
# <work>/out/vpx (the same static library the Android build uses), then CMake
# builds the agent. All generated files stay under the work directory on D:.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK=""
BUILD=""
JOBS="$(nproc 2>/dev/null || echo 4)"
SKIP_SELFTEST=0

fail() {
    echo "build-agent: $*" >&2
    exit 1
}

while [ $# -gt 0 ]; do
    case "$1" in
        --work) WORK="${2:-}"; shift 2 ;;
        --build) BUILD="${2:-}"; shift 2 ;;
        --jobs) JOBS="${2:-}"; shift 2 ;;
        --skip-selftest) SKIP_SELFTEST=1; shift ;;
        -h|--help) sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
        *) fail "unknown argument: $1" ;;
    esac
done

[ -n "$WORK" ] || fail "--work is required"
[ -d "$WORK/src" ] || fail "prepared source not found: $WORK/src (run prepare-source.sh first)"
[ -f "$WORK/src/tgcalls/tgcalls/v2/InstanceV2Impl.cpp" ] || fail "prepared source is incomplete"
[ -f "$WORK/prepare-state.json" ] || fail "prepare-state.json is missing; rerun prepare-source.sh"

VPX_NEEDED=0
[ -f "$WORK/out/vpx/lib/libvpx.a" ] || VPX_NEEDED=1

for tool in cmake make gcc g++; do
    command -v "$tool" >/dev/null 2>&1 || fail "required tool is missing: $tool"
done

if [ "$VPX_NEEDED" -eq 1 ]; then
    echo "build-agent: building libvpx (static, vp8+vp9, x86_64-linux-gcc)"
    (
        cd "$WORK/src/third_party/libvpx"
        ./configure \
            --target=x86_64-linux-gcc \
            --disable-examples --disable-tools --disable-docs --disable-unit-tests \
            --disable-webm-io --enable-vp8 --enable-vp9 \
            --disable-shared --enable-static \
            --prefix="$WORK/out/vpx"
        make -j"$JOBS"
        make install
    ) || fail "libvpx build failed"
fi

[ -f "$WORK/out/vpx/lib/libvpx.a" ] || fail "libvpx static library was not produced"

BUILD="${BUILD:-$WORK/build-release}"
GENERATOR=()
command -v ninja >/dev/null 2>&1 && GENERATOR=(-G Ninja)
cmake -S "$HERE" -B "$BUILD" \
    -DCMAKE_BUILD_TYPE=Release \
    -DMILKY_MEDIA_SRC="$WORK/src" \
    -DMILKY_MEDIA_OUT="$WORK/out" \
    "${GENERATOR[@]}"

cmake --build "$BUILD" --target milky_media_agent --parallel "$JOBS" \
    || fail "milky_media_agent build failed"

BIN="$BUILD/milky_media_agent"
[ -x "$BIN" ] || fail "agent binary not found at $BIN"

mkdir -p "$WORK/artifacts"
cp -f "$BIN" "$WORK/artifacts/milky_media_agent"
chmod +x "$WORK/artifacts/milky_media_agent"
sha256sum "$WORK/artifacts/milky_media_agent"

if [ "$SKIP_SELFTEST" -eq 0 ]; then
    echo "build-agent: running the in-process P2P audio self-test"
    "$WORK/artifacts/milky_media_agent" --selftest-p2p 5 \
        || fail "agent self-test failed"
fi

echo "build-agent: agent ready at $WORK/artifacts/milky_media_agent"
