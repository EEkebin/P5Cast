#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <vector>
#include <cstdio>
#include <stdexcept>
#include <cmath>
#include <memory>
#include <string>
#include <algorithm>
using Microsoft::WRL::ComPtr;
template<class F> F symbol(HMODULE module,const char* name) {
    auto proc=reinterpret_cast<F>(GetProcAddress(module,name));
    if(!proc) throw std::runtime_error(name);
    return proc;
}
unsigned stripeAt(int x,int y,int shift,bool hdr) {
    unsigned stripe=unsigned((x-shift)/18+y/48)&7;
    return hdr?(0xc0000000u|stripe*112*0x100401):(0xff000000u|stripe*33*0x010101);
}

void verifyMidpointPixels(HMODULE ngx,const wchar_t* path,ID3D11Device* d,ID3D11DeviceContext* ctx,
    ID3D11Texture2D* color,ID3D11Texture2D* motion,ID3D11Texture2D* depth,bool hdr) {
    using Open=unsigned(__cdecl*)(ID3D11Device*,const wchar_t*,const wchar_t*,int,int,int,void**);
    using Eval=unsigned(__cdecl*)(void*,ID3D11Texture2D*,ID3D11Texture2D*,ID3D11Texture2D*,ID3D11Texture2D*,int,int*);
    auto open=symbol<Open>(ngx,"NgxFgOpen");
    auto eval=symbol<Eval>(ngx,"NgxFgEvaluate");
    auto close=symbol<void(__cdecl*)(void*)>(ngx,"NgxFgClose");
    auto root=std::wstring(path);root.resize(root.find_last_of(L"\\/"));
    wchar_t local[32768]; if(!GetEnvironmentVariableW(L"LOCALAPPDATA",local,_countof(local))) throw std::runtime_error("Missing test cache path");
    auto cache=std::wstring(local)+L"\\Cadrivect\\NGX";
    D3D11_TEXTURE2D_DESC desc; color->GetDesc(&desc);
    UINT w=desc.Width,h=desc.Height;
    void* session=nullptr;
    if(open(d,root.c_str(),cache.c_str(),w,h,hdr,&session)!=1) throw std::runtime_error("Reopen NGX model");
    std::unique_ptr<void,decltype(close)> owned(session,close);
    desc.BindFlags=D3D11_BIND_UNORDERED_ACCESS|D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> output,read;
    if(FAILED(d->CreateTexture2D(&desc,nullptr,&output))) throw std::runtime_error("Midpoint texture");
    desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.Usage=D3D11_USAGE_STAGING;
    if(FAILED(d->CreateTexture2D(&desc,nullptr,&read))) throw std::runtime_error("Readback texture");
    std::vector<unsigned> pixels(w*h);
    int tested=0;
    for(int n=0;n<12;n++) {
        for(UINT y=0;y<h;y++)for(UINT x=0;x<w;x++)pixels[y*w+x]=stripeAt(x,y,n*4,hdr);
        ctx->UpdateSubresource(color,0,nullptr,pixels.data(),w*4,0);
        int generated=0;
        if(eval(owned.get(),color,motion,depth,output.Get(),n==0,&generated)!=1) throw std::runtime_error("NGX test evaluation");
        if(n==0 && generated) throw std::runtime_error("Reset created a fake midpoint");
        if(!generated) continue;
        ctx->CopyResource(read.Get(),output.Get());
        D3D11_MAPPED_SUBRESOURCE map{};
        if(FAILED(ctx->Map(read.Get(),0,D3D11_MAP_READ,0,&map)))throw std::runtime_error("Read generated pixels");
        unsigned mask=hdr?1023:255;
        double oldError=0,newError=0,halfError=0;
        for(UINT y=32;y<h-32;y+=13) {
            const UINT* row=reinterpret_cast<const UINT*>(static_cast<const unsigned char*>(map.pData)+y*map.RowPitch);
            for(UINT x=64;x<w-64;x++) {
                int v=row[x]&mask;
                oldError+=std::abs(v-int(stripeAt(x,y,4*n-4,hdr)&mask));
                newError+=std::abs(v-int(stripeAt(x,y,4*n,hdr)&mask));
                halfError+=std::abs(v-int(stripeAt(x,y,4*n-2,hdr)&mask));
            }
        }
        ctx->Unmap(read.Get(),0);
        if(halfError<oldError*.9 && halfError<newError*.9)tested++;
    }
    std::printf("%d generated images tracked the geometric midpoint more closely than either endpoint.\n",tested);
    if(tested<4)throw std::runtime_error("Output did not show the expected halfway motion");
}
RECT monitorArea(bool hdr) {
    ComPtr<IDXGIFactory1> factory;
    if(FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) throw std::runtime_error("DXGI factory");
    for(UINT a=0;;a++) {
        ComPtr<IDXGIAdapter1> adapter;
        if(factory->EnumAdapters1(a,&adapter)==DXGI_ERROR_NOT_FOUND) break;
        for(UINT i=0;;i++) {
            ComPtr<IDXGIOutput> output;
            if(adapter->EnumOutputs(i,&output)==DXGI_ERROR_NOT_FOUND) break;
            ComPtr<IDXGIOutput6> ext;
            DXGI_OUTPUT_DESC1 info{};
            if(SUCCEEDED(output.As(&ext)) && SUCCEEDED(ext->GetDesc1(&info)) && info.AttachedToDesktop &&
                ((info.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020)==hdr)) return info.DesktopCoordinates;
        }
    }
    if(hdr) throw std::runtime_error("Full-size HDR10 test requires an HDR-enabled monitor.");
    return {0,0,1280,720};
}
int wmain(int argc,wchar_t** args) {
    if(argc!=4) return 2;
    using Device=HRESULT(__cdecl*)(IUnknown*,ID3D11Device**);
    using Support=int(__cdecl*)(ID3D11Device*,wchar_t*,int);
    using Create=int(__cdecl*)(HWND,ID3D11Device*,int,int,int,void**,wchar_t*,int);
    using Present=int(__cdecl*)(void*,ID3D11Texture2D*,ID3D11Texture2D*,ID3D11Texture2D*,int,float,int*,wchar_t*,int);
    using Close=void(__cdecl*)(void*);
    int code=1;
    try {
        HMODULE factory=LoadLibraryExW(args[1],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        HMODULE bridge=LoadLibraryExW(args[2],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        auto createDevice=symbol<Device>(factory,"P5CreateD3D12Effects");
        auto support=symbol<Support>(bridge,"P5FgSupport");
        auto create=symbol<Create>(bridge,"P5FgCreate");
        auto present=symbol<Present>(bridge,"P5FgPresent");
        auto close=symbol<Close>(bridge,"P5FgClose");
        ComPtr<ID3D11Device> device;
        if(FAILED(createDevice(nullptr,&device))) throw std::runtime_error("D3D12 effects device");
        wchar_t error[512]{};
        if(support(device.Get(),error,512)) {std::fwprintf(stderr,L"Support: %ls\n",error);return 3;}
        std::puts("DLSS Frame Generation adapter check passed.");fflush(stdout);
        bool hdr=!wcscmp(args[3],L"hdr");
        bool headless=!wcscmp(args[3],L"support");
        if(headless) return 0;
        const UINT width=1280,height=720;
        auto area=monitorArea(hdr);
        HWND parent=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,L"STATIC",L"",WS_POPUP,
            area.left,area.top,width,height,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        if(!parent) throw std::runtime_error("Test host window");
        ShowWindow(parent,SW_SHOWNOACTIVATE);
        void* state{};
        if(create(parent,device.Get(),width,height,hdr,&state,error,512)) {std::fwprintf(stderr,L"Create: %ls\n",error);return 4;}
        std::puts("NVIDIA NGX frame-generation model created.");fflush(stdout);
        ComPtr<ID3D11DeviceContext> ctx;
        device->GetImmediateContext(&ctx);
        std::vector<UINT> color(width*height);
        std::vector<UINT> vectors(width*height,0x0000c400); // -4 source pixels X, current -> previous
        std::vector<float> z(width*height,.5f);
        ComPtr<ID3D11Texture2D> inputs[3];
        DXGI_FORMAT formats[]={hdr?DXGI_FORMAT_R10G10B10A2_UNORM:DXGI_FORMAT_B8G8R8A8_UNORM,DXGI_FORMAT_R16G16_FLOAT,DXGI_FORMAT_R32_FLOAT};
        for(UINT i=0;i<3;i++) {
            D3D11_TEXTURE2D_DESC d{};
            d.Width=width;d.Height=height;d.MipLevels=d.ArraySize=d.SampleDesc.Count=1;d.Format=formats[i];
            d.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
            if(FAILED(device->CreateTexture2D(&d,nullptr,&inputs[i])))throw std::runtime_error("Test texture");
        }
        ctx->UpdateSubresource(inputs[1].Get(),0,nullptr,vectors.data(),width*4,0);
        ctx->UpdateSubresource(inputs[2].Get(),0,nullptr,z.data(),width*4,0);
        int extras=0,contributing=0;
        bool failed=false;
        for(int n=0;n<120;n++) {
            for(UINT y=0;y<height;y++) for(UINT x=0;x<width;x++) {
                color[y*width+x]=stripeAt(x,y,n*4,hdr);
            }
            ctx->UpdateSubresource(inputs[0].Get(),0,nullptr,color.data(),width*4,0);
            if(n==30)ShowWindow(parent,SW_HIDE);
            if(n==38)ShowWindow(parent,SW_SHOWNOACTIVATE);
            bool reset=n==0 || n==60;
            int extra=0;
            if(present(state,inputs[0].Get(),inputs[1].Get(),inputs[2].Get(),reset,16.67f,&extra,error,512)) {
                std::fwprintf(stderr,L"Frame %d: %ls\n",n,error);failed=true;break;
            }
            if(extra && (reset || (n>=30 && n<=38))) throw std::runtime_error("Extra presentation on reset or hidden host");
            extras+=extra;if(extra)contributing++;
            MSG msg;
            while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}
            Sleep(16);
        }
        close(state);DestroyWindow(parent);
        std::printf("%s: %d extra NGX presentations from %d contributing samples.\n",hdr?"HDR10":"SDR",extras,contributing);
        if(!failed) verifyMidpointPixels(factory,args[1],device.Get(),ctx.Get(),inputs[0].Get(),inputs[1].Get(),inputs[2].Get(),hdr);
        code=!failed && extras>=8 && contributing>=6 ? 0 : 5;
    } catch(std::exception const& e) {std::fprintf(stderr,"Probe error: %s\n",e.what());}
    return code;
}
