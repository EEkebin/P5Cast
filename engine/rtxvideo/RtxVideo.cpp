// This software contains source code provided by NVIDIA Corporation.
// NVIDIA SDK headers and the runtime remain subject to NVIDIA's RTX Video SDK license.
#include <d3d11.h>
#include <new>
#include "nvsdk_ngx_helpers_vsr.h"

// One NGX mode owns the renderer device for the lifetime of a player session.
struct VideoSession {
    ID3D11Device* device = nullptr;
    NVSDK_NGX_Parameter* parameters = nullptr;
    NVSDK_NGX_Handle* feature = nullptr;
    ~VideoSession() {
        if (feature) NVSDK_NGX_D3D11_ReleaseFeature(feature);
        if (parameters) NVSDK_NGX_D3D11_DestroyParameters(parameters);
        if (device) {
            NVSDK_NGX_D3D11_Shutdown1(device);
            device->Release();
        }
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
    auto result = NVSDK_NGX_D3D11_Init_with_ProjectID("e4dd82b5-56f6-43f3-bea7-4ae1f0f5523e",
        NVSDK_NGX_ENGINE_TYPE_CUSTOM, "Cadrivect", cachePath, device, &info);
    if (NVSDK_NGX_SUCCEED(result)) {
        session->device = device;
        device->AddRef();
        result = NVSDK_NGX_D3D11_GetCapabilityParameters(&session->parameters);
    }
    if (NVSDK_NGX_SUCCEED(result)) {
        int available = 0;
        result = session->parameters->Get(NVSDK_NGX_Parameter_VSR_Available, &available);
        if (NVSDK_NGX_SUCCEED(result) && !available) result = NVSDK_NGX_Result_FAIL_FeatureNotSupported;
    }
    if (NVSDK_NGX_SUCCEED(result)) {
        ID3D11DeviceContext* context = nullptr;
        device->GetImmediateContext(&context);
        NVSDK_NGX_Feature_Create_Params create = {};
        result = NGX_D3D11_CREATE_VSR_EXT(context, &session->feature, session->parameters, &create);
        context->Release();
    }
    if (NVSDK_NGX_FAILED(result)) { delete session; return result; }
    *output = session;
    return result;
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
    frame.pInput = input;
    frame.pOutput = output;
    frame.InputSubrectSize = {srcWidth, srcHeight};
    frame.OutputSubrectSize = {dstWidth, dstHeight};
    frame.QualityLevel = static_cast<NVSDK_NGX_VSR_QualityLevel>(quality);
    return NGX_D3D11_EVALUATE_VSR_EXT(context, session->feature, session->parameters, &frame);
}

VIDEO_API void VideoDestroy(VideoSession* session) { delete session; }
