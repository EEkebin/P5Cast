#include "../third_party/nis/NIS_Config.h"
#include <cstddef>
#include <cstdio>

// Each row of eight taps is a pair of RGBA32F texels. Export the SDK's
// actual arrays at build time so no coefficient parsing occurs in the player.
int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) return 1;
    static_assert(kPhaseCount == 64 && kFilterSize == 8, "Unexpected NIS coefficient layout");
    static_assert(offsetof(NISConfig, kScaleX) == 48 && offsetof(NISConfig, kInputViewportOriginX) == 72 &&
                  offsetof(NISConfig, reserved1) == 108, "Update the NIS shader constant layout");
    FILE* file = nullptr;
    if (_wfopen_s(&file, argv[1], L"wb") != 0) return 1;
    bool complete = fwrite(coef_scale, sizeof(coef_scale), 1, file) == 1 &&
                    fwrite(coef_usm, sizeof(coef_usm), 1, file) == 1;
    return fclose(file) == 0 && complete ? 0 : 1;
}
