// Intel XeSS-FG/XeLL on the D3D12 queue underlying P5Cast's effect textures.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dxgi1_6.h>
#include <array>
#include <cmath>
#include <cstdio>
#include <memory>
#include <string>
#include "xell/xell_d3d12.h"
#include "xess_fg/xefg_swapchain_d3d12.h"
#include "../nvidia/D3D12Effects.h"

namespace {
struct Failure { const wchar_t* operation; LONG result; };
void Check(LONG status,const wchar_t* op) { if(status<0) throw Failure{op,status}; }
void Require(bool ok,const wchar_t* op) { if(!ok) throw Failure{op,E_INVALIDARG}; }
void ClearReason(wchar_t* reason,int chars) { if(reason && chars>0) *reason=L'\0'; }
int Report(Failure failure,wchar_t* reason,int chars) {
    if(reason && chars>0) _snwprintf_s(reason,chars,_TRUNCATE,L"Intel XeSS-FG: %s (0x%08X).",failure.operation,(UINT)failure.result);
    return (int)failure.result;
}

struct Module {
    HMODULE handle{};
    ~Module(){if(handle) FreeLibrary(handle);}
    void Open(const wchar_t* name) {
        HMODULE self{};
        if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&ClearReason),&self)) throw Failure{L"finding bridge",HRESULT_FROM_WIN32(GetLastError())};
        std::wstring path(32768,L'\0');
        DWORD n=GetModuleFileNameW(self,path.data(),static_cast<DWORD>(path.size()));
        Require(n && n<path.size(),L"resolving runtime directory");
        path.resize(n); path.resize(path.find_last_of(L"\\/")+1);
        path+=L"xess-fg\\"; path+=name;
        handle=LoadLibraryExW(path.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!handle) throw Failure{name,HRESULT_FROM_WIN32(GetLastError())};
    }
    template<class T> void Bind(T& dest,const char* name) {
        dest=reinterpret_cast<T>(GetProcAddress(handle,name));
        if(!dest) throw Failure{L"loading pinned SDK exports",HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND)};
    }
};

struct Intel {
    Module ll,fg;
    decltype(&xellD3D12CreateContext) createLl{};
    decltype(&xellDestroyContext) destroyLl{};
    decltype(&xellSetSleepMode) sleepMode{};
    decltype(&xellSleep) sleep{};
    decltype(&xellAddMarkerData) mark{};
    decltype(&xefgSwapChainD3D12CreateContext) createFg{};
    decltype(&xefgSwapChainDestroy) destroyFg{};
    decltype(&xefgSwapChainSetLatencyReduction) link{};
    decltype(&xefgSwapChainD3D12InitFromSwapChainDesc) initialize{};
    decltype(&xefgSwapChainD3D12GetSwapChainPtr) swapchain{};
    decltype(&xefgSwapChainSetEnabled) enable{};
    decltype(&xefgSwapChainD3D12TagFrameResource) tag{};
    decltype(&xefgSwapChainTagFrameConstants) constants{};
    decltype(&xefgSwapChainSetPresentId) presentId{};
    decltype(&xefgSwapChainGetLastPresentStatus) presentStatus{};
    xell_context_handle_t latency{};
    xefg_swapchain_handle_t generation{};
    void Open(ID3D12Device* device) {
        ll.Open(L"libxell.dll"); fg.Open(L"libxess_fg.dll");
        ll.Bind(createLl,"xellD3D12CreateContext"); ll.Bind(destroyLl,"xellDestroyContext");
        ll.Bind(sleepMode,"xellSetSleepMode"); ll.Bind(sleep,"xellSleep"); ll.Bind(mark,"xellAddMarkerData");
        fg.Bind(createFg,"xefgSwapChainD3D12CreateContext"); fg.Bind(destroyFg,"xefgSwapChainDestroy");
        fg.Bind(link,"xefgSwapChainSetLatencyReduction"); fg.Bind(initialize,"xefgSwapChainD3D12InitFromSwapChainDesc");
        fg.Bind(swapchain,"xefgSwapChainD3D12GetSwapChainPtr"); fg.Bind(enable,"xefgSwapChainSetEnabled");
        fg.Bind(tag,"xefgSwapChainD3D12TagFrameResource"); fg.Bind(constants,"xefgSwapChainTagFrameConstants");
        fg.Bind(presentId,"xefgSwapChainSetPresentId"); fg.Bind(presentStatus,"xefgSwapChainGetLastPresentStatus");
        Check(createLl(device,&latency),L"creating the XeLL context");
        Check(createFg(device,&generation),L"device or driver does not support XeSS-FG");
        Check(link(generation,latency),L"linking XeLL to XeSS-FG");
    }
    ~Intel() {
        if(generation) destroyFg(generation);
        if(latency) destroyLl(latency);
    }
};

