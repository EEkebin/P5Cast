#include "FrucIpc.h"
#include <memory>
#include <string>
#include <cwchar>
#include <utility>

extern "C" IMAGE_DOS_HEADER __ImageBase;
namespace {
struct Client {
    ComPtr<ID3D11DeviceContext4> context;
    ComPtr<ID3D11Texture2D> input,output;
    ComPtr<ID3D11Fence> fence;
    Handle mapping,request,response,parent,inputShare,outputShare,fenceShare,process,job,frameReady;
    View view;
    bool ready{};
    ~Client() {
        if(process.value) {
            if(ready) {
                view.data->stop=true;
                SetEvent(request.value);
            }
            if(!ready || WaitForSingleObject(process.value,1000)!=WAIT_OBJECT_0) {
                TerminateProcess(process.value,1);
                WaitForSingleObject(process.value,1000);
            }
        }
    }
    HRESULT Await(DWORD timeout) {
        HANDLE wait[]={response.value,process.value};
        DWORD rc=WaitForMultipleObjects(2,wait,FALSE,timeout);
        if(rc==WAIT_OBJECT_0) return view.data->result;
        if(rc==WAIT_TIMEOUT) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        return HRESULT_FROM_WIN32(rc==WAIT_FAILED?GetLastError():ERROR_PROCESS_ABORTED);
    }
    bool Fits(ID3D11Texture2D* texture) const {
        if(!texture) return false;
        D3D11_TEXTURE2D_DESC d{};texture->GetDesc(&d);
        return d.Width==view.data->width && d.Height==view.data->height && d.Format==DXGI_FORMAT_R8G8B8A8_UNORM &&
               d.ArraySize==1 && d.MipLevels==1 && d.SampleDesc.Count==1;
    }
    HRESULT StartWorker() {
        wchar_t path[32768]{};
        auto size=GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase),path,_countof(path));
        if(!size || size>=_countof(path)) return HRESULT_FROM_WIN32(ERROR_BAD_PATHNAME);
        std::wstring exe(path,size);
        exe.replace(exe.find_last_of(L'\\')+1,std::wstring::npos,L"P5Cast.FrucHost.exe");
        std::wstring cmd=L"\""+exe+L"\" "+std::to_wstring(reinterpret_cast<uintptr_t>(mapping.value));
        HANDLE pass[]={mapping.value,parent.value,request.value,response.value,inputShare.value,outputShare.value,fenceShare.value};
        SIZE_T bytes{};
        InitializeProcThreadAttributeList(nullptr,1,0,&bytes);
        if(!bytes) return HRESULT_FROM_WIN32(GetLastError());
        std::unique_ptr<BYTE[]> buffer(new BYTE[bytes]);
        auto attributes=reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(buffer.get());
        if(!InitializeProcThreadAttributeList(attributes,1,0,&bytes)) return HRESULT_FROM_WIN32(GetLastError());
        BOOL ok=UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,pass,sizeof(pass),nullptr,nullptr);
        PROCESS_INFORMATION pi{};
        if(ok) {
            STARTUPINFOEXW si{};si.StartupInfo.cb=sizeof(si);si.lpAttributeList=attributes;
            ok=CreateProcessW(exe.c_str(),cmd.data(),nullptr,nullptr,TRUE,
                   CREATE_NO_WINDOW|CREATE_SUSPENDED|EXTENDED_STARTUPINFO_PRESENT,nullptr,nullptr,&si.StartupInfo,&pi);
        }
        DWORD err=ok?ERROR_SUCCESS:GetLastError();
        DeleteProcThreadAttributeList(attributes);
        if(!ok) return HRESULT_FROM_WIN32(err);
        process.value=pi.hProcess;Handle thread{pi.hThread};
        job.value=CreateJobObjectW(nullptr,nullptr);if(!job.value) return HRESULT_FROM_WIN32(GetLastError());
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if(!SetInformationJobObject(job.value,JobObjectExtendedLimitInformation,&limits,sizeof(limits)) ||
           !AssignProcessToJobObject(job.value,process.value) || ResumeThread(thread.value)==DWORD(-1))
            return HRESULT_FROM_WIN32(GetLastError());
        HRESULT hr=Await(8000);
        ready=SUCCEEDED(hr);
        return hr;
    }
    HRESULT Init(ID3D11Device* gpu,UINT width,UINT height,const wchar_t* runtime) {
        SECURITY_ATTRIBUTES sa{sizeof(sa),nullptr,TRUE};
        mapping.value=CreateFileMappingW(INVALID_HANDLE_VALUE,&sa,PAGE_READWRITE,0,sizeof(Packet),nullptr);
        if(!mapping.value) return HRESULT_FROM_WIN32(GetLastError());
        view.data=static_cast<Packet*>(MapViewOfFile(mapping.value,FILE_MAP_ALL_ACCESS,0,0,sizeof(Packet)));
        if(!view.data) return HRESULT_FROM_WIN32(GetLastError());
        auto& p=*view.data;p.version=1;p.width=width;p.height=height;
        if(wcslen(runtime)>=_countof(p.runtime)) return HRESULT_FROM_WIN32(ERROR_FILENAME_EXCED_RANGE);
        wcscpy_s(p.runtime,runtime);wcscpy_s(p.step,L"creating the FRUC playback worker");
        request.value=CreateEventW(&sa,FALSE,FALSE,nullptr);
        response.value=CreateEventW(&sa,FALSE,FALSE,nullptr);
        frameReady.value=CreateEventW(nullptr,FALSE,FALSE,nullptr);
        if(!request.value || !response.value || !frameReady.value) return HRESULT_FROM_WIN32(GetLastError());
        if(!DuplicateHandle(GetCurrentProcess(),GetCurrentProcess(),GetCurrentProcess(),&parent.value,SYNCHRONIZE,TRUE,0))
            return HRESULT_FROM_WIN32(GetLastError());
        ComPtr<IDXGIDevice> dxgi;ComPtr<IDXGIAdapter> adapter;DXGI_ADAPTER_DESC desc{};
        HRESULT hr=gpu->QueryInterface(IID_PPV_ARGS(&dxgi));if(FAILED(hr)) return hr;
        hr=dxgi->GetAdapter(&adapter);if(FAILED(hr)) return hr;
        hr=adapter->GetDesc(&desc);if(FAILED(hr)) return hr;
        p.adapter=desc.AdapterLuid;
        ComPtr<ID3D11Device5> device;
        hr=gpu->QueryInterface(IID_PPV_ARGS(&device));if(FAILED(hr)) return hr;
        ComPtr<ID3D11DeviceContext> base;gpu->GetImmediateContext(&base);
        hr=base.As(&context);if(FAILED(hr)) return hr;
        hr=device->CreateFence(0,D3D11_FENCE_FLAG_SHARED,IID_PPV_ARGS(&fence));if(FAILED(hr)) return hr;
        hr=fence->CreateSharedHandle(&sa,GENERIC_ALL,nullptr,&fenceShare.value);if(FAILED(hr)) return hr;
        D3D11_TEXTURE2D_DESC d{};d.Width=width;d.Height=height;d.SampleDesc.Count=d.ArraySize=d.MipLevels=1;
        d.Format=DXGI_FORMAT_R8G8B8A8_UNORM;d.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        d.MiscFlags=D3D11_RESOURCE_MISC_SHARED|D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
        auto texture=[&](ComPtr<ID3D11Texture2D>& image,Handle& share)->HRESULT {
            HRESULT r=device->CreateTexture2D(&d,nullptr,&image);if(FAILED(r)) return r;
            ComPtr<IDXGIResource1> resource;r=image.As(&resource);if(FAILED(r)) return r;
            return resource->CreateSharedHandle(&sa,DXGI_SHARED_RESOURCE_READ|DXGI_SHARED_RESOURCE_WRITE,nullptr,&share.value);
        };
        hr=texture(input,inputShare);if(FAILED(hr)) return hr;
        hr=texture(output,outputShare);if(FAILED(hr)) return hr;
        p.parent=parent.value;p.request=request.value;p.response=response.value;
        p.input=inputShare.value;p.output=outputShare.value;p.fence=fenceShare.value;
        return StartWorker();
    }
};
}
// Return a stable pointer even after the failing session's mapped packet has been unmapped.
thread_local wchar_t operation[128];
#define EXPORT extern "C" __declspec(dllexport)
EXPORT HRESULT P5FrucOpen(ID3D11Device* gpu,UINT w,UINT h,const wchar_t* runtime,Client** out,const wchar_t** step) {
    if(!gpu || !w || !h || !out || !runtime || !step) return E_INVALIDARG;
    *out=nullptr;*step=operation;wcscpy_s(operation,L"opening the FRUC playback worker");
    try {
        std::unique_ptr<Client> client(new Client);
        HRESULT hr=client->Init(gpu,w,h,runtime);
        if(FAILED(hr)) {
            if(client->view.data && client->view.data->step[0]) wcscpy_s(operation,client->view.data->step);
            return hr;
        }
        *out=client.release();return S_OK;
    } catch(const std::bad_alloc&) {return E_OUTOFMEMORY;}
}
EXPORT HRESULT P5FrucProcess(Client* c,ID3D11Texture2D* source,ID3D11Texture2D* midpoint,int* valid) {
    if(!c || !valid || !c->Fits(source) || !c->Fits(midpoint)) return E_INVALIDARG;
    *valid=0;
    auto& p=*c->view.data;++p.sequence;p.valid=0;p.result=E_PENDING;
    c->context->CopyResource(c->input.Get(),source);
    HRESULT hr=c->context->Signal(c->fence.Get(),p.sequence*2-1);if(FAILED(hr)) return hr;
    c->context->Flush();
    if(!SetEvent(c->request.value)) return HRESULT_FROM_WIN32(GetLastError());
    hr=c->Await(4000);if(FAILED(hr)) return hr;
    const UINT64 finished=p.sequence*2;
    if(c->fence->GetCompletedValue()<finished) {
        hr=c->fence->SetEventOnCompletion(finished,c->frameReady.value);if(FAILED(hr)) return hr;
        DWORD rc=WaitForSingleObject(c->frameReady.value,3000);
        if(rc!=WAIT_OBJECT_0) return HRESULT_FROM_WIN32(rc==WAIT_TIMEOUT?ERROR_TIMEOUT:GetLastError());
    }
    if(c->fence->GetCompletedValue()==UINT64_MAX) return DXGI_ERROR_DEVICE_REMOVED;
    if(p.valid) c->context->CopyResource(midpoint,c->output.Get());
    *valid=p.valid;
    return S_OK;
}
EXPORT void P5FrucClose(Client* c) {delete c;}
