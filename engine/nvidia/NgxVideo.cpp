// Cadrivect's C ABI to the published NVIDIA DLSS SDK.
#include "D3D12Effects.h"
#include <new>
#include "nvsdk_ngx_helpers_d3d.h"
#include "DlssModelHint.h"
#include "NgxDevice.h"

extern "C" __declspec(dllexport) HRESULT P5SetDlssModelHint(ID3D11Device* device,unsigned preset) {
    if (!device || (preset && preset!=11 && preset!=12 && preset!=13)) return E_INVALIDARG;
    return device->SetPrivateData(P5DlssModelId,sizeof(preset),&preset);
}

extern "C" __declspec(dllexport) HRESULT P5CreateD3D12Effects(IUnknown* adapter, ID3D11Device** device) {
    if (!device) return E_POINTER;
    *device=nullptr;
    ComPtr<ID3D12Device> native;
    HRESULT hr=D3D12CreateDevice(adapter,D3D_FEATURE_LEVEL_11_0,IID_PPV_ARGS(&native));
    if (FAILED(hr)) return hr;
    D3D12_COMMAND_QUEUE_DESC desc{};
    desc.Type=D3D12_COMMAND_LIST_TYPE_DIRECT;
    ComPtr<ID3D12CommandQueue> queue;
    hr=native->CreateCommandQueue(&desc,IID_PPV_ARGS(&queue));
    if (FAILED(hr)) return hr;
    IUnknown* queues[]={queue.Get()};
    ComPtr<ID3D11Device> wrapper;
    hr=D3D11On12CreateDevice(native.Get(),D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,queues,1,0,&wrapper,nullptr,nullptr);
    if (FAILED(hr)) return hr;
    ComPtr<ID3D11On12Device2> interop;
    hr=wrapper.As(&interop);
    if (FAILED(hr)) return hr;
    hr=wrapper->SetPrivateDataInterface(EffectsQueueId,queue.Get());
    if (SUCCEEDED(hr)) *device=wrapper.Detach();
    return hr;
}

struct VideoState {
    ID3D11Device* device{};
    Effects12 dx12;
    NVSDK_NGX_Parameter* parameters{};
    NVSDK_NGX_Handle* feature{};
    unsigned inputWidth{};
    unsigned inputHeight{};
    bool initialized{};
    NVSDK_NGX_Result releaseFeature() {
        if (dx12.device) {
            if (FAILED(dx12.Wait(dx12.submitted))) return NVSDK_NGX_Result_Fail;
            return NVSDK_NGX_D3D12_ReleaseFeature(feature);
        }
        return NVSDK_NGX_D3D11_ReleaseFeature(feature);
    }
    ~VideoState() {
        if (feature) releaseFeature();
        if (parameters) {
            if (dx12.device) NVSDK_NGX_D3D12_DestroyParameters(parameters);
            else NVSDK_NGX_D3D11_DestroyParameters(parameters);
        }
        if (initialized) {
            if (dx12.device) releaseNgxDevice(dx12.device.Get());
            else NVSDK_NGX_D3D11_Shutdown1(device);
        }
        if (device) device->Release();
    }
};

#define EXPORT extern "C" __declspec(dllexport)
static NVSDK_NGX_PerfQuality_Value qualityForSize(const VideoState& state, unsigned width, unsigned height, bool dlaa) {
    if (dlaa) return NVSDK_NGX_PerfQuality_Value_DLAA;
    // A Remote Play input has a fixed size. Match NGX to the actual reconstruction ratio
    // so the runtime can use its recommended model for Performance or Ultra Performance.
    if (width >= 3u * state.inputWidth && height >= 3u * state.inputHeight)
        return NVSDK_NGX_PerfQuality_Value_UltraPerformance;
    if (width >= 2u * state.inputWidth && height >= 2u * state.inputHeight)
        return NVSDK_NGX_PerfQuality_Value_MaxPerf;
    return NVSDK_NGX_PerfQuality_Value_MaxQuality;
}