D3D12_RESOURCE_BARRIER Transition(ID3D12Resource* resource,D3D12_RESOURCE_STATES before,D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition={resource,D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,before,after};
    return barrier;
}

struct Session {
    Effects12 gpu;
    ComPtr<ID3D11Device> wrapper;
    ComPtr<ID3D11DeviceContext> context;
    // Declared before chain: the host must release the DXGI proxy before destroying XeSS-FG and XeLL.
    Intel sdk;
    ComPtr<IDXGISwapChain3> chain;
    HWND child{};
    UINT width{},height{},frame{1};
    bool shown{},tearing{};
    DXGI_FORMAT format{};
    HRESULT lastDxgi{};
    xefg_swapchain_result_t lastQuery{};
    xefg_swapchain_present_status_t lastStatus{};

    void Open(HWND parent,ID3D11Device* device,UINT w,UINT h,bool hdr) {
        wrapper=device; width=w; height=h;
        HRESULT hr=gpu.Open(device);
        Check(hr==S_FALSE ? E_NOINTERFACE : hr,L"opening the native D3D12 rendering queue");
        wrapper->GetImmediateContext(&context);
        sdk.Open(gpu.device.Get());
        child=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_NOPARENTNOTIFY,L"STATIC",L"",
            WS_CHILD|WS_DISABLED|WS_CLIPCHILDREN|WS_CLIPSIBLINGS,0,0,w,h,parent,nullptr,GetModuleHandleW(nullptr),nullptr);
        if(!child) throw Failure{L"creating the presentation surface",HRESULT_FROM_WIN32(GetLastError())};
        ComPtr<IDXGIFactory2> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)),L"creating DXGI factory");
        ComPtr<IDXGIFactory5> newer;
        BOOL allowTearing=FALSE;
        if(SUCCEEDED(factory.As(&newer)) && SUCCEEDED(newer->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING,&allowTearing,sizeof(allowTearing))))
            tearing=allowTearing!=FALSE;
        format=hdr ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width=w; desc.Height=h; desc.Format=format; desc.SampleDesc.Count=1;
        desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount=3;
        desc.Scaling=DXGI_SCALING_STRETCH; desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.AlphaMode=DXGI_ALPHA_MODE_IGNORE; desc.Flags=tearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0;
        xefg_swapchain_d3d12_init_params_t init{};
        init.maxInterpolatedFrames=1;
        // Remote Play carries a finished picture with HUD already composited.
        init.uiMode=XEFG_SWAPCHAIN_UI_MODE_NONE;
        Check(sdk.initialize(sdk.generation,child,&desc,nullptr,gpu.queue.Get(),factory.Get(),&init),L"initializing Intel's proxy swap chain");
        Check(sdk.swapchain(sdk.generation,IID_PPV_ARGS(&chain)),L"obtaining Intel's DXGI proxy");
        auto color=hdr ? DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 : DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        UINT support=0;
        Check(chain->CheckColorSpaceSupport(color,&support),L"checking the display color space");
        if(!(support&DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT)) throw Failure{L"display color space is unavailable",DXGI_ERROR_UNSUPPORTED};
        Check(chain->SetColorSpace1(color),L"setting the display color space");
        xell_sleep_params_t lowLatency{}; lowLatency.bLowLatencyMode=1;
        Check(sdk.sleepMode(sdk.latency,&lowLatency),L"enabling XeLL pacing");
        Check(sdk.enable(sdk.generation,1),L"enabling XeSS-FG");
    }
    void Marker(xell_latency_marker_type_t point) {Check(sdk.mark(sdk.latency,frame,point),L"recording XeLL frame markers");}

    void CopyAndTag(ID3D11Texture2D* color,ID3D11Texture2D* motion,ID3D11Texture2D* depth) {
        std::array<ID3D11Texture2D*,3> input={color,motion,depth};
        DXGI_FORMAT expected[]={format,DXGI_FORMAT_R16G16_FLOAT,DXGI_FORMAT_R32_FLOAT};
        for(size_t i=0;i<input.size();++i) {
            Require(input[i]!=nullptr,L"missing picture, motion, or depth");
            D3D11_TEXTURE2D_DESC desc{}; input[i]->GetDesc(&desc);
            ComPtr<ID3D11Device> owner; input[i]->GetDevice(&owner);
            Require(owner.Get()==wrapper.Get() && desc.Width==width && desc.Height==height &&
                desc.Format==expected[i] && desc.SampleDesc.Count==1 && desc.ArraySize==1 && desc.MipLevels==1,L"invalid input texture");
        }
        ComPtr<ID3D12Resource> back;
        Check(chain->GetBuffer(chain->GetCurrentBackBufferIndex(),IID_PPV_ARGS(&back)),L"opening proxy back buffer");
        std::array<ComPtr<ID3D12Resource>,3> native;
        size_t acquired=0;
        HRESULT work=S_OK;
        for(;acquired<input.size();++acquired) {
            work=gpu.interop->UnwrapUnderlyingResource(input[acquired],gpu.queue.Get(),IID_PPV_ARGS(&native[acquired]));
            if(FAILED(work)) break;
        }
        context->Flush();
        LONG tags=0;
        bool queued=false;
        if(SUCCEEDED(work)) {
            work=gpu.Submit([&](ID3D12GraphicsCommandList* list){
                D3D12_RESOURCE_BARRIER barriers[]={
                    Transition(back.Get(),D3D12_RESOURCE_STATE_PRESENT,D3D12_RESOURCE_STATE_COPY_DEST),
                    Transition(native[0].Get(),D3D12_RESOURCE_STATE_COMMON,D3D12_RESOURCE_STATE_COPY_SOURCE),
                    Transition(native[1].Get(),D3D12_RESOURCE_STATE_COMMON,D3D12_RESOURCE_STATE_COPY_SOURCE),
                    Transition(native[2].Get(),D3D12_RESOURCE_STATE_COMMON,D3D12_RESOURCE_STATE_COPY_SOURCE)};
                list->ResourceBarrier(4,barriers);
                list->CopyResource(back.Get(),native[0].Get());
                for(unsigned i=1;i<3 && tags>=0;++i) {
                    xefg_swapchain_d3d12_resource_data_t tag{};
                    tag.type=i==1 ? XEFG_SWAPCHAIN_RES_MOTION_VECTOR : XEFG_SWAPCHAIN_RES_DEPTH;
                    tag.validity=XEFG_SWAPCHAIN_RV_ONLY_NOW;
                    tag.resourceSize={width,height}; tag.pResource=native[i].Get(); tag.incomingState=D3D12_RESOURCE_STATE_COPY_SOURCE;
                    tags=sdk.tag(sdk.generation,list,frame,&tag);
                }
                for(auto& b:barriers) std::swap(b.Transition.StateBefore,b.Transition.StateAfter);
                list->ResourceBarrier(4,barriers);
            });
            queued=SUCCEEDED(work);
        }
        ID3D12Fence* fences[]={gpu.fence.Get()};
        for(size_t i=0;i<acquired;++i){
            HRESULT returned=gpu.interop->ReturnUnderlyingResource(input[i],queued?1:0,&gpu.submitted,fences);
            if(SUCCEEDED(work)) work=returned;
        }
        Check(work,L"submitting the real frame and its motion/depth textures");
        Check(tags,L"tagging motion and depth for frame generation");
    }
    void Present(ID3D11Texture2D* color,ID3D11Texture2D* motion,ID3D11Texture2D* depth,bool reset,float ms,int* extra) {
        Check(sdk.sleep(sdk.latency,frame),L"pacing the input frame");
        Marker(XELL_SIMULATION_START);
        xefg_swapchain_frame_constant_data_t constants{};
        for(int i=0;i<16;i+=5) constants.viewMatrix[i]=constants.projectionMatrix[i]=1;
        constants.motionVectorScaleX=constants.motionVectorScaleY=1;
        // Intel suppresses generated presents while occluded. Re-prime until the
        // first visible frame so a scene cut while hidden cannot leak across it.
        constants.resetHistory=reset || frame==1 || lastDxgi==DXGI_STATUS_OCCLUDED;
        constants.frameRenderTime=std::isfinite(ms) && ms>0 ? ms : 16.667f;
        Check(sdk.constants(sdk.generation,frame,&constants),L"tagging stream frame constants");
        Marker(XELL_SIMULATION_END); Marker(XELL_RENDERSUBMIT_START);
        CopyAndTag(color,motion,depth);
        Check(sdk.presentId(sdk.generation,frame),L"identifying the real frame");
        Marker(XELL_RENDERSUBMIT_END);
        if(!shown){
            if(!SetWindowPos(child,HWND_TOP,0,0,0,0,SWP_SHOWWINDOW|SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE))
                throw Failure{L"showing the presentation surface",HRESULT_FROM_WIN32(GetLastError())};
            shown=true;
        }
        Marker(XELL_PRESENT_START);
        HRESULT presented=chain->Present(0,tearing?DXGI_PRESENT_ALLOW_TEARING:0);
        Marker(XELL_PRESENT_END);
        Check(presented,L"presenting the Intel swap chain");
        xefg_swapchain_present_status_t status{};
        auto queried=sdk.presentStatus(sdk.generation,&status);
        lastDxgi=presented; lastQuery=queried; lastStatus=status;
        if(++frame==0) frame=1;
        Check(queried,L"reading Intel's presentation status");
        if(queried==XEFG_SWAPCHAIN_RESULT_SUCCESS) {
            Check(status.frameGenResult,L"interpolating the current presentation");
            if(presented==S_OK && status.isFrameGenEnabled && status.frameGenResult==XEFG_SWAPCHAIN_RESULT_SUCCESS && status.framesPresented==2)
                *extra=1;
        }
    }
    ~Session() {
        if(child && IsWindow(child)) ShowWindow(child,SW_HIDE);
        if(gpu.fence && gpu.queue) {
            UINT64 finalSignal=gpu.submitted+1;
            if(SUCCEEDED(gpu.queue->Signal(gpu.fence.Get(),finalSignal))) gpu.Wait(finalSignal);
        }
        chain.Reset();
        // Destroy the proxy and its native chain while their HWND is still valid.
        if(sdk.generation) { sdk.destroyFg(sdk.generation); sdk.generation=nullptr; }
        if(child && IsWindow(child) && !DestroyWindow(child))
            PostMessageW(child,WM_CLOSE,0,0);
    }
};
}

