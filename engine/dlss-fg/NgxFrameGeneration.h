#pragma once
// Compiled inside Cadrivect.Ngx so SR and FG share one SDK/device registration.
#include "nvsdk_ngx_helpers_dlssg_d3d.h"
#include <memory>

struct NgxFrames {
    Effects12 gpu;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D12Resource> disabled,readback;
    NVSDK_NGX_Handle* model{};
    NVSDK_NGX_Parameter* params{};
    UINT w{},h{};
    bool hdr{},ownsDevice{};

    NVSDK_NGX_Result Open(ID3D11Device* d,const wchar_t* root,const wchar_t* cache,int width,int height,bool useHdr) {
        if(!d || !root || !cache || gpu.Open(d)!=S_OK) return NVSDK_NGX_Result_FAIL_InvalidParameter;
        auto r=acquireNgxDevice(gpu.device.Get(),root,cache);
        if(NVSDK_NGX_FAILED(r))return r;
        ownsDevice=true;
        r=NVSDK_NGX_D3D12_GetCapabilityParameters(&params);
        int supported{};
        if(NVSDK_NGX_SUCCEED(r)) r=params->Get(NVSDK_NGX_Parameter_FrameGeneration_Available,&supported);
        if(NVSDK_NGX_FAILED(r) || !supported) {
            int reason=0;
            if(params && NVSDK_NGX_SUCCEED(params->Get(NVSDK_NGX_Parameter_FrameGeneration_FeatureInitResult,&reason)) &&
                NVSDK_NGX_FAILED(static_cast<NVSDK_NGX_Result>(reason))) return static_cast<NVSDK_NGX_Result>(reason);
            return NVSDK_NGX_Result_FAIL_FeatureNotSupported;
        }
        if(!width && !height) return NVSDK_NGX_Result_Success; // Capability-only query.
        if(width<=0 || height<=0) return NVSDK_NGX_Result_FAIL_InvalidParameter;
        w=width; h=height; hdr=useHdr; d->GetImmediateContext(&context);
        NVSDK_NGX_DLSSG_Create_Params create{};
        create.Width=create.RenderWidth=w; create.Height=create.RenderHeight=h;
        create.NativeBackbufferFormat=hdr?DXGI_FORMAT_R10G10B10A2_UNORM:DXGI_FORMAT_B8G8R8A8_UNORM;
        auto hr=gpu.Submit([&](ID3D12GraphicsCommandList* cmd){r=NGX_D3D12_CREATE_DLSSG(cmd,1,1,&model,params,&create);});
        if(FAILED(hr) || FAILED(gpu.Wait(gpu.submitted))) return NVSDK_NGX_Result_Fail;
        if(NVSDK_NGX_FAILED(r)) return r;
        D3D12_HEAP_PROPERTIES heap{}; heap.Type=D3D12_HEAP_TYPE_DEFAULT; heap.CreationNodeMask=heap.VisibleNodeMask=1;
        D3D12_RESOURCE_DESC b{}; b.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER; b.Width=16; b.Height=1;
        b.DepthOrArraySize=b.MipLevels=b.SampleDesc.Count=1; b.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR; b.Flags=D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        hr=gpu.device->CreateCommittedResource(&heap,D3D12_HEAP_FLAG_NONE,&b,D3D12_RESOURCE_STATE_UNORDERED_ACCESS,nullptr,IID_PPV_ARGS(&disabled));
        if(FAILED(hr)) return NVSDK_NGX_Result_Fail;
        heap.Type=D3D12_HEAP_TYPE_READBACK; b.Flags=D3D12_RESOURCE_FLAG_NONE;
        hr=gpu.device->CreateCommittedResource(&heap,D3D12_HEAP_FLAG_NONE,&b,D3D12_RESOURCE_STATE_COPY_DEST,nullptr,IID_PPV_ARGS(&readback));
        return SUCCEEDED(hr)?NVSDK_NGX_Result_Success:NVSDK_NGX_Result_Fail;
    }

