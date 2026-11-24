# P5Cast playback engine

The WPF dashboard loads the engine in-process and owns its video surface, fullscreen state and Stats sidebar. A dedicated STA worker renders into the dashboard’s video surface. The default Direct3D 11 path presents directly to the existing child window. The shipped player executable also supports standalone capture diagnostics.

## Sources

| Source | How frames reach the renderer |
| --- | --- |
| Direct PS5 | A background libchiaki helper sends H.264 or HEVC access units through a local named pipe restricted to the current Windows user. FFmpeg decodes YUV420 or YUV420P10. GPU conversion uses the actual plane strides, luma samples, range, matrix and transfer function. The helper handles stereo Opus and SDL controller input. |
| Sony Remote Play | Windows Graphics Capture supplies the restored Sony window’s client area in BGRA8 or floating-point scRGB. Sony’s existing session supplies the audio and controller input. |
| Offline demo | A 960×540 GPU scene enters the presentation pipeline directly and accepts keyboard steering from the dashboard. |

Direct loads the three hash-verified FFmpeg DLLs from its local decoder cache and validates the exact libavcodec and libavutil versions. HEVC HDR can be presented as PQ/BT.2020 or mapped to SDR. The optional Sunshine/Moonlight client uses Moonlight’s own decoding and presentation path. [Decoder preparation](../sources/Prepare-Decoder.ps1) · [Direct helper protocol](../sources/native/README.md)

## Presentation

Choose `--renderer auto`, `d3d11`, `d3d12`, `vulkan`, or `opengl` before starting playback. Auto selects D3D11 on Windows. D3D11 draws into a DXGI backbuffer. D3D12 uses D3D11On12 for its shader graph and submits a GPU copy of the finished texture into a flip swap chain on the same D3D12 queue. Vulkan renders into a Vulkan swap chain on the same DXVK device. OpenGL uses GLSL for its picture and motion passes, shares textures with the D3D11 capture and overlay code through WGL_NV_DX_interop2, and swaps the window from that GL context. D3D11, D3D12 and Vulkan can present a 10-bit PQ/BT.2020 frame when the monitor and driver support HDR10. OpenGL presents SDR.

Windows Graphics Capture captures Sony Remote Play on system D3D11. D3D12 opens a shared Windows texture on the same adapter, and Vulkan imports the shared allocation with Win32 external memory. A keyed mutex orders access to the captured image. Direct PS5 playback uploads the decoded video planes separately. Individual providers such as OpenDLSS-NR can perform additional transfers inside their own effect pipeline.

## Processing

