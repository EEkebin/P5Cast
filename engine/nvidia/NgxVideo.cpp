// Cadrivect's C ABI to the published NVIDIA DLSS SDK.
#include <d3d11.h>
#include <new>
#include "nvsdk_ngx_helpers_d3d.h"

struct VideoState {
    ID3D11Device* device{};
    NVSDK_NGX_Parameter* parameters{};
    NVSDK_NGX_Handle* feature{};
    unsigned inputWidth{};
    unsigned inputHeight{};
    bool initialized{};
    ~VideoState() {
        if (feature) NVSDK_NGX_D3D11_ReleaseFeature(feature);
        if (parameters) NVSDK_NGX_D3D11_DestroyParameters(parameters);
        if (initialized) NVSDK_NGX_D3D11_Shutdown1(device);
        if (device) device->Release();
    }
};

#define EXPORT extern "C" __declspec(dllexport)
EXPORT unsigned NgxConfigureFeature(VideoState* state, ID3D11DeviceContext* context,
    unsigned outputWidth, unsigned outputHeight, int dlaa, int hdr) {
    if (!state || !context || !outputWidth || !outputHeight) return NVSDK_NGX_Result_FAIL_InvalidParameter;
    if (state->feature) {
        auto released=NVSDK_NGX_D3D11_ReleaseFeature(state->feature);
        if (NVSDK_NGX_FAILED(released)) return released;
        state->feature=nullptr;
    }
    NVSDK_NGX_DLSS_Create_Params create{};
    create.Feature.InWidth=state->inputWidth;
    create.Feature.InHeight=state->inputHeight;
    create.Feature.InTargetWidth=outputWidth;
    create.Feature.InTargetHeight=outputHeight;
    create.Feature.InPerfQualityValue=dlaa ? NVSDK_NGX_PerfQuality_Value_DLAA : NVSDK_NGX_PerfQuality_Value_MaxQuality;
    create.InFeatureCreateFlags=NVSDK_NGX_DLSS_Feature_Flags_MVLowRes | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure;
    if (hdr) create.InFeatureCreateFlags |= NVSDK_NGX_DLSS_Feature_Flags_IsHDR;
    return NGX_D3D11_CREATE_DLSS_EXT(context,&state->feature,state->parameters,&create);
}
EXPORT unsigned NgxOpen2(ID3D11Device* device, ID3D11DeviceContext* context,
    const wchar_t* runtimeFolder, const wchar_t* dataFolder, unsigned inputWidth,
    unsigned inputHeight, unsigned outputWidth, unsigned outputHeight, int dlaa, int hdr,
    VideoState** out, int* stage) {
    if (!out || !stage || !device || !context || !runtimeFolder || !dataFolder)
        return NVSDK_NGX_Result_FAIL_InvalidParameter;
    *out=nullptr;
    *stage=0;
    VideoState* state=new(std::nothrow) VideoState{};
    if (!state) return NVSDK_NGX_Result_Fail;
    device->AddRef();
    state->device=device;
    state->inputWidth=inputWidth;
    state->inputHeight=inputHeight;
    NVSDK_NGX_FeatureCommonInfo info{};
    const wchar_t* paths[]={runtimeFolder};
    info.PathListInfo.Path=paths;
    info.PathListInfo.Length=1;
    *stage=1;
    NVSDK_NGX_Result result=NVSDK_NGX_D3D11_Init_with_ProjectID(
        "15b28241-0e5d-45d2-8a98-f9676d9723ea",NVSDK_NGX_ENGINE_TYPE_CUSTOM,"Cadrivect 1",
        dataFolder,device,&info,NVSDK_NGX_Version_API);
    if (NVSDK_NGX_SUCCEED(result)) {
        state->initialized=true;
        *stage=2;
        result=NVSDK_NGX_D3D11_GetCapabilityParameters(&state->parameters);
    }
    if (NVSDK_NGX_SUCCEED(result)) {
        *stage=3;
        int available=0;
        result=state->parameters->Get(NVSDK_NGX_Parameter_SuperSampling_Available,&available);
        if (NVSDK_NGX_SUCCEED(result) && !available) result=NVSDK_NGX_Result_FAIL_FeatureNotSupported;
    }
    if (NVSDK_NGX_SUCCEED(result)) {
        *stage=4;
        result=static_cast<NVSDK_NGX_Result>(NgxConfigureFeature(state,context,outputWidth,outputHeight,dlaa,hdr));
    }
    if (NVSDK_NGX_FAILED(result)) delete state;
    else *out=state;
    return result;
}
// Keep the original SDR entry point available during in-place application upgrades.
EXPORT unsigned NgxOpen(ID3D11Device* device, ID3D11DeviceContext* context,
    const wchar_t* runtimeFolder, const wchar_t* dataFolder, unsigned inputWidth,
    unsigned inputHeight, unsigned outputWidth, unsigned outputHeight, int dlaa,
    VideoState** out, int* stage) {
    return NgxOpen2(device,context,runtimeFolder,dataFolder,inputWidth,inputHeight,outputWidth,outputHeight,dlaa,0,out,stage);
}
EXPORT unsigned NgxEvaluate(VideoState* state,ID3D11DeviceContext* context,ID3D11Resource* color,
    ID3D11Resource* motion,ID3D11Resource* depth,ID3D11Resource* output,
    float jitterX,float jitterY,float frameMs,int reset) {
    if (!state || !context || !color || !motion || !depth || !output) return NVSDK_NGX_Result_FAIL_InvalidParameter;
    NVSDK_NGX_D3D11_DLSS_Eval_Params params{};
    params.Feature.pInColor=color;
    params.Feature.pInOutput=output;
    params.pInMotionVectors=motion;
    params.pInDepth=depth;
    params.InRenderSubrectDimensions.Width=state->inputWidth;
    params.InRenderSubrectDimensions.Height=state->inputHeight;
    params.InMVScaleX=params.InMVScaleY=1.0f;
    params.InJitterOffsetX=jitterX;
    params.InJitterOffsetY=jitterY;
    params.InFrameTimeDeltaInMsec=frameMs;
    params.InPreExposure=params.InExposureScale=1.0f;
    params.InReset=reset;
    return NGX_D3D11_EVALUATE_DLSS_EXT(context,state->feature,state->parameters,&params);
}
EXPORT void NgxClose(VideoState* state) { delete state; }
