#!/usr/bin/env bash
# Build only already-approved, pinned sources. Never downloads sources or modifies reference/src.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
prefix=${PICTURESHARP_HEIF_TOOLS:-"$root/reference/tools"}
[[ "$prefix" = /* ]] || { echo 'PICTURESHARP_HEIF_TOOLS must be absolute' >&2; exit 2; }
build="$root/reference/build"
jobs=${HEIF_BUILD_JOBS:-4}
mkdir -p "$build" "$prefix"
while read -r name revision repository; do
  [[ -z "$name" || "$name" == \#* ]] && continue
  source="$root/reference/src/$name"
  [[ -d "$source/.git" && $(git -C "$source" rev-parse HEAD) == "$revision" ]] || {
    echo "Missing pinned source: $name. Run fetch-heif-reference.sh after approval." >&2; exit 1;
  }
  [[ -z $(git -C "$source" status --porcelain) ]] || { echo "Modified reference source: $name" >&2; exit 1; }
  # Some upstream build systems generate files inside the source directory. Give them a disposable copy.
  if [[ ! -d "$build/source-$name" ]]; then
    mkdir -p "$build/source-$name"
    git -C "$source" archive HEAD | tar -x -C "$build/source-$name"
  fi
done < "$root/scripts/heif-reference-versions.tsv"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
configure() {
  local name=$1 source=$2
  shift 2
  cmake -S "$source" -B "$build/$name" -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$prefix" \
    -DCMAKE_INSTALL_LIBDIR=lib -DCMAKE_PREFIX_PATH="$prefix" -DCMAKE_INSTALL_RPATH="$prefix/lib" \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5 "$@"
}
install_build() {
  cmake --build "$build/$1" --parallel "$jobs"
  cmake --install "$build/$1"
}
configure x265-10 "$build/source-x265/source" -DHIGH_BIT_DEPTH=ON -DEXPORT_C_API=OFF -DENABLE_CLI=OFF \
  -DENABLE_SHARED=OFF -DENABLE_LIBNUMA=OFF
cmake --build "$build/x265-10" --parallel "$jobs"
mkdir -p "$build/x265"
ln -sf "$build/x265-10/libx265.a" "$build/x265/libx265_main10.a"
configure x265 "$build/source-x265/source" -DEXTRA_LIB=x265_main10.a -DEXTRA_LINK_FLAGS="-L$build/x265" \
  -DLINKED_10BIT=ON -DENABLE_CLI=OFF -DENABLE_SHARED=ON -DENABLE_LIBNUMA=OFF
install_build x265
configure libde265 "$build/source-libde265" -DENABLE_SDL=OFF -DENABLE_DECODER=OFF -DENABLE_ENCODER=OFF
install_build libde265
configure kvazaar "$build/source-kvazaar" -DBUILD_TESTS=OFF -DGIT_SUBMODULE=OFF
install_build kvazaar
configure libheif-1.20.2 "$build/source-libheif-1.20.2" -DWITH_X265=ON -DWITH_KVAZAAR=ON -DWITH_LIBDE265=ON \
  -DENABLE_PLUGIN_LOADING=OFF -DWITH_EXAMPLES=ON -DBUILD_TESTING=OFF -DWITH_GDK_PIXBUF=OFF \
  -DWITH_AOM_DECODER=OFF -DWITH_AOM_ENCODER=OFF -DWITH_DAV1D=OFF -DWITH_LIBSHARPYUV=OFF
install_build libheif-1.20.2
for target in lib/TLibVideoIO lib/TLibCommon lib/TLibDecoder lib/TAppCommon app/TAppDecoder; do
  make -C "$build/source-HM/build/linux/$target" -j "$jobs" release \
    RELEASE_CPPFLAGS='-O3 -Wno-error' RELEASE_CFLAGS='-O3 -Wno-error'
done
install -m 755 "$build/source-HM/bin/TAppDecoderStatic" "$prefix/bin/TAppDecoder"
cp "$root/scripts/heif-reference-versions.tsv" "$prefix/versions.tsv"
"$prefix/bin/heif-enc" --list-encoders
"$prefix/bin/heif-info" --version
