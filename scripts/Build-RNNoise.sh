#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
build="$root/artifacts/rnnoise-build"
mkdir -p "$build" "$root/artifacts/native"
if [ ! -d "$build/source/.git" ]; then
  git clone --depth 1 --branch v0.2 https://github.com/xiph/rnnoise.git "$build/source"
fi
cd "$build/source"
test "$(git rev-parse HEAD)" = 904a876dce1f9ab8860c0a5000ed151f9f6eef58
model=rnnoise_data-0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37.tar.gz
if [ ! -f "$model" ]; then
  curl --fail --location --retry 3 "https://media.xiph.org/rnnoise/models/$model" -o "$model"
fi
printf '%s  %s\n' b327d2fc5095be9ed66c5246a86b1a1ce180e9de875c4e5e8778f975560d1f035da40a8686dc1c3fd91c8e709be65d2638eccaa9f866b6f3d85f8d0d16bd2184 "$model" | sha512sum -c -
./autogen.sh
./configure --disable-static --enable-shared --disable-examples CFLAGS='-O2 -DWIN32' LDFLAGS='-static-libgcc'
make -j2
cp .libs/librnnoise-0.dll "$root/artifacts/native/voicebridge-rnnoise.dll"
objdump -p "$root/artifacts/native/voicebridge-rnnoise.dll" | sed -n '/DLL Name/p'
if objdump -p "$root/artifacts/native/voicebridge-rnnoise.dll" | grep -Ei 'DLL Name:.*(libgcc|libwinpthread|libstdc\+\+)'; then
  echo 'Unexpected compiler runtime dependency' >&2
  exit 1
fi
cp COPYING "$root/artifacts/native/RNNoise-COPYING.txt"
printf 'RNNoise v0.2 / 904a876dce1f9ab8860c0a5000ed151f9f6eef58\n' > "$root/artifacts/native/BUILD.txt"
