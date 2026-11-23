#pragma once
#include "api/include/ffx_api_loader.h"
#ifndef P5_FSR_VK
#include "../nvidia/D3D12Effects.h"
#include "api/include/dx12/ffx_api_dx12.h"
#endif
struct FsrApi {
    HMODULE module{};
    ffxFunctions functions{};
    bool Load(const wchar_t* absoluteLoaderPath) {
        module=LoadLibraryExW(absoluteLoaderPath,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) return false;
        ffxLoadFunctions(&functions,module);
        return functions.CreateContext && functions.DestroyContext && functions.Query && functions.Dispatch && functions.Configure;
    }
    ~FsrApi() { if (module) FreeLibrary(module); }
};
