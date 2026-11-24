// Standalone test. Read back the real DXGI buffer after the production GPU copy, before flip-discard.
#include "D3D12Presenter.cpp"
#include <dxgi1_6.h>
#include <cstdio>
#include <stdexcept>
#include <thread>
#include <vector>

namespace {
void Require(HRESULT hr, const char* step)
{
    if (FAILED(hr)) {
        std::fprintf(stderr, "%s failed (0x%08lx)\n", step, static_cast<unsigned long>(hr));
        throw std::runtime_error(step);
    }
}

ComPtr<ID3D11Device> CreateEffectsDevice(HMODULE bridge)
{
    using Create = HRESULT(__cdecl*)(IUnknown*, ID3D11Device**);
    auto create = reinterpret_cast<Create>(GetProcAddress(bridge, "P5CreateD3D12Effects"));
    if (!create) throw std::runtime_error("Missing P5CreateD3D12Effects");
    ComPtr<ID3D11Device> device;
    Require(create(nullptr, &device), "Create production effects device");
    return device;
}

bool FindHdrMonitor(RECT& area)
{
    ComPtr<IDXGIFactory1> factory;
    Require(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "Enumerate displays");
    for (UINT a = 0;; ++a) {
        ComPtr<IDXGIAdapter1> adapter;
        HRESULT hr = factory->EnumAdapters1(a, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        Require(hr, "Enumerate adapter");
        for (UINT n = 0;; ++n) {
            ComPtr<IDXGIOutput> output;
            hr = adapter->EnumOutputs(n, &output);
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            Require(hr, "Enumerate output");
            ComPtr<IDXGIOutput6> extended;
            DXGI_OUTPUT_DESC1 d{};
            if (SUCCEEDED(output.As(&extended)) && SUCCEEDED(extended->GetDesc1(&d)) && d.AttachedToDesktop &&
                d.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020) {
                area = d.DesktopCoordinates;
                return true;
            }
        }
    }
    return false;
}

void VerifyPixels(D3D12Display& display, UINT width, UINT height, unsigned sequence)
{
    std::vector<UINT> pixels(size_t(width) * height);
    for (UINT y = 0; y < height; ++y) for (UINT x = 0; x < width; ++x) {
        UINT value = sequence * 127 + x * 97 + y * 389;
        pixels[size_t(y) * width + x] = display.format == DXGI_FORMAT_R10G10B10A2_UNORM
            ? 0xc0000000u | (value & 1023u) | ((value * 3u & 1023u) << 10) | ((value * 7u & 1023u) << 20)
            : 0xff000000u | (value & 255u) | ((value * 3u & 255u) << 8) | ((value * 7u & 255u) << 16);
    }
    D3D11_TEXTURE2D_DESC td{};
    td.Width = width;
    td.Height = height;
    td.ArraySize = td.MipLevels = td.SampleDesc.Count = 1;
    td.Format = display.format;
    td.BindFlags = D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> source;
    Require(display.wrapper->CreateTexture2D(&td, nullptr, &source), "Create test frame");
    display.context->UpdateSubresource(source.Get(), 0, nullptr, pixels.data(), width * 4, 0);
    Require(display.CopyFrame(source.Get()), "Copy native frame");
    auto back = display.buffers[display.chain->GetCurrentBackBufferIndex()];
    auto desc = back->GetDesc();
    if (desc.Width != width || desc.Height != height || desc.Format != td.Format)
        throw std::runtime_error("Swap-chain buffer has unexpected size or format");
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout{};
    UINT64 bytes = 0;
    display.gpu.device->GetCopyableFootprints(&desc, 0, 1, 0, &layout, nullptr, nullptr, &bytes);
    D3D12_HEAP_PROPERTIES readbackHeap{};
    readbackHeap.Type = D3D12_HEAP_TYPE_READBACK;
    readbackHeap.CreationNodeMask = readbackHeap.VisibleNodeMask = 1;
    D3D12_RESOURCE_DESC buffer{};
    buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    buffer.Width = bytes;
    buffer.Height = buffer.DepthOrArraySize = buffer.MipLevels = buffer.SampleDesc.Count = 1;
    buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ComPtr<ID3D12Resource> readback;
    Require(display.gpu.device->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE, &buffer,
        D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback)), "Create verification readback");
    Require(display.gpu.Submit([&](ID3D12GraphicsCommandList* list) {
        auto barrier = Transition(back.Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_SOURCE);
        list->ResourceBarrier(1, &barrier);
        D3D12_TEXTURE_COPY_LOCATION src{}, dst{};
        src.pResource = back.Get();
        src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst.pResource = readback.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        dst.PlacedFootprint = layout;
        list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter);
        list->ResourceBarrier(1, &barrier);
    }), "Submit verification copy");
    Require(display.gpu.Wait(display.gpu.submitted), "Complete verification copy");
    void* mapped = nullptr;
    D3D12_RANGE range{0, static_cast<SIZE_T>(bytes)};
    Require(readback->Map(0, &range, &mapped), "Read actual swap-chain pixels");
    bool equal = true;
    for (UINT y = 0; y < height; ++y) {
        auto row = static_cast<const BYTE*>(mapped) + layout.Offset + size_t(y) * layout.Footprint.RowPitch;
        if (memcmp(row, pixels.data() + size_t(y) * width, width * 4) != 0) {equal = false; break;}
    }
    D3D12_RANGE written{};
    readback->Unmap(0, &written);
    if (!equal) throw std::runtime_error("Swap-chain pixels differ from the effects frame");
    // Reuse the source on 11On12 after the native copy and exercise the normal presentation entry point.
    display.context->UpdateSubresource(source.Get(), 0, nullptr, pixels.data(), width * 4, 0);
    Require(p5_d3d12_display_frame(&display, source.Get()), "Present completed frame");
}

