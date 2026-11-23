# AMD temporal upscaling

The D3D12 and Vulkan bridges load their respective signed upscalers from AMD FSR SDK 2.4.0. AMD selects a compatible provider for the device. After creating the context, the bridge queries its provider version. A device running FSR 3.1.6 reports that version even when the same runtime contains FSR 4.1.2. [AMD SDK release](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/tag/v2.4.0) · [AMD provider queries](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/v2.4.0/Kits/FidelityFX/docs/getting-started/ffx-api.md)

The renderer supplies decoded color, backward motion estimated from the stream in source pixels, and a flat depth plane. Both bridges use zero jitter because the PS5 stream does not expose camera offsets. SDR input uses perceptual sRGB values. HDR uses linear floating-point values.

Run `.\engine\amd\Build.ps1` after building the D3D12 and Vulkan effects devices. The build fetches individually pinned headers, DLLs and notices, checks their SHA-256 hashes and verifies AMD’s signature on each runtime. Run `.\engine\amd\Test-Fsr.ps1` and `.\engine\amd\Test-Fsr.ps1 -Backend vulkan` on a compatible Windows GPU. The test prints the active provider and reads back SDR and HDR image patches across a history reset.
