set -euo pipefail

OUTPUT="${1:?Usage: bash build-wgpu-native.sh <output directory>}"
REVISION=33133da4ec5a0174cb21539ef2d3346f75200411
EXPECTED_EXPORTS=250
CARGO_ROOT="${CARGO_HOME:-$HOME/.cargo}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT INT TERM

git clone --quiet https://github.com/gfx-rs/wgpu-native.git "$WORK/wgpu-native"
cd "$WORK/wgpu-native"
git checkout --quiet "$REVISION"
git submodule update --init --quiet ffi/webgpu-headers

sed -i '/^# We want the wgpu-core GLES backend on Unix/,/^features = \["gles"\]/d' Cargo.toml
if grep -q 'cfg(all(unix, not(target_os = "ios"), not(target_os = "macos")))' Cargo.toml; then
    echo "The Unix OpenGL backend could not be removed from Cargo.toml" >&2
    exit 1
fi

if [ "$(grep -c '#\[cfg(gles)\]' src/conv.rs)" != "1" ]; then
    echo "src/conv.rs changed, the OpenGL report guard was not found exactly once" >&2
    exit 1
fi
sed -i 's/#\[cfg(gles)\]/#[cfg(any(feature = "angle", windows))]/' src/conv.rs

export RUSTFLAGS="--remap-path-prefix=$HOME=/home --remap-path-prefix=$CARGO_ROOT=/cargo --remap-path-prefix=$WORK=/build"
export BINDGEN_EXTRA_CLANG_ARGS="-I$(cc -print-file-name=include)"
cargo build --release --locked

LIBRARY=target/release/libwgpu_native.so
if strings "$LIBRARY" | grep -q 'libEGL'; then
    echo "The OpenGL backend is still linked into $LIBRARY" >&2
    exit 1
fi
if strings "$LIBRARY" | grep -qF "$HOME"; then
    echo "$LIBRARY still contains a local home path" >&2
    exit 1
fi

EXPORTS="$(nm -D --defined-only "$LIBRARY" | awk '$3 ~ /^wgpu/' | wc -l)"
if [ "$EXPORTS" != "$EXPECTED_EXPORTS" ]; then
    echo "$LIBRARY exports $EXPORTS functions, expected $EXPECTED_EXPORTS" >&2
    exit 1
fi

mkdir -p "$OUTPUT"
cp "$LIBRARY" "$OUTPUT/libwgpu_native.so"
echo "Built $OUTPUT/libwgpu_native.so from wgpu-native $REVISION without the OpenGL backend ($EXPORTS exports)"