EXPORT unsigned NgxConfigureFeature2(VideoState* state, ID3D11DeviceContext* context,
    unsigned outputWidth, unsigned outputHeight, int dlaa, int hdr, int requestedQuality, int* activeQuality) {
    // The bridge uses NGX PerfQuality values. -1 means choose from the live image dimensions.
    if (requestedQuality != -1 && (requestedQuality < NVSDK_NGX_PerfQuality_Value_MaxPerf ||
        requestedQuality > NVSDK_NGX_PerfQuality_Value_UltraPerformance)) return NVSDK_NGX_Result_FAIL_InvalidParameter;
    if (!state || !context || !outputWidth || !outputHeight) return NVSDK_NGX_Result_FAIL_InvalidParameter;
    if (state->feature) {
        auto released=state->releaseFeature();
        if (NVSDK_NGX_FAILED(released)) return released;
        state->feature=nullptr;
    }
    ApplyDlssModelHint(state->device,state->parameters);
    NVSDK_NGX_DLSS_Create_Params create{};
    create.Feature.InWidth=state->inputWidth;
    create.Feature.InHeight=state->inputHeight;
    create.Feature.InTargetWidth=outputWidth;
    create.Feature.InTargetHeight=outputHeight;
    create.Feature.InPerfQualityValue=(dlaa || requestedQuality == -1)
        ? qualityForSize(*state,outputWidth,outputHeight,dlaa!=0)
        : static_cast<NVSDK_NGX_PerfQuality_Value>(requestedQuality);
    create.InFeatureCreateFlags=NVSDK_NGX_DLSS_Feature_Flags_MVLowRes | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure;
    if (hdr) create.InFeatureCreateFlags |= NVSDK_NGX_DLSS_Feature_Flags_IsHDR;
    NVSDK_NGX_Result result=NVSDK_NGX_Result_Fail;
    if (state->dx12.device) {
        auto hr=state->dx12.Submit([&](ID3D12GraphicsCommandList* list) {
            result=NGX_D3D12_CREATE_DLSS_EXT(list,1,1,&state->feature,state->parameters,&create);
        });
        if (FAILED(hr)) return NVSDK_NGX_Result_Fail;
    } else result=NGX_D3D11_CREATE_DLSS_EXT(context,&state->feature,state->parameters,&create);
    if (NVSDK_NGX_SUCCEED(result) && activeQuality) *activeQuality=create.Feature.InPerfQualityValue;
    return result;
}
EXPORT unsigned NgxConfigureFeature(VideoState* state, ID3D11DeviceContext* context,
    unsigned outputWidth, unsigned outputHeight, int dlaa, int hdr) {
    return NgxConfigureFeature2(state,context,outputWidth,outputHeight,dlaa,hdr,-1,nullptr);
}
EXPORT unsigned NgxOpen3(ID3D11Device* device, ID3D11DeviceContext* context,
    const wchar_t* runtimeFolder, const wchar_t* dataFolder, unsigned inputWidth,
    unsigned inputHeight, unsigned outputWidth, unsigned outputHeight, int dlaa, int hdr,
    int requestedQuality, int* activeQuality, VideoState** out, int* stage) {
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
    HRESULT opened=state->dx12.Open(device);
    if (FAILED(opened)) { delete state; return NVSDK_NGX_Result_Fail; }
    const char* project="15b28241-0e5d-45d2-8a98-f9676d9723ea";
    NVSDK_NGX_Result result=state->dx12.device
        ? acquireNgxDevice(state->dx12.device.Get(),runtimeFolder,dataFolder)
        : NVSDK_NGX_D3D11_Init_with_ProjectID(project,NVSDK_NGX_ENGINE_TYPE_CUSTOM,"Cadrivect 1",dataFolder,device,&info,NVSDK_NGX_Version_API);
    if (NVSDK_NGX_SUCCEED(result)) {
        state->initialized=true;
        *stage=2;
        result=state->dx12.device ? NVSDK_NGX_D3D12_GetCapabilityParameters(&state->parameters) : NVSDK_NGX_D3D11_GetCapabilityParameters(&state->parameters);
    }
    if (NVSDK_NGX_SUCCEED(result)) {
        *stage=3;
        int available=0;
        result=state->parameters->Get(NVSDK_NGX_Parameter_SuperSampling_Available,&available);
        if (NVSDK_NGX_SUCCEED(result) && !available) result=NVSDK_NGX_Result_FAIL_FeatureNotSupported;
    }
    if (NVSDK_NGX_SUCCEED(result)) {
        *stage=4;
        result=static_cast<NVSDK_NGX_Result>(NgxConfigureFeature2(state,context,outputWidth,outputHeight,dlaa,hdr,requestedQuality,activeQuality));
    }
    if (NVSDK_NGX_FAILED(result)) delete state;
    else *out=state;
    return result;
}
EXPORT unsigned NgxOpen2(ID3D11Device* device, ID3D11DeviceContext* context,
    const wchar_t* runtimeFolder, const wchar_t* dataFolder, unsigned inputWidth,
    unsigned inputHeight, unsigned outputWidth, unsigned outputHeight, int dlaa, int hdr,
    VideoState** out, int* stage) {
    return NgxOpen3(device,context,runtimeFolder,dataFolder,inputWidth,inputHeight,outputWidth,outputHeight,dlaa,hdr,-1,nullptr,out,stage);
}
// Keep the original SDR entry point available during in-place application upgrades.
EXPORT unsigned NgxOpen(ID3D11Device* device, ID3D11DeviceContext* context,
    const wchar_t* runtimeFolder, const wchar_t* dataFolder, unsigned inputWidth,
    unsigned inputHeight, unsigned outputWidth, unsigned outputHeight, int dlaa,
    VideoState** out, int* stage) {
    return NgxOpen2(device,context,runtimeFolder,dataFolder,inputWidth,inputHeight,outputWidth,outputHeight,dlaa,0,out,stage);
}
template<class Params, class Resource> static void setFrame(Params& params,const VideoState& state,
    Resource* color,Resource* motion,Resource* depth,Resource* output,float jitterX,float jitterY,float frameMs,int reset) {
    params.Feature.pInColor=color;
    params.Feature.pInOutput=output;
    params.pInMotionVectors=motion;
    params.pInDepth=depth;
    params.InRenderSubrectDimensions.Width=state.inputWidth;
    params.InRenderSubrectDimensions.Height=state.inputHeight;
    params.InMVScaleX=params.InMVScaleY=1.0f;
    params.InJitterOffsetX=jitterX;
    params.InJitterOffsetY=jitterY;
    params.InFrameTimeDeltaInMsec=frameMs;
    params.InPreExposure=params.InExposureScale=1.0f;
    params.InReset=reset;
}
EXPORT unsigned NgxEvaluate(VideoState* state,ID3D11DeviceContext* context,ID3D11Resource* color,
    ID3D11Resource* motion,ID3D11Resource* depth,ID3D11Resource* output,
    float jitterX,float jitterY,float frameMs,int reset) {
    if (!state || !context || !color || !motion || !depth || !output) return NVSDK_NGX_Result_FAIL_InvalidParameter;
    NVSDK_NGX_D3D11_DLSS_Eval_Params params{};
    if (state->dx12.device) {
        ID3D11Resource* resources[]={color,motion,depth,output};
        NVSDK_NGX_Result result=NVSDK_NGX_Result_Fail;
        auto hr=state->dx12.WithResources(context,resources,[&](ID3D12GraphicsCommandList* list,ComPtr<ID3D12Resource>* tex) {
            NVSDK_NGX_D3D12_DLSS_Eval_Params native{};
            setFrame(native,*state,tex[0].Get(),tex[1].Get(),tex[2].Get(),tex[3].Get(),jitterX,jitterY,frameMs,reset);
            result=NGX_D3D12_EVALUATE_DLSS_EXT(list,state->feature,state->parameters,&native);
        });
        return FAILED(hr) ? NVSDK_NGX_Result_Fail : result;
    }
    setFrame(params,*state,color,motion,depth,output,jitterX,jitterY,frameMs,reset);
    return NGX_D3D11_EVALUATE_DLSS_EXT(context,state->feature,state->parameters,&params);
}
EXPORT void NgxClose(VideoState* state) { delete state; }

#include "../dlss-fg/NgxFrameGeneration.h"
