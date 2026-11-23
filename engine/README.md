# Cadrivect player

The Windows x64 player captures the client area of a selected window with Windows Graphics Capture, processes GPU textures and displays them in a flip-model swap chain. It supports BGRA8 SDR and floating-point scRGB capture for HDR10 presentation. Sound and controller input stay with the source application. The fullscreen surface keeps the source window focused.

```powershell
.\engine\build.ps1
.\engine\bin\CadrivectPlayer.exe --process RemotePlay --mode fsr --multiplier 1
```

Use `--hwnd 12345` to choose a particular window. Add `--windowed` for a movable preview. In fullscreen, press **Ctrl+Alt+B** to compare the original picture or **Ctrl+Alt+Q** to close. Windowed playback also accepts **B** and **Esc** while focused. A source must stay open and restored.

| Setting | Behavior |
| --- | --- |
| `--mode original` | Bilinear scaling to fit the display, preserving aspect ratio |
| `--mode sharp` | Spatial sharpening with a five-tap filter |
| `--mode fsr` | AMD FidelityFX Super Resolution 1 EASU followed by RCAS |
| `--mode rtxvideo` | NVIDIA RTX Video Super Resolution for SDR |
| `--mode dlss` or `--mode dlaa` | Experimental NVIDIA NGX processing with estimated video motion. Both support floating-point HDR |
| `--display primary` or `--display \\.\DISPLAY2` | Select a Windows monitor or TV for playback |
| `--resolution native\|720\|1080\|1440\|4k` | Cap processing resolution and fit the result to the window |
| `--hdr auto\|off\|hdr10` | Choose adaptive, SDR, or explicit PQ/BT.2020 HDR10 presentation |
| `--multiplier 1` | Display each captured frame immediately |
| `--multiplier 2`, `3`, `4` | Experimental block-motion interpolation between distinct captured frames, up to the requested multiplier |

Interpolation uses NVIDIA optical flow when available and falls back to a 12-pixel tile search. It checks match quality and filters duplicate images on the GPU. It adds latency and can introduce distortion around moving HUD elements, scene cuts or fast pans. Multipliers are targets. GPU workload, capture cadence, Windows presentation and monitor refresh rate determine visible output.

## Verification

Run this in an interactive Windows desktop session. The test opens a moving source, captures it through the normal Windows API, runs the selected GPU shaders and checks pixel readback. App counters measure submitted frames. Monitor scan-out and controller-to-display latency require separate instrumentation.

```powershell
$report = Join-Path $env:TEMP 'cadrivect-render.json'
$player = Start-Process .\engine\bin\CadrivectPlayer.exe -ArgumentList "--self-test --mode fsr --multiplier 3 --seconds 5 --report `"$report`"" -PassThru -Wait
if ($player.ExitCode) { throw 'Player verification failed.' }
Get-Content $report
```

For a real source, pass `--hwnd`, `--seconds 8`, `--report <path>` and `--verify-output`. The optional diagnostic reads 576 interior image samples once per second to report minimum and maximum luminance. Reports contain aggregate statistics. Errors are written to `%LOCALAPPDATA%\LumaRelay\player-error.log`. Routine playback keeps pixel data in GPU memory.

## Dependencies

Core playback uses the Windows .NET Framework, Direct3D 11 and SharpDX 4.2.0. NVIDIA options use the packaged NGX and RTX Video runtimes and the installed NVIDIA driver. The build verifies SHA-256 hashes of the downloaded NuGet packages and compiles shaders into the executable. SharpDX.D3DCompiler is a build-time dependency.

AMD's unmodified single-precision FSR 1 headers are vendored from commit `a21ffb8f6c13233ba336352bdff293894c706575`. License notices ship alongside the player. [AMD FidelityFX FSR](https://github.com/GPUOpen-Effects/FidelityFX-FSR/tree/a21ffb8f6c13233ba336352bdff293894c706575) · [SharpDX 4.2.0](https://github.com/sharpdx/SharpDX/tree/v4.2.0)
