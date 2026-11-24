#pragma once
#include <mutex>
#include <string>
#include <map>

// NGX registration is per native device. FG and SR/DLAA can be created and
// destroyed in any order, including when the user switches the picture mode.
static std::mutex ngxDeviceLock;
static std::map<ID3D12Device*,unsigned> ngxDeviceRefs;
static NVSDK_NGX_Result acquireNgxDevice(ID3D12Device* device,const wchar_t* root,const wchar_t* data) {
    std::lock_guard<std::mutex> guard(ngxDeviceLock);
    auto it=ngxDeviceRefs.find(device);
    if(it!=ngxDeviceRefs.end()) {++it->second;return NVSDK_NGX_Result_Success;}
    std::wstring fg=std::wstring(root)+L"\\dlss-fg";
    const wchar_t* paths[]={root,fg.c_str()};
    NVSDK_NGX_FeatureCommonInfo info{};
    info.PathListInfo.Path=paths;info.PathListInfo.Length=2;
    auto result=NVSDK_NGX_D3D12_Init_with_ProjectID("15b28241-0e5d-45d2-8a98-f9676d9723ea",NVSDK_NGX_ENGINE_TYPE_CUSTOM,
        "Cadrivect 1",data,device,&info,NVSDK_NGX_Version_API);
    if(NVSDK_NGX_SUCCEED(result)) {
        try {ngxDeviceRefs.emplace(device,1);}
        catch(...) {NVSDK_NGX_D3D12_Shutdown1(device);return NVSDK_NGX_Result_Fail;}
    }
    return result;
}
static void releaseNgxDevice(ID3D12Device* device) {
    std::lock_guard<std::mutex> guard(ngxDeviceLock);
    auto it=ngxDeviceRefs.find(device);
    if(it!=ngxDeviceRefs.end() && --it->second==0) {
        NVSDK_NGX_D3D12_Shutdown1(device);
        ngxDeviceRefs.erase(it);
    }
}
#include "NgxDeviceApi.h"
extern "C" __declspec(dllexport) const NgxDeviceApi* P5NgxDeviceApi() {
    static const NgxDeviceApi api={
        acquireNgxDevice,releaseNgxDevice,
        [](NVSDK_NGX_Parameter** p){return NVSDK_NGX_D3D12_GetCapabilityParameters(p);},
        [](NVSDK_NGX_Parameter* p){return NVSDK_NGX_D3D12_DestroyParameters(p);},
        [](ID3D12GraphicsCommandList* l,NVSDK_NGX_Feature f,NVSDK_NGX_Parameter* p,NVSDK_NGX_Handle** h){return NVSDK_NGX_D3D12_CreateFeature(l,f,p,h);},
        [](ID3D12GraphicsCommandList* l,NVSDK_NGX_Handle* h,NVSDK_NGX_Parameter* p){return NVSDK_NGX_D3D12_EvaluateFeature_C(l,h,p,nullptr);},
        [](NVSDK_NGX_Handle* h){return NVSDK_NGX_D3D12_ReleaseFeature(h);}
    };
    return &api;
}