void VerifyStopWhileQueueBusy(HWND window, ID3D11Device* device)
{
    D3D12Display* raw = nullptr;
    Require(p5_d3d12_display_create(window, device, 137, 95, 0, &raw), "Create stop test presenter");
    std::unique_ptr<D3D12Display> display(raw);
    VerifyPixels(*display, 137, 95, 1);
    ComPtr<ID3D12Fence> gate;
    Require(display->gpu.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&gate)), "Create queue gate");
    HANDLE returned = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!returned) throw std::runtime_error("Cannot create queue test event");
    Require(display->gpu.queue->Wait(gate.Get(), 1), "Pause shared queue during resize");
    std::thread recover([&] {
        // Rescue a regressed, infinitely blocking implementation as well.
        WaitForSingleObject(returned, 5000);
        gate->Signal(1);
    });
    HRESULT resize = display->EnsureSize(163, 101);
    SetEvent(returned);
    recover.join();
    if (resize != DXGI_ERROR_WAIT_TIMEOUT) throw std::runtime_error("Stalled resize did not time out");
    VerifyPixels(*display, 163, 101, 2);
    std::puts("D3D12 stalled resize timed out and recovered with exact pixels.");
    ResetEvent(returned);
    // Release the queue after Stop returns, with a watchdog for the blocking implementation.
    std::thread release([&] {
        WaitForSingleObject(returned, 1500);
        gate->Signal(2);
    });
    HRESULT hr = display->gpu.queue->Wait(gate.Get(), 2);
    HWND child = display->child;
    auto start = GetTickCount64();
    p5_d3d12_display_destroy(display.release());
    auto elapsed = GetTickCount64() - start;
    SetEvent(returned);
    release.join();
    CloseHandle(returned);
    Require(hr, "Pause shared D3D12 queue");
    if (elapsed >= 1200 || IsWindow(child)) throw std::runtime_error("Stop waited for a blocked GPU or retained the playback child");
    std::printf("D3D12 closed its window in %llu ms with a blocked shared queue.\n", elapsed);
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) {std::fprintf(stderr, "Pass the absolute path to Cadrivect.Ngx.dll.\n"); return 2;}
    const auto previous = GetThreadDesktop(GetCurrentThreadId());
    wchar_t name[80];
    swprintf_s(name, L"P5Cast-DXGI-Test-%lu", GetCurrentProcessId());
    HDESK desktop = CreateDesktopW(name, nullptr, nullptr, 0, GENERIC_ALL, nullptr);
    if (!desktop || !SetThreadDesktop(desktop)) return 2;
    HMODULE bridge = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    HWND window{};
    int status = 1;
    try {
        if (!bridge) throw std::runtime_error("Could not load the production effects bridge");
        auto device = CreateEffectsDevice(bridge);
        RECT monitor{};
        bool hasHdr = FindHdrMonitor(monitor);
        window = CreateWindowExW(WS_EX_NOACTIVATE, L"STATIC", L"", WS_POPUP | WS_VISIBLE,
            monitor.left + 32, monitor.top + 32, 400, 300, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (!window) throw std::runtime_error("Could not create the test window");
        for (int hdr = 0; hdr <= int(hasHdr); ++hdr) {
            D3D12Display* raw = nullptr;
            Require(p5_d3d12_display_create(window, device.Get(), 137, 95, hdr, &raw), "Create native presenter");
            std::unique_ptr<D3D12Display> display(raw);
            VerifyPixels(*display, 137, 95, 1);
            Require(p5_d3d12_display_resize(raw, 259, 133), "Resize child before changing source");
            VerifyPixels(*display, 137, 95, 2);
            VerifyPixels(*display, 259, 133, 3);
            std::printf("D3D12 %s swap-chain pixels, source reuse, and resize passed.\n", hdr ? "PQ/10-bit" : "SDR/8-bit");
        }
        if (!hasHdr) std::puts("HDR10 pixel test skipped because Windows has no active HDR output.");
        VerifyStopWhileQueueBusy(window, device.Get());
        status = 0;
    } catch (const std::exception& e) {std::fprintf(stderr, "%s\n", e.what());}
    if (window) DestroyWindow(window);
    if (bridge) FreeLibrary(bridge);
    SetThreadDesktop(previous);
    CloseDesktop(desktop);
    return status;
}
