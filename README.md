# LumaRelay

A Windows x64 player and streaming host for PS5 Remote Play. Play on the host PC, or pair another device and stream the host’s display and sound. LumaRelay has its own dashboard and Direct3D 11 player. Sony Remote Play, chiaki-ng, Sunshine, and Moonlight run as separate applications.

## Try it on one PC

Download the Windows x64 ZIP from the [private releases](https://github.com/EEkebin/LumaRelay/releases), extract the whole folder, and open **LumaRelay.exe**. For Start menu entries, run **Install.cmd** from the extracted folder. The installer copies the app into your Windows user profile. Use the same package on the client PC.

1. Choose **Play → Arcade demo → Play demo**. Steer through gates with the arrow keys or A/D. Space boosts, M toggles sound, and F11 toggles fullscreen. The demo also accepts an XInput controller.
2. To try the GPU player, leave the demo running, return to LumaRelay, select **AMD FSR 1** and **2×**, and click **Open enhanced view**. The player fills the source monitor. **Ctrl+Alt+B** compares against the original view. **Ctrl+Alt+Q** closes the enhanced view.
3. For a PS5, choose **PS Remote Play → Open Remote Play** and connect in the Sony app using your existing pairing. Alternatively, choose **Direct to PS5 → Open direct client** and register your console in chiaki-ng. Once the game is visible, open the enhanced view. Keep the source window restored. Audio and controller input continue through the source app. [Sony setup](https://www.playstation.com/en-us/support/games/playstation-remote-play-on-pc-and-mac/) · [chiaki-ng setup](https://chiaki-ng.github.io/chiaki-ng/setup/configuration/)

## Play from another device

1. On the host, open **Host**, select its LAN or private VPN IPv4 address, and click **Start sharing**. This starts an encrypted Sunshine session that shares the desktop and system audio. A paired client can send keyboard, mouse, and supported gamepad input. Keep the host awake with its display active.
2. On another Windows PC, open **LumaRelay Client** from the Start menu, or select **Client** in the portable app. Enter the host address and click **Pair with host**. Enter the displayed PIN on the host and click **Approve PIN**.
3. Select a stream profile and click **Start stream**. Switch the host into the game or its enhanced view. A supported Moonlight client on a handheld can also connect to the same address and launch the **LumaRelay** application entry.
4. **Ctrl+Alt+Shift+Q** ends a Moonlight PC session. On the host, click **Stop sharing** when finished. The host runs independently of the dashboard, so reopen the dashboard if needed.

For internet play, connect both devices to the same private VPN first, then bind the host to its VPN address and enter that address on the client. LAN connections require the host firewall to allow Sunshine from the client. LumaRelay leaves router and firewall configuration to the owner. An external device and a real internet connection are needed to validate bandwidth, firewall access, and latency on your network.

**Remote controllers.** Local play uses the controller support of Sony or chiaki-ng. To forward a gamepad from Moonlight, Sunshine needs a compatible virtual gamepad driver on the host. The Host page links to upstream setup. The keyboard-driven demo works without that driver. The direct client also offers its own input configuration. [Sunshine Windows driver options](https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2getting__started.html#windows)

## Picture quality

- **Direct to PS5** on the device you are playing on receives the console stream in one hop. This avoids another video encode. After local registration, chiaki-ng also supports remote connections through PlayStation Network on compatible networks. [Remote connection guide](https://chiaki-ng.github.io/chiaki-ng/setup/remoteconnection/)
- **Host streaming** uses the host GPU and sends a fresh compressed video stream plus Opus audio. Both the PS5 source and the hosted output are lossy. For the least conversion, play directly. For a lightweight client, use the host. Match the output to the client display and select a bitrate the network can sustain.
- **AMD FSR 1** runs the published EASU and RCAS shaders on the captured picture. **Sharpen** uses a separate spatial filter. **Original** uses ordinary sampling. The enhanced player currently processes SDR.
- **2×, 3× and 4× interpolation** estimate motion from consecutive captured pictures. These are target multipliers. Display refresh, capture cadence and GPU load cap the visible result. Fast pans, cuts, and HUDs can show artifacts. **Off** gives the shortest processing path.
- **NVIDIA DLSS and DLAA** remain outside this release. Remote Play delivers finished pictures, while the game-engine integrations use depth, jitter and motion data. The interpolation in this build is LumaRelay’s own implementation. [NVIDIA input requirements](https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideDLSS.md)

## Components and local data

Sony mode launches the installed official client. Direct mode downloads the pinned upstream chiaki-ng portable release on first use. Host and Client download pinned portable Sunshine and Moonlight builds from their upstream GitHub releases. The scripts compare SHA-256 digests before extraction. Component versions and digests are in [SourceTools.ps1](sources/SourceTools.ps1) and [packages.json](streaming/packages.json).

Builds are unsigned. The release includes a SHA-256 checksum and the private repository contains the build scripts. Hardware capture uses Windows Graphics Capture, SharpDX 4.2.0, and the AMD FSR 1 headers. The packaged GPU binaries include the SharpDX and FidelityFX notices.

LumaRelay stores preferences and downloaded tools under `%LOCALAPPDATA%\LumaRelay`. Host identity, client pairing data and the Windows user-protected management secret stay in that profile. Sony and chiaki-ng manage their own console pairing data. The project does not request a PlayStation password. See the [chiaki-ng project](https://github.com/chiaki-ng/chiaki-ng), [Sunshine project](https://github.com/SunshineStream/Sunshine) and [Moonlight project](https://github.com/moonlight-stream/moonlight-qt).

## Build and test

Use Windows 11 x64 with a logged-in desktop, .NET Framework 4.8, Windows PowerShell 5.1, and a hardware Direct3D 11 adapter. Build scripts use the Windows Framework compiler. The first GPU build downloads hash-pinned SharpDX packages from NuGet.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
.\LumaRelay.exe
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke-Dashboard.ps1
$player = Start-Process .\engine\bin\LumaPlayer.exe -ArgumentList '--self-test --mode fsr --multiplier 3' -Wait -PassThru
$player.ExitCode
powershell -NoProfile -ExecutionPolicy Bypass -File .\Package.ps1
```

The dashboard smoke test operates a temporary demo and verifies real UI actions. The player self-test captures an animated window and samples GPU output. Console pairing, physical DualSense input, and WAN play need separate testing with your equipment.
