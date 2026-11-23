# RTX Video Super Resolution

The player passes the captured SDR texture to NVIDIA's Direct3D 11 Video Super Resolution feature at High quality. The destination is an 8-bit RGBA texture with unordered access. Input and output stay on the active graphics device. Each player session owns its NGX device initialization and releases its feature before shutdown.

Run the following in Windows PowerShell from the repository root after building the managed player. The GPU test needs an RTX GPU and a compatible driver.

```powershell
.\engine\rtxvideo\Build.ps1 -OutDir .\engine\bin -Verify
```

The test runs entirely offscreen. It feeds one 960 × 540 BGRA frame to NVIDIA's bicubic baseline and High model, reads both 1920 × 1080 outputs, and measures differing RGB pixels. The production renderer uses High on every evaluation.

The build retrieves NVIDIA's pinned SDK 1.1.0 from its public NGC file catalog, verifies the archive's SHA-256, and verifies the NVIDIA Authenticode signature and digest of the release runtime. The packaged components are `Cadrivect.RtxVideo.dll`, `nvngx_vsr.dll`, and `NVIDIA_RTX_Video_SDK_License.pdf`. The NVIDIA software in those binaries is provided under the accompanying license. The SDK headers, libraries and developer samples remain in the build workspace. [NVIDIA SDK download](https://catalog.ngc.nvidia.com/orgs/nvidia/multimedia/models/dlpp/1.5/file-browser) · [NVIDIA RTX Video SDK](https://developer.nvidia.com/rtx-video-sdk)
