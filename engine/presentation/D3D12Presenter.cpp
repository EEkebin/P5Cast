#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dxgi1_4.h>
#include <array>
#include <memory>
#include <new>
#include "../nvidia/D3D12Effects.h"

namespace {
HRESULT WindowFailure()
{
    DWORD error = GetLastError();
    return error ? HRESULT_FROM_WIN32(error) : E_FAIL;
}

D3D12_RESOURCE_BARRIER Transition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
{
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after};
    return barrier;
}

struct D3D12Display {
    static constexpr UINT bufferCount = 2;
    HWND child{};
    bool shown{};
    UINT bufferWidth{}, bufferHeight{};
    DXGI_FORMAT format{};
    DXGI_COLOR_SPACE_TYPE colorSpace{};
    Effects12 gpu;
    ComPtr<ID3D11Device> wrapper;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain3> chain;
    std::array<ComPtr<ID3D12Resource>, bufferCount> buffers;

    HRESULT Open(HWND parent, ID3D11Device* device, UINT width, UINT height, bool hdr)
    {
        wrapper = device;
        HRESULT hr = gpu.Open(device);
        if (hr != S_OK) return hr == S_FALSE ? E_NOINTERFACE : hr;
        wrapper->GetImmediateContext(&context);
        child = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_NOPARENTNOTIFY, L"STATIC", L"",
            WS_CHILD | WS_DISABLED | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, width, height, parent, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (!child) return WindowFailure();

        ComPtr<IDXGIFactory2> factory;
        hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
        if (FAILED(hr)) return hr;
        format = hdr ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
        colorSpace = hdr ? DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 : DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = width;
        desc.Height = height;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = bufferCount;
        desc.Scaling = DXGI_SCALING_STRETCH;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        ComPtr<IDXGISwapChain1> created;
        hr = factory->CreateSwapChainForHwnd(gpu.queue.Get(), child, &desc, nullptr, nullptr, &created);
        if (FAILED(hr)) return hr;
        hr = created.As(&chain);
        if (FAILED(hr)) return hr;
        hr = factory->MakeWindowAssociation(child, DXGI_MWA_NO_ALT_ENTER);
        if (FAILED(hr)) return hr;
        return LoadBuffers(width, height);
    }

    HRESULT LoadBuffers(UINT width, UINT height)
    {
        UINT support = 0;
        HRESULT hr = chain->CheckColorSpaceSupport(colorSpace, &support);
        if (FAILED(hr)) return hr;
        if (!(support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT)) return DXGI_ERROR_UNSUPPORTED;
        hr = chain->SetColorSpace1(colorSpace);
        if (FAILED(hr)) return hr;
        std::array<ComPtr<ID3D12Resource>, bufferCount> ready;
        for (UINT i = 0; i < bufferCount; ++i) {
            hr = chain->GetBuffer(i, IID_PPV_ARGS(&ready[i]));
            if (FAILED(hr)) return hr;
        }
        buffers.swap(ready);
        bufferWidth = width;
        bufferHeight = height;
        return S_OK;
    }

    HRESULT Drain()
    {
        // Queue the fence after the last Present, which follows the copy's own fence.
        UINT64 done = gpu.submitted + 1;
        HRESULT hr = gpu.queue->Signal(gpu.fence.Get(), done);
        if (FAILED(hr)) return hr;
        gpu.submitted = done;
        return gpu.Wait(done);
    }

    HRESULT EnsureSize(UINT width, UINT height)
    {
        if (width == bufferWidth && height == bufferHeight && buffers[0]) return S_OK;
        HRESULT hr = Drain();
        if (FAILED(hr)) return hr;
        for (auto& buffer : buffers) buffer.Reset();
        hr = chain->ResizeBuffers(bufferCount, width, height, format, 0);
        if (FAILED(hr)) return hr;
        return LoadBuffers(width, height);
    }

    HRESULT CopyFrame(ID3D11Texture2D* texture)
    {
        D3D11_TEXTURE2D_DESC desc{};
        texture->GetDesc(&desc);
        if (desc.Format != format || desc.SampleDesc.Count != 1 || desc.ArraySize != 1 || desc.MipLevels != 1)
            return E_INVALIDARG;
        ComPtr<ID3D11Device> owner;
        texture->GetDevice(&owner);
        if (owner.Get() != wrapper.Get()) return E_INVALIDARG;
        HRESULT hr = EnsureSize(desc.Width, desc.Height);
        if (FAILED(hr)) return hr;

        ComPtr<ID3D12Resource> source;
        hr = gpu.interop->UnwrapUnderlyingResource(texture, gpu.queue.Get(), IID_PPV_ARGS(&source));
        if (FAILED(hr)) return hr;
        // Submit outstanding 11On12 rendering before our native copy on the very same queue.
        context->Flush();
        auto destination = buffers[chain->GetCurrentBackBufferIndex()].Get();
        hr = gpu.Submit([&](ID3D12GraphicsCommandList* list) {
            D3D12_RESOURCE_BARRIER barriers[] = {
                Transition(source.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE),
                Transition(destination, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST)
            };
            list->ResourceBarrier(2, barriers);
            list->CopyResource(destination, source.Get());
            for (auto& barrier : barriers) std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter);
            list->ResourceBarrier(2, barriers);
        });
        ID3D12Fence* fences[] = {gpu.fence.Get()};
        HRESULT returned = gpu.interop->ReturnUnderlyingResource(texture, SUCCEEDED(hr) ? 1 : 0, &gpu.submitted, fences);
        return FAILED(hr) ? hr : returned;
    }

    HRESULT Frame(ID3D11Texture2D* texture)
    {
        HRESULT hr = CopyFrame(texture);
        if (FAILED(hr)) return hr;
        // A hidden child can report DXGI_STATUS_OCCLUDED and discard its first frame.
        if (!shown) {
            ShowWindow(child, SW_SHOWNA);
            shown = true;
        }
        return chain->Present(0, 0);
    }

    ~D3D12Display()
    {
        if (chain) Drain();
        for (auto& buffer : buffers) buffer.Reset();
        chain.Reset();
        if (child) DestroyWindow(child);
    }
};
}

extern "C" __declspec(dllexport) HRESULT p5_d3d12_display_create(HWND parent, ID3D11Device* device,
    int width, int height, int hdr, D3D12Display** result)
{
    if (!result) return E_POINTER;
    *result = nullptr;
    if (!device || !IsWindow(parent) || width <= 0 || height <= 0) return E_INVALIDARG;
    std::unique_ptr<D3D12Display> display(new(std::nothrow) D3D12Display());
    if (!display) return E_OUTOFMEMORY;
    HRESULT hr = display->Open(parent, device, width, height, hdr != 0);
    if (SUCCEEDED(hr)) *result = display.release();
    return hr;
}

extern "C" __declspec(dllexport) HRESULT p5_d3d12_display_frame(D3D12Display* display, ID3D11Texture2D* texture)
{
    return display && texture ? display->Frame(texture) : E_INVALIDARG;
}

extern "C" __declspec(dllexport) HRESULT p5_d3d12_display_resize(D3D12Display* display, int width, int height)
{
    if (!display || width <= 0 || height <= 0) return E_INVALIDARG;
    // The compositor stretches the old buffer until the app commits a new render size.
    if (!SetWindowPos(display->child, nullptr, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE))
        return WindowFailure();
    return S_OK;
}

extern "C" __declspec(dllexport) void p5_d3d12_display_destroy(D3D12Display* display)
{
    delete display;
}
