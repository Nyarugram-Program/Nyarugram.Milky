#!/usr/bin/env bash
# Prepares the exact upstream source tree used to build milky_media_agent.
#
# Upstream identity is pinned in upstream.json:
#   - Botgram Android commit (voip, openssl and tracked third_party directories)
#   - tgcalls and dependency submodule commits (provided as plain checkouts
#     under --native, because a Botgram worktree only contains gitlinks)
#
# The tracked Botgram files are extracted from the git object database with
# `git archive`, so line endings and file content are exactly the committed
# blobs rather than whatever a Windows checkout happens to contain. Only the
# directories the Linux agent compiles or includes are copied. The script then
# applies patches/0001-linux-build.patch and refuses to continue on any failure.
#
# Both the pre-patch and patched trees are verified against the SHA-256
# manifests in upstream.json, so a modified upstream checkout cannot silently
# become the build input.
#
# Usage:
#   ./media/prepare-source.sh \
#       --botgram /mnt/w/Work/Botgram \
#       --native  /mnt/d/BotgramBuild/native \
#       --dest    /mnt/d/BotgramBuild/milky-media-clean
#
# Add --allow-unverified only when intentionally recording a new upstream tree.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BOTGRAM_COMMIT="2fab5dcf4a574c0aae7a60ed3fb1e959e2062cb5"
TGCALLS_COMMIT="1398c1648f52666b3249111ec3c5b531946e41e4"
BORINGSSL_COMMIT="2b44a3701a4788e1ef866ddc7f143060a3d196c9"
ABSL_COMMIT="54fac219c4ef0bc379dfffb0b8098725d77ac81b"
OPUS_COMMIT="22244de5a79bd1d6d623c32e72bf1954b56235be"
LIBYUV_COMMIT="28ce69c2744a6aafdb58564e7b884aec3f66be5f"
LIBVPX_COMMIT="1024874c5919305883187e2953de8fcb4c3d7fa6"
OPENH264_COMMIT="652bdb7719f30b52b08e506645a7322ff1b2cc6f"

BOTGRAM=""
NATIVE=""
DEST=""
ALLOW_UNVERIFIED=0

usage() {
    sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
}

fail() {
    echo "prepare-source: $*" >&2
    exit 1
}

while [ $# -gt 0 ]; do
    case "$1" in
        --botgram) BOTGRAM="${2:-}"; shift 2 ;;
        --native) NATIVE="${2:-}"; shift 2 ;;
        --dest) DEST="${2:-}"; shift 2 ;;
        --allow-unverified) ALLOW_UNVERIFIED=1; shift ;;
        -h|--help) usage ;;
        *) fail "unknown argument: $1" ;;
    esac
done

[ -n "$BOTGRAM" ] || fail "--botgram is required"
[ -n "$NATIVE" ] || fail "--native is required"
[ -n "$DEST" ] || fail "--dest is required"
[ -d "$BOTGRAM" ] || fail "Botgram checkout not found: $BOTGRAM"
[ -d "$NATIVE" ] || fail "native dependency directory not found: $NATIVE"
[ -e "$DEST" ] && fail "destination already exists; use a fresh directory: $DEST"

# ---------------------------------------------------------------- upstream identity

git -C "$BOTGRAM" rev-parse --git-dir >/dev/null 2>&1 \
    || fail "$BOTGRAM is not a git checkout; the pinned commit cannot be verified"
git -C "$BOTGRAM" cat-file -e "$BOTGRAM_COMMIT^{commit}" 2>/dev/null \
    || fail "Botgram commit $BOTGRAM_COMMIT is not present in $BOTGRAM"

require_dir() {
    [ -d "$1" ] || fail "missing upstream directory: $1"
}
require_file() {
    [ -f "$1" ] || fail "missing upstream file: $1"
}

component() {
    local name="$1" commit="$2" dir="$NATIVE/$1-$2"
    require_dir "$dir"
    if git -C "$dir" rev-parse --git-dir >/dev/null 2>&1; then
        local actual
        actual="$(git -C "$dir" rev-parse HEAD)"
        [ "$actual" = "$commit" ] || fail "$name commit is $actual, expected $commit"
    fi
    echo "$dir"
}

TGCALLS_DIR="$(component tgcalls "$TGCALLS_COMMIT")"
BORINGSSL_DIR="$(component boringssl "$BORINGSSL_COMMIT")"
ABSL_DIR="$(component abseil-cpp "$ABSL_COMMIT")"
OPUS_DIR="$(component opus "$OPUS_COMMIT")"
LIBYUV_DIR="$(component libyuv "$LIBYUV_COMMIT")"
LIBVPX_DIR="$(component libvpx "$LIBVPX_COMMIT")"
OPENH264_DIR="$(component openh264 "$OPENH264_COMMIT")"

