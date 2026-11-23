# Cadrivect

A Windows x64 player and streaming host for PS5 Remote Play. Play on the host PC, or pair another device and stream the host’s display and sound. Cadrivect has its own dashboard and Direct3D 11 player. Sony Remote Play, chiaki-ng, Sunshine, and Moonlight run as separate applications.

## Try it on one PC

Download the Windows x64 ZIP from the [private releases](https://github.com/EEkebin/Cadrivect/releases), extract the whole folder, and open **Cadrivect.exe**. For Start menu entries, run **Install.cmd** from the extracted folder. The installer copies the app into your Windows user profile. Use the same package on the client PC.

1. Choose **Play → Arcade demo → Play demo**. Steer through gates with the arrow keys or A/D. Space boosts, M toggles sound, and F11 toggles fullscreen. The demo also accepts an XInput controller.
2. To try the GPU player, leave the demo running, return to Cadrivect, select **AMD FSR 1** and **2×**, and click **Open enhanced view**. The player opens on the chosen playback display. **Ctrl+Alt+B** compares against the original view. **Ctrl+Alt+Q** closes the enhanced view.
3. For a PS5, choose **PS Remote Play → Open Remote Play** and connect in the Sony app using your existing pairing. Alternatively, choose **Direct to PS5 → Open direct client** and register your console in chiaki-ng. Once the game is visible, open the enhanced view. Keep the source window restored. Audio and controller input continue through the source app. [Sony setup](https://www.playstation.com/en-us/support/games/playstation-remote-play-on-pc-and-mac/) · [chiaki-ng setup](https://chiaki-ng.github.io/chiaki-ng/setup/configuration/)

## Play from another device

1. On the host, open **Host**, select its LAN or private VPN IPv4 address and the display to share, then click **Start sharing**. This starts an encrypted Sunshine session that shares the desktop and system audio. A paired client can send keyboard, mouse, and supported gamepad input. Keep the host awake with its display active. Stop and restart sharing to change the shared display.
2. On another Windows PC, open **Cadrivect Client** from the Start menu, or select **Client** in the portable app. Enter the host address and click **Pair with host**. Enter the displayed PIN on the host and click **Approve PIN**.
3. Select a stream profile. For an HDR-capable client display, enable **Request an HDR stream** and use Auto, HEVC, or AV1. Then click **Start stream**. Switch the host into the game or its enhanced view. A supported Moonlight client on a handheld can also connect to the same address and launch the **Cadrivect** application entry.
4. **Ctrl+Alt+Shift+Q** ends a Moonlight PC session. On the host, click **Stop sharing** when finished. The host runs independently of the dashboard, so reopen the dashboard if needed. While hosting, **Manage paired clients → Load devices** lists clients and lets you revoke individual pairings.

When the dashboard connects to its own PC, it starts a video-only Moonlight preview. The source audio still plays once on the host. A client on another device receives the stream audio normally.

For internet play, connect both devices to the same private VPN first, then bind the host to its VPN address and enter that address on the client. LAN connections require the host firewall to allow Sunshine from the client. Cadrivect leaves router and firewall configuration to the owner. An external device and a real internet connection are needed to validate bandwidth, firewall access, and latency on your network.

**Remote controllers.** Local play uses the controller support of Sony or chiaki-ng. To forward a gamepad from Moonlight, Sunshine needs a compatible virtual gamepad driver on the host. The Host page links to upstream setup. The keyboard-driven demo works without that driver. The direct client also offers its own input configuration. [Sunshine Windows driver options](https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2getting__started.html#windows)

## Picture quality

- **Direct to PS5** on the device you are playing on receives the console stream in one hop. This avoids another video encode. After local registration, chiaki-ng also supports remote connections through PlayStation Network on compatible networks. [Remote connection guide](https://chiaki-ng.github.io/chiaki-ng/setup/remoteconnection/)
- **Host streaming** uses the host GPU and sends a fresh compressed video stream plus Opus audio. Both the PS5 source and the hosted output are lossy. For the least conversion, play directly. For a lightweight client, use the host. Match the output to the client display and select a bitrate the network can sustain.
- **AMD FSR 1** runs the published EASU and RCAS shaders on the captured picture. **Sharpen** uses a separate spatial filter. **Original** uses ordinary sampling.
- **2×, 3× and 4× interpolation** estimate motion from consecutive captured pictures. These are target multipliers. Display refresh, capture cadence and GPU load cap the visible result. Fast pans, cuts, and HUDs can show artifacts. **Off** gives the shortest processing path.
- **NVIDIA RTX Video Super Resolution** applies the RTX Video SDK's High setting to captured SDR video. Choose this first if you want to clean up an SDR stream. Auto uses SDR presentation for this mode. [NVIDIA RTX Video SDK](https://developer.nvidia.com/rtx-video-sdk)
- **NVIDIA DLSS and DLAA** use the NVIDIA NGX runtime. DLSS enlarges the source when the output is larger and selects DLAA when the source already fills or exceeds the requested resolution. Explicit DLAA always processes the original size. Both modes are experimental on finished video. The player estimates motion and supplies a flat depth plane and zero camera jitter. Use **Ctrl+Alt+B** to compare moving text, fences, or fast pans. [NVIDIA DLSS SDK](https://github.com/NVIDIA/DLSS)
- **Hardware motion estimation** uses NVIDIA Optical Flow when available. A GPU block matcher supports other adapters. Cadrivect’s interpolator consumes this motion to generate intermediate frames. The session report records the active NVIDIA effect, successful evaluations, and hardware motion pairs.

## Displays and HDR

Choose the **Playback display** by its Windows number and model. The app defaults to your primary display and remembers your selection. While playing, use **Ctrl+Alt+Enter** to toggle fullscreen, **Ctrl+Alt+Right** to cycle displays, or drag the window by its title bar. Selecting a different display in the dashboard moves the active player. **Resolution** caps the processing size while keeping the aspect ratio. Native uses the available window or display size. These controls preserve your desktop resolution.

**Auto** presents HDR10 on a display where Windows HDR is active, and SDR on an SDR display. The RTX Video SR mode uses its SDR pipeline. Explicit **HDR10** checks both the display and swap chain before starting. HDR10 captures floating-point scRGB, preserves highlight range through processing, and presents a 10-bit PQ/BT.2020 signal. Windows and the panel control final presentation. SDR video on an HDR display retains the luminance supplied by Windows. For native HDR from the console, enable HDR on the PS5 and Windows display, install the Microsoft HEVC Video Extension, and select **Enable HDR** under **Video Quality for Remote Play** in the Sony client. [Sony Windows HDR setup](https://remoteplay.dl.playstation.net/remoteplay/lang/en/1100001.html)

## Components and local data

Sony mode launches the installed official client. Direct mode downloads the pinned upstream chiaki-ng portable release on first use. Host and Client download pinned portable Sunshine and Moonlight builds from their upstream GitHub releases. The scripts compare SHA-256 digests before extraction. Component versions and digests are in [SourceTools.ps1](sources/SourceTools.ps1) and [packages.json](streaming/packages.json).

Builds are unsigned. The release includes a SHA-256 checksum and the private repository contains the build scripts. Hardware capture uses Windows Graphics Capture, SharpDX 4.2.0, and the AMD FSR 1 headers. The packaged GPU binaries include the SharpDX and FidelityFX notices.

Cadrivect keeps the original `%LOCALAPPDATA%\LumaRelay` profile for preferences, streaming tools and client pairings. NVIDIA caches use `%LOCALAPPDATA%\Cadrivect`. Sony and chiaki-ng manage their own console pairing data. [chiaki-ng](https://github.com/chiaki-ng/chiaki-ng) · [Sunshine](https://github.com/SunshineStream/Sunshine) · [Moonlight](https://github.com/moonlight-stream/moonlight-qt)

This software contains source code provided by NVIDIA Corporation. The NVIDIA components are supplied under the NVIDIA RTX SDK terms included in `engine/bin/NVIDIA-RTX-LICENSE.txt` and `engine/bin/NVIDIA_RTX_Video_SDK_License.pdf`. The release includes signed NVIDIA runtimes and Cadrivect’s unsigned integration binaries.

## Build and test

Use Windows 11 x64 with a logged-in desktop, .NET Framework 4.8, Windows PowerShell 5.1, and a hardware Direct3D 11 adapter. The first build retrieves hash-pinned SharpDX packages, NVIDIA SDK files and Microsoft C++ build tools as needed. NVIDIA enhancement modes require a compatible RTX GPU and driver. The ZIP includes the runtime files.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
.\Cadrivect.exe
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke-Dashboard.ps1
$player = Start-Process .\engine\bin\CadrivectPlayer.exe -ArgumentList '--self-test --mode fsr --multiplier 3' -Wait -PassThru
$player.ExitCode
powershell -NoProfile -ExecutionPolicy Bypass -File .\Package.ps1
```

For standalone demo or host/client tests, close PS Remote Play and confirm the Sony session has disconnected before starting the test. Keep the console awake by leaving the rest-mode box unchecked. The dashboard smoke test operates a temporary demo and verifies real UI actions. The player self-test captures an animated window and samples GPU output. Console pairing, physical DualSense input, and WAN play need separate testing with your equipment.

## Future platforms

Linux (Arch and Ubuntu) and macOS on MacBooks are planned desktop targets. An Android client would use the phone as the screen and its paired physical controller for input. Direct-to-PS5 play would avoid a second encode. Connecting through the PC host would let the host handle graphics processing. On-device Android upscaling will depend on measured latency and sustained performance. The current Cadrivect release targets Windows.
