# NVIDIA DLSS and DLAA for captured video

`NgxVideo` creates an NVIDIA NGX D3D11 feature for the selected mode and checks the result of each evaluation. At native resolution it requests DLAA. For enlargement it requests Quality below 2× per dimension, Performance from 2×, and Ultra Performance from 3×. NGX and the driver choose the model for that request. The renderer supplies color, pixel-space motion toward the previous frame, normalized depth, a history-reset signal and the output texture. For capture sources, the host estimates motion from video and uses flat depth. Real game-engine vectors, per-object depth and the original camera jitter are unavailable.

The renderer checks stationary edges before assigning a block motion vector to text. A source-resolution mask identifies narrow, nearly neutral strokes. After the NGX evaluation, a GPU pass uses the source RGB around those strokes to limit changes in local contrast. The mask and the corrected frames stay on the GPU.

Run `.\tests\Smoke-NgxQuality.ps1 -Display '\\.\DISPLAY2'` with an HDR display to compare the final SDR and HDR10 output against Original. The fixture measures uniform patches, fractional coverage at text edges, and changes in moving detail.

The Windows build downloads the official NVIDIA release runtime and build inputs at a fixed revision, checks each file against `sdk-files.json`, and validates the runtime's Authenticode publisher. The compiler helper uses an active x64 Visual Studio developer shell or fetches the pinned Microsoft toolchain into the current user's build cache. Packaged users run the existing binaries.

From the repository root, after building the player and its SharpDX dependencies, run:

```powershell
.\engine\nvidia\Build.ps1 -OutDir .\engine\bin -Verify
```

The motion test runs 14 genuine NGX evaluations per mode. Separate neutral-field tests check five SDR code values from black to reference white and four FP16 HDR levels from 20 to 1000 nits. GPU readback checks that a changing input changes the output. A stationary pattern is repeated for eight frames after a history reset, then compared against a native-size reference for DLAA and a bilinearly enlarged reference for DLSS. The reported differences establish that each effect changes the image. Evaluating perceived quality requires real gameplay.

NVIDIA SDK components distributed with the application are subject to the included `NVIDIA-RTX-LICENSE.txt`. NVIDIA DLSS SDK version 310.9.16, revision `9981c6350dc58c0ce21f024fd65e31e851c31689`. [Upstream source](https://github.com/NVIDIA/DLSS/tree/9981c6350dc58c0ce21f024fd65e31e851c31689) · [NVIDIA RTX SDK license](https://github.com/NVIDIA/DLSS/blob/9981c6350dc58c0ce21f024fd65e31e851c31689/LICENSE.txt)
