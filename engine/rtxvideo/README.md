# RTX Video Super Resolution

The player evaluates NVIDIA RTX Video Super Resolution at High quality on Direct3D 11 and 12. SDR feeds the full-color video directly to the SDK. For HDR10, the SDK processes an 8-bit monochrome guide whose brightness uses the PQ curve. P5Cast applies the resulting local detail to the original floating-point BT.2020 color, bounds it around edges, and encodes the result to the 10-bit PQ display surface. The HDR stream retains its own brightness and color throughout. NVIDIA's SDK 1.1.0 documents 8-bit SDR input for VSR. All textures stay on the active graphics device.

Run the following in Windows PowerShell from the repository root after building the managed player. The GPU test needs an RTX GPU and a compatible driver.

```powershell
.\engine\rtxvideo\Build.ps1 -OutDir .\engine\bin -Verify
```

The test runs entirely offscreen. It feeds one 960 × 540 BGRA frame to NVIDIA's bicubic baseline and High model, reads both 1920 × 1080 outputs, and measures differing RGB pixels. The production renderer uses High on every evaluation.

On an HDR-enabled display, `tests\Smoke-NgxQuality.ps1` also measures the player's final HDR10 output against Original, including peak highlights, flat colors, fine detail and a grayscale ramp.

The build retrieves NVIDIA's pinned SDK 1.1.0 from its public NGC file catalog, verifies the archive's SHA-256, and verifies the NVIDIA Authenticode signature and digest of the release runtime. The packaged components are `Cadrivect.RtxVideo.dll`, `nvngx_vsr.dll`, and `NVIDIA_RTX_Video_SDK_License.pdf`. The NVIDIA software in those binaries is provided under the accompanying license. The SDK headers, libraries and developer samples remain in the build workspace. [NVIDIA SDK download](https://catalog.ngc.nvidia.com/orgs/nvidia/multimedia/models/dlpp/1.5/file-browser) · [NVIDIA RTX Video SDK](https://developer.nvidia.com/rtx-video-sdk)