| Option | Behavior |
| --- | --- |
| `--mode original` | Exact texel loads at native size, bilinear enlargement, and four-region sampling during reduction. |
| `--mode sharp` | Bounded cubic enlargement and a spatial detail filter. |
| `--mode fsr` | FidelityFX Super Resolution 1 EASU for enlargement and RCAS sharpening. Native-size input goes directly to RCAS. |
| `--mode fsr-temporal` | AMD FSR temporal reconstruction through the AMD FSR SDK 2.4 on D3D12 or Vulkan. The active provider reports its version after context creation. |
| `--mode dlss-nr` | Experimental native-size NVIDIA DLSS 5 neural rendering. Requires Vulkan, SDR, a supported GPU and the optional provider. The runtime must pass an image test before processing the stream. |
| `--mode open-nr` | OpenDLSS-NR at source resolution. Direct3D 11, Direct3D 12 and Vulkan presentation use Vulkan inference on the selected GPU. HDR10 playback uses a tone-mapped RGB guide for inference and transfers predicted RGB changes back to the original floating-point HDR frame by inverting the guide tone mapping. Text coverage and peak highlights are protected. Requires a compatible driver and 310.8.0 weights. The model directory comes from the app selection (`P5CAST_OPEN_NR_MODEL`), `DLSS5VK_MODEL`, or `%LOCALAPPDATA%\P5Cast\providers\open-nr\model`, in that order. |
| `--mode open-nr-rtxvideo` | Run OpenDLSS-NR at source resolution, then RTX Video SR at the upscaling target. Other supported suffixes are `sharp`, `fsr`, `nis`, `fsr-temporal`, `dlss` and `dlaa`. Use `--mode open-nr` for the neural pass alone. Frame generation uses the usual provider and multiplier. |
| `--mode rtxvideo` | NVIDIA RTX Video Super Resolution at High quality on SDR, or HDR10 detail enhancement using a PQ luminance guide. |
| `--dlss-quality auto\|quality\|balanced\|performance\|ultra-performance` | Select the NGX quality mode for DLSS enlargement. Auto chooses by scale ratio. Native-size processing uses DLAA. |
| `--dlss-model auto\|k\|m\|l` | Request the driver default, DLSS 4 K or a DLSS 4.5 M/L preset. The snapshot reports the requested hint. |
| `--mode dlss` or `dlaa` | Experimental NVIDIA NGX processing in SDR or floating-point HDR. The player estimates motion, uses a constant depth plane and supplies zero camera jitter. DLSS uses DLAA when the input already fills or exceeds the processing dimensions. |
| `--comparison enhanced`, `original`, `side-by-side`, `wipe`, `hdr-sdr` | Side by side fits both views into separate panes. Wipe compares the original and enhanced frames at matching coordinates. On an HDR10 output, hdr-sdr instead compares the same finished frame with an SDR preview at Windows SDR white. Both splits support horizontal and vertical dragging. |
| `--wipe-position 0.5` | Initial divider position along its usable travel, from 0 to 1. Both endpoints leave room to grab the handle. Embedded callers can change this live with `SetWipePosition(float)`. |
| `--wipe-horizontal` | Places Original above Enhanced. The default vertical orientation puts Original on the left. Embedded callers can rotate the divider live with `SetWipeHorizontal(bool)`. |
| `--multiplier 1`, `2`, `3`, `4` | 1 presents captured frames. Higher values request intermediate frames. Achieved rates depend on source cadence, rendering time and display refresh. |
| `--frame-generation p5cast`, `amd`, or `fruc` | P5Cast offers motion-based interpolation at 2×–4×. AMD FSR uses multiplier 2 on D3D12 and Vulkan. The detected FSR 3 provider uses optical flow and the FSR 4 provider uses machine learning. NVIDIA FRUC uses optical flow at 2× in SDR or HDR10 on supported D3D11 and D3D12 devices. The HDR10 path uses a PQ guide and recovers fine gradations from the floating-point sources. |
| `--frame-generation xess-fg` or `dlss-fg` | Experimental D3D12 AI frame generation. Set `--renderer d3d12 --multiplier 2`. Each provider has a separate runtime/device probe. Frame generation receives the completed picture, estimated screen-space motion and flat depth. Standalone interpolator buttons select P5Cast's own generator. |
| `--resolution native`, `720`, `1080`, `1440`, `4k` | A fixed target scales the active upscaler to fit those dimensions at the source aspect ratio, independently of the window. Native fits the playback area. Targets smaller than the source preserve its original resolution. |
| `--hdr auto`, `off`, `hdr10` | Auto follows the active Windows display. D3D12 and Vulkan use SDR when the driver cannot create an HDR10 swap chain. Explicit HDR10 requires D3D11, D3D12 or Vulkan with an active HDR display and supported PQ/BT.2020 output. OpenGL presents SDR on Windows. |
| `--display primary` or `--display \\.\DISPLAY2` | Selects a monitor in standalone mode. Embedded presentation follows the dashboard surface. |

P5Cast's interpolator uses NVIDIA Optical Flow or a GPU block matcher. Duplicate checks exclude repeated source pictures from temporal processing. A stationary-edge check protects text from nearby motion. The renderer resets history after live picture and multiplier changes. A rejected switch preserves the previous picture. A vendor evaluation failure can fall back to Original and supplies a reason to the dashboard.

The D3D12 AI frame generators process the composited swap-chain image. Side-by-side and wipe therefore compare input and enhanced picture detail at matching scale, while generated frames can span both panes. Full-frame `--comparison original` bypasses frame generation.

