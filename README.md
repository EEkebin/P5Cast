# P5Cast

P5Cast is a Windows x64 app for playing a PS5 over a network. The dashboard contains the video surface, fullscreen control, picture settings and live frame-rate dial. Direct to PS5 connects to the console and decodes its stream on this PC. Sony Remote Play uses an existing session in Sony’s app. The optional PC host lets a second device play the enhanced output over a LAN or private VPN.

## Try it offline

Download the **Windows x64 setup EXE** and run it to install P5Cast for your Windows account. Open **P5Cast** from Start. To run the portable version, extract the entire Windows x64 ZIP and open `P5Cast.exe`. Each download has a SHA-256 checksum. [Windows releases](https://github.com/EEkebin/P5Cast/releases)

Setup installs under `%LOCALAPPDATA%\Programs\P5Cast` by default. You can remove it from **Settings → Apps → Installed apps**. Console registrations and preferences are stored separately and carry over on upgrades.

For optional DLSS 5 neural rendering on a compatible GPU, see [Set up OpenDLSS-NR](#set-up-opendlss-nr).

1. Open **Play**, choose **Offline demo**, then click the triangular play button. A 960×540 scene runs inside the dashboard. Use the arrow keys or A/D to steer and Space to accelerate. The demo runs without a console, network connection, decoder download or controller.
2. The two-line title bar button toggles the performance drawer. The **Settings** button beside **Fullscreen** toggles the bottom drawer. Drag the left edge of the performance drawer or the top edge of the settings drawer to resize it. You can also Tab to a handle and use the arrow keys. P5Cast remembers the sizes and open state of both drawers. Begin with **Original** and interpolation **Off**. Try **Sharpen**, **FSR 1 · Spatial** or an available NVIDIA mode. Pick **2×**, **3×** or **4×** to test interpolation.
3. Use **Fullscreen** or F11 for edge-to-edge video. Press Escape or double-click the picture to return. **Stop** turns the same video area black and clears the live measurements. With the dashboard focused, Ctrl+Alt+O toggles the sidebar, Ctrl+Alt+B toggles comparison, and Ctrl+Alt+Q stops playback.

Use **Pop out video** (the arrow-in-a-window button beside **Fullscreen**) to move the picture to a separate, resizable player. Drag the player to another monitor or choose **Playback display** in the dashboard. The dashboard keeps the settings and stats. The player keeps both comparison splits, hold-to-zoom, wheel magnification and fullscreen. Click **Dock** or close the player window to return the picture to the dashboard while the session continues.

Connection, disconnect and graphics API checks show a cancellable progress card inside the playback frame. You can still use the settings, sidebar and title bar. The connection card clears on the first video frame. Picture, frame generation and comparison controls apply directly. Stop gives you one second to keep playing before P5Cast sends the disconnect or rest command. Click the footer status for timestamped history.

Click the speaker beside the source picker to adjust stream volume from 0–100% or mute it. P5Cast remembers the level and mute state across sessions. Direct to PS5 applies the saved level to decoded audio before playback. For Sony Remote Play and the embedded network player, P5Cast adjusts the source’s Windows audio session and restores its level on disconnect. At 100%, you hear the original level. The offline demo is silent.

## Change the interface colors

Open **Settings → Interface → Color theme**. Mint, Ember and Daylight apply immediately, including to the frame-rate dial, side panel, menus and dialogs. P5Cast saves the selection for the next launch.

Choose **Edit JSON** to open `%LOCALAPPDATA%\P5Cast\themes\custom.json` in Notepad. P5Cast creates the first template from the current built-in theme. Save your changes, then choose **Reload**. You can also select **Custom JSON** to load an existing file. This example starts with Daylight and changes the accent and the two FPS dial colors.

```json
{
  "base": "daylight",
  "Accent": "#234CC8",
  "Source": "#146694",
  "Generated": "#854679"
}
```

| Color keys | What they control |
| --- | --- |
| `Background`, `Surface`, `Panel` | Window, cards and inset surfaces. The dialog backdrop takes its tint from Background. |
| `Control`, `Hover`, `Selected` | Buttons, fields, hover and selection. |
| `Border`, `StrongBorder` | Outlines, slider tracks, scroll thumbs and minor gauge ticks. |
| `Text`, `Muted` | Main text and secondary labels. |
| `Accent`, `AccentText` | Focus, primary actions and text on accent-colored controls. |
| `Source`, `Generated`, `Track` | The source and generated segments of the FPS dial and its empty track. Generated also colors informational notices. |
| `Error`, `Danger` | Error text and the Close button’s hover state. |

Use `mint`, `ember` or `daylight` for `base`. Omitted keys inherit the base colors. If a reload fails, the Interface panel points out the problem and keeps the last applied palette.

## Compare the picture

In the performance drawer, open **Picture → Comparison view**. Choose **Side by side** for the full original on the left and the full enhanced picture on the right. The left follows the source frame rate. The right uses your selected effects and interpolation. Both views share the same stream, audio and controls.

Choose **Draggable split** for one full-size picture. Drag the centered drawer-style grip or grab anywhere along the divider to compare the same screen pixels. Turn on **Horizontal split** to place Original above Enhanced and drag up or down. P5Cast remembers the orientation. The original and enhanced layers keep identical framing and scale, including in fullscreen. The slider in the drawer also accepts the arrow keys. The handle stops inside each edge so you can always drag it back. Choose **Original** or **Enhanced** for a completely unobstructed view.

With HDR10 output active, select **HDR / SDR split** under **Picture → Comparison view**. The left side (top in horizontal mode) previews the same frame tone-mapped to SDR at your Windows SDR white level. The other side keeps HDR10. Both sides include the same selected effects, and the divider also works with every effect off.

Hold the left mouse button on the picture to inspect it through a circular loupe. Grab the comparison divider to move the split and the loupe together. Scroll while holding to select native size or 2×, 3×, 4×, or 5×. Releasing the button dismisses the loupe and leaves the divider in place. Escape cancels the drag and restores the previous split. The loupe starts at 2× and magnifies individual output pixels, including the enhancement visible at that spot. This works with Direct to PS5, Sony Remote Play capture, and the offline demo.

For a spatial comparison of sharpening or upscaling, set **Frame generation → Off**. With interpolation on, the original holds each source frame while the enhanced side can advance between frames. Under **Stream diagnostics → Video**, **Input → output** shows incoming and processed video resolution. **Playback area** shows the size of the player on screen, including any black bars. **Display refresh** shows the Windows display mode in Hz.

Choose **Original** or **Enhanced** to give either picture the whole window. During playback, **Ctrl+Alt+B** switches to the original and back to your previous view, including side by side. You can try this with **Offline demo** and **Sharpen** before connecting.

## Connect to a PS5

| Source | How to connect | Audio and controls |
| --- | --- | --- |
| **Direct to PS5** | Choose a paired console and enter its reachable LAN address. Choose 30 or 60 FPS under **Stream quality**, then click the play button. The console stream defaults to 1080p. Choose 720p, 540p or 360p under **PS5 stream resolution** to request a smaller source. | The native helper decodes stereo Opus, plays it on the Windows audio device and sends input from the first SDL-compatible controller. |
| **Sony Remote Play** | Open Sony’s installed PC app, connect to the console using its existing pairing, and leave its window restored. Select **Sony Remote Play** in P5Cast and click the play button. | Sony’s app plays audio and handles controller input. P5Cast captures its client area and processes the video. [Sony’s PC setup](https://remoteplay.dl.playstation.net/remoteplay/lang/en/1100001.html) |

Under **Play**, **Console** contains pairing, address, wake and stop controls. **Stream quality** sets the next Direct connection's source resolution, target frame rate and requested video bitrate. **Graphics & display** contains the graphics API, monitor, video resolution and color output.

**Play → Interface → Interface scale** changes text, controls and the FPS dial across the app. **Auto** follows the monitor containing the window and accounts for Windows display scaling. At 100% Windows scaling, a 1080p monitor uses 100% interface scale and a 1440p monitor uses about 133%. Choose a percentage from 75% to 200% to save a manual preference. A small window limits the applied scale to keep controls reachable. Fullscreen video still fills the display.

Choose **Play → Graphics & display → Graphics API** while playback is stopped, then start **Offline demo** or connect. **Auto** selects Direct3D 11 on Windows. Each Windows renderer keeps its finished frames on the GPU through presentation. Direct3D 11 presents directly, Direct3D 12 uses the effects device’s own command queue, Vulkan presents from its DXVK device, and OpenGL swaps its window using shared GPU textures. Direct3D 11, Direct3D 12 and Vulkan support HDR10 when Windows HDR is active and the driver accepts a 10-bit PQ/BT.2020 surface. OpenGL requires WGL_NV_DX_interop2 and presents SDR. Switching back from OpenGL restores your color preference. Picture options still depend on the selected GPU and API. The separate Moonlight network client manages its own graphics output.

Direct defaults to a **100 Mbps video request**, the top setting offered by the pinned chiaki-ng client. Choose a lower target for slower links. The PS5 varies its compressed output with the scene and connection. It also chooses the Opus audio bitrate. In **Stream diagnostics**, compare requested and received video rates and inspect received audio bitrate, sample rate, channels and queue duration. [Upstream PS5 bitrate settings](https://github.com/streetpea/chiaki-ng/blob/v1.10.1/gui/src/qml/SettingsDialog.qml#L1400-L1420)

**Play → Console → Touchpad** defaults to **Physical click only** for Direct to PS5. Press the pad down to click. Resting a finger, tapping or swiping sends no touch input in this mode. For a game that uses swipes or touch positions, choose **Clicks and touch gestures** before connecting. The controller feedback setting independently controls haptics and adaptive triggers.

During playback, click the circular **Restart stream** arrow beside Play/Stop to reconnect with your current settings, including touchpad mode, stream resolution and requested bitrate. Restart keeps the PS5 awake and preserves your saved Stop choice.

When reconnecting to the same PS5, P5Cast gives the console a short interval to release the previous session. If the PS5 reports Remote Play is busy or refuses the new connection before the first frame, P5Cast retries automatically up to five times. The connection status shows the delay before the next attempt. **Cancel** stops pending retries. The same behavior applies when you stop and press Play again.

**Play → Console → DualSense feedback** is on by default for Direct to PS5. Connect a DualSense or DualSense Edge before starting the session. Games that use adaptive triggers control L2 and R2 independently over USB or compatible Bluetooth. The PS5’s **Trigger Effect Intensity** setting controls their strength. For detailed vibration, use USB and keep the controller’s four-channel playback endpoint enabled in Windows. P5Cast sends the game’s haptic waveform to the two actuator channels on that specific controller. Your usual game audio output and stream volume are independent. If you change between a generic gamepad and a DualSense, reconnect the stream to negotiate the new controller type.

Press **Stop** or close P5Cast during Direct playback to open the disconnect dialog. Choose **Keep playing** or press Escape to return to the stream. Your settings drawer stays where you left it. **Put PS5 into rest mode** starts checked. Clear it to leave the console awake for this disconnect. Select **Remember my choice for this console** to skip the prompt in future sessions. You can change a saved choice under **Play → Console → When I disconnect**. Losing the network connection or cancelling before the first picture leaves the console's power state alone.

**First-time setup.** On a new installation, the setup guide walks through the playback monitor, HDR or SDR output, graphics API, requested PS5 stream resolution, and console pairing. Select an existing PS5, add one with its Link Device code, or continue and pair later. **Finish** saves your choices. **Skip setup** discards the draft. Pairing a console registers it immediately.

Open **Setup** beside **Settings** to run it again, including during playback. An existing installation keeps its saved settings. While the guide is open, the video surface is hidden and the session stays connected. A different console and PS5 stream resolution take effect on the next connection. In the bottom settings drawer, choose a section from the side navigation on wide layouts or the section selector on compact layouts.

**Pair a console for Direct.** On the PS5, enable **Settings → System → Remote Play → Enable Remote Play**. In P5Cast, choose **Direct to PS5 → Add console**. Enter the console’s address and the Base64 PSN Account ID for the profile you play on. On the console, select **Link Device** and enter its eight-digit code in P5Cast. The app prefills the account ID when it can identify a single account from previous registrations. Select **Pair console**, choose the saved console, and **Connect**. The Account ID represents eight bytes and differs from your online display name. The upstream registration guide describes how to obtain it. [Registration and Account ID guide](https://chiaki-ng.github.io/chiaki-ng/setup/configuration/#registering-your-playstation)

P5Cast stores new Direct registration keys with Windows encryption scoped to the current user. Existing compatible chiaki-ng registrations under that Windows user can also appear in **Paired console**. The Sony app keeps its own pairing.

**Wake from Rest Mode.** **Direct to PS5 → Connect** checks the saved address, wakes a paired console in Rest Mode, and continues into playback. **Play → Console → Wake PS5** wakes it without starting a stream. On the PS5, enable **Stay Connected to the Internet** and **Enable Turning On PS5 from Network** under **Settings → System → Power Saving → Features Available in Rest Mode**. Wake checks use the saved console address over UDP 9302, including across routed subnets or a private VPN. If discovery is filtered, Connect still tries the normal Remote Play connection. [Sony’s Remote Play setup](https://www.playstation.com/en-gb/support/games/playstation-remote-play-on-pc-and-mac/)

On first use, Direct prepares its FFmpeg decoder. It reuses verified libraries from a compatible local portable installation or downloads the pinned upstream archive, verifies it, and extracts three DLLs. The cache is checked on subsequent launches. Once prepared and paired, Direct can play over the local network without a P5Cast cloud service. The offline demo works before this step. [Decoder preparation](sources/Prepare-Decoder.ps1)

## Picture quality and HDR

The PS5 sends compressed video. Direct decodes those pixels once before enhancement. Hosting the PC adds another lossy encode. A 1080p stream produces 1080p video in Original, Sharpen and DLAA, including with frame interpolation. An upscaler such as FSR can produce a larger video frame when the upscaling target is larger than the source.

| Picture mode | Processing |
| --- | --- |
| **Original** | Preserves native-size texels, fits the picture to the window and samples multiple regions when reducing it. |
| **Sharpen** | Applies a spatial detail filter to edges and fine lettering at the incoming video resolution. |
| **NIS · Spatial** | NVIDIA Image Scaling runs on each decoded frame. It scales and sharpens when the selected target is larger than the source, and uses native-resolution sharpening otherwise. Available in SDR and HDR10 on Direct3D 11, Direct3D 12 and Vulkan after a GPU check. [NVIDIA Image Scaling SDK](https://github.com/NVIDIAGameWorks/NVIDIAImageScaling) |
| **FSR 1 · Spatial** | Uses AMD’s EASU for enlargement and RCAS for sharpening. Native-size input skips the enlargement pass. |
| **FSR Temporal** | Uses the AMD FSR SDK on D3D12 or Vulkan. The button shows the provider the GPU can run, for example **FSR 3.1.6 · Temporal** on the RTX 4090. AMD supplies FSR 4.1.2 for eligible Radeon hardware and FSR 3.1.6 for other compatible GPUs, including the tested RTX 4090. The temporal upscaler uses an estimated HUD mask to reduce trails and color loss on moving text. |
| **DLSS 5 NR** | Experimental neural rendering at the source resolution. NVIDIA’s provider requires Vulkan, SDR, a supported RTX 50 series GPU and the optional NVIDIA runtime. **Use open source** selects OpenDLSS-NR for SDR or HDR10 playback on Direct3D 11, Direct3D 12 and Vulkan. For HDR10, the model processes a tone-mapped guide. P5Cast transfers predicted RGB color and shading changes to the original HDR picture, protects text strokes and limits changes near peak highlights. All three renderers run inference with Vulkan compute. |
| **NVIDIA RTX Video SR** | Runs RTX Video SR at High quality. In HDR10, the NVIDIA model enhances a PQ luminance guide. P5Cast applies that detail to the original high-precision BT.2020 color. |
| **NVIDIA DLSS / DLAA** | Uses NVIDIA NGX in SDR or floating-point HDR. Native processing uses DLAA. Under **Picture → DLSS quality**, choose Auto, Quality, Balanced, Performance or Ultra Performance. Each manual choice keeps the incoming stream and output resolution you selected. Auto requests Quality below 2× per dimension, Performance from 2× (including 1080p → 4K), and Ultra Performance from 3×. **DLSS model preference** offers Driver default, DLSS 4 preset K and DLSS 4.5 presets M and L. The driver can override a preference. P5Cast estimates motion from decoded frames. It uses the current and previous frames to protect thin, colored HUD strokes and the background they uncover when scrolling. DLAA processes at source resolution, so its effect on a still SDR menu can be subtle. |

Choose one picture mode at a time. Frame generation is a separate choice. With both off, P5Cast shows Original and disables Enhanced, side-by-side and split. Turning on the first effect selects Enhanced. P5Cast tests the chosen device before enabling vendor effects. DLSS and DLAA run through D3D11 or D3D12. NVIDIA optical flow can supply motion on D3D11. Other backends estimate motion on the selected GPU. [NVIDIA DLSS SDK](https://github.com/NVIDIA/DLSS/releases/tag/v310.9.16)

**Apply OpenDLSS-NR first (AI)** in **Picture** adds a source-resolution neural rendering pass before your selected picture effect. Choose RTX Video SR, DLSS, DLAA, FSR Temporal, FSR 1, NIS or Sharpen, or use Original to run NR alone. The toggle defaults off. Choose **Model…** to set up the model. For example, a 1080p stream with RTX Video SR and a 1440p target runs NR at 1080p, then RTX Video SR at 1440p. DLAA keeps the source resolution. Each stage must be available on the selected GPU and renderer. Frame generation remains independently selectable.

**Frame generation** is independent of the picture filter. **P5Cast interpolation** estimates motion between captured frames at 2×, 3× or 4×. With DLAA or RTX Video SR it interpolates between enhanced pictures. With NIS it interpolates first and then applies the spatial filter. The frame-rate dial counts intermediate frames actually submitted.

The frame-rate dial shows pictures submitted by P5Cast, split into source and generated pictures. **Stream FPS** counts decoded Direct PS5 pictures, including repeated pictures. **Estimate game FPS** under **Frame rate** samples visible changes before enhancement, frame generation and local frame drops. It defaults off and can be switched during playback. A still scene, startup or interrupted stream shows a dash. The Direct video stream carries no internal game-engine FPS counter. NVIDIA FRUC reports the rate of generated frames submitted for presentation.

In **Picture**, **Standard · no AI** includes Original, Sharpen, NIS and FSR 1. **AI enhancement** includes RTX Video SR, DLSS, DLAA and neural rendering. The dashboard places AMD temporal upscaling and frame generation according to the provider version reported by AMD. FSR 3 uses conventional algorithms and FSR 4 uses machine learning. The provider version can differ between the upscaler and frame generator. **AMD · AI status unknown** appears when P5Cast cannot classify that provider.

Under **Picture → Frame generation**, select one provider. P5Cast interpolation and NVIDIA FRUC use motion estimation. The D3D12 AI providers use motion estimated from the decoded video and a flat depth surface because Remote Play supplies the finished picture with the HUD already drawn. These inputs can produce artifacts around subtitles, camera cuts and objects moving in front of one another.

XeSS FG and DLSS FG receive the final picture after P5Cast composes the comparison. **Draggable split** and **Side by side** compare picture processing at matching scale, but the generator can interpolate both sides. Select **Comparison view → Original** for full-frame playback at the incoming frame rate with frame generation bypassed.

| Provider | Supported output |
| --- | --- |
| **AMD FSR 2×** | Direct3D 12 and Vulkan, in SDR or HDR10. The RTX 4090 used FSR 3.1.6 in testing. On eligible RX 9000 hardware the Direct3D 12 SDK can select frame generation 4.0.2. [AMD FSR SDK](https://gpuopen.com/amd-fsr-sdk/) |
| **Intel XeSS FG 2×** | Direct3D 12, SDR or HDR10. Uses Intel XeSS-FG and Xe Low Latency (XeLL). In an RTX 4090 test, Intel reported 30 extra presentations for 32 moving inputs in each color mode, including two history resets. [Intel XeSS-FG developer guide](https://github.com/intel/xess/blob/v3.0.2/doc/xess_fg_developer_guide_english.md) |
| **NVIDIA DLSS FG 2×** | Experimental Direct3D 12 provider on compatible RTX hardware. P5Cast asks NVIDIA NGX for one intermediate frame, checks whether the SDK suppressed it, and presents it between source frames. The dashboard checks SDK and driver support. |
| **NVIDIA FRUC 2×** | Direct3D 11 and 12, in SDR or HDR10, on NVIDIA hardware that passes the midpoint test. With HDR10, FRUC interpolates 8-bit PQ guides. P5Cast uses the full-precision source frames to recover finer gradations where the generated color agrees. Some moving gradients can still show banding. To set up a new PC, select **Import FRUC runtime…** and choose NVIDIA's Optical Flow SDK ZIP or its x64 `NvOFFRUC.dll`. The importer also installs the matching CUDA 11 runtime. [NVIDIA SDK download](https://developer.nvidia.com/opticalflow/download) · [FRUC programming guide](https://docs.nvidia.com/video-technologies/optical-flow-sdk/nvfruc-programming-guide/index.html) |

### Set up OpenDLSS-NR

P5Cast includes the open-source neural renderer. Its compatible **310.8.0 full model** is a separate download from NVIDIA. The setup EXE and portable ZIP use the same model folder.

1. Open **Picture** in the right-hand drawer and check **Use open source for DLSS 5 NR**.
2. Click **Download model**. P5Cast downloads NVIDIA’s RTX Remix package (about 368 MB), checks its SHA-256 and the NVIDIA runtime signature, and prepares the model in your Windows profile. The button handles the download and file placement for you. [NVIDIA model package](https://d1oqksizpb0e3d.cloudfront.net/rtx-remix-ngx_sdk_dlnr@2.7z)
3. Wait for **OpenDLSS-NR model verified.** Select **DLSS 5 NR** to use it on its own. To combine it with an upscaler, select that upscaler and check **Apply OpenDLSS-NR first (AI)**.

**Where the files go.** Paste `%LOCALAPPDATA%\P5Cast\providers\open-nr\model` into File Explorer’s address bar. It contains the manifest, NVIDIA’s license, and a nested folder with 11 stage files.

```text
%LOCALAPPDATA%\P5Cast\providers\open-nr\model\
  manifest.json
  NVIDIA-LICENSE.txt
  model\stage-0.bin
  …
  model\stage-10.bin
```

**Already have the prepared model?** Keep the whole folder together wherever you want it. In **Picture → Model… → Choose model…**, select the top-level `manifest.json`. P5Cast checks the model on your selected GPU and remembers the location. [OpenDLSS-NR model format](https://github.com/maanHimself/OpenDLSS-NR#model-directory)

**Already downloaded the NVIDIA `.7z`?** Open PowerShell in the folder containing `P5Cast.exe` and run the following command, adjusting the archive path if needed. The helper prints the prepared folder as `modelDirectory`. Use **Choose model…** to select its manifest.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\sources\Install-OpenNrModel.ps1" -ArchivePath "$env:USERPROFILE\Downloads\rtx-remix-ngx_sdk_dlnr@2.7z"
```

On an RTX 4090 with driver 621.24, a local 60 FPS HEVC test clip with OpenDLSS-NR and frame generation off presented about 48 FPS on Direct3D 11, 49 FPS on Direct3D 12, and 44 FPS on Vulkan at 1080p. At 720p, Direct3D 11 presented 60 FPS. Choose a 720p console stream if you want to try this effect at 60 FPS on similar hardware.

AMD Ray Regeneration 1.2.1 requires lighting and material buffers from a ray-traced game. The PS5 Remote Play stream supplies color video. [AMD Ray Regeneration](https://gpuopen.com/amd-fsr-ray-regeneration/)

Choose a **Playback display**, an **Upscaling target**, and **Color output**. The default **Fit playback area** lets an active upscaler enlarge the video to fit the playback area. The Direct PS5 connection defaults to 1920×1080. On a 2560×1440 fullscreen display, DLSS can enlarge that stream to 2560×1440. With Fit playback area, DLSS applies native-resolution DLAA in a window smaller than the stream. Choosing a fixed target such as 4K runs the upscaler at that size even in a smaller window. Targets preserve the source aspect ratio. A target below the source keeps processing at the original resolution. Picture shows the input and processed sizes, plus the on-screen size when it differs. **Auto** color follows the selected display and renderer. For an HDR PS5 stream with both NVIDIA effects, choose Direct3D 11 or 12, **Auto** on an HDR display or **HDR10**, then **RTX Video SR** and **NVIDIA FRUC 2×** under Picture. Explicit **HDR10** requires an HDR-enabled display and a presentation surface that supports 10-bit PQ/BT.2020. Direct negotiates the console’s stream format when connecting. Reconnect to request a different console format. Direct supports 10-bit HEVC HDR and maps HDR input to SDR for an SDR display. Sony capture uses floating-point scRGB for HDR. [Sony HDR setup](https://remoteplay.dl.playstation.net/remoteplay/lang/en/1100001.html)

## Reading Stats

**Output FPS** counts frames submitted for presentation each second. One arc shows how much of that total came from the source and how much came from frame generation. For example, 60 source frames plus 60 generated frames yield 120 output FPS. The triangular marker shows the target. Under the dial, use **Dial max** to slide or type a scale from **120 to 1,000 FPS**. P5Cast starts at **240 FPS** and remembers your setting. Above the chosen maximum the arc stays full, its colors preserve the source/generated proportions, and the center still shows the measured FPS. Open **Stream diagnostics → Video → Source FPS** to see the rate of source frames the renderer accepted. A dash indicates a missing measurement.

Direct sessions save a playback trace under `%LOCALAPPDATA%\P5Cast\diagnostics`. The latest four CSV files are kept, with up to 3,600 samples each. If playback stutters, note the time. Look for changes in audio underruns, receiver loss, frames replaced before rendering, and PC submission time around the same timestamp. The audio frame and device callback gap columns show the longest interval observed in each helper reporting period. To investigate an effect, compare the selected mode with the effective mode and comparison view, then check the input and output sizes. `generatedFps` shows the rate of intermediate frames submitted during the sample. `targetPresentedFps` shows the requested total. The trace contains no console identifiers or media.

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

Intel(R) Xe Super Sampling (XeSS) SDK: Copyright (C) 2025 Intel Corporation. The Windows release includes Intel's signed XeSS-FG and XeLL binaries, the Intel Simplified Software License in `engine/bin/xess-fg/LICENSE.txt`, and Intel's third-party notices in `engine/bin/xess-fg/third-party-programs.txt`. The NVIDIA DLSS Frame Generation runtime and its RTX SDK license are in `engine/bin/dlss-fg/`. [Intel software license](https://github.com/intel/xess/blob/v3.0.2/LICENSE.txt)

## Share on Discord

For **Go Live**, start a Direct PS5 session and join a voice channel in Discord desktop. Choose **Share Your Screen → Applications → P5Cast**, enable sound, and start the broadcast in Discord. Direct audio plays in a child process of P5Cast. Sony’s app plays its own audio in a separate process. Discord controls broadcast quality independently of the PS5 connection. [Discord screen-sharing guide](https://support.discord.com/hc/en-us/articles/360040816151-Go-Live-and-Screen-Share)

Rich Presence and the private project button default to on. You can turn either off under **Play → Discord**. Saved preferences take precedence over the defaults. Discord desktop must be running with activity sharing enabled. P5Cast uses the public application ID and local desktop RPC. While P5Cast is open, the activity reads “Idle · Waiting for PS5 connection” until the first PS5 picture. It returns to idle after disconnect and clears when you close P5Cast or turn off sharing. The optional project button works during idle and playback. **Show game title** checks the connected Direct console periodically. A **Title override** applies to this session. When the title is unavailable the activity describes remote play. The optional private GitHub button links to the project for collaborators with access. [Discord Rich Presence documentation](https://docs.discord.com/developers/discord-social-sdk/development-guides/setting-rich-presence)

## Build and test

Use Windows 11 x64, .NET Framework 4.8, Windows PowerShell 5.1, a hardware Direct3D 11 adapter and Git for Windows. Building the OpenDLSS-NR kernels requires Python 3 on PATH as `python`. First builds download the pinned compilers, source packages and SDK inputs. The package contains the native core and graphics runtimes. The following tests use synthetic video and do not need a PS5.

```powershell
.\Build.ps1
.\P5Cast.exe
.\tests\Smoke-Settings.ps1
.\tests\Smoke-Decoder.ps1
.\tests\Smoke-Embedded.ps1
.\engine\hdrtest\Verify.ps1
.\Package.ps1
```

To build the setup EXE from that ZIP, install Inno Setup 7 and run `.\installer\Build-Installer.ps1 -Archive .\dist\P5Cast-0.32.0-windows-x64.zip`. Pass `-Compiler` with the path to `ISCC.exe` if it is outside your PATH. [Inno Setup downloads](https://jrsoftware.org/inno-setup/downloads)

The decoder test compares every YUV sample in lossless H.264, HEVC and HEVC Main 10 fixtures. The embedded test renders fixture packets in a WPF child surface. The HDR test checks GPU transfer functions and a one-pixel stroke target. A physical controller, the actual console and a second client on the intended network are needed to check their behavior together. [Player diagnostics and CLI](engine/README.md)

The installable app targets Windows x64. The portable SDL3 presentation library accepts BGRA frames from a host-owned SDL window. Linux hosts can select Vulkan or OpenGL, macOS hosts can select Metal, and Android hosts can request Vulkan or OpenGL ES 2. Device drivers determine availability. Ports still need a native application shell, decoder integration, audio and gamepad handling. [Portable presenter and CMake build](engine/presentation)

To exercise the portable library on a Linux desktop or Mac with SDL3 development files and CMake installed, run these commands from the repository root. The test opens a graphics context and submits frames using each platform's configured drivers.

```sh
cmake -S engine/presentation -B /tmp/p5cast-presentation -DBUILD_TESTING=ON
cmake --build /tmp/p5cast-presentation
ctest --test-dir /tmp/p5cast-presentation --output-on-failure
```

Add `-DP5CAST_TEST_HDR10=ON` to the configure command to exercise 10-bit PQ output on an HDR-enabled display. The HDR test uses Vulkan on Linux and Metal on macOS.
