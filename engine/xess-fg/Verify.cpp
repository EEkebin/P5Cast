#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <dxgi1_6.h>
#include <cstdio>
#include <stdexcept>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace {
void Require(bool ok,const char* what) {if(!ok) throw std::runtime_error(what);}
void Checked(int status,const wchar_t* reason) {
    if(status){std::fwprintf(stderr,L"%ls\n",reason);throw std::runtime_error("SDK verification failed");}
}
template<class T> T Load(HMODULE module,const char* name) {
    auto proc=GetProcAddress(module,name);
    Require(proc!=nullptr,name);
    return reinterpret_cast<T>(proc);
}
}
int wmain(int argc,wchar_t** argv) {
    if(argc<3 || argc>4) return 2;
    bool hdr=argc==4 && !wcscmp(argv[3],L"hdr");
    HMODULE deviceModule=LoadLibraryExW(argv[1],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    HMODULE bridge=LoadLibraryExW(argv[2],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    HWND window{}; void* session{};
    void (__cdecl* close)(void*){};
    int exit=1;
    try {
        Require(deviceModule && bridge,"Loading native bridges");
        auto deviceFactory=Load<HRESULT(__cdecl*)(IUnknown*,ID3D11Device**)>(deviceModule,"P5CreateD3D12Effects");
        auto support=Load<int(__cdecl*)(ID3D11Device*,wchar_t*,int)>(bridge,"P5FgSupport");
        auto create=Load<int(__cdecl*)(HWND,ID3D11Device*,int,int,int,void**,wchar_t*,int)>(bridge,"P5FgCreate");
        auto present=Load<int(__cdecl*)(void*,ID3D11Texture2D*,ID3D11Texture2D*,ID3D11Texture2D*,int,float,int*,wchar_t*,int)>(bridge,"P5FgPresent");
        close=Load<void(__cdecl*)(void*)>(bridge,"P5FgClose");
        auto report=Load<void(__cdecl*)(void*,LONG*,int*,int*,UINT*,UINT*)>(bridge,"P5FgLastIntelStatus");
        ComPtr<ID3D11Device> device;
        Require(SUCCEEDED(deviceFactory(nullptr,&device)),"Creating production effects device");
        wchar_t reason[512]{};
        Checked(support(device.Get(),reason,512),reason);
        ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; DXGI_ADAPTER_DESC adapterInfo{};
        Require(SUCCEEDED(device.As(&dxgi)) && SUCCEEDED(dxgi->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&adapterInfo)),"Checking adapter");
        std::wprintf(L"XeSS-FG/XeLL device check passed on %ls\n",adapterInfo.Description); std::fflush(stdout);
        int left=0,top=0;
        {
            bool found=false;
            for(UINT n=0;;++n) {
                ComPtr<IDXGIOutput> output;
                if(adapter->EnumOutputs(n,&output)!=S_OK) break;
                ComPtr<IDXGIOutput6> modern; DXGI_OUTPUT_DESC1 d{};
                if(SUCCEEDED(output.As(&modern)) && SUCCEEDED(modern->GetDesc1(&d)) && d.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020){
                    left=d.DesktopCoordinates.right-650;top=d.DesktopCoordinates.bottom-400;found=true;break;
                }
            }
            Require(!hdr || found,"No active HDR10 output on this adapter");
        }
        const UINT w=640,h=360;
        // Occluded or invisible desktops suppress the extra presents in the Intel runtime.
        window=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW|WS_EX_TOPMOST,L"STATIC",L"XeSS-FG test",WS_POPUP|WS_VISIBLE,left,top,w,h,nullptr,nullptr,nullptr,nullptr);
        Require(window!=nullptr,"Creating test surface");
        Checked(create(window,device.Get(),w,h,hdr,&session,reason,512),reason);
        ComPtr<ID3D11DeviceContext> context;device->GetImmediateContext(&context);
        ComPtr<ID3D11Texture2D> color,motion,depth;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width=w;desc.Height=h;desc.ArraySize=desc.MipLevels=desc.SampleDesc.Count=1;
        desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        desc.Format=hdr?DXGI_FORMAT_R10G10B10A2_UNORM:DXGI_FORMAT_B8G8R8A8_UNORM;
        Require(SUCCEEDED(device->CreateTexture2D(&desc,nullptr,&color)),"Creating frame");
        desc.Format=DXGI_FORMAT_R16G16_FLOAT;
        Require(SUCCEEDED(device->CreateTexture2D(&desc,nullptr,&motion)),"Creating motion");
        desc.Format=DXGI_FORMAT_R32_FLOAT;
        Require(SUCCEEDED(device->CreateTexture2D(&desc,nullptr,&depth)),"Creating depth");
        std::vector<UINT> mv(w*h,0x0000c400u); // current -> previous is (-4, 0) pixels
        std::vector<float> z(w*h,0.5f);
        std::vector<UINT> rgb(w*h);
        context->UpdateSubresource(motion.Get(),0,nullptr,mv.data(),w*4,0);
        context->UpdateSubresource(depth.Get(),0,nullptr,z.data(),w*4,0);
        int extras=0;
        for(int i=0;i<32;++i) {
            for(UINT y=0;y<h;++y) for(UINT x=0;x<w;++x){
                UINT p=((x-4*i)/16+y/16)&1;
                rgb[w*y+x]=hdr ? (p?0xeb0b02c0:0xcc0300c0) : (p?0xff959b94:0xff32403a);
            }
            context->UpdateSubresource(color.Get(),0,nullptr,rgb.data(),w*4,0);
            int extra=-1;
            Checked(present(session,color.Get(),motion.Get(),depth.Get(),i==0||i==16,16.67f,&extra,reason,512),reason);
            Require(extra==0 || extra==1,"Unexpected Intel presentation count");
            extras+=extra;
            if(i==0 || i==15 || i==31){
                LONG dxgiResult{};int queried{},interpolation{}; UINT enabled{},count{};
                report(session,&dxgiResult,&queried,&interpolation,&enabled,&count);
                std::printf("frame %d DXGI=%08lx status=%d interpolation=%d enabled=%u framesPresented=%u\n",i+1,dxgiResult,queried,interpolation,enabled,count);
            }
            Sleep(12);
        }
        std::wprintf(L"%ls real inputs: 32; Intel-confirmed extra presents: %d\n",hdr?L"HDR10":L"SDR",extras);
        Require(extras>0,"Intel reported zero generated presents");
        close(session); session=nullptr; exit=0;
    } catch(const std::exception& e){std::fprintf(stderr,"%s\n",e.what());}
    if(session && close) close(session);
    if(window) DestroyWindow(window);
    if(bridge) FreeLibrary(bridge);
    if(deviceModule) FreeLibrary(deviceModule);
    return exit;
}
