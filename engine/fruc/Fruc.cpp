// P5Cast's x64 adapter for the NVIDIA Optical Flow SDK 4/5 FRUC C ABI.
// Verify the vendor module and CUDA runtime before loading either into the worker.
#include "FrucIpc.h"
#include <wintrust.h>
#include <softpub.h>
#include <wincrypt.h>
#include <cstdint>
#include <cwchar>
#include <memory>
#include <new>
#include <string>

namespace {
struct CreateParams {
    UINT width,height; void* device; int resourceType,surfaceFormat,cudaType;
    UINT reserved[32];
};
struct Frame {
    void* texture; double time; size_t pitch; void* repeated; UINT reserved[32];
};
struct ProcessIn {Frame frame; UINT skipWarp; UINT64 waitValue,unused; UINT reserved[32];};
struct ProcessOut {Frame frame; UINT64 signalValue,unused; UINT reserved[32];};
struct Registration {void* surfaces[10]; void* fence; UINT count;};
struct Unregistration {void* surfaces[10]; UINT count;};
static_assert(sizeof(CreateParams)==160 && sizeof(Frame)==160 && sizeof(ProcessIn)==312 &&
              sizeof(ProcessOut)==304 && sizeof(Registration)==96 && sizeof(Unregistration)==88);
using CreateFn=int (WINAPI*)(const CreateParams*,void**);
using RegisterFn=int (WINAPI*)(void*,const Registration*);
using UnregisterFn=int (WINAPI*)(void*,const Unregistration*);
using ProcessFn=int (WINAPI*)(void*,const ProcessIn*,const ProcessOut*);
using DestroyFn=int (WINAPI*)(void*);

struct Module {
    HMODULE handle{};
    ~Module() {if(handle) FreeLibrary(handle);}
};

// NVIDIA's CUDA 11.8.89 x64 redistributable is unsigned. Match the exact
// SDK redistributable payload when loading it in place of a signed binary.
bool IsPinnedCuda11(HANDLE file) {
    const BYTE expected[32]={0xba,0x5c,0x2f,0xb5,0x26,0xc4,0xee,0x4b,0xb2,0x18,0xce,0xb3,0xfa,0x5e,0x8b,0xfd,
                            0xe8,0x9c,0xe4,0x74,0xf3,0x87,0x11,0xfd,0xcc,0xe8,0x02,0x54,0x9b,0xf9,0xfc,0x6f};
    LARGE_INTEGER length{},zero{};
    if(!GetFileSizeEx(file,&length) || length.QuadPart<=0 || length.QuadPart>8*1024*1024 ||
       !SetFilePointerEx(file,zero,nullptr,FILE_BEGIN)) return false;
    std::unique_ptr<BYTE[]> data(new(std::nothrow) BYTE[length.LowPart]);
    DWORD read{},size=32;
    BYTE digest[32]{};
    return data && ReadFile(file,data.get(),length.LowPart,&read,nullptr) && read==length.LowPart &&
           CryptHashCertificate2(L"SHA256",0,nullptr,data.get(),read,digest,&size) && size==32 &&
           !memcmp(expected,digest,size);
}
// Hold the image against writes and replacement from verification through loading.
// Restrict dependencies to the system directory and explicitly preload CUDA.
HRESULT LoadNvidia(Module& module,const std::wstring& path,bool cuda=false) {
    HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,0,nullptr);
    if(file==INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    WINTRUST_FILE_INFO info{};info.cbStruct=sizeof(info);info.pcwszFilePath=path.c_str();info.hFile=file;
    WINTRUST_DATA trust{};
    trust.cbStruct=sizeof(trust);trust.dwUIChoice=WTD_UI_NONE;trust.fdwRevocationChecks=WTD_REVOKE_NONE;
    trust.dwUnionChoice=WTD_CHOICE_FILE;trust.pFile=&info;trust.dwStateAction=WTD_STATEACTION_VERIFY;
    trust.dwProvFlags=WTD_CACHE_ONLY_URL_RETRIEVAL;
    GUID policy=WINTRUST_ACTION_GENERIC_VERIFY_V2;
    HRESULT result=WinVerifyTrust(nullptr,&policy,&trust);
    if(result==S_OK) {
        auto provider=WTHelperProvDataFromStateData(trust.hWVTStateData);
        auto signer=provider?WTHelperGetProvSignerFromChain(provider,0,FALSE,0):nullptr;
        auto cert=(signer && signer->csCertChain)?signer->pasCertChain[0].pCert:nullptr;
        wchar_t publisher[128]{};
        if(!cert || !CertGetNameStringW(cert,CERT_NAME_ATTR_TYPE,0,(void*)szOID_ORGANIZATION_NAME,publisher,128) ||
           _wcsicmp(publisher,L"NVIDIA Corporation")!=0) result=TRUST_E_SUBJECT_NOT_TRUSTED;
    }
    trust.dwStateAction=WTD_STATEACTION_CLOSE;
    WinVerifyTrust(nullptr,&policy,&trust);
    if(result!=S_OK && cuda && IsPinnedCuda11(file)) result=S_OK;
    if(result==S_OK) {
        module.handle=LoadLibraryExW(path.c_str(),nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!module.handle) result=HRESULT_FROM_WIN32(GetLastError());
    } else if(SUCCEEDED(result)) result=TRUST_E_SUBJECT_NOT_TRUSTED;
    CloseHandle(file);
    return result;
}

struct Session {
    Module cuda,library;
    ComPtr<ID3D11DeviceContext4> context;
    ComPtr<ID3D11Fence> fence;
    ComPtr<ID3D11Texture2D> textures[3]; // midpoint, alternating real inputs
    Registration reg{};
    void* fruc{};
    UnregisterFn unregister{};
    DestroyFn destroy{};
    ProcessFn process{};
    HANDLE completed{};
    UINT width{},height{};
    UINT64 frames{},value{};
    bool registered{};
    ~Session() {
        if(registered) {
            Unregistration remove{};remove.count=reg.count;
            memcpy(remove.surfaces,reg.surfaces,reg.count*sizeof(void*));
            unregister(fruc,&remove);
        }
        if(fruc && destroy) destroy(fruc);
        if(completed) CloseHandle(completed);
    }
    bool Matches(ID3D11Texture2D* texture) const {
        if(!texture) return false;
        D3D11_TEXTURE2D_DESC d{};texture->GetDesc(&d);
        return d.Width==width && d.Height==height && d.Format==DXGI_FORMAT_R8G8B8A8_UNORM &&
               d.SampleDesc.Count==1 && d.MipLevels==1 && d.ArraySize==1;
    }
};

HRESULT Open(Session& s,ID3D11Device* gpu,UINT width,UINT height,const std::wstring& path,const wchar_t*& step) {
    const auto slash=path.find_last_of(L"\\/");
    if(slash==std::wstring::npos) return E_INVALIDARG;
    step=L"loading the NVIDIA runtime cudart64_110.dll";
    HRESULT hr=LoadNvidia(s.cuda,path.substr(0,slash+1)+L"cudart64_110.dll",true);
    if(FAILED(hr)) return hr;
    step=L"loading the NVIDIA runtime NvOFFRUC.dll";
    hr=LoadNvidia(s.library,path);if(FAILED(hr)) return hr;
    auto create=(CreateFn)GetProcAddress(s.library.handle,"NvOFFRUCCreate");
    auto bind=(RegisterFn)GetProcAddress(s.library.handle,"NvOFFRUCRegisterResource");
    s.unregister=(UnregisterFn)GetProcAddress(s.library.handle,"NvOFFRUCUnregisterResource");
    s.destroy=(DestroyFn)GetProcAddress(s.library.handle,"NvOFFRUCDestroy");
    s.process=(ProcessFn)GetProcAddress(s.library.handle,"NvOFFRUCProcess");
    if(!create || !bind || !s.unregister || !s.destroy || !s.process) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    step=L"creating Direct3D 11 shared resources";
    ComPtr<ID3D11Device5> device;
    hr=gpu->QueryInterface(IID_PPV_ARGS(&device));if(FAILED(hr)) return hr;
    ComPtr<ID3D11DeviceContext> immediate;gpu->GetImmediateContext(&immediate);
    hr=immediate.As(&s.context);if(FAILED(hr)) return hr;
    hr=device->CreateFence(0,D3D11_FENCE_FLAG_SHARED,IID_PPV_ARGS(&s.fence));if(FAILED(hr)) return hr;
    s.completed=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    if(!s.completed) return HRESULT_FROM_WIN32(GetLastError());
    s.width=width;s.height=height;
    D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;
    desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.SampleDesc.Count=1;
    desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags=D3D11_RESOURCE_MISC_SHARED|D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    for(auto& texture:s.textures) {
        hr=device->CreateTexture2D(&desc,nullptr,&texture);if(FAILED(hr)) return hr;
        s.reg.surfaces[s.reg.count++]=texture.Get();
    }
    step=L"initializing NVIDIA FRUC";
    CreateParams params{};params.width=width;params.height=height;params.device=gpu;
    params.resourceType=1;params.surfaceFormat=1; // DirectX11Resource, ARGBSurface
    int status=create(&params,&s.fruc);if(status) return MAKE_HRESULT(SEVERITY_ERROR,FACILITY_ITF,status);
    step=L"registering textures with NVIDIA FRUC";s.reg.fence=s.fence.Get();
    status=bind(s.fruc,&s.reg);if(status) return MAKE_HRESULT(SEVERITY_ERROR,FACILITY_ITF,status);
    s.registered=true;
    return S_OK;
}
}

