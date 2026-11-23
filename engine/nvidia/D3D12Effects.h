#pragma once
#include <d3d11.h>
#include <d3d12.h>
#include <d3d11on12.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

// Associate the exact direct queue with the wrapper so native SDKs can enqueue
// work on the same device and timeline as the translated D3D11 commands.
static const GUID EffectsQueueId={0xcd8ca2f0,0xa477,0x4a4c,{0x83,0x61,0xb1,0xf5,0xda,0x4e,0xc8,0x32}};

// Native NVIDIA features share this queue with the translated shaders. Each allocator
// is reused only after its fence completes. No CPU readback or second device is needed.
struct Effects12 {
    ComPtr<ID3D11On12Device2> interop;
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12Fence> fence;
    ComPtr<ID3D12GraphicsCommandList> commands;
    struct Slot { ComPtr<ID3D12CommandAllocator> allocator; UINT64 submitted{}; } slots[3];
    unsigned next{};
    UINT64 submitted{};

    HRESULT Open(ID3D11Device* wrapper) {
        HRESULT hr=wrapper->QueryInterface(IID_PPV_ARGS(&interop));
        if (hr==E_NOINTERFACE) return S_FALSE;
        if (FAILED(hr)) return hr;
        hr=interop->GetD3D12Device(IID_PPV_ARGS(&device));
        if (FAILED(hr)) return hr;
        UINT size=sizeof(ID3D12CommandQueue*);
        hr=wrapper->GetPrivateData(EffectsQueueId,&size,queue.GetAddressOf());
        if (FAILED(hr)) return hr;
        hr=device->CreateFence(0,D3D12_FENCE_FLAG_NONE,IID_PPV_ARGS(&fence));
        if (FAILED(hr)) return hr;
        for (auto& slot : slots) {
            hr=device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,IID_PPV_ARGS(&slot.allocator));
            if (FAILED(hr)) return hr;
        }
        hr=device->CreateCommandList(0,D3D12_COMMAND_LIST_TYPE_DIRECT,slots[0].allocator.Get(),nullptr,IID_PPV_ARGS(&commands));
        if (FAILED(hr)) return hr;
        return commands->Close();
    }
    HRESULT Wait(UINT64 value) {
        if (!value || fence->GetCompletedValue()>=value) return S_OK;
        return fence->SetEventOnCompletion(value,nullptr);
    }
    template<class Record> HRESULT Submit(Record record) {
        auto& slot=slots[next];
        HRESULT hr=Wait(slot.submitted);
        if (FAILED(hr)) return hr;
        hr=slot.allocator->Reset();
        if (FAILED(hr)) return hr;
        hr=commands->Reset(slot.allocator.Get(),nullptr);
        if (FAILED(hr)) return hr;
        record(commands.Get());
        hr=commands->Close();
        if (FAILED(hr)) return hr;
        ID3D12CommandList* lists[]={commands.Get()};
        queue->ExecuteCommandLists(1,lists);
        UINT64 signal=submitted+1;
        hr=queue->Signal(fence.Get(),signal);
        if (FAILED(hr)) return hr;
        slot.submitted=submitted=signal;
        next=(next+1)%3;
        return S_OK;
    }
    // NGX reads every resource except the last, which receives the reconstructed image.
    template<size_t N,class Record> HRESULT WithResources(ID3D11DeviceContext* context,
        ID3D11Resource* (&inputs)[N],Record record) {
        ComPtr<ID3D12Resource> textures[N];
        D3D12_RESOURCE_BARRIER barriers[N]{};
        size_t count=0;
        HRESULT hr=S_OK;
        for (;count<N;count++) {
            hr=interop->UnwrapUnderlyingResource(inputs[count],queue.Get(),IID_PPV_ARGS(&textures[count]));
            if (FAILED(hr)) break;
            auto& b=barriers[count];
            b.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            b.Transition.pResource=textures[count].Get();
            b.Transition.Subresource=D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            b.Transition.StateBefore=D3D12_RESOURCE_STATE_COMMON;
            b.Transition.StateAfter=count==N-1 ? D3D12_RESOURCE_STATE_UNORDERED_ACCESS : D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        }
        // Unwrap schedules any required work but does not itself flush the 11-on-12 context.
        context->Flush();
        if (SUCCEEDED(hr)) hr=Submit([&](ID3D12GraphicsCommandList* list) {
            list->ResourceBarrier(N,barriers);
            record(list,textures);
            for (auto& b : barriers) {
                b.Transition.StateBefore=b.Transition.StateAfter;
                b.Transition.StateAfter=D3D12_RESOURCE_STATE_COMMON;
            }
            list->ResourceBarrier(N,barriers);
        });
        ID3D12Fence* fences[]={fence.Get()};
        const bool executed=SUCCEEDED(hr);
        for (size_t i=0;i<count;i++) {
            HRESULT returned=interop->ReturnUnderlyingResource(inputs[i],executed?1:0,&submitted,fences);
            if (SUCCEEDED(hr)) hr=returned;
        }
        return hr;
    }
};
