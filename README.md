# P5Cast

P5Cast is a Windows x64 app for playing a PS5 over a network. The dashboard contains the video surface, fullscreen control, picture settings and live frame-rate dial. Direct to PS5 connects to the console and decodes its stream on this PC. Sony Remote Play uses an existing session in Sony’s app. The optional PC host lets a second device play the enhanced output over a LAN or private VPN.

## Try it offline

Extract the entire Windows x64 ZIP and open `P5Cast.exe`. To install it for your Windows user, run **Install.cmd** from the extracted folder. The release includes a SHA-256 checksum. [Private Windows releases](https://github.com/EEkebin/P5Cast/releases)

The installer places the app in `%LOCALAPPDATA%\Programs\P5Cast` and adds one **P5Cast** shortcut to the Start menu. Existing console registrations, preferences and network pairings carry over when upgrading from Cadrivect.

1. Open **Play**, choose **Offline demo**, then click the triangular play button. A 960×540 scene runs inside the dashboard. Use the arrow keys or A/D to steer and Space to accelerate. The demo runs without a console, network connection, decoder download or controller.
2. The two-line title bar button toggles the performance drawer. The **Settings** button beside **Fullscreen** toggles the bottom drawer. Drag the left edge of the performance drawer or the top edge of the settings drawer to resize it. You can also Tab to a handle and use the arrow keys. P5Cast remembers the sizes and open state of both drawers. Begin with **Original** and interpolation **Off**. Try **Sharpen**, **AMD FSR 1** or an available NVIDIA mode. Pick **2×**, **3×** or **4×** to test interpolation.
3. Use **Fullscreen** or F11 for edge-to-edge video. Press Escape or double-click the picture to return. **Stop** turns the same video area black and clears the live measurements. With the dashboard focused, Ctrl+Alt+O toggles the sidebar, Ctrl+Alt+B toggles comparison, and Ctrl+Alt+Q stops playback.

The play button becomes a spinner while P5Cast connects. Click the spinner to cancel. Once video is playing, the button becomes a stop square. Click the status message at the bottom of the window to see timestamped updates from this run. Press Escape to return to the player.

## Compare the picture

In the performance drawer, open **Picture → Comparison view**. Choose **Side by side** for the full original on the left and the full enhanced picture on the right. The left follows the source frame rate. The right uses your selected effects and interpolation. Both views share the same stream, audio and controls.

Choose **Draggable split** for one full-size picture. Drag the centered drawer-style grip or grab anywhere along the divider to compare the same screen pixels. Turn on **Horizontal split** to place Original above Enhanced and drag up or down. P5Cast remembers the orientation. The original and enhanced layers keep identical framing and scale, including in fullscreen. The slider in the drawer also accepts the arrow keys. The handle stops inside each edge so you can always drag it back. Choose **Original** or **Enhanced** for a completely unobstructed view.

For a spatial comparison of sharpening or upscaling, set **Frame interpolation → Off**. With interpolation on, the original holds each source frame while the enhanced side can advance between frames. Under **Stream diagnostics → Video**, **Processing resolution** shows the effect raster and **Display refresh** shows the Windows display mode in Hz. The FPS dial shows how many frames P5Cast submitted per second.

Choose **Original** or **Enhanced** to give either picture the whole window. During playback, **Ctrl+Alt+B** switches to the original and back to your previous view, including side by side. You can try this with **Offline demo** and **Sharpen** before connecting.

## Connect to a PS5

| Source | How to connect | Audio and controls |
| --- | --- | --- |
| **Direct to PS5** | Choose a paired console and enter its reachable LAN address. Choose 30 or 60 FPS under **Stream quality**, then click the play button. The native helper requests a 1080p console stream. | The native helper decodes stereo Opus, plays it on the Windows audio device and sends input from the first SDL-compatible controller. |
| **Sony Remote Play** | Open Sony’s installed PC app, connect to the console using its existing pairing, and leave its window restored. Select **Sony Remote Play** in P5Cast and click the play button. | Sony’s app plays audio and handles controller input. P5Cast captures its client area and processes the video. [Sony’s PC setup](https://remoteplay.dl.playstation.net/remoteplay/lang/en/1100001.html) |

Under **Play**, **Console** contains pairing, address, wake and stop controls. **Stream quality** sets the next Direct connection's target frame rate and requested video bitrate. **Graphics & display** contains the graphics API, monitor, video resolution and color output.

**Play → Interface → Interface scale** changes text, controls and the FPS dial across the app. **Auto** follows the monitor containing the window and accounts for Windows display scaling. At 100% Windows scaling, a 1080p monitor uses 100% interface scale and a 1440p monitor uses about 133%. Choose a percentage from 75% to 200% to save a manual preference. A small window limits the applied scale to keep controls reachable. Fullscreen video still fills the display.

Choose **Play → Graphics & display → Graphics API** while playback is stopped, then start **Offline demo** or connect. **Auto** selects Direct3D 11 on Windows. **Direct3D 11**, **Direct3D 12**, and **Vulkan** support HDR10 when Windows HDR is active and the driver accepts a 10-bit PQ/BT.2020 surface on the selected display. **OpenGL** presents SDR. Windows still uses Direct3D 11 for capture and effects. The additional GPU readback and upload can increase latency, especially at high resolutions. Your renderer choice persists, and switching back from OpenGL restores your color setting. The separate Moonlight network client manages its own graphics output.

Direct defaults to a **100 Mbps video request**, the top setting offered by the pinned chiaki-ng client. Choose a lower target for slower links. The PS5 varies its compressed output with the scene and connection. It also chooses the Opus audio bitrate. In **Stream diagnostics**, compare requested and received video rates and inspect received audio bitrate, sample rate, channels and queue duration. [Upstream PS5 bitrate settings](https://github.com/streetpea/chiaki-ng/blob/v1.10.1/gui/src/qml/SettingsDialog.qml#L1400-L1420)

Press **Stop** or close P5Cast during Direct playback to open the disconnect prompt. **Put PS5 into rest mode** starts checked. Clear it to leave the console awake for this disconnect. Select **Remember my choice for this console** to skip the prompt in future sessions. You can change a saved choice under **Play → Console → When I disconnect**. Losing the network connection or cancelling before the first picture leaves the console's power state alone.

**Pair a console for Direct.** On the PS5, enable **Settings → System → Remote Play → Enable Remote Play**. In P5Cast, choose **Direct to PS5 → Add console**. Enter the console’s address and the Base64 PSN Account ID for the profile you play on. On the console, select **Link Device** and enter its eight-digit code in P5Cast. The app prefills the account ID when it can identify a single account from previous registrations. Select **Pair console**, choose the saved console, and **Connect**. The Account ID represents eight bytes and differs from your online display name. The upstream registration guide describes how to obtain it. [Registration and Account ID guide](https://chiaki-ng.github.io/chiaki-ng/setup/configuration/#registering-your-playstation)

P5Cast stores new Direct registration keys with Windows encryption scoped to the current user. Existing compatible chiaki-ng registrations under that Windows user can also appear in **Paired console**. The Sony app keeps its own pairing.

**Wake from Rest Mode.** **Direct to PS5 → Connect** checks the saved address, wakes a paired console in Rest Mode, and continues into playback. **Play → Console → Wake PS5** wakes it without starting a stream. On the PS5, enable **Stay Connected to the Internet** and **Enable Turning On PS5 from Network** under **Settings → System → Power Saving → Features Available in Rest Mode**. Wake checks use the saved console address over UDP 9302, including across routed subnets or a private VPN. If discovery is filtered, Connect still tries the normal Remote Play connection. [Sony’s Remote Play setup](https://www.playstation.com/en-gb/support/games/playstation-remote-play-on-pc-and-mac/)

On first use, Direct prepares its FFmpeg decoder. It reuses verified libraries from a compatible local portable installation or downloads the pinned upstream archive, verifies it, and extracts three DLLs. The cache is checked on subsequent launches. Once prepared and paired, Direct can play over the local network without a P5Cast cloud service. The offline demo works before this step. [Decoder preparation](sources/Prepare-Decoder.ps1)

## Picture quality and HDR

The PS5 sends compressed video. Direct decodes those pixels once before enhancement. Hosting the PC adds another lossy encode. The playback resolution controls local processing and presentation. A 4K presentation can upscale the console’s 1080p input.

| Picture mode | Processing |
| --- | --- |
| **Original** | Preserves native-size texels, fits the picture to the window and samples multiple regions when reducing it. |
| **Sharpen** | Uses bounded cubic reconstruction during enlargement and a spatial detail filter to improve edges and fine lettering. |
| **AMD FSR 1** | Uses AMD’s EASU for enlargement and RCAS for sharpening. Native-size input skips the enlargement pass. |
| **NVIDIA RTX Video SR** | Runs the NVIDIA RTX Video SDK at High quality on SDR video. Select SDR to make this mode available during HDR playback. |
| **NVIDIA DLSS / DLAA** | Uses the NVIDIA NGX runtime in SDR or floating-point HDR. DLSS uses DLAA when the input already meets or exceeds the processing resolution. P5Cast uses the decoded source to preserve thin, near-neutral menu and HUD lettering when reconstruction changes its contrast. These modes are experimental on finished console video. The player estimates motion from decoded frames because the stream omits game depth and camera jitter. |

NVIDIA choices appear enabled after the app runs a capability test on the actual Direct3D adapter. Optical-flow hardware supplies motion estimates where supported; other adapters use a GPU block matcher. P5Cast’s interpolation inserts frames between source pictures. Its 2×–4× multipliers are targets. Interpolation adds processing delay and can distort moving text or objects. Native NVIDIA DLSS Frame Generation is unavailable in this build. [NVIDIA DLSS SDK](https://github.com/NVIDIA/DLSS) · [NVIDIA RTX Video SDK](https://developer.nvidia.com/rtx-video-sdk)

Choose a **Playback display**, a **Resolution** cap or **Native**, and **Color output**. **Auto** follows the selected display and renderer. RTX Video SR uses SDR. Explicit **HDR10** requires an HDR-enabled display and a presentation surface that supports 10-bit PQ/BT.2020. Changing output settings during playback affects local presentation. Direct negotiates the console’s stream format when connecting; reconnect to request a different console format. Direct supports 10-bit HEVC HDR and maps HDR input to SDR for an SDR display. Sony capture uses floating-point scRGB for HDR. Follow Sony’s own Windows HDR setup for its client. [Sony HDR setup](https://remoteplay.dl.playstation.net/remoteplay/lang/en/1100001.html)

## Reading Stats

**Output FPS** counts frames submitted for presentation each second. One arc shows how much of that total came from the source (mint) and how much came from frame generation (amber). For example, 60 source frames plus 60 generated frames yield 120 output FPS. The white tick marks the target. To see how many frames arrived before processing, open **Stream diagnostics → Video → Incoming FPS**. A dash indicates a missing measurement.

For Direct, **Video bitrate** measures incoming compressed video. **Video decode** measures the local decoding step. **Receive → submit** starts at local packet receipt and ends at presentation submission. **Audio queue** measures buffered audio in the native player and excludes Windows mixer and device buffers. **Handshake RTT** is a round-trip estimate from session setup. Controller-to-screen latency requires an external measurement. Sony capture reports **Capture → submit** and leaves console network telemetry unavailable.

## Play from another device

1. On the PC running the game, open **Host**, choose a private LAN or VPN IPv4 address and the display to share, then select **Start sharing**. Sunshine shares the selected desktop and system audio. Stop and restart sharing to change the captured monitor.
2. On another Windows PC, open **P5Cast** and select the **Client** tab. Enter the host address, select **Pair with host**, and enter the client’s PIN under **Approve PIN** on the host. This pairing is between the two computers.
3. Under **Client → Stream quality**, choose the video preset, a requested video bitrate up to 500 Mbps, and a stereo or surround audio layout. The initial bitrate selection is 500 Mbps. Select **Use video preset** to use the preset bandwidth or the saved client setting. Under **Connection**, select **Start stream**. For HDR, use an HDR-capable host display and client display, enable **Request an HDR stream**, and use Auto, HEVC or AV1. The dashboard hosts Moonlight’s client surface. Third-party Moonlight clients can launch the host’s **P5Cast** application entry.
4. Use **Stop sharing** to end the host service. **Host → Paired devices** lists paired devices and supports revocation. A same-PC preview keeps sound on the host to avoid playing it twice.

Upgraded hosts also publish their previous application name so existing Moonlight shortcuts still work.

The host must remain awake and reachable. Configure the host firewall for the private network and, for internet play, connect both devices through a private VPN. Remote gamepad forwarding requires a compatible virtual gamepad driver on the host. End-to-end performance depends on both devices, the network path and the chosen bitrate. [Sunshine Windows setup](https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2getting__started.html#windows) · [Moonlight](https://github.com/moonlight-stream/moonlight-qt)

## Dependencies and local data

The dashboard and Direct3D player are P5Cast code. `CadrivectDirect.exe` is a background helper built against libchiaki; its license, build instructions and source archive ship with the helper. Direct play runs without the chiaki-ng desktop app or Qt. The three first-use FFmpeg DLLs come from the pinned upstream portable release and report GPL version 3 or later. The decoder bridge uses FFmpeg headers under the accompanying LGPL terms. Source and hashes for the FFmpeg release, Sunshine and Moonlight are recorded in the project. [Direct helper and notices](sources/native/README.md) · [FFmpeg runtime pins](sources/Prepare-Decoder.ps1) · [Network package pins](streaming/packages.json)

The release includes unsigned P5Cast binaries, signed NVIDIA runtimes, and the SharpDX, FidelityFX, NVIDIA Optical Flow and NVIDIA RTX SDK notices. This software contains source code provided by NVIDIA Corporation. New Direct pairing data, FFmpeg and NVIDIA caches live under `%LOCALAPPDATA%\Cadrivect`. Existing dashboard preferences, cached streaming tools and Sunshine pairings use `%LOCALAPPDATA%\LumaRelay`.

## Share on Discord

For **Go Live**, start a Direct PS5 session and join a voice channel in Discord desktop. Choose **Share Your Screen → Applications → P5Cast**, enable sound, and start the broadcast in Discord. Direct audio plays in a child process of P5Cast. Sony’s app plays its own audio in a separate process. Discord controls broadcast quality independently of the PS5 connection. [Discord screen-sharing guide](https://support.discord.com/hc/en-us/articles/360040816151-Go-Live-and-Screen-Share)

Rich Presence and the private project button default to on. You can turn either off under **Play → Discord**. Saved preferences take precedence over the defaults. Discord desktop must be running with activity sharing enabled. P5Cast uses the public application ID and local desktop RPC. While P5Cast is open, the activity reads “Idle · Waiting for PS5 connection” until the first PS5 picture. It returns to idle after disconnect and clears when you close P5Cast or turn off sharing. The optional project button works during idle and playback. **Show game title** checks the connected Direct console periodically. A **Title override** applies to this session. When the title is unavailable the activity describes remote play. The optional private GitHub button links to the project for collaborators with access. [Discord Rich Presence documentation](https://docs.discord.com/developers/discord-social-sdk/development-guides/setting-rich-presence)

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

The installable app targets Windows x64. The portable SDL3 presentation library accepts BGRA frames from a host-owned SDL window. Linux hosts can select Vulkan or OpenGL, macOS hosts can select Metal, and Android hosts can request Vulkan or OpenGL ES 2. Device drivers determine availability. Ports still need a native application shell, decoder integration, audio and gamepad handling. [Portable presenter and CMake build](engine/presentation)

To exercise the portable library on a Linux desktop or Mac with SDL3 development files and CMake installed, run these commands from the repository root. The test opens a graphics context and submits frames using each platform's configured drivers.

```sh
cmake -S engine/presentation -B /tmp/p5cast-presentation -DBUILD_TESTING=ON
cmake --build /tmp/p5cast-presentation
ctest --test-dir /tmp/p5cast-presentation --output-on-failure
```

Add `-DP5CAST_TEST_HDR10=ON` to the configure command to exercise 10-bit PQ output on an HDR-enabled display. The HDR test uses Vulkan on Linux and Metal on macOS.