HRESULT OpenFrucCore(ID3D11Device* gpu,UINT w,UINT h,const wchar_t* runtime,void** out,const wchar_t** step) {
    if(!gpu || !w || !h || !runtime || !out || !step) return E_INVALIDARG;
    *out=nullptr;*step=L"opening NVIDIA FRUC";
    try {
        std::unique_ptr<Session> session(new Session);
        HRESULT hr=Open(*session,gpu,w,h,runtime,*step);if(FAILED(hr)) return hr;
        *out=session.release();return S_OK;
    } catch(const std::bad_alloc&) {return E_OUTOFMEMORY;}
}
HRESULT ProcessFrucCore(void* opaque,ID3D11Texture2D* source,ID3D11Texture2D* midpoint,int* valid) {
    auto s=static_cast<Session*>(opaque);
    if(!s || !valid || !s->Matches(source) || !s->Matches(midpoint)) return E_INVALIDARG;
    *valid=0;
    auto input=s->textures[1+(s->frames%2)].Get();
    s->context->CopyResource(input,source);
    const UINT64 ready=++s->value,finished=++s->value;
    HRESULT hr=s->context->Signal(s->fence.Get(),ready);if(FAILED(hr)) return hr;
    s->context->Flush();
    ProcessIn in{};in.frame.texture=input;in.frame.time=double(s->frames+1);
    in.frame.pitch=s->width*4u;in.waitValue=ready;
    int repeated=0;
    ProcessOut out{};out.frame.texture=s->textures[0].Get();out.frame.pitch=in.frame.pitch;
    out.frame.time=s->frames?in.frame.time-.5:in.frame.time;out.frame.repeated=&repeated;out.signalValue=finished;
    const int status=s->process(s->fruc,&in,&out);
    if(status) return MAKE_HRESULT(SEVERITY_ERROR,FACILITY_ITF,status);
    if(s->fence->GetCompletedValue()<finished) {
        hr=s->fence->SetEventOnCompletion(finished,s->completed);if(FAILED(hr)) return hr;
        DWORD result=WaitForSingleObject(s->completed,3000);
        if(result!=WAIT_OBJECT_0) return HRESULT_FROM_WIN32(result==WAIT_TIMEOUT?ERROR_TIMEOUT:GetLastError());
    }
    if(s->fence->GetCompletedValue()==UINT64_MAX) return DXGI_ERROR_DEVICE_REMOVED;
    if(s->frames++ && !repeated) {
        s->context->CopyResource(midpoint,s->textures[0].Get());
        *valid=1;
    }
    return S_OK;
}
void CloseFrucCore(void* opaque) {delete static_cast<Session*>(opaque);}
