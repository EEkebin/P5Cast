#pragma once
// D3D12 VSR, SR and FG must call the same copy of the NGX runtime and
// release the device registration only after the last feature closes.
struct NgxDeviceApi {
    NVSDK_NGX_Result (*acquire)(ID3D12Device*,const wchar_t*,const wchar_t*);
    void (*release)(ID3D12Device*);
    NVSDK_NGX_Result (*getParameters)(NVSDK_NGX_Parameter**);
    NVSDK_NGX_Result (*destroyParameters)(NVSDK_NGX_Parameter*);
    NVSDK_NGX_Result (*create)(ID3D12GraphicsCommandList*,NVSDK_NGX_Feature,NVSDK_NGX_Parameter*,NVSDK_NGX_Handle**);
    NVSDK_NGX_Result (*evaluate)(ID3D12GraphicsCommandList*,NVSDK_NGX_Handle*,NVSDK_NGX_Parameter*);
    NVSDK_NGX_Result (*close)(NVSDK_NGX_Handle*);
};