#define EXPORT extern "C" __declspec(dllexport) int __cdecl
EXPORT P5FgSupport(ID3D11Device* device,wchar_t* reason,int chars) {
    ClearReason(reason,chars);
    try {
        Require(device!=nullptr,L"missing render device");
        ComPtr<ID3D11On12Device2> wrapper;
        Check(device->QueryInterface(IID_PPV_ARGS(&wrapper)),L"Direct3D 12 is required");
        ComPtr<ID3D12Device> native;
        Check(wrapper->GetD3D12Device(IID_PPV_ARGS(&native)),L"opening D3D12 device");
        Intel sdk; sdk.Open(native.Get()); return 0;
    } catch(Failure f){return Report(f,reason,chars);} catch(...){return Report({L"checking SDK support",E_FAIL},reason,chars);}
}
EXPORT P5FgCreate(HWND parent,ID3D11Device* device,int width,int height,int hdr,void** session,wchar_t* reason,int chars) {
    ClearReason(reason,chars);
    if(session) *session=nullptr;
    try {
        Require(session && IsWindow(parent) && device && width>0 && height>0,L"invalid presentation surface");
        auto created=std::make_unique<Session>();
        created->Open(parent,device,width,height,hdr!=0);
        *session=created.release(); return 0;
    } catch(Failure f){return Report(f,reason,chars);} catch(...){return Report({L"creating presentation",E_FAIL},reason,chars);}
}
EXPORT P5FgPresent(void* session,ID3D11Texture2D* finished,ID3D11Texture2D* motion,ID3D11Texture2D* depth,
    int reset,float frameMs,int* extraPresented,wchar_t* reason,int chars) {
    ClearReason(reason,chars);
    if(extraPresented) *extraPresented=0;
    try {
        Require(session && extraPresented,L"invalid frame");
        static_cast<Session*>(session)->Present(finished,motion,depth,reset!=0,frameMs,extraPresented);
        return 0;
    } catch(Failure f){return Report(f,reason,chars);} catch(...){return Report({L"processing presentation",E_FAIL},reason,chars);}
}
extern "C" __declspec(dllexport) void __cdecl P5FgClose(void* session) {delete static_cast<Session*>(session);}
extern "C" __declspec(dllexport) void __cdecl P5FgLastIntelStatus(void* session,LONG* dxgi,int* query,int* interpolation,UINT* enabled,UINT* count) {
    if(!session || !dxgi || !query || !interpolation || !enabled || !count) return;
    auto& s=*static_cast<Session*>(session);
    *dxgi=s.lastDxgi; *query=s.lastQuery; *interpolation=s.lastStatus.frameGenResult;
    *enabled=s.lastStatus.isFrameGenEnabled; *count=s.lastStatus.framesPresented;
}