    NVSDK_NGX_Result Evaluate(ID3D11Texture2D* color,ID3D11Texture2D* motion,ID3D11Texture2D* depth,
        ID3D11Texture2D* output,bool reset,int* generated) {
        if(!model || !color || !motion || !depth || !output || !generated)return NVSDK_NGX_Result_FAIL_InvalidParameter;
        *generated=0;
        ID3D11Resource* resources[]={color,motion,depth,output};
        NVSDK_NGX_DLSSG_Opt_Eval_Params opt{};
        // NVIDIA's direct DLSS-FG Programming Guide, Required Input Engine Data:
        // NGX takes the scale to pixel units. Streamline takes the scale to UVs.
        opt.mvecScale[0]=1;opt.mvecScale[1]=1;
        opt.orthoProjection=true;opt.cameraMotionIncluded=true;opt.reset=reset;opt.colorBuffersHDR=hdr;
        opt.cameraRight[0]=opt.cameraUp[1]=opt.cameraFwd[2]=1;
        opt.cameraNear=.1f; opt.cameraFar=1000; opt.cameraFOV=1.5707963f;opt.cameraAspectRatio=float(w)/h;
        for(int k=0;k<4;k++)opt.cameraViewToClip[k][k]=opt.clipToCameraView[k][k]=opt.clipToLensClip[k][k]=opt.clipToPrevClip[k][k]=opt.prevClipToClip[k][k]=1;
        opt.mvecsSubrectSize=opt.depthSubrectSize=opt.backbufferSubrectSize=opt.outputInterpSubrectSize={w,h};
        NVSDK_NGX_Result result=NVSDK_NGX_Result_Fail;
        auto hr=gpu.WithResources(context.Get(),resources,[&](ID3D12GraphicsCommandList* list,ComPtr<ID3D12Resource>* native){
            NVSDK_NGX_D3D12_DLSSG_Eval_Params eval{};
            eval.pBackbuffer=native[0].Get();eval.pMVecs=native[1].Get();eval.pDepth=native[2].Get();
            eval.pOutputInterpFrame=native[3].Get();eval.pOutputDisableInterpolation=disabled.Get();
            result=NGX_D3D12_EVALUATE_DLSSG(list,model,params,&eval,&opt);
            if(NVSDK_NGX_FAILED(result)) return;
            D3D12_RESOURCE_BARRIER barrier{}; barrier.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition={disabled.Get(),0,D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_COPY_SOURCE};
            list->ResourceBarrier(1,&barrier);list->CopyBufferRegion(readback.Get(),0,disabled.Get(),0,4);
            std::swap(barrier.Transition.StateAfter,barrier.Transition.StateBefore);list->ResourceBarrier(1,&barrier);
        });
        if(FAILED(hr)||FAILED(gpu.Wait(gpu.submitted)))return NVSDK_NGX_Result_Fail;
        if(NVSDK_NGX_FAILED(result)) return result;
        void* ptr=nullptr; D3D12_RANGE read={0,1},written={0,0};
        if(FAILED(readback->Map(0,&read,&ptr)))return NVSDK_NGX_Result_Fail;
        *generated=(!reset && *static_cast<const unsigned char*>(ptr)==0)?1:0;
        readback->Unmap(0,&written);
        return result;
    }
    ~NgxFrames() {
        if(gpu.fence)gpu.Wait(gpu.submitted);
        if(model)NVSDK_NGX_D3D12_ReleaseFeature(model);
        if(params)NVSDK_NGX_D3D12_DestroyParameters(params);
        if(ownsDevice)releaseNgxDevice(gpu.device.Get());
    }
};

EXPORT unsigned NgxFgOpen(ID3D11Device* d,const wchar_t* root,const wchar_t* cache,int w,int h,int hdr,NgxFrames** out) {
    if(!out) return NVSDK_NGX_Result_FAIL_InvalidParameter;
    *out=nullptr;
    try {
        auto state=std::make_unique<NgxFrames>();auto r=state->Open(d,root,cache,w,h,hdr!=0);
        if(NVSDK_NGX_SUCCEED(r))*out=state.release();
        return r;
    }catch(...){return NVSDK_NGX_Result_Fail;}
}
EXPORT unsigned NgxFgEvaluate(NgxFrames* state,ID3D11Texture2D* color,ID3D11Texture2D* motion,ID3D11Texture2D* depth,
    ID3D11Texture2D* output,int reset,int* generated) {
    return state?state->Evaluate(color,motion,depth,output,reset!=0,generated):NVSDK_NGX_Result_FAIL_InvalidParameter;
}
EXPORT void NgxFgClose(NgxFrames* state) {delete state;}
