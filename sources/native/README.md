# P5Cast Direct

This Windows x64 process runs the PS5 Remote Play session using libchiaki 1.10.1 from upstream commit `f30da0aaeb96fc0026cc03683153f682352e7d7c`. The P5Cast player receives H.264 or HEVC access units directly over a local pipe. The core decodes stereo Opus and sends input from the first SDL-compatible game controller. It opens a network session only after the player accepts the pipe and the dashboard supplies a registered host.

## Building and checking

Run `powershell -NoProfile -ExecutionPolicy Bypass -File sources/native/Build.ps1 -Verify` in the repository on Windows x64 with Git for Windows installed. The build uses a hash-pinned private MSYS2 toolchain under the current user's local application data. Verification uses SDL's dummy audio backend, a virtual controller, and local named pipes. The resulting `bin` directory includes the native executable, its runtime DLLs, notices and source archive. [Official chiaki-ng release](https://github.com/chiaki-ng/chiaki-ng/releases/tag/v1.10.1) · [MSYS2 packages and source recipes](https://packages.msys2.org/)

## Player protocol

`CadrivectDirect.exe pipeName host codec [fps]` accepts a random local pipe name, a reachable console address, codec 1 (H.264), 2 (HEVC) or 3 (HDR HEVC), and 30 or 60 frames per second. The default is 60. The upstream profile requests 1080p at 15 Mbps. The console supplies the actual frames. The dashboard writes a 16-byte registration key and 16-byte session key to the helper's inherited standard input. Four optional ASCII login PIN digits may follow. The caller closes stdin after writing.

The player creates a local input-only byte pipe before starting the core. The core sends `CDV1` and a little-endian 32-bit codec ID. Each video packet contains a 32-bit byte count followed by one Annex-B access unit, up to 16 MiB. A new profile begins with parameter sets attached to its first random-access picture. A zero byte count introduces four 32-bit telemetry values, in order: queued SDL audio in microseconds, observed queue underruns, startup network RTT in microseconds and target audio queue duration in microseconds. A value of `UINT32_MAX` indicates an unavailable duration. The target is 15 ms. The queue measurement excludes the Windows mixer and playback device buffers. The core ends its session when the player disconnects.

`CadrivectDirect.exe --register host` takes an eight-byte PSN account ID followed by the eight ASCII digits from the console's Link Device screen via inherited stdin. On success its inherited stdout yields 74 bytes: `CDR1`, six hardware-address bytes, a 32-byte zero-padded UTF-8 nickname, a 16-byte registration key and a 16-byte session key. The dashboard protects the result with Windows DPAPI. Stdout and stdin must both be redirected pipes. The core never writes keys or PINs to a file.

Exit codes 20 through 27 indicate invalid input, unavailable player pipe, initialization failure, PS5 session failure, required console login PIN, incorrect console login PIN, audio-device failure, and registration failure respectively.

## Source and licenses

The native core is distributed under the upstream Affero GPL v3 with its OpenSSL exception. Its full license, statically linked third-party notices, runtime package versions and available shared-library notices accompany the executable. The source archive includes the core and the libchiaki, nanopb, jerasure and gf-complete sources used in this build. To compile from the extracted archive with the MSYS2 UCRT64 dependencies listed in `packages.sha256`, run `cmake -S . -B out -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.10 -DCHIAKI_SOURCE="$PWD/upstream"` followed by `cmake --build out --target CadrivectDirect`.
