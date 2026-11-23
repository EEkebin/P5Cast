# P5Cast playback engine

The WPF dashboard loads the engine in-process and owns its video surface, fullscreen state and Stats sidebar. A dedicated STA worker drives a Direct3D 11 swap chain in the dashboard’s child window. The shipped player executable also supports standalone capture diagnostics.

## Sources

| Source | How frames reach the renderer |
| --- | --- |
| Direct PS5 | A background libchiaki helper sends H.264 or HEVC access units through a local named pipe restricted to the current Windows user. FFmpeg decodes YUV420 or YUV420P10. GPU conversion uses the actual plane strides, luma samples, range, matrix and transfer function. The helper handles stereo Opus and SDL controller input. |
| Sony Remote Play | Windows Graphics Capture supplies the restored Sony window’s client area in BGRA8 or floating-point scRGB. Sony’s existing session supplies the audio and controller input. |
| Offline demo | A 960×540 GPU scene enters the presentation pipeline directly and accepts keyboard steering from the dashboard. |

Direct loads the three hash-verified FFmpeg DLLs from its local decoder cache and validates the exact libavcodec and libavutil versions. HEVC HDR can be presented as PQ/BT.2020 or mapped to SDR. The optional Sunshine/Moonlight client uses Moonlight’s own decoding and presentation path. [Decoder preparation](../sources/Prepare-Decoder.ps1) · [Direct helper protocol](../sources/native/README.md)

## Processing

| Option | Behavior |
| --- | --- |
| `--mode original` | Exact texel loads at native size, bilinear enlargement, and four-region sampling during reduction. |
| `--mode sharp` | Bounded cubic enlargement and a spatial detail filter. |
| `--mode fsr` | FidelityFX Super Resolution 1 EASU for enlargement and RCAS sharpening. Native-size input goes directly to RCAS. |
| `--mode rtxvideo` | NVIDIA RTX Video Super Resolution at High quality on SDR. |
| `--mode dlss` or `dlaa` | Experimental NVIDIA NGX processing in SDR or floating-point HDR. The player estimates motion, uses a constant depth plane and supplies zero camera jitter. DLSS uses DLAA when the input already fills or exceeds the processing dimensions. |
| `--multiplier 1`, `2`, `3`, `4` | 1 presents captured frames. Higher values request intermediate frames. Achieved rates depend on source cadence, rendering time and display refresh. |
| `--resolution native`, `720`, `1080`, `1440`, `4k` | Caps the processing raster and preserves aspect ratio within the presentation viewport. |
| `--hdr auto`, `off`, `hdr10` | Auto follows the Windows display HDR state, with SDR for RTX Video SR. Explicit HDR10 requires an active HDR display and a swap chain supporting PQ/BT.2020. |
| `--display primary` or `--display \\.\DISPLAY2` | Selects a monitor in standalone mode. Embedded presentation follows the dashboard surface. |

Interpolation uses NVIDIA Optical Flow or a GPU block matcher. Duplicate checks exclude repeated source pictures from temporal processing. A stationary-edge check protects text from nearby motion. The renderer resets history after live picture and multiplier changes. A rejected switch preserves the previous picture. A vendor evaluation failure can fall back to Original and supplies a reason to the dashboard. P5Cast’s interpolator supplies 2×–4×. Native NVIDIA DLSS Frame Generation is unavailable.

## Embedding and telemetry

Run `Cadrivect.Engine.EmbeddedPlayback.Run(string[])` on an STA worker with `--surface <HWND>`. The HWND must be a child of the calling process. `Configure` changes the picture mode and interpolation. `ConfigureOutput` changes the local resolution cap and presentation color. `Compare` temporarily selects Original and `Stop` terminates the session. A live resolution/HDR change can reset input history. The incoming console codec stays fixed until reconnection. Vendor bridges and models load relative to the engine assembly. [Engine API](EmbeddedPlayback.cs) · [Dashboard binding](../app/EngineSession.cs)

`Snapshot()` exposes `sampleId`, `elapsedMs`, source/output dimensions, requested and effective modes, and recent source, submission and generated frame rates. The analog gauge draws three concentric arcs for source, generated and submitted rates, with a separate target marker. All arcs share a fixed 0–240+ FPS scale. Submissions include interpolated frames. Exact rates above 240 FPS remain visible in the numeric legend. Display scan-out and controller-to-display latency require separate measurements. Direct also reports compressed bitrate, decode time, receive-to-submit time, queued audio, underrun count and setup handshake RTT. WGC supplies capture-to-submit time. The audio queue excludes the Windows mixer and device buffers. Missing values appear as a dash. [Telemetry producer](Player.cs) · [Dashboard sidebar](../app/PlaybackMeter.cs)

## Testing

From the repository root on Windows x64, build the package inputs and exercise the offscreen decoder, embedded presentation, and GPU color/edge tests.

```powershell
.\Build.ps1
.\tests\Smoke-Decoder.ps1
.\tests\Smoke-Embedded.ps1
.\engine\hdrtest\Verify.ps1
```

The decoder test checks every YUV sample and the color metadata of H.264, HEVC and HEVC Main 10 fixtures. The embedded test submits packets to a normal WPF child surface. The HDR test validates PQ output, scaling, and one-pixel strokes. Add `-Nvidia` to the HDR test to check FP16 DLSS/DLAA output on compatible hardware.

Use `CadrivectPlayer.exe --capabilities --report <path>` for a JSON adapter report. A positive DLSS, DLAA or RTX Video SR result requires an actual evaluation and a successful output check. The optical-flow check executes the hardware API. Run the probe in a separate process during live playback.

For standalone diagnostics, use `CadrivectPlayer.exe --self-test --mode fsr --multiplier 3 --seconds 5 --report <path>` in an interactive Windows session. Replace `--self-test` with `--hwnd <handle>` or `--process RemotePlay` to use a restored source window. Adding `--verify-output` reads 576 interior samples periodically for a luminance range. Routine playback keeps diagnostic pixel readback off. Standalone player hotkeys should be tested with one standalone player running.

## Dependencies

Rendering uses .NET Framework 4.8, Direct3D 11 and SharpDX 4.2.0. HLSL is compiled at build time and embedded in the player assembly. The FSR 1 headers are pinned to AMD commit `a21ffb8f6c13233ba336352bdff293894c706575`. Signed NVIDIA runtimes and their notices ship beside the engine. [AMD FidelityFX FSR](https://github.com/GPUOpen-Effects/FidelityFX-FSR/tree/a21ffb8f6c13233ba336352bdff293894c706575) · [SharpDX 4.2.0](https://github.com/sharpdx/SharpDX/tree/v4.2.0) · [NGX integration](nvidia/README.md) · [RTX Video integration](rtxvideo/README.md)
