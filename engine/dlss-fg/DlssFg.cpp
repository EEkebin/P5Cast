#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <cmath>
#include <memory>
#include <string>
using Microsoft::WRL::ComPtr;

namespace {
struct Failure {std::wstring message;};
void require(bool ok,const wchar_t* what) {if(!ok)throw Failure{what};}
void checkNgx(unsigned result,const wchar_t* what) {
    if(result==1) return;
    wchar_t value[32];swprintf_s(value,L" (NGX 0x%08X).",result);
    throw Failure{std::wstring(what)+value};
}
void check(HRESULT hr,const wchar_t* what) {
    if(FAILED(hr)) {
        wchar_t value[32];swprintf_s(value,L" (0x%08X).",unsigned(hr));
        throw Failure{std::wstring(what)+value};
    }
}
int moduleAnchor;
std::wstring moduleDirectory() {
    HMODULE self{};
    require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&moduleAnchor),&self),L"Cannot locate the DLSS Frame Generation bridge.");
    wchar_t path[32768];
    auto length=GetModuleFileNameW(self,path,_countof(path));
    require(length && length<_countof(path),L"Could not resolve the NVIDIA runtime directory.");
    std::wstring dir(path,length);return dir.substr(0,dir.find_last_of(L"\\/"));
}
std::wstring dataDirectory() {
    PWSTR local{};
    check(SHGetKnownFolderPath(FOLDERID_LocalAppData,0,nullptr,&local),L"Getting the NVIDIA cache directory");
    std::wstring path(local); CoTaskMemFree(local);path+=L"\\Cadrivect\\NGX";
    int error=SHCreateDirectoryExW(nullptr,path.c_str(),nullptr);
    require(error==ERROR_SUCCESS || error==ERROR_ALREADY_EXISTS || error==ERROR_FILE_EXISTS,L"Could not open NVIDIA's cache directory.");
    return path;
}
struct Module {
    HMODULE handle{};
    void load(const std::wstring& path) {
        handle=LoadLibraryExW(path.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        require(handle!=nullptr,L"A P5Cast D3D12 display or NVIDIA SDK bridge is missing.");
    }
    template<class T> void method(T& value,const char* name) {
        value=reinterpret_cast<T>(GetProcAddress(handle,name));
        require(value!=nullptr,L"Install matching versions of the P5Cast NVIDIA and display bridges.");
    }
    ~Module(){if(handle) FreeLibrary(handle);}
};
struct Provider {
    Module ngx,presentation;
    void* model{};void* display{};
    unsigned (__cdecl* open)(ID3D11Device*,const wchar_t*,const wchar_t*,int,int,int,void**){};
    unsigned (__cdecl* eval)(void*,ID3D11Texture2D*,ID3D11Texture2D*,ID3D11Texture2D*,ID3D11Texture2D*,int,int*){};
    void (__cdecl* close)(void*){};
    HRESULT (__cdecl* createDisplay)(HWND,ID3D11Device*,int,int,int,void**){};
    HRESULT (__cdecl* present)(void*,ID3D11Texture2D*){};
    void (__cdecl* closeDisplay)(void*){};
    ComPtr<ID3D11Texture2D> midpoint;
    HWND parent{};
    HANDLE timer{};
    UINT w{},h{};
    bool needsReset=true;
    DXGI_FORMAT format{};
    void Open(HWND host,ID3D11Device* device,int width,int height,bool hdr) {
        require(device && ((!width && !height) || (width>0 && height>0 && IsWindow(host))),L"Invalid D3D12 device or playback surface.");
        std::wstring root=moduleDirectory(),cache=dataDirectory();
        ngx.load(root+L"\\Cadrivect.Ngx.dll");
        ngx.method(open,"NgxFgOpen"); ngx.method(eval,"NgxFgEvaluate");ngx.method(close,"NgxFgClose");
        checkNgx(open(device,root.c_str(),cache.c_str(),width,height,hdr,&model),
            L"DLSS Frame Generation needs an eligible D3D12 GPU, driver and hardware-accelerated GPU scheduling");
        if(!width) return;
        parent=host; w=width; h=height;format=hdr?DXGI_FORMAT_R10G10B10A2_UNORM:DXGI_FORMAT_B8G8R8A8_UNORM;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width=w;desc.Height=h; desc.ArraySize=desc.MipLevels=desc.SampleDesc.Count=1;
        desc.Format=format;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS;
        check(device->CreateTexture2D(&desc,nullptr,&midpoint),L"Creating the NVIDIA midpoint texture");
        presentation.load(root+L"\\P5Cast.Presentation.dll");
        presentation.method(createDisplay,"p5_d3d12_display_create");presentation.method(present,"p5_d3d12_display_frame");
        presentation.method(closeDisplay,"p5_d3d12_display_destroy");
        check(createDisplay(parent,device,w,h,hdr,&display),L"Opening the NVIDIA playback surface");
        timer=CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);
        if(!timer)timer=CreateWaitableTimerW(nullptr,FALSE,nullptr);
        require(timer!=nullptr,L"Creating a frame pacing timer failed.");
    }
    int Frame(ID3D11Texture2D* color,ID3D11Texture2D* motion,ID3D11Texture2D* depth,bool reset,float frameMs) {
        ID3D11Texture2D* textures[]={color,motion,depth};
        DXGI_FORMAT formats[]={format,DXGI_FORMAT_R16G16_FLOAT,DXGI_FORMAT_R32_FLOAT};
        for(int i=0;i<3;i++) {
            require(textures[i]!=nullptr,L"Missing a DLSS Frame Generation input.");
            D3D11_TEXTURE2D_DESC d{};textures[i]->GetDesc(&d);
            require(d.Width==w && d.Height==h && d.Format==formats[i] && d.SampleDesc.Count==1,
                L"Incorrect color, motion or depth dimensions/format for DLSS Frame Generation.");
        }
        if(!IsWindowVisible(parent) || IsIconic(GetAncestor(parent,GA_ROOT))) {needsReset=true;return 0;}
        bool prime=reset || needsReset;
        int produced=0;
        checkNgx(eval(model,color,motion,depth,midpoint.Get(),prime,&produced),L"Evaluating NVIDIA DLSS Frame Generation");
        int extra=0;
        bool occluded=false;
        if(produced && !prime) {
            auto hr=present(display,midpoint.Get());check(hr,L"Presenting NVIDIA's interpolated frame");
            occluded=hr==DXGI_STATUS_OCCLUDED;
            if(hr==S_OK) {
                // Keep the flip holding the generated picture for a real half-frame
                // interval. A reset, SDK suppression, or occlusion consumes no slot.
                double halfMs=std::min(50.,double(frameMs)*.5);
                LARGE_INTEGER wait{}; wait.QuadPart=-LONGLONG(0.5+halfMs*10000.);
                require(SetWaitableTimer(timer,&wait,0,nullptr,nullptr,FALSE) && WaitForSingleObject(timer,1000)==WAIT_OBJECT_0,
                    L"DLSS frame pacing failed.");
                extra=1;
            }
        }
        auto real=present(display,color);
        check(real,L"Presenting the real video frame");
        needsReset=occluded || real!=S_OK;
        return real==S_OK ? extra : 0;
    }
    ~Provider() {
        if(display)closeDisplay(display);
        if(model)close(model);
        if(timer)CloseHandle(timer);
    }
};
template<class F> int invoke(wchar_t* reason,int chars,F work) {
    if(reason && chars>0) reason[0]=0;
    try {work();return 0;}
    catch(const Failure& fail){if(reason && chars>0)wcsncpy_s(reason,chars,fail.message.c_str(),_TRUNCATE);}
    catch(...){if(reason && chars>0)wcsncpy_s(reason,chars,L"Unexpected NVIDIA frame-generation failure.",_TRUNCATE);}
    return -1;
}
}
#define EXPORT extern "C" __declspec(dllexport)
EXPORT int P5FgSupport(ID3D11Device* d,wchar_t* reason,int chars) {
    return invoke(reason,chars,[&]{Provider check;check.Open(nullptr,d,0,0,false);});
}
EXPORT int P5FgCreate(HWND parent,ID3D11Device* d,int w,int h,int hdr,void** session,wchar_t* reason,int chars) {
    if(session)*session=nullptr;
    return invoke(reason,chars,[&]{
        require(session && w>0 && h>0,L"Invalid frame-generation session.");
        auto result=std::make_unique<Provider>();result->Open(parent,d,w,h,hdr!=0);*session=result.release();
    });
}
EXPORT int P5FgPresent(Provider* session,ID3D11Texture2D* color,ID3D11Texture2D* motion,ID3D11Texture2D* depth,
    int reset,float frameMs,int* extraPresented,wchar_t* reason,int chars) {
    if(extraPresented)*extraPresented=0;
    return invoke(reason,chars,[&]{
        require(session && extraPresented && std::isfinite(frameMs) && frameMs>0,L"Invalid DLSS frame-generation submission.");
        *extraPresented=session->Frame(color,motion,depth,reset!=0,frameMs);
    });
}
EXPORT void P5FgClose(Provider* session){delete session;}
