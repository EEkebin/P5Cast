// This software contains source code provided by NVIDIA Corporation.
// NVIDIA SDK headers and the runtime remain subject to NVIDIA's RTX Video SDK license.
#include "../nvidia/D3D12Effects.h"
#include <new>
#include "nvsdk_ngx_helpers_vsr.h"
#include "../nvidia/NgxDeviceApi.h"
#include <string>

// D3D12 shares the device registration and feature handles with the DLSS bridge.
struct VideoSession {
    ID3D11Device* device = nullptr;
    Effects12 dx12;
    HMODULE sharedModule{};
    const NgxDeviceApi* shared{};
    NVSDK_NGX_Parameter* parameters = nullptr;
    NVSDK_NGX_Handle* feature = nullptr;
    ~VideoSession() {
        if (dx12.device) {
            dx12.Wait(dx12.submitted);
            if (feature) shared->close(feature);
            if (parameters) shared->destroyParameters(parameters);
            if (device) shared->release(dx12.device.Get());
        } else {
            if (feature) NVSDK_NGX_D3D11_ReleaseFeature(feature);
            if (parameters) NVSDK_NGX_D3D11_DestroyParameters(parameters);
            if (device) NVSDK_NGX_D3D11_Shutdown1(device);
        }
        if (device) device->Release();
        if(sharedModule) FreeLibrary(sharedModule);
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
    if(session->dx12.device) {
        std::wstring path=std::wstring(featurePath)+L"\\Cadrivect.Ngx.dll";
        session->sharedModule=LoadLibraryExW(path.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        auto get=session->sharedModule ? reinterpret_cast<const NgxDeviceApi*(*)()>(GetProcAddress(session->sharedModule,"P5NgxDeviceApi")) : nullptr;
        if(!get || !(session->shared=get())) { delete session; return NVSDK_NGX_Result_FAIL_NotInitialized; }
    }
    const char* project="e4dd82b5-56f6-43f3-bea7-4ae1f0f5523e";
    auto result=session->dx12.device
        ? session->shared->acquire(session->dx12.device.Get(),featurePath,cachePath)
        : NVSDK_NGX_D3D11_Init_with_ProjectID(project,NVSDK_NGX_ENGINE_TYPE_CUSTOM,"Cadrivect",cachePath,device,&info);
    if (NVSDK_NGX_SUCCEED(result)) {
        session->device = device;
        device->AddRef();
        result=session->dx12.device ? session->shared->getParameters(&session->parameters) : NVSDK_NGX_D3D11_GetCapabilityParameters(&session->parameters);
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
                session->parameters->Set(NVSDK_NGX_Parameter_CreationNodeMask,1u);
                session->parameters->Set(NVSDK_NGX_Parameter_VisibilityNodeMask,1u);
                result=session->shared->create(list,NVSDK_NGX_Feature_VSR,session->parameters,&session->feature);
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
            auto* p=session->parameters;
            p->Set(NVSDK_NGX_Parameter_Input1,tex[0].Get());
            p->Set(NVSDK_NGX_Parameter_Output,tex[1].Get());
            p->Set(NVSDK_NGX_Parameter_Rect_X,0u);p->Set(NVSDK_NGX_Parameter_Rect_Y,0u);
            p->Set(NVSDK_NGX_Parameter_Rect_W,srcWidth);p->Set(NVSDK_NGX_Parameter_Rect_H,srcHeight);
            p->Set(NVSDK_NGX_Parameter_OutRect_X,0u);p->Set(NVSDK_NGX_Parameter_OutRect_Y,0u);
            p->Set(NVSDK_NGX_Parameter_OutRect_W,dstWidth);p->Set(NVSDK_NGX_Parameter_OutRect_H,dstHeight);
            p->Set(NVSDK_NGX_Parameter_VSR_QualityLevel,quality);
            result=session->shared->evaluate(list,session->feature,p);
        });
        return FAILED(hr) ? NVSDK_NGX_Result_Fail : result;
    }
    setFrame(frame,input,output,srcWidth,srcHeight,dstWidth,dstHeight,quality);
    return NGX_D3D11_EVALUATE_VSR_EXT(context, session->feature, session->parameters, &frame);
}

VIDEO_API void VideoDestroy(VideoSession* session) { delete session; }