The Intel provider uses the XeSS-FG proxy swap chain with XeLL pacing. It reads `framesPresented` from Intel after each visible presentation. To verify it on a real display, run `.\engine\xess-fg\Build.ps1 -Verify` from the project root and add `-VerifyHdr` for an active HDR10 display. Each test briefly shows a non-activating window. The RTX 4090 test reported 30 extra presentations from 32 moving inputs in SDR and again in HDR10, with two history resets per run. [Intel XeSS-FG developer guide](https://github.com/intel/xess/blob/v3.0.2/doc/xess_fg_developer_guide_english.md)

The NVIDIA provider uses NGX 310.9.16 on D3D12. After building the player, run `.\engine\dlss-fg\Build.ps1 -Verify` on a PC with an eligible NVIDIA GPU and an active HDR10 display. On the RTX 4090, each SDR and HDR10 test produced 109 extra presentations from 120 source frames. The two resets, eight hidden frames and first frame after showing the window again produced no extra presentations. In a separate pixel test, all 11 generated images per mode placed moving stripes closer to their halfway position than either source frame. [NVIDIA DLSS SDK](https://github.com/NVIDIA/DLSS/releases/tag/v310.9.16)

## Embedding and telemetry

Run `Cadrivect.Engine.EmbeddedPlayback.Run(string[])` on an STA worker with `--surface <HWND>`. The HWND must be a child of the calling process. `Configure` changes the picture mode and interpolation. `ConfigureOutput` changes the local upscaling target and presentation color. `Compare` temporarily selects Original and `Stop` terminates the session. A live resolution/HDR change can reset input history. The incoming console codec stays fixed until reconnection. Vendor bridges and models load relative to the engine assembly. [Engine API](EmbeddedPlayback.cs) · [Dashboard binding](../app/EngineSession.cs)

`Snapshot()` exposes `sampleId`, `elapsedMs`, source/output dimensions, active presentation and processing APIs (`renderer` and `processingRenderer`), requested and effective picture modes, and recent source, submission and generated frame rates. The analog gauge divides one arc into source and generated portions of the submitted rate, with a separate target marker. The source portion is the submitted rate minus the generated rate from the same sample interval. Stream diagnostics reports the rate of accepted source frames separately. The dial defaults to 240 FPS and its scale can be set from 120 to 1,000 FPS. Display scan-out and controller-to-display latency require separate measurements. Direct also reports compressed bitrate, decode time, receive-to-submit time, queued audio, underrun count and setup handshake RTT. WGC supplies capture-to-submit time. The audio queue excludes the Windows mixer and device buffers. Missing values appear as a dash. [Telemetry producer](Player.cs) · [Dashboard sidebar](../app/PlaybackMeter.cs)

## Testing

From the repository root on Windows x64, build the package inputs and exercise the offscreen decoder, embedded presentation, and GPU color/edge tests.

```powershell
.\Build.ps1
.\tests\Smoke-Decoder.ps1
.\tests\Smoke-Embedded.ps1
.\tests\Smoke-Embedded.ps1 -Renderer vulkan
.\engine\hdrtest\Verify.ps1
```

To exercise a real HDR display with the HEVC Main 10 fixture, use `.\tests\Smoke-Embedded.ps1 -Renderer vulkan -HdrDisplay '\\.\DISPLAY2'`. Substitute the Windows device name of an HDR-enabled monitor. The test verifies the negotiated presentation format, live SDR/HDR switching and, when available, moving the same playing surface to an SDR monitor and back.

The decoder test checks every YUV sample and the color metadata of H.264, HEVC and HEVC Main 10 fixtures. The embedded test submits packets to a normal WPF child surface. The HDR test validates PQ output, scaling, and one-pixel strokes. Add `-Nvidia` to the HDR test to check FP16 DLSS/DLAA output on compatible hardware.

Use `CadrivectPlayer.exe --capabilities --report <path>` for a JSON adapter report. Its `presentationDrivers` list identifies the API drivers included in the build. Opening a session checks whether the current hardware and driver can present to the selected display. Add `--renderer d3d12` or `--renderer vulkan` to test that backend. A positive AMD temporal upscaling, AMD frame generation, DLSS, DLAA or RTX Video SR result requires an actual evaluation and a successful output check. The optical-flow check executes the hardware API. Run the probe in a separate process during live playback.

To check AMD frame generation on a Windows GPU, run `engine\amd\framegeneration\Test-FrameGeneration.ps1 -Backend vulkan` or use `-Backend d3d12`. The fixture measures a moving object's midpoint and samples static SDR and HDR patches. Add `-Display '\\.\DISPLAY2'` with the name of an HDR-enabled display to exercise the embedded player's cadence, color output and comparison controls.

For standalone diagnostics, use `CadrivectPlayer.exe --self-test --mode fsr --multiplier 3 --seconds 5 --report <path>` in an interactive Windows session. Replace `--self-test` with `--hwnd <handle>` or `--process RemotePlay` to use a restored source window. Adding `--verify-output` reads 576 interior samples periodically for a luminance range. Routine playback keeps diagnostic pixel readback off. Standalone player hotkeys should be tested with one standalone player running.

On an HDR-enabled Windows monitor, run `.\engine\presentation\Build.ps1 -VerifyHdr` to check D3D12 and Vulkan. The probe compares packed 10-bit pixels after the presenter blits them into a matching readable target. It also submits the frame to the negotiated PQ swap chain.

## Dependencies

The player embeds NVIDIA Image Scaling SDK 1.0.3's spatial filter shaders and coefficients. The MIT license ships in the Windows package. [NIS integration and pinned sources](nis/README.md)

The Windows render host uses .NET Framework 4.8 and SharpDX 4.2.0. HLSL is compiled at build time and embedded in the player assembly. The FSR 1 headers are pinned to AMD commit `a21ffb8f6c13233ba336352bdff293894c706575`. Signed NVIDIA runtimes and their notices ship beside the engine. [AMD FidelityFX FSR](https://github.com/GPUOpen-Effects/FidelityFX-FSR/tree/a21ffb8f6c13233ba336352bdff293894c706575) · [SharpDX 4.2.0](https://github.com/sharpdx/SharpDX/tree/v4.2.0) · [NGX integration](nvidia/README.md) · [RTX Video integration](rtxvideo/README.md)

For Direct PS5 input, `--estimate-game-fps` enables decoded-luma sampling for the optional `estimatedGameFps` metric. The metric is null while disabled or when there is insufficient motion. Live `streamFps` is measured from the decoded frame count independently of sampling. `frucPresentedFps` counts submitted FRUC intermediate pictures.
