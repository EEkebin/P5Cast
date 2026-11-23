// This software contains source code provided by NVIDIA Corporation.
// NVIDIA SDK headers and the runtime remain subject to NVIDIA's RTX Video SDK license.
#include "../nvidia/D3D12Effects.h"
#include <new>
#include "nvsdk_ngx_helpers_vsr.h"

// One NGX mode owns the renderer device for the lifetime of a player session.
struct VideoSession {
    ID3D11Device* device = nullptr;
    Effects12 dx12;
    NVSDK_NGX_Parameter* parameters = nullptr;
    NVSDK_NGX_Handle* feature = nullptr;
    ~VideoSession() {
        if (dx12.device) {
            dx12.Wait(dx12.submitted);
            if (feature) NVSDK_NGX_D3D12_ReleaseFeature(feature);
            if (parameters) NVSDK_NGX_D3D12_DestroyParameters(parameters);
            if (device) NVSDK_NGX_D3D12_Shutdown1(dx12.device.Get());
        } else {
            if (feature) NVSDK_NGX_D3D11_ReleaseFeature(feature);
            if (parameters) NVSDK_NGX_D3D11_DestroyParameters(parameters);
            if (device) NVSDK_NGX_D3D11_Shutdown1(device);
        }
        if (device) device->Release();
    }
};

#define VIDEO_API extern "C" __declspec(dllexport)

VIDEO_API unsigned VideoCreate(ID3D11Device* device, const wchar_t* cachePath,
    const wchar_t* featurePath, VideoSession** output) {
    if (!device || !cachePath || !featurePath || !output) return NVSDK_NGX_Result_FAIL_InvalidParameter;
    *output = nullptr;
    auto session = new(std::nothrow) VideoSession;
    if (!session) return E_OUTOFMEMORY;
    NVSDK_NGX_FeatureCommonInfo info = {};
    info.PathListInfo.Path = &featurePath;
    info.PathListInfo.Length = 1;
    HRESULT opened=session->dx12.Open(device);
    if (FAILED(opened)) { delete session; return NVSDK_NGX_Result_Fail; }
    const char* project="e4dd82b5-56f6-43f3-bea7-4ae1f0f5523e";
    auto result=session->dx12.device
        ? NVSDK_NGX_D3D12_Init_with_ProjectID(project,NVSDK_NGX_ENGINE_TYPE_CUSTOM,"Cadrivect",cachePath,session->dx12.device.Get(),&info)
        : NVSDK_NGX_D3D11_Init_with_ProjectID(project,NVSDK_NGX_ENGINE_TYPE_CUSTOM,"Cadrivect",cachePath,device,&info);
    if (NVSDK_NGX_SUCCEED(result)) {
        session->device = device;
        device->AddRef();
        result=session->dx12.device ? NVSDK_NGX_D3D12_GetCapabilityParameters(&session->parameters) : NVSDK_NGX_D3D11_GetCapabilityParameters(&session->parameters);
    }
    if (NVSDK_NGX_SUCCEED(result)) {
        int available = 0;
        result = session->parameters->Get(NVSDK_NGX_Parameter_VSR_Available, &available);
        if (NVSDK_NGX_SUCCEED(result) && !available) result = NVSDK_NGX_Result_FAIL_FeatureNotSupported;
    }
    if (NVSDK_NGX_SUCCEED(result)) {
        NVSDK_NGX_Feature_Create_Params create{};
        if (session->dx12.device) {
            auto hr=session->dx12.Submit([&](ID3D12GraphicsCommandList* list) {
                result=NGX_D3D12_CREATE_VSR_EXT(list,1,1,&session->feature,session->parameters,&create);
            });
            if (FAILED(hr)) result=NVSDK_NGX_Result_Fail;
        } else {
            ComPtr<ID3D11DeviceContext> context;
            device->GetImmediateContext(&context);
            result=NGX_D3D11_CREATE_VSR_EXT(context.Get(),&session->feature,session->parameters,&create);
        }
    }
    if (NVSDK_NGX_FAILED(result)) { delete session; return result; }
    *output = session;
    return result;
}

template<class Params,class Resource> static void setFrame(Params& frame,Resource* input,Resource* output,
    unsigned srcWidth,unsigned srcHeight,unsigned dstWidth,unsigned dstHeight,unsigned quality) {
    frame.pInput = input;
    frame.pOutput = output;
    frame.InputSubrectSize = {srcWidth, srcHeight};
    frame.OutputSubrectSize = {dstWidth, dstHeight};
    frame.QualityLevel = static_cast<NVSDK_NGX_VSR_QualityLevel>(quality);
}

VIDEO_API unsigned VideoEvaluate(VideoSession* session, ID3D11DeviceContext* context,
    ID3D11Texture2D* input, ID3D11Texture2D* output, unsigned srcWidth, unsigned srcHeight,
    unsigned dstWidth, unsigned dstHeight, unsigned quality) {
    if (!session || !context || !input || !output || quality > 4)
        return NVSDK_NGX_Result_FAIL_InvalidParameter;
    D3D11_TEXTURE2D_DESC inDesc, outDesc;
    input->GetDesc(&inDesc);
    output->GetDesc(&outDesc);
    if (!srcWidth || !srcHeight || !dstWidth || !dstHeight || inDesc.Width < srcWidth ||
        inDesc.Height < srcHeight || outDesc.Width < dstWidth || outDesc.Height < dstHeight)
        return NVSDK_NGX_Result_FAIL_InvalidParameter;
    if (!(outDesc.BindFlags & D3D11_BIND_UNORDERED_ACCESS)) return NVSDK_NGX_Result_FAIL_RWFlagMissing;
    NVSDK_NGX_D3D11_VSR_Eval_Params frame = {};
    if (session->dx12.device) {
        ID3D11Resource* resources[]={input,output};
        NVSDK_NGX_Result result=NVSDK_NGX_Result_Fail;
        auto hr=session->dx12.WithResources(context,resources,[&](ID3D12GraphicsCommandList* list,ComPtr<ID3D12Resource>* tex) {
            NVSDK_NGX_D3D12_VSR_Eval_Params native{};
            setFrame(native,tex[0].Get(),tex[1].Get(),srcWidth,srcHeight,dstWidth,dstHeight,quality);
            result=NGX_D3D12_EVALUATE_VSR_EXT(list,session->feature,session->parameters,&native);
        });
        return FAILED(hr) ? NVSDK_NGX_Result_Fail : result;
    }
    setFrame(frame,input,output,srcWidth,srcHeight,dstWidth,dstHeight,quality);
    return NGX_D3D11_EVALUATE_VSR_EXT(context, session->feature, session->parameters, &frame);
}

VIDEO_API void VideoDestroy(VideoSession* session) { delete session; }
