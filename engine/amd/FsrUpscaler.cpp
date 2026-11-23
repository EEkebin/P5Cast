#include "FsrApi.h"
#include "upscalers/include/ffx_upscale.h"
#ifdef P5_FSR_VK
#include "FsrVulkanCommands.h"
#endif
#include <new>
#include <memory>

struct Upscaler {
#ifdef P5_FSR_VK
    FsrVulkanCommands gpu;
#else
    Effects12 gpu;
#endif
    FsrApi api;
    ffxContext context{};
    FfxApiDimensions2D input{},output{};
    bool hdr{};
    ~Upscaler() {
        if (context) {
#ifdef P5_FSR_VK
            gpu.Drain();
#else
            gpu.Wait(gpu.submitted);
#endif
            api.functions.DestroyContext(&context,nullptr);
        }
    }
};

#define P5_EXPORT extern "C" __declspec(dllexport)
P5_EXPORT int P5FsrOpen(ID3D11Device* device,const wchar_t* loader,int w,int h,int ow,int oh,int hdr,
                         Upscaler** out,const char** version) {
    if (!device || !loader || !out || !version || w<1 || h<1 || ow<w || oh<h) return -1;
    *out=nullptr; *version=nullptr;
    std::unique_ptr<Upscaler> up(new(std::nothrow) Upscaler{});
    if (!up || !up->api.Load(loader)) return -2;
#ifdef P5_FSR_VK
    if (!up->gpu.Open(device)) return -2;
#else
    if (up->gpu.Open(device)!=S_OK) return -2;
#endif
    up->input={static_cast<unsigned>(w),static_cast<unsigned>(h)};
    up->output={static_cast<unsigned>(ow),static_cast<unsigned>(oh)};
    up->hdr=hdr!=0;
#ifdef P5_FSR_VK
    ffxCreateBackendVKDesc backend{};
    backend.header.type=FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_VK;
    backend.deviceContext={up->gpu.device,up->gpu.physical,vkGetDeviceProcAddr};
#else
    ffxCreateBackendDX12Desc backend{};
    backend.header.type=FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backend.device=up->gpu.device.Get();
#endif
    ffxCreateContextDescUpscale create{};
    create.header.type=FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    create.header.pNext=&backend.header;
    create.flags=FFX_UPSCALE_ENABLE_AUTO_EXPOSURE | (up->hdr ? FFX_UPSCALE_ENABLE_HIGH_DYNAMIC_RANGE : FFX_UPSCALE_ENABLE_NON_LINEAR_COLORSPACE);
    create.maxRenderSize=up->input;
    create.maxUpscaleSize=up->output;
    auto result=up->api.functions.CreateContext(&up->context,&create.header,nullptr);
    if (result) return static_cast<int>(result);
    ffxQueryGetProviderVersion provider{};
    provider.header.type=FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
    result=up->api.functions.Query(&up->context,&provider.header);
    if (result || !provider.versionName || !provider.versionId) return -3;
    *version=provider.versionName;
    *out=up.release();
    return 0;
}
template<size_t N>
static int Dispatch(Upscaler* up,ID3D11DeviceContext* ctx,ID3D11Resource* (&resources)[N],float milliseconds,int reset) {
    if (!up || !ctx) return -1;
    for (auto texture : resources) if (!texture) return -1;
    ffxReturnCode_t result=0;
    auto record=[&](void* list,const FfxApiResource* r) {
        ffxDispatchDescUpscale frame{};
        frame.header.type=FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
        frame.commandList=list;
        frame.color=r[0]; frame.motionVectors=r[1]; frame.depth=r[2]; frame.output=r[N-1];
        if (N==5) {
            // The HUD is already composited into the stream. Its camera and
            // motion can differ from the game behind it. Relax both history
            // accumulation and locks where the video-derived mask indicates ink.
            frame.transparencyAndComposition=r[3];
            frame.reactive=r[3];
        }
        frame.motionVectorScale={1,1};
        frame.renderSize=up->input;
        frame.upscaleSize=up->output;
        frame.frameTimeDelta=milliseconds;
        frame.preExposure=1;
        frame.reset=reset!=0;
        // Remote Play provides a composite video plane and no camera metadata.
        frame.cameraNear=0.1f; frame.cameraFar=100.f;
        frame.cameraFovAngleVertical=1.04719755f; frame.viewSpaceToMetersFactor=1;
        frame.flags=up->hdr ? 0 : FFX_UPSCALE_FLAG_NON_LINEAR_COLOR_SRGB;
        result=up->api.functions.Dispatch(&up->context,&frame.header);
    };
#ifdef P5_FSR_VK
    if (!up->gpu.WithResources(resources,record)) return -4;
#else
    auto hr=up->gpu.WithResources(ctx,resources,[&](ID3D12GraphicsCommandList* list,ComPtr<ID3D12Resource>* tex) {
        FfxApiResource descriptions[N]{};
        for(size_t i=0;i<N;i++) descriptions[i]=ffxApiGetResourceDX12(tex[i].Get(),
            i==N-1 ? FFX_API_RESOURCE_STATE_UNORDERED_ACCESS : FFX_API_RESOURCE_STATE_COMPUTE_READ);
        record(list,descriptions);
    });
    if (FAILED(hr)) return static_cast<int>(hr);
#endif
    return static_cast<int>(result);
}
P5_EXPORT int P5FsrDispatch(Upscaler* up,ID3D11DeviceContext* ctx,ID3D11Resource* color,ID3D11Resource* motion,
                             ID3D11Resource* depth,ID3D11Resource* output,float milliseconds,int reset) {
    ID3D11Resource* resources[]={color,motion,depth,output};
    return Dispatch(up,ctx,resources,milliseconds,reset);
}
P5_EXPORT int P5FsrDispatchHud(Upscaler* up,ID3D11DeviceContext* ctx,ID3D11Resource* color,ID3D11Resource* motion,
                               ID3D11Resource* depth,ID3D11Resource* output,float milliseconds,int reset,ID3D11Resource* composition) {
    if (!composition) return P5FsrDispatch(up,ctx,color,motion,depth,output,milliseconds,reset);
    ID3D11Resource* resources[]={color,motion,depth,composition,output};
    return Dispatch(up,ctx,resources,milliseconds,reset);
}
P5_EXPORT void P5FsrClose(Upscaler* up) { delete up; }
