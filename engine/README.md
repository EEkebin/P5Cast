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

Choose a renderer before starting a session with `--renderer auto`, `d3d11`, `d3d12`, `vulkan`, or `opengl`. Auto selects D3D11 on Windows. D3D12, Vulkan and OpenGL upload the finished image through SDL3 and present in a child of the same video surface. SDR uses BGRA8. On HDR-capable D3D12 and Vulkan devices, the player outputs packed 10-bit PQ/BT.2020 values to a verified HDR10 swap chain. The extra GPU readback and upload can affect frame times at high resolutions. D3D12 runs the effects shader graph through D3D11On12 and evaluates NVIDIA features on the underlying D3D12 textures and command queue. A privately named DXVK library runs the shader graph on Vulkan. OpenGL runs its own GLSL picture and motion passes. Windows capture and OpenGL dashboard overlay composition use system D3D11. If the requested API cannot initialize or present, playback reports the driver error.

## Processing

| Option | Behavior |
| --- | --- |
| `--mode original` | Exact texel loads at native size, bilinear enlargement, and four-region sampling during reduction. |
| `--mode sharp` | Bounded cubic enlargement and a spatial detail filter. |
| `--mode fsr` | FidelityFX Super Resolution 1 EASU for enlargement and RCAS sharpening. Native-size input goes directly to RCAS. |
| `--mode fsr-temporal` | AMD FSR temporal reconstruction through the AMD FSR SDK 2.4 on D3D12 or Vulkan. The active provider reports its version after context creation. |
| `--mode rtxvideo` | NVIDIA RTX Video Super Resolution at High quality on SDR. |
| `--dlss-quality auto\|quality\|balanced\|performance\|ultra-performance` | Select the NGX quality mode for DLSS enlargement. Auto chooses by scale ratio. Native-size processing uses DLAA. |
| `--dlss-model auto\|k\|m\|l` | Request the driver default, DLSS 4 K or a DLSS 4.5 M/L preset. The snapshot reports the requested hint. |
| `--mode dlss` or `dlaa` | Experimental NVIDIA NGX processing in SDR or floating-point HDR. The player estimates motion, uses a constant depth plane and supplies zero camera jitter. DLSS uses DLAA when the input already fills or exceeds the processing dimensions. |
| `--comparison enhanced`, `original`, `side-by-side`, `wipe` | Changes the view independently of picture settings. Side by side fits the entire image into each pane. Wipe overlays the original and enhanced frames at the same full-size pixel coordinates. |
| `--wipe-position 0.5` | Initial divider position along its usable travel, from 0 to 1. Both endpoints leave room to grab the handle. Embedded callers can change this live with `SetWipePosition(float)`. |
| `--wipe-horizontal` | Places Original above Enhanced. The default vertical orientation puts Original on the left. Embedded callers can rotate the divider live with `SetWipeHorizontal(bool)`. |
| `--multiplier 1`, `2`, `3`, `4` | 1 presents captured frames. Higher values request intermediate frames. Achieved rates depend on source cadence, rendering time and display refresh. |
| `--frame-generation p5cast` or `amd` | Select an interpolation provider. AMD uses multiplier 2 on D3D12 and Vulkan and reports the active provider version. |
| `--resolution native`, `720`, `1080`, `1440`, `4k` | Caps enlargement by an active upscaler. Native allows enlargement up to the fitted picture size. Source-sized processing is preserved when the limit or window is smaller than the input. |
| `--hdr auto`, `off`, `hdr10` | Auto follows the active Windows display. D3D12 and Vulkan use SDR when the driver cannot create an HDR10 swap chain. Explicit HDR10 requires D3D11, D3D12 or Vulkan with an active HDR display and supported PQ/BT.2020 output. OpenGL and RTX Video SR use SDR on Windows. |
| `--display primary` or `--display \\.\DISPLAY2` | Selects a monitor in standalone mode. Embedded presentation follows the dashboard surface. |

Interpolation uses NVIDIA Optical Flow or a GPU block matcher. Duplicate checks exclude repeated source pictures from temporal processing. A stationary-edge check protects text from nearby motion. The renderer resets history after live picture and multiplier changes. A rejected switch preserves the previous picture. A vendor evaluation failure can fall back to Original and supplies a reason to the dashboard. P5Cast’s interpolator supplies 2×–4×. Native NVIDIA DLSS Frame Generation is unavailable.

## Embedding and telemetry

Run `Cadrivect.Engine.EmbeddedPlayback.Run(string[])` on an STA worker with `--surface <HWND>`. The HWND must be a child of the calling process. `Configure` changes the picture mode and interpolation. `ConfigureOutput` changes the local resolution cap and presentation color. `Compare` temporarily selects Original and `Stop` terminates the session. A live resolution/HDR change can reset input history. The incoming console codec stays fixed until reconnection. Vendor bridges and models load relative to the engine assembly. [Engine API](EmbeddedPlayback.cs) · [Dashboard binding](../app/EngineSession.cs)

`Snapshot()` exposes `sampleId`, `elapsedMs`, source/output dimensions, active presentation and processing APIs (`renderer` and `processingRenderer`), requested and effective picture modes, and recent source, submission and generated frame rates. The analog gauge divides one arc into source and generated portions of the submitted rate, with a separate target marker. The source portion is the submitted rate minus the generated rate from the same sample interval. Incoming FPS is reported separately under Stream diagnostics. The dial starts at a 0–240 FPS scale and expands in 240 FPS increments when needed. Display scan-out and controller-to-display latency require separate measurements. Direct also reports compressed bitrate, decode time, receive-to-submit time, queued audio, underrun count and setup handshake RTT. WGC supplies capture-to-submit time. The audio queue excludes the Windows mixer and device buffers. Missing values appear as a dash. [Telemetry producer](Player.cs) · [Dashboard sidebar](../app/PlaybackMeter.cs)

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

The Windows render host uses .NET Framework 4.8 and SharpDX 4.2.0. HLSL is compiled at build time and embedded in the player assembly. The FSR 1 headers are pinned to AMD commit `a21ffb8f6c13233ba336352bdff293894c706575`. Signed NVIDIA runtimes and their notices ship beside the engine. [AMD FidelityFX FSR](https://github.com/GPUOpen-Effects/FidelityFX-FSR/tree/a21ffb8f6c13233ba336352bdff293894c706575) · [SharpDX 4.2.0](https://github.com/sharpdx/SharpDX/tree/v4.2.0) · [NGX integration](nvidia/README.md) · [RTX Video integration](rtxvideo/README.md)
