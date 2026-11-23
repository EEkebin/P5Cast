#include "FrucIpc.h"
#include <cwchar>

struct Owner {void* core{}; ~Owner(){if(core) CloseFrucCore(core);}};
struct Worker {
    ComPtr<ID3D11Device5> device;
    ComPtr<ID3D11DeviceContext4> context;
    ComPtr<ID3D11Texture2D> input,output;
    ComPtr<ID3D11Fence> fence;
    Owner session;
    HRESULT Start(Packet& p) {
        const wchar_t* step=L"opening shared GPU surfaces";
        wcscpy_s(p.step,step);
        ComPtr<IDXGIFactory4> factory;
        HRESULT hr=CreateDXGIFactory1(IID_PPV_ARGS(&factory));if(FAILED(hr)) return hr;
        ComPtr<IDXGIAdapter> adapter;
        hr=factory->EnumAdapterByLuid(p.adapter,IID_PPV_ARGS(&adapter));if(FAILED(hr)) return hr;
        ComPtr<ID3D11Device> base;ComPtr<ID3D11DeviceContext> immediate;
        hr=D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                             nullptr,0,D3D11_SDK_VERSION,&base,nullptr,&immediate);if(FAILED(hr)) return hr;
        hr=base.As(&device);if(FAILED(hr)) return hr;
        hr=immediate.As(&context);if(FAILED(hr)) return hr;
        hr=device->OpenSharedResource1(p.input,IID_PPV_ARGS(&input));if(FAILED(hr)) return hr;
        hr=device->OpenSharedResource1(p.output,IID_PPV_ARGS(&output));if(FAILED(hr)) return hr;
        hr=device->OpenSharedFence(p.fence,IID_PPV_ARGS(&fence));if(FAILED(hr)) return hr;
        hr=OpenFrucCore(device.Get(),p.width,p.height,p.runtime,&session.core,&step);
        wcscpy_s(p.step,step);
        return hr;
    }
    HRESULT Process(Packet& p) {
        HRESULT hr=context->Wait(fence.Get(),p.sequence*2-1);if(FAILED(hr)) return hr;
        hr=ProcessFrucCore(session.core,input.Get(),output.Get(),&p.valid);if(FAILED(hr)) return hr;
        hr=context->Signal(fence.Get(),p.sequence*2);context->Flush();
        return hr;
    }
};
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR command,int) {
    wchar_t* end=nullptr;
    auto handle=reinterpret_cast<HANDLE>(_wcstoui64(command,&end,10));
    if(!handle || !end || *end) return 2;
    View map{static_cast<Packet*>(MapViewOfFile(handle,FILE_MAP_ALL_ACCESS,0,0,sizeof(Packet)))};
    if(!map.data || map.data->version!=1) return 3;
    auto& p=*map.data;
    Worker worker;
    p.result=worker.Start(p);SetEvent(p.response);
    if(FAILED(p.result)) return 4;
    HANDLE wait[]={p.parent,p.request};
    while(WaitForMultipleObjects(2,wait,FALSE,INFINITE)==WAIT_OBJECT_0+1 && !p.stop) {
        p.result=worker.Process(p);
        SetEvent(p.response);
        if(FAILED(p.result)) break;
    }
    return 0;
}
