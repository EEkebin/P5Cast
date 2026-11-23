#include "../FsrApi.h"
#include "framegeneration/include/ffx_framegeneration.h"
#ifdef P5_FSR_VK
#include "../FsrVulkanCommands.h"
#endif
#include <memory>
#include <new>

struct FsrFrameGenerator {
#ifdef P5_FSR_VK
    FsrVulkanCommands gpu;
#else
    Effects12 gpu;
#endif
    FsrApi api;
    ffxContext context{};
    FfxApiDimensions2D render{}, output{};
    uint64_t frameId{};
    bool hdr{};
    ~FsrFrameGenerator() {
        if(context) {
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
P5_EXPORT int P5FsrFgOpen(ID3D11Device* device,const wchar_t* loader,int w,int h,int ow,int oh,int hdr,
                          FsrFrameGenerator** result,const char** version) {
    if(!device || !loader || !result || !version || w<1 || h<1 || ow<w || oh<h) return E_INVALIDARG;
    *result=nullptr; *version=nullptr;
    std::unique_ptr<FsrFrameGenerator> fg(new(std::nothrow) FsrFrameGenerator{});
    if(!fg) return E_OUTOFMEMORY;
    if(!fg->api.Load(loader)) return E_NOINTERFACE;
#ifdef P5_FSR_VK
    if(!fg->gpu.Open(device)) return E_NOINTERFACE;
#else
    if(fg->gpu.Open(device)!=S_OK) return E_NOINTERFACE;
#endif
    fg->render={static_cast<uint32_t>(w),static_cast<uint32_t>(h)};
    fg->output={static_cast<uint32_t>(ow),static_cast<uint32_t>(oh)};
    fg->hdr=hdr!=0;
#ifdef P5_FSR_VK
    ffxCreateBackendVKDesc backend{};
    backend.header.type=FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_VK;
    backend.deviceContext={fg->gpu.device,fg->gpu.physical,vkGetDeviceProcAddr};
#else
    ffxCreateBackendDX12Desc backend{};
    backend.header.type=FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backend.device=fg->gpu.device.Get();
#endif
    ffxCreateContextDescFrameGenerationVersion apiVersion{};
    apiVersion.header.type=FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION;
    apiVersion.header.pNext=&backend.header;
    apiVersion.version=FFX_FRAMEGENERATION_VERSION;
    ffxCreateContextDescFrameGeneration create{};
    create.header.type=FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
    create.header.pNext=&apiVersion.header;
    create.displaySize=fg->output;
    create.maxRenderSize=fg->render;
    create.backBufferFormat=hdr ? FFX_API_SURFACE_FORMAT_R16G16B16A16_FLOAT : FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM;
    create.flags=hdr ? FFX_FRAMEGENERATION_ENABLE_HIGH_DYNAMIC_RANGE : 0;
    auto status=fg->api.functions.CreateContext(&fg->context,&create.header,nullptr);
    if(status) return int(status);
    ffxQueryGetProviderVersion provider{};
    provider.header.type=FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
    status=fg->api.functions.Query(&fg->context,&provider.header);
    if(status || !provider.versionId || !provider.versionName) return E_FAIL;
    *version=provider.versionName;
    *result=fg.release();
    return 0;
}

P5_EXPORT int P5FsrFgDispatch(FsrFrameGenerator* fg,ID3D11DeviceContext* ctx,ID3D11Resource* color,ID3D11Resource* motion,
                              ID3D11Resource* depth,ID3D11Resource* output,float milliseconds,int reset) {
    if(!fg || !ctx || !color || !motion || !depth || !output || !(milliseconds>0)) return E_INVALIDARG;
    ffxConfigureDescFrameGeneration config{};
    config.header.type=FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
    config.flags=FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY;
    config.frameGenerationEnabled=true;
    config.frameID=++fg->frameId;
    config.generationRect={0,0,static_cast<int32_t>(fg->output.width),static_cast<int32_t>(fg->output.height)};
    int result=static_cast<int>(fg->api.functions.Configure(&fg->context,&config.header));
    if(result) return 0x10000+result;
    ID3D11Resource* resources[]={color,motion,depth,output};
    auto record=[&](void* commands,const FfxApiResource* images) {
        ffxDispatchDescFrameGenerationPrepareV2 prep{};
        prep.header.type=FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_V2;
        prep.frameID=config.frameID;
        prep.commandList=commands;
        prep.renderSize=fg->render;
        prep.motionVectorScale={1.f,1.f};
        prep.frameTimeDelta=milliseconds;
        prep.reset=reset || config.frameID==1;
        prep.cameraNear=.1f; prep.cameraFar=100.f;
        prep.cameraFovAngleVertical=1.04719755f; prep.viewSpaceToMetersFactor=1.f;
        prep.cameraRight[0]=1; prep.cameraUp[1]=1; prep.cameraForward[2]=1;
        prep.motionVectors=images[1];
        prep.depth=images[2];
        result=static_cast<int>(fg->api.functions.Dispatch(&fg->context,&prep.header));
        if(result) {result+=0x20000;return;}
        ffxDispatchDescFrameGeneration frame{};
        frame.header.type=FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION;
        frame.frameID=config.frameID;
        frame.commandList=commands;
        frame.generationRect=config.generationRect;
        frame.presentColor=images[0];
        frame.outputs[0]=images[3];
        // Both the FSR 3 fallback and the ML provider expose a midpoint dispatch.
        frame.numGeneratedFrames=1;
        frame.reset=prep.reset;
        frame.backbufferTransferFunction=fg->hdr ? FFX_API_BACKBUFFER_TRANSFER_FUNCTION_SCRGB : FFX_API_BACKBUFFER_TRANSFER_FUNCTION_SRGB;
        frame.minMaxLuminance[0]=0.f;
        frame.minMaxLuminance[1]=fg->hdr?1000.f:80.f;
        result=static_cast<int>(fg->api.functions.Dispatch(&fg->context,&frame.header));
        if(result) result+=0x30000;
    };
#ifdef P5_FSR_VK
    if(!fg->gpu.WithResources(resources,record)) return E_FAIL;
#else
    const HRESULT hr=fg->gpu.WithResources(ctx,resources,[&](ID3D12GraphicsCommandList* commands,auto& textures) {
        FfxApiResource images[]={ffxApiGetResourceDX12(textures[0].Get()),ffxApiGetResourceDX12(textures[1].Get()),
            ffxApiGetResourceDX12(textures[2].Get()),ffxApiGetResourceDX12(textures[3].Get(),FFX_API_RESOURCE_STATE_UNORDERED_ACCESS)};
        record(commands,images);
    });
    if(FAILED(hr)) return static_cast<int>(hr);
#endif
    return result;
}
P5_EXPORT void P5FsrFgClose(FsrFrameGenerator* fg) {delete fg;}
