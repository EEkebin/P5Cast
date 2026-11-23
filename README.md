# P5Cast

P5Cast is a Windows x64 app for playing a PS5 over a network. The dashboard contains the video surface, fullscreen control, picture settings and live frame-rate dial. Direct to PS5 connects to the console and decodes its stream on this PC. Sony Remote Play uses an existing session in Sony’s app. The optional PC host lets a second device play the enhanced output over a LAN or private VPN.

## Try it offline

Extract the entire Windows x64 ZIP and open `P5Cast.exe`. To install it for your Windows user and add Start menu entries, run **Install.cmd** from the extracted folder. The release includes a SHA-256 checksum. [Private Windows releases](https://github.com/EEkebin/P5Cast/releases)

The installer places the app in `%LOCALAPPDATA%\Programs\P5Cast` and adds **P5Cast** and **P5Cast Client** under **Start → P5Cast**. Existing console registrations, preferences and network pairings carry over when upgrading from Cadrivect.

1. Open **Play**, choose **Offline demo**, then **Play demo**. A 960×540 scene runs inside the dashboard. Use the arrow keys or A/D to steer and Space to accelerate. The demo runs without a console, network connection, decoder download or controller.
2. The **Stats** panel is visible at startup. Begin with **Original** and interpolation **Off**. Try **Sharpen**, **AMD FSR 1** or an available NVIDIA mode, then compare with **Compare original**. Pick **2×**, **3×** or **4×** to test interpolation.
3. Use **Fullscreen** or F11 for edge-to-edge video. Press Escape or double-click the picture to return. **Stop** turns the same video area black and clears the live measurements. With the dashboard focused, Ctrl+Alt+O toggles Stats, Ctrl+Alt+B toggles comparison, and Ctrl+Alt+Q stops playback.

## Connect to a PS5

| Source | How to connect | Audio and controls |
| --- | --- | --- |
| **Direct to PS5** | Choose a paired console and enter its reachable LAN address. Choose 30 or 60 FPS under **Stream quality**, then **Connect**. The native helper requests a 1080p console stream. | The native helper decodes stereo Opus, plays it on the Windows audio device and sends input from the first SDL-compatible controller. |
| **Sony Remote Play** | Open Sony’s installed PC app, connect to the console using its existing pairing, and leave its window restored. Select **Sony Remote Play → Connect** in P5Cast. | Sony’s app plays audio and handles controller input. P5Cast captures its client area and processes the video. [Sony’s PC setup](https://remoteplay.dl.playstation.net/remoteplay/lang/en/1100001.html) |

Under **Play**, **Console** contains pairing, address and stop behavior. **Stream quality** sets the next Direct connection's target frame rate and requested video bitrate. **Display** controls the local monitor, resolution and HDR. The sidebar keeps the FPS dial visible above collapsible **Picture** and **Stream diagnostics** sections.

Direct defaults to a **100 Mbps video request**, the top setting offered by the pinned chiaki-ng client. Choose a lower target for slower links. The PS5 varies its compressed output with the scene and connection. It also chooses the Opus audio bitrate. In **Stream diagnostics**, compare requested and received video rates and inspect received audio bitrate, sample rate, channels and queue duration. [Upstream PS5 bitrate settings](https://github.com/streetpea/chiaki-ng/blob/v1.10.1/gui/src/qml/SettingsDialog.qml#L1400-L1420)

Press **Stop** in an active Direct session to choose whether to put the PS5 into rest mode. Both checkboxes start unchecked. **Remember my choice** applies to the selected console. To change it, open **Console → When I press Stop**. Closing the app or losing the connection simply disconnects. Rest mode requires a running Direct session and the dashboard reports a command failure if it cannot submit the request.

**Pair a console for Direct.** On the PS5, enable **Settings → System → Remote Play → Enable Remote Play**. In P5Cast, choose **Direct to PS5 → Add console**. Enter the console’s address and the Base64 PSN Account ID for the profile you play on. On the console, select **Link Device** and enter its eight-digit code in P5Cast. The app prefills the account ID when it can identify a single account from previous registrations. Select **Pair console**, choose the saved console, and **Connect**. The Account ID represents eight bytes and differs from your online display name. The upstream registration guide describes how to obtain it. [Registration and Account ID guide](https://chiaki-ng.github.io/chiaki-ng/setup/configuration/#registering-your-playstation)

P5Cast stores new Direct registration keys with Windows encryption scoped to the current user. Existing compatible chiaki-ng registrations under that Windows user can also appear in **Paired console**. The Sony app keeps its own pairing. Direct needs a reachable, awake console with the intended user available. The native connection uses the console address; a private VPN must provide a route to it for play away from home.

On first use, Direct prepares its FFmpeg decoder. It reuses verified libraries from a compatible local portable installation or downloads the pinned upstream archive, verifies it, and extracts three DLLs. The cache is checked on subsequent launches. Once prepared and paired, Direct can play over the local network without a P5Cast cloud service. The offline demo works before this step. [Decoder preparation](sources/Prepare-Decoder.ps1)

## Picture quality and HDR

The PS5 sends compressed video. Direct decodes those pixels once before enhancement. Hosting the PC adds another lossy encode. The playback resolution controls local processing and presentation. A 4K presentation can upscale the console’s 1080p input.

| Picture mode | Processing |
| --- | --- |
| **Original** | Preserves native-size texels, fits the picture to the window and samples multiple regions when reducing it. |
| **Sharpen** | Uses bounded cubic reconstruction during enlargement and a spatial detail filter to improve edges and fine lettering. |
| **AMD FSR 1** | Uses AMD’s EASU for enlargement and RCAS for sharpening. Native-size input skips the enlargement pass. |
| **NVIDIA RTX Video SR** | Runs the NVIDIA RTX Video SDK at High quality on SDR video. Select SDR to make this mode available during HDR playback. |
| **NVIDIA DLSS / DLAA** | Uses the NVIDIA NGX runtime in SDR or floating-point HDR. DLSS uses DLAA when the input already meets or exceeds the processing resolution. These modes are experimental on finished console video. Motion comes from the decoded pictures; original game depth and camera jitter are unavailable. |

NVIDIA choices appear enabled after the app runs a capability test on the actual Direct3D adapter. Optical-flow hardware supplies motion estimates where supported; other adapters use a GPU block matcher. P5Cast’s interpolation inserts frames between source pictures. Its 2×–4× multipliers are targets. Interpolation adds processing delay and can distort moving text or objects. Native NVIDIA DLSS Frame Generation is unavailable in this build. [NVIDIA DLSS SDK](https://github.com/NVIDIA/DLSS) · [NVIDIA RTX Video SDK](https://developer.nvidia.com/rtx-video-sdk)

Choose a **Playback display**, a **Resolution** cap or **Native**, and **Color output**. **Auto** uses HDR10 when Windows HDR is active on the selected display, with SDR for RTX Video SR. Explicit **HDR10** requires an HDR-enabled display and a presentation surface that supports 10-bit PQ/BT.2020. Changing output settings during playback affects local presentation. Direct negotiates the console’s stream format when connecting; reconnect to request a different console format. Direct supports 10-bit HEVC HDR and maps HDR input to SDR for an SDR display. Sony capture uses floating-point scRGB for HDR. Follow Sony’s own Windows HDR setup for its client. [Sony HDR setup](https://remoteplay.dl.playstation.net/remoteplay/lang/en/1100001.html)

## Reading Stats

**Output FPS** is the number of frames P5Cast sent to the screen each second. The large mint number and outer arc show that rate. **From PS5** (blue) shows incoming console frames. **Added** (amber) shows interpolated frames included in the output. For example, 60 incoming frames and 60 added frames can produce 120 output frames per second. The white tick marks the target on the shared 0–240+ scale. Display scan-out can differ. A dash indicates a missing measurement.

For Direct, **Video bitrate** measures incoming compressed video. **Video decode** measures the local decoding step. **Receive → submit** starts at local packet receipt and ends at presentation submission. **Audio queue** measures buffered audio in the native player and excludes Windows mixer and device buffers. **Handshake RTT** is a round-trip estimate from session setup. Controller-to-screen latency requires an external measurement. Sony capture reports **Capture → submit** and leaves console network telemetry unavailable.

## Play from another device

1. On the PC running the game, open **Host**, choose a private LAN or VPN IPv4 address and the display to share, then select **Start sharing**. Sunshine shares the selected desktop and system audio. Stop and restart sharing to change the captured monitor.
2. On another Windows PC, open **P5Cast Client** or the **Client** tab. Enter the host address, select **Pair with host**, and enter the client’s PIN under **Approve PIN** on the host. This pairing is between the two computers.
3. Under **Client → Stream quality**, choose the video preset, a requested video bitrate up to 500 Mbps, and a stereo or surround audio layout. The initial bitrate selection is 500 Mbps. Select **Use video preset** to use the preset bandwidth or the saved client setting. Under **Connection**, select **Start stream**. For HDR, use an HDR-capable host display and client display, enable **Request an HDR stream**, and use Auto, HEVC or AV1. The dashboard hosts Moonlight’s client surface. Third-party Moonlight clients can launch the host’s **P5Cast** application entry.
4. Use **Stop sharing** to end the host service. **Host → Paired devices** lists paired devices and supports revocation. A same-PC preview keeps sound on the host to avoid playing it twice.

Upgraded hosts also publish their previous application name so existing Moonlight shortcuts still work.

The host must remain awake and reachable. Configure the host firewall for the private network and, for internet play, connect both devices through a private VPN. Remote gamepad forwarding requires a compatible virtual gamepad driver on the host. End-to-end performance depends on both devices, the network path and the chosen bitrate. [Sunshine Windows setup](https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2getting__started.html#windows) · [Moonlight](https://github.com/moonlight-stream/moonlight-qt)

## Dependencies and local data

The dashboard and Direct3D player are P5Cast code. `CadrivectDirect.exe` is a background helper built against libchiaki; its license, build instructions and source archive ship with the helper. Direct play runs without the chiaki-ng desktop app or Qt. The three first-use FFmpeg DLLs come from the pinned upstream portable release and report GPL version 3 or later. The decoder bridge uses FFmpeg headers under the accompanying LGPL terms. Source and hashes for the FFmpeg release, Sunshine and Moonlight are recorded in the project. [Direct helper and notices](sources/native/README.md) · [FFmpeg runtime pins](sources/Prepare-Decoder.ps1) · [Network package pins](streaming/packages.json)

The release includes unsigned P5Cast binaries, signed NVIDIA runtimes, and the SharpDX, FidelityFX, NVIDIA Optical Flow and NVIDIA RTX SDK notices. This software contains source code provided by NVIDIA Corporation. New Direct pairing data, FFmpeg and NVIDIA caches live under `%LOCALAPPDATA%\Cadrivect`. Existing dashboard preferences, cached streaming tools and Sunshine pairings use `%LOCALAPPDATA%\LumaRelay`.

## Share on Discord

For **Go Live**, start a Direct PS5 session and join a voice channel in Discord desktop. Choose **Share Your Screen → Applications → P5Cast**, enable sound, and start the broadcast in Discord. Direct audio plays in a child process of P5Cast. Sony’s app plays its own audio in a separate process. Discord controls broadcast quality independently of the PS5 connection. [Discord screen-sharing guide](https://support.discord.com/hc/en-us/articles/360040816151-Go-Live-and-Screen-Share)

For **Rich Presence**, open **Play → Discord** and enable **Show P5Cast activity on Discord**. Discord desktop must be running with activity sharing enabled. P5Cast uses the public application ID and local desktop RPC. While P5Cast is open, the activity reads “Idle · Waiting for PS5 connection” until the first PS5 picture. It returns to idle after disconnect and clears when you close P5Cast or turn off sharing. The optional project button works during idle and playback. **Show game title** checks the connected Direct console periodically. A **Title override** applies to this session. When the title is unavailable the activity describes remote play. The optional private GitHub button links to the project for collaborators with access. [Discord Rich Presence documentation](https://docs.discord.com/developers/discord-social-sdk/development-guides/setting-rich-presence)

## Build and test

Use Windows 11 x64, .NET Framework 4.8, Windows PowerShell 5.1, a hardware Direct3D 11 adapter and Git for Windows. First builds download the pinned compilers, source packages and SDK inputs. The package contains the native core and graphics runtimes. The following tests use synthetic video and do not need a PS5.

```powershell
.\Build.ps1
.\P5Cast.exe
.\tests\Smoke-Settings.ps1
.\tests\Smoke-Decoder.ps1
.\tests\Smoke-Embedded.ps1
.\engine\hdrtest\Verify.ps1
.\Package.ps1
```

The decoder test compares every YUV sample in lossless H.264, HEVC and HEVC Main 10 fixtures. The embedded test renders fixture packets in a WPF child surface. The HDR test checks GPU transfer functions and a one-pixel stroke target. A physical controller, the actual console and a second client on the intended network are needed to check their behavior together. [Player diagnostics and CLI](engine/README.md)

The current app targets Windows. Linux, macOS and Android are future platform targets.
