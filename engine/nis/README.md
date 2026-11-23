# NVIDIA Image Scaling

The player uses NVIDIA Image Scaling SDK 1.0.3, pinned to commit `35e13ba316c98eeecf16f37eae70ce88019911f6`. The files in `engine/third_party/nis` are unmodified copies. The upstream `licence.txt` is stored as `LICENSE.txt` and distributed with the Windows player as `NVIDIA-NIS-LICENSE.txt`. [Pinned SDK](https://github.com/NVIDIAGameWorks/NVIDIAImageScaling/tree/35e13ba316c98eeecf16f37eae70ce88019911f6/NIS) · [MIT license](https://github.com/NVIDIAGameWorks/NVIDIAImageScaling/blob/35e13ba316c98eeecf16f37eae70ce88019911f6/licence.txt)

The build compiles the SDK shader and embeds its two FP32 coefficient tables. Each texture contains 64 filter phases, with eight coefficients per phase packed into two RGBA texels. `NisConfig.Constants` builds the 112-byte shader constant buffer for a full-frame viewport using the SDK's SDR or HDR PQ configuration. NVScaler supports between 1× and 2× enlargement per axis in one pass. NVSharpen uses a native-size viewport.