require_file "$TGCALLS_DIR/tgcalls/v2/InstanceV2Impl.cpp"
require_file "$BORINGSSL_DIR/CMakeLists.txt"
require_file "$ABSL_DIR/CMakeLists.txt"
require_file "$OPUS_DIR/CMakeLists.txt"
require_file "$LIBYUV_DIR/CMakeLists.txt"
require_file "$LIBVPX_DIR/configure"
require_file "$OPENH264_DIR/codec/api/wels/codec_api.h"

# ---------------------------------------------------------------------- copy tree

copy_tree() {
    cp -a "$1" "$2" || fail "copy failed: $1 -> $2"
}

mkdir -p "$DEST/src/third_party/xiph" "$DEST/src/third_party/openh264/codec"
ARCHIVE="$DEST/.botgram-archive"
mkdir -p "$ARCHIVE"

git -C "$BOTGRAM" archive --format=tar "$BOTGRAM_COMMIT" \
    TMessagesProj/jni/voip \
    TMessagesProj/jni/openssl \
    TMessagesProj/jni/third_party/libsrtp \
    TMessagesProj/jni/third_party/usrsctplib \
    TMessagesProj/jni/third_party/rnnoise \
    TMessagesProj/jni/third_party/pffft \
    | tar -x -C "$ARCHIVE" \
    || fail "git archive of Botgram $BOTGRAM_COMMIT failed"

copy_tree "$ARCHIVE/TMessagesProj/jni/voip" "$DEST/src/voip"
copy_tree "$ARCHIVE/TMessagesProj/jni/openssl" "$DEST/src/openssl"
for dir in libsrtp usrsctplib rnnoise pffft; do
    copy_tree "$ARCHIVE/TMessagesProj/jni/third_party/$dir" "$DEST/src/third_party/$dir"
done
rm -rf "$ARCHIVE"

copy_tree "$TGCALLS_DIR" "$DEST/src/tgcalls"
copy_tree "$BORINGSSL_DIR" "$DEST/src/third_party/boringssl"
copy_tree "$ABSL_DIR" "$DEST/src/third_party/absl"
copy_tree "$OPUS_DIR" "$DEST/src/third_party/xiph/opus"
copy_tree "$LIBYUV_DIR" "$DEST/src/third_party/libyuv"
copy_tree "$LIBVPX_DIR" "$DEST/src/third_party/libvpx"
copy_tree "$OPENH264_DIR/codec/api" "$DEST/src/third_party/openh264/codec/api"

remove_vcs() {
    find "$DEST/src" -type d \( -name .git -o -name .github -o -name .gitlab \) \
        -prune -exec rm -rf {} + 2>/dev/null || true
}
remove_vcs

tree_hash() {
    local root="$1"
    ( cd "$root" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum ) \
        | sha256sum | awk '{print $1}'
}

pre_patch_hash="$(tree_hash "$DEST/src")"

# ------------------------------------------------------------------------- patch

if ! ( cd "$DEST/src" && patch --batch --forward -p1 --no-backup-if-mismatch \
        < "$HERE/patches/0001-linux-build.patch" >/dev/null ); then
    fail "Linux build patch did not apply cleanly"
fi

patched_hash="$(tree_hash "$DEST/src")"

# ------------------------------------------------------------------------ verify

expected_pre="$(grep -o '"prepared_tree_sha256": "[0-9a-f]*"' "$HERE/upstream.json" | cut -d'"' -f4 || true)"
expected_patched="$(grep -o '"patched_tree_sha256": "[0-9a-f]*"' "$HERE/upstream.json" | cut -d'"' -f4 || true)"

if [ "$ALLOW_UNVERIFIED" -eq 1 ]; then
    echo "prepare-source: recording hashes (unverified mode)"
elif [ -n "$expected_pre" ] && [ -n "$expected_patched" ]; then
    [ "$pre_patch_hash" = "$expected_pre" ] \
        || fail "pre-patch source hash mismatch (got $pre_patch_hash, expected $expected_pre); upstream differs from the pinned tree"
    [ "$patched_hash" = "$expected_patched" ] \
        || fail "patched source hash mismatch (got $patched_hash, expected $expected_patched)"
    echo "prepare-source: source tree verified against upstream.json"
else
    fail "upstream.json has no recorded hashes; run once with --allow-unverified, then commit the values"
fi

cat > "$DEST/prepare-state.json" <<EOF
{
  "botgram_commit": "$BOTGRAM_COMMIT",
  "tgcalls_commit": "$TGCALLS_COMMIT",
  "prepared_tree_sha256": "$pre_patch_hash",
  "patched_tree_sha256": "$patched_hash",
  "patch": "patches/0001-linux-build.patch"
}
EOF

echo "prepare-source: prepared $DEST/src"
echo "prepare-source: pre-patch  sha256 $pre_patch_hash"
echo "prepare-source: patched    sha256 $patched_hash"
