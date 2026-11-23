#!/usr/bin/env bash
set -euo pipefail
src=$(cygpath -u "$CADRIVECT_NATIVE_SRC")
work=$(cygpath -u "$CADRIVECT_NATIVE_BUILD")
cd "$src"
packages=()
while read -r digest file; do
    packages+=("/var/cache/pacman/pkg/${file%$'\r'}")
done < packages.sha256
pacman -U --needed --noconfirm "${packages[@]}"
cache_args=()
if [[ -f "$work/output/CMakeCache.txt" ]] && ! grep -Fqx "CMAKE_HOME_DIRECTORY:INTERNAL=$(cygpath -am "$src")" "$work/output/CMakeCache.txt"; then
    cache_args+=(--fresh)
fi
cmake "${cache_args[@]}" -S . -B "$work/output" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DPython_EXECUTABLE="$(cygpath -am /ucrt64/bin/python3.exe)" \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.10 -DCHIAKI_SOURCE="$(cygpath -am "$work/upstream")"
cmake --build "$work/output" --parallel 6 --target CadrivectDirect

# Resolve each imported DLL from the same signed UCRT64 package set used to link.
stage=$(mktemp -d "$work/runtime-XXXXXXXX")
mkdir -p "$stage/licenses"
cp "$work/output/CadrivectDirect.exe" "$stage/"
strip "$stage/CadrivectDirect.exe"
pending=(CadrivectDirect.exe)
declare -A seen
for ((i=0; i<${#pending[@]}; i++)); do
    item=${pending[i]}
    while read -r lib; do
        [[ -f "/ucrt64/bin/$lib" && -z "${seen[${lib,,}]:-}" ]] || continue
        seen[${lib,,}]=1
        cp "/ucrt64/bin/$lib" "$stage/$lib"
        pending+=("$lib")
        pkg=$(pacman -Qqo "/ucrt64/bin/$lib")
        pacman -Q "$pkg" >> "$stage/licenses/runtime-packages.txt"
        while read -r license; do
            [[ -f "$license" ]] || continue
            dest="$stage/licenses/${license#/ucrt64/share/licenses/}"
            mkdir -p "$(dirname "$dest")"
            cp "$license" "$dest"
        done < <(pacman -Qlq "$pkg" | grep '^/ucrt64/share/licenses/' || true)
    done < <(objdump -p "$stage/$item" | sed -n 's/.*DLL Name: //p' | tr -d '\r')
done
sort -u -o "$stage/licenses/runtime-packages.txt" "$stage/licenses/runtime-packages.txt"
cp "$work/upstream/LICENSES/AGPL-3.0-only-OpenSSL.txt" "$stage/licenses/"
for sub in nanopb gf-complete jerasure; do
    mkdir -p "$stage/licenses/$sub"
    find "$work/upstream/third-party/$sub" -maxdepth 1 -type f \( -iname 'copying*' -o -iname 'license*' \) -exec cp {} "$stage/licenses/$sub/" \;
done
# Include the sources of this executable, including the exact upstream modules
# statically linked into it, next to the distributed native binary.
tar --exclude=.git --exclude=__pycache__ --exclude='*.pyc' -czf "$stage/Direct-source.tar.gz" \
    -C "$src" Audio.c Direct.c Direct.h Gamepad.c Register.c Verify.c Video.c CMakeLists.txt \
        Build.ps1 build-native.sh packages.sha256 LICENSE.txt README.md \
    -C "$work" upstream/.gitmodules upstream/CMakeLists.txt upstream/COPYING upstream/LICENSES \
        upstream/cmake upstream/lib upstream/third-party/CMakeLists.txt \
        upstream/third-party/nanopb upstream/third-party/gf-complete upstream/third-party/jerasure
mkdir -p "$src/bin"
cp -R "$stage/." "$src/bin/"
"$src/bin/CadrivectDirect.exe" --version
if [[ ${CADRIVECT_NATIVE_VERIFY:-0} == 1 ]]; then
    cmake --build "$work/output" --parallel 6 --target CadrivectDirectVerify
    cp "$work/output/CadrivectDirectVerify.exe" "$stage/"
    "$stage/CadrivectDirectVerify.exe"
fi
