# LumaRelay player

The Windows x64 player captures the client area of a selected window with Windows Graphics Capture, copies the image between Direct3D 11 textures on the GPU and displays it in a flip-model swap chain. The player accepts SDR video. Sound and controller input stay with the source application. The fullscreen surface keeps the source window focused.

```powershell
.\engine\build.ps1
.\engine\bin\LumaPlayer.exe --process RemotePlay --mode fsr --multiplier 1
```

Use `--hwnd 12345` to choose a particular window. Add `--windowed` for a movable preview. In fullscreen, press **Ctrl+Alt+B** to compare the original picture or **Ctrl+Alt+Q** to close. Windowed playback also accepts **B** and **Esc** while focused. A source must stay open and restored.

| Setting | Behavior |
| --- | --- |
| `--mode original` | Bilinear scaling to fit the display, preserving aspect ratio |
| `--mode sharp` | Spatial sharpening with a five-tap filter |
| `--mode fsr` | AMD FidelityFX Super Resolution 1 EASU followed by RCAS |
| `--multiplier 1` | Display each captured frame immediately |
| `--multiplier 2`, `3`, `4` | Experimental block-motion interpolation between distinct captured frames, up to the requested multiplier |

Interpolation estimates motion on 12-pixel tiles, rejects poor matches and filters duplicate images by comparing all RGB pixels on the GPU. It adds latency and can introduce distortion around moving HUD elements, scene cuts or fast pans. Multipliers are targets. GPU workload, capture cadence, Windows presentation and monitor refresh rate determine visible output.

## Verification

Run this in an interactive Windows desktop session. The test opens a moving source, captures it through the normal Windows API, runs the selected GPU shaders and checks pixel readback. App counters measure submitted frames. Monitor scan-out and controller-to-display latency require separate instrumentation.

```powershell
$report = Join-Path $env:TEMP 'lumarelay-render.json'
$player = Start-Process .\engine\bin\LumaPlayer.exe -ArgumentList "--self-test --mode fsr --multiplier 3 --seconds 5 --report `"$report`"" -PassThru -Wait
if ($player.ExitCode) { throw 'Player verification failed.' }
Get-Content $report
```

For a real source, pass `--hwnd`, `--seconds 8`, `--report <path>` and `--verify-output`. The optional diagnostic reads 576 interior image samples once per second to report minimum and maximum luminance. Reports contain aggregate statistics. Errors are written to `%LOCALAPPDATA%\LumaRelay\player-error.log`. Routine playback keeps pixel data in GPU memory.

## Dependencies

The offline runtime uses the Windows .NET Framework, Direct3D 11 and three MIT-licensed SharpDX 4.2.0 assemblies. The build verifies SHA-256 hashes of the downloaded NuGet packages and compiles shaders into the executable. SharpDX.D3DCompiler is a build-time dependency.

AMD's unmodified single-precision FSR 1 headers are vendored from commit `a21ffb8f6c13233ba336352bdff293894c706575`. License notices ship alongside the player. [AMD FidelityFX FSR](https://github.com/GPUOpen-Effects/FidelityFX-FSR/tree/a21ffb8f6c13233ba336352bdff293894c706575) · [SharpDX 4.2.0](https://github.com/sharpdx/SharpDX/tree/v4.2.0)

