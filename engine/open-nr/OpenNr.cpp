#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <d3d11on12.h>
#include <dxgi.h>
#include "vk_context.h"
#include "../vulkan/DxvkInterop.h"
#include "nr_graph.h"
#include "reference.h"
#include "numeric.h"
#include <filesystem>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <array>
#include <algorithm>
#include "ModelCheck.h"

namespace fs = std::filesystem;
namespace {
std::mutex dispatchLock; // volk's device dispatch table is process-global inside this DLL.
int dllAnchor;
std::string Utf8(const fs::path& path) {
    auto value = path.u8string();
    return {value.begin(), value.end()};
}
struct MissingModel : std::runtime_error {
    using std::runtime_error::runtime_error;
};

void Dx(HRESULT result, const char* what) {
    if (FAILED(result))
        throw std::runtime_error(what);
}
void Reason(wchar_t* dest, int capacity, const char* message) {
    if (!dest || capacity <= 0)
        return;
    dest[0] = L'\0';
    if (!*message)
        return;
    const int length = static_cast<int>(strnlen_s(message, 32767));
    for (int byteCount = std::min(length, capacity - 1); byteCount > 0; --byteCount) {
        const int written = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, message, byteCount, dest, capacity - 1);
        if (written > 0) {
            dest[written] = L'\0';
            return;
        }
    }
    wcsncpy_s(dest, capacity, L"OpenDLSS-NR provider error", _TRUNCATE);
}
fs::path Assets() {
    HMODULE dll{};
    wchar_t path[32768];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&dllAnchor), &dll))
        throw std::runtime_error("Could not locate the OpenDLSS-NR bridge");
    DWORD n = GetModuleFileNameW(dll, path, static_cast<DWORD>(std::size(path)));
    if (!n || n >= std::size(path))
        throw std::runtime_error("Could not locate OpenDLSS-NR kernel assets");
    return fs::path(path).parent_path() / L"open-nr";
}

struct AdapterId {
    bool uuid = false;
    LUID luid{};
    std::array<uint8_t, VK_UUID_SIZE> bytes{};
    explicit AdapterId(ID3D11Device* input) {
        ComPtr<DxvkDevice> dxvk;
        if (SUCCEEDED(input->QueryInterface(IID_PPV_ARGS(&dxvk)))) {
            VkInstance instance{};
            VkPhysicalDevice gpu{};
            VkDevice unused{};
            dxvk->GetVulkanHandles(&instance, &gpu, &unused);
            auto props = reinterpret_cast<PFN_vkGetPhysicalDeviceProperties2>(
                vkGetInstanceProcAddr(instance, "vkGetPhysicalDeviceProperties2"));
            if (!gpu || !props)
                throw std::runtime_error("P5Cast could not identify the selected Vulkan adapter");
            VkPhysicalDeviceIDProperties id{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
            VkPhysicalDeviceProperties2 p{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, &id};
            props(gpu, &p);
            memcpy(bytes.data(), id.deviceUUID, bytes.size());
            uuid = true;
            return;
        }
        ComPtr<ID3D11On12Device1> wrapped;
        if (SUCCEEDED(input->QueryInterface(IID_PPV_ARGS(&wrapped)))) {
            ComPtr<ID3D12Device> native;
            Dx(wrapped->GetD3D12Device(IID_PPV_ARGS(&native)), "Could not identify the D3D12 adapter");
            luid = native->GetAdapterLuid();
            return;
        }
        ComPtr<IDXGIDevice> dxgi;
        ComPtr<IDXGIAdapter> adapter;
        DXGI_ADAPTER_DESC description{};
        Dx(input->QueryInterface(IID_PPV_ARGS(&dxgi)), "Could not identify the Direct3D adapter");
        Dx(dxgi->GetAdapter(&adapter), "Could not read the Direct3D adapter");
        Dx(adapter->GetDesc(&description), "Could not read the Direct3D adapter LUID");
        luid = description.AdapterLuid;
    }
    bool Matches(const VkPhysicalDeviceIDProperties& id) const {
        return uuid ? !memcmp(bytes.data(), id.deviceUUID, bytes.size())
                    : id.deviceLUIDValid && !memcmp(&luid, id.deviceLUID, VK_LUID_SIZE);
    }
};

struct ComputeDevice {
    VkInstance instance{};
    VkPhysicalDevice physical{};
    VkDevice device{};
    VkQueue queue{};
    uint32_t family{};
    static inline VkInstance activeInstance{};
    static inline VkDevice activeDevice{};
    void Activate() const {
        if (instance && instance != activeInstance) {
            volkLoadInstanceOnly(instance);
            activeInstance = instance;
            activeDevice = VK_NULL_HANDLE;
        }
        if (device && device != activeDevice) {
            volkLoadDevice(device);
            activeDevice = device;
        }
    }
    void Open(ID3D11Device* input) {
        if (volkInitialize() != VK_SUCCESS)
            throw std::runtime_error("OpenDLSS-NR needs a working Vulkan loader");
        AdapterId selected(input);
        VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
        app.pApplicationName = "P5Cast OpenDLSS-NR";
        app.apiVersion = VK_API_VERSION_1_3;
        VkInstanceCreateInfo create{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
        create.pApplicationInfo = &app;
        VK_CHECK(vkCreateInstance(&create, nullptr, &instance));
        Activate();
        uint32_t count = 0;
        VK_CHECK(vkEnumeratePhysicalDevices(instance, &count, nullptr));
        std::vector<VkPhysicalDevice> gpus(count);
        VK_CHECK(vkEnumeratePhysicalDevices(instance, &count, gpus.data()));
        for (auto gpu : gpus) {
            VkPhysicalDeviceIDProperties id{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
            VkPhysicalDeviceProperties2 p{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, &id};
            vkGetPhysicalDeviceProperties2(gpu, &p);
            if (!selected.Matches(id))
                continue;
            if (p.properties.vendorID != 0x10de || p.properties.apiVersion < VK_API_VERSION_1_3)
                throw std::runtime_error(
                    "OpenDLSS-NR needs an NVIDIA GPU with Vulkan 1.3 and FP8 cooperative matrices");
            physical = gpu;
            break;
        }
        if (!physical)
            throw std::runtime_error("OpenDLSS-NR could not match a Vulkan device to the playback GPU");
        vk::DeviceRequirements required;
        VK_CHECK(vkEnumerateDeviceExtensionProperties(physical, nullptr, &count, nullptr));
        std::vector<VkExtensionProperties> extensions(count);
        VK_CHECK(vkEnumerateDeviceExtensionProperties(physical, nullptr, &count, extensions.data()));
        for (auto name : required.extensions)
            if (std::none_of(extensions.begin(), extensions.end(),
                             [=](const VkExtensionProperties& x) { return !strcmp(name, x.extensionName); }))
                throw std::runtime_error(std::string("Playback GPU or driver lacks OpenDLSS-NR extension ") + name);
        vkGetPhysicalDeviceQueueFamilyProperties(physical, &count, nullptr);
        std::vector<VkQueueFamilyProperties> queues(count);
        vkGetPhysicalDeviceQueueFamilyProperties(physical, &count, queues.data());
        for (family = 0; family < count; family++)
            if (queues[family].queueFlags & VK_QUEUE_COMPUTE_BIT)
                break;
        if (family == count)
            throw std::runtime_error("OpenDLSS-NR needs a Vulkan compute queue");
        float priority = 1;
        VkDeviceQueueCreateInfo q{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
        q.queueFamilyIndex = family;
        q.queueCount = 1;
        q.pQueuePriorities = &priority;
        VkDeviceCreateInfo info{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, &required.features};
        info.queueCreateInfoCount = 1;
        info.pQueueCreateInfos = &q;
        info.enabledExtensionCount = static_cast<uint32_t>(required.extensions.size());
        info.ppEnabledExtensionNames = required.extensions.data();
        // Device creation validates all requested feature bits on this adapter, beyond extension names.
        VK_CHECK(vkCreateDevice(physical, &info, nullptr, &device));
        Activate();
        vkGetDeviceQueue(device, family, 0, &queue);
    }
    ~ComputeDevice() {
        if (device)
            vkDestroyDevice(device, nullptr);
        if (instance)
            vkDestroyInstance(instance, nullptr);
        if (instance == activeInstance) {
            activeInstance = VK_NULL_HANDLE;
            activeDevice = VK_NULL_HANDLE;
        }
    }
};

struct FrameParams {
    uint32_t width, height, fullWidth, fullHeight, history, seed, parity;
    float blend;
};
// D3D11 UpdateSubresource reads this allocation on the CPU. The first host-visible
// Vulkan type on discrete GPUs can be write-combined: sequential reads are extremely
// slow there. Prefer cached system RAM and let the transfer engine copy into it.
void CreateReadback(const ComputeDevice& gpu, VkDeviceSize bytes, vk::Buffer& staging) {
    VkBufferCreateInfo create{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    create.size = bytes;
    create.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    VK_CHECK(vkCreateBuffer(gpu.device, &create, nullptr, &staging.buffer));
    VkMemoryRequirements requirements{};
    vkGetBufferMemoryRequirements(gpu.device, staging.buffer, &requirements);
    VkPhysicalDeviceMemoryProperties memory{};
    vkGetPhysicalDeviceMemoryProperties(gpu.physical, &memory);
    uint32_t selected = UINT32_MAX;
    for (uint32_t i = 0; i < memory.memoryTypeCount; i++) {
        const auto flags = memory.memoryTypes[i].propertyFlags;
        const auto needed = VK_MEMORY_PROPERTY_HOST_COHERENT_BIT | VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT;
        if (!(requirements.memoryTypeBits & (1u << i)) || (flags & needed) != needed)
            continue;
        if (selected == UINT32_MAX || (flags & VK_MEMORY_PROPERTY_HOST_CACHED_BIT))
            selected = i;
        if (flags & VK_MEMORY_PROPERTY_HOST_CACHED_BIT)
            break;
    }
    if (selected == UINT32_MAX)
        throw std::runtime_error("OpenDLSS-NR requires coherent host-visible video memory");
    VkMemoryAllocateInfo alloc{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    alloc.allocationSize = requirements.size;
    alloc.memoryTypeIndex = selected;
    VK_CHECK(vkAllocateMemory(gpu.device, &alloc, nullptr, &staging.memory));
    VK_CHECK(vkBindBufferMemory(gpu.device, staging.buffer, staging.memory, 0));
    VK_CHECK(vkMapMemory(gpu.device, staging.memory, 0, VK_WHOLE_SIZE, 0, &staging.mapped));
}
struct Session {
    ComputeDevice gpu;
    ComPtr<ID3D11Device> source;
    ComPtr<ID3D11DeviceContext> immediate;
    ComPtr<ID3D11Texture2D> inputStaging[2];
    std::unique_ptr<vk::Context> ctx;
    std::unique_ptr<nr::Model> model;
    std::unique_ptr<nr::Kernels> kernels;
    std::unique_ptr<nr::Graph> graph;
    nr::Activation* features{};
    vk::Pipeline prepare{}, compose{};
    vk::Buffer color{}, motion{}, uploadColor{}, uploadMotion{}, history{}, output{}, params{}, readback{};
    VkCommandPool pool{};
    VkCommandBuffer frame{};
    VkFence fence{};
    PFN_vkWaitForFences waitFence{};
    bool pending = false, opened = false;
    FrameParams state{};
    bool haveHistory = false;
    void Open(ID3D11Device* device, const fs::path& directory, uint32_t w, uint32_t h) {
        if (!device || !w || !h || w > 4096 || h > 4096 || uint64_t(w) * h > 4096ull * 2160)
            throw std::runtime_error("Unsupported OpenDLSS-NR frame dimensions or device");
        source = device;
        source->GetImmediateContext(&immediate);
        gpu.Open(device);
        const fs::path assets = Assets(), shaders = assets / L"shaders";
        if (!fs::is_regular_file(shaders / L"video-prepare.spv") ||
            !fs::is_regular_file(shaders / L"video-compose.spv") || !fs::is_directory(assets / L"ptx"))
            throw std::runtime_error("OpenDLSS-NR kernel assets are missing");
        std::error_code fileError;
        if (directory.empty() || !fs::is_regular_file(directory / L"manifest.json", fileError))
            throw MissingModel("Select the folder containing the OpenDLSS-NR 310.8.0 model manifest");
        auto files = [&] {
            try {
                return ValidateOpenNrModel(directory);
            } catch (const std::runtime_error& error) {
                throw MissingModel(error.what());
            }
        }();
        ctx = std::make_unique<vk::Context>(gpu.instance, gpu.physical, gpu.device, gpu.family, 0);
        try {
            model = std::make_unique<nr::Model>(*ctx, Utf8(directory), true);
        } catch (const std::runtime_error& error) {
            if (std::string_view(error.what()).starts_with("stage SHA-256 mismatch:"))
                throw MissingModel(error.what());
            throw;
        }
        kernels = std::make_unique<nr::Kernels>(*ctx, Utf8(shaders));
        kernels->setSiluTable(ref::siluTable());
        auto g = nr::Geometry::fromValid(w, h);
        state = {w, h, g.fullWidth, g.fullHeight, 0, 0, 0, 1.f};
        const auto& blend = model->tensor(70, 0, "blend_scale");
        if (blend.byteLength < 2)
            throw std::runtime_error("Model blend_scale is incomplete");
        state.blend = num::f16ToF32(uint16_t(blend.bytes[0] | (blend.bytes[1] << 8)));
        graph = std::make_unique<nr::Graph>(*ctx, *model, *kernels, g, nr::Graph::Options{});
        features = graph->allocate("video input", g.fullWidth * g.fullHeight, 16, nr::Format::F32);
        const VkDeviceSize pixels = VkDeviceSize(w) * h;
        color = ctx->createBuffer(pixels * 8, false, "video color");
        motion = ctx->createBuffer(pixels * 4, false, "video motion");
        uploadColor = ctx->createBuffer(pixels * 8, true, "video color upload");
        uploadMotion = ctx->createBuffer(pixels * 4, true, "video motion upload");
        history = ctx->createBuffer(pixels * 16, false, "video history");
        output = ctx->createBuffer(pixels * 8, false, "video output");
        CreateReadback(gpu, pixels * 8, readback);
        params = ctx->createBuffer(sizeof(state), true, "video parameters");
        for (int i = 0; i < 2; i++) {
            D3D11_TEXTURE2D_DESC d{};
            d.Width = w;
            d.Height = h;
            d.MipLevels = d.ArraySize = d.SampleDesc.Count = 1;
            d.Format = i == 0 ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_R16G16_FLOAT;
            d.Usage = D3D11_USAGE_STAGING;
            d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            Dx(source->CreateTexture2D(&d, nullptr, &inputStaging[i]),
               "OpenDLSS-NR could not create a frame staging texture");
        }
        prepare = ctx->createComputePipeline(ctx->loadShaderModule(Utf8(shaders / L"video-prepare.spv")), {},
                                             "video prepare");
        compose = ctx->createComputePipeline(ctx->loadShaderModule(Utf8(shaders / L"video-compose.spv")), {},
                                             "video compose");
        VkCommandPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
        poolInfo.queueFamilyIndex = gpu.family;
        VK_CHECK(vkCreateCommandPool(gpu.device, &poolInfo, nullptr, &pool));
        VkCommandBufferAllocateInfo alloc{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
        alloc.commandPool = pool;
        alloc.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        alloc.commandBufferCount = 1;
        VK_CHECK(vkAllocateCommandBuffers(gpu.device, &alloc, &frame));
        VkFenceCreateInfo f{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        VK_CHECK(vkCreateFence(gpu.device, &f, nullptr, &fence));
        waitFence = reinterpret_cast<PFN_vkWaitForFences>(vkGetDeviceProcAddr(gpu.device, "vkWaitForFences"));
        // The kernel graph and descriptor sets are invariant across frames. Only the uploaded
        // image, estimated motion, history parity, and reset/seed change between submissions.
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        VK_CHECK(vkBeginCommandBuffer(frame, &begin));
        // Preprocessing and history reprojection revisit neighboring pixels. Keep those
        // reads in device-local memory and move each uploaded frame across PCIe once.
        VkBufferCopy upload{0, 0, pixels * 8};
        vkCmdCopyBuffer(frame, uploadColor.buffer, color.buffer, 1, &upload);
        upload.size = pixels * 4;
        vkCmdCopyBuffer(frame, uploadMotion.buffer, motion.buffer, 1, &upload);
        ctx->transferBarrier(frame);
        ctx->computeBarrier(frame);
        Dispatch(prepare, features->buffer, g.fullWidth, g.fullHeight);
        ctx->computeBarrier(frame);
        graph->record(frame, *features);
        ctx->computeBarrier(frame);
        Dispatch(compose, graph->head().buffer, w, h);
        VkMemoryBarrier barrier{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
        barrier.srcAccessMask = VK_ACCESS_SHADER_WRITE_BIT;
        barrier.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
        vkCmdPipelineBarrier(frame, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 1,
                             &barrier, 0, nullptr, 0, nullptr);
        VkBufferCopy copy{0, 0, pixels * 8};
        vkCmdCopyBuffer(frame, output.buffer, readback.buffer, 1, &copy);
        barrier.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        barrier.dstAccessMask = VK_ACCESS_HOST_READ_BIT;
        vkCmdPipelineBarrier(frame, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &barrier, 0,
                             nullptr, 0, nullptr);
        VK_CHECK(vkEndCommandBuffer(frame));
        opened = true;
    }
    void Dispatch(vk::Pipeline pipeline, const vk::Buffer& graphBuffer, unsigned w, unsigned h) {
        const vk::Buffer* bindings[vk::kGenericBindings] = {&color, &motion, &history, &graphBuffer, &output, &params};
        auto desc = ctx->allocateSet(bindings);
        vkCmdBindPipeline(frame, VK_PIPELINE_BIND_POINT_COMPUTE, pipeline.pipeline);
        vkCmdBindDescriptorSets(frame, VK_PIPELINE_BIND_POINT_COMPUTE, ctx->pipelineLayout(), 0, 1, &desc, 0, nullptr);
        vkCmdDispatch(frame, (w + 7) / 8, (h + 7) / 8, 1);
    }
    void Evaluate(ID3D11Texture2D* image, ID3D11Texture2D* vectors, ID3D11Texture2D* result, bool reset) {
        if (pending)
            throw std::runtime_error("The previous OpenDLSS-NR frame is still on the GPU");
        ID3D11Texture2D* textures[] = {image, vectors, result};
        for (int i = 0; i < 3; i++) {
            if (!textures[i])
                throw std::runtime_error("Missing OpenDLSS-NR video texture");
            D3D11_TEXTURE2D_DESC d{};
            textures[i]->GetDesc(&d);
            ComPtr<ID3D11Device> owner;
            textures[i]->GetDevice(&owner);
            if (owner.Get() != source.Get() || d.Width != state.width || d.Height != state.height || d.MipLevels != 1 ||
                d.ArraySize != 1 || d.SampleDesc.Count != 1 ||
                d.Format != (i == 1 ? DXGI_FORMAT_R16G16_FLOAT : DXGI_FORMAT_R16G16B16A16_FLOAT))
                throw std::runtime_error("OpenDLSS-NR texture or adapter mismatch");
            if (i < 2)
                immediate->CopyResource(inputStaging[i].Get(), textures[i]);
        }
        for (int i = 0; i < 2; i++) {
            D3D11_MAPPED_SUBRESOURCE map{};
            Dx(immediate->Map(inputStaging[i].Get(), 0, D3D11_MAP_READ, 0, &map),
               "OpenDLSS-NR could not read the input");
            uint32_t pitch = state.width * (i == 0 ? 8 : 4);
            auto dest = static_cast<uint8_t*>((i == 0 ? uploadColor : uploadMotion).mapped);
            if (map.RowPitch == pitch)
                memcpy(dest, map.pData, size_t(pitch) * state.height);
            else
                for (uint32_t y = 0; y < state.height; y++)
                    memcpy(dest + size_t(y) * pitch, static_cast<uint8_t*>(map.pData) + size_t(y) * map.RowPitch,
                           pitch);
            immediate->Unmap(inputStaging[i].Get(), 0);
        }
        state.history = haveHistory && !reset;
        memcpy(params.mapped, &state, sizeof(state));
        VK_CHECK(vkResetFences(gpu.device, 1, &fence));
        VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
        submit.commandBufferCount = 1;
        submit.pCommandBuffers = &frame;
        VK_CHECK(vkQueueSubmit(gpu.queue, 1, &submit, fence));
        pending = true;
        const VkResult completed = waitFence(gpu.device, 1, &fence, VK_TRUE, 2000000000ull);
        if (completed == VK_TIMEOUT)
            throw std::runtime_error("OpenDLSS-NR exceeded the two-second frame deadline");
        if (completed == VK_SUCCESS || completed == VK_ERROR_DEVICE_LOST)
            pending = false;
        VK_CHECK(completed);
        if (kernels->chainTimeouts().waits)
            throw std::runtime_error("OpenDLSS-NR kernel synchronization timed out");
        immediate->UpdateSubresource(result, 0, nullptr, readback.mapped, state.width * 8, 0);
        Dx(source->GetDeviceRemovedReason(), "OpenDLSS-NR output transfer failed");
        state.seed++;
        state.parity ^= 1;
        haveHistory = true;
    }
    ~Session() {
        gpu.Activate();
        // Model setup performs short synchronous upload submissions. A failed setup
        // can stop in the middle of one; a completed session has only its frame fence.
        if (gpu.device && !opened && ctx)
            vkDeviceWaitIdle(gpu.device);
        if (pending)
            waitFence(gpu.device, 1, &fence, VK_TRUE, UINT64_MAX);
        if (pool)
            vkDestroyCommandPool(gpu.device, pool, nullptr);
        if (fence)
            vkDestroyFence(gpu.device, fence, nullptr);
        if (ctx) {
            if (prepare.pipeline)
                ctx->destroyPipeline(prepare);
            if (compose.pipeline)
                ctx->destroyPipeline(compose);
            for (auto p : {&color, &motion, &uploadColor, &uploadMotion, &output, &history, &params, &readback})
                ctx->destroyBuffer(*p);
        }
    }
};

struct PendingRelease {
    Session* session;
    HMODULE module;
};
void CALLBACK ReleaseAfterFrame(PTP_CALLBACK_INSTANCE callback, void* parameter) {
    std::unique_ptr<PendingRelease> release(static_cast<PendingRelease*>(parameter));
    Session* session = release->session;
    // A device-specific entry point stays valid while other sessions switch volk's
    // active dispatch table. Keep both the device and this DLL alive for this wait.
    session->waitFence(session->gpu.device, 1, &session->fence, VK_TRUE, UINT64_MAX);
    FreeLibraryWhenCallbackReturns(callback, release->module);
    std::lock_guard<std::mutex> guard(dispatchLock);
    session->pending = false;
    delete session;
}
} // namespace

#define OPEN_EXPORT extern "C" __declspec(dllexport)
OPEN_EXPORT int P5OpenNrOpen(ID3D11Device* device, const wchar_t* model, int width, int height, Session** out,
                             wchar_t* reason, int count) {
    if (!out)
        return 1;
    *out = nullptr;
    std::lock_guard<std::mutex> guard(dispatchLock);
    try {
        auto state = std::make_unique<Session>();
        state->Open(device, model ? model : L"", width, height);
        *out = state.release();
        Reason(reason, count, "");
        return 0;
    } catch (const MissingModel& e) {
        Reason(reason, count, e.what());
        return 2;
    } catch (const std::exception& e) {
        Reason(reason, count, e.what());
        return 1;
    }
}
OPEN_EXPORT int P5OpenNrEvaluate(Session* session, ID3D11Texture2D* color, ID3D11Texture2D* motion,
                                 ID3D11Texture2D* output, int reset, wchar_t* reason, int count) {
    std::lock_guard<std::mutex> guard(dispatchLock);
    try {
        if (!session)
            throw std::runtime_error("Missing OpenDLSS-NR session");
        session->gpu.Activate();
        session->Evaluate(color, motion, output, reset != 0);
        Reason(reason, count, "");
        return 0;
    } catch (const std::exception& e) {
        Reason(reason, count, e.what());
        return 1;
    }
}
OPEN_EXPORT void P5OpenNrClose(Session* session) {
    std::lock_guard<std::mutex> guard(dispatchLock);
    if (!session)
        return;
    if (session->pending) {
        session->gpu.Activate();
        if (vkGetFenceStatus(session->gpu.device, session->fence) == VK_NOT_READY) {
            auto release = new (std::nothrow) PendingRelease{session, nullptr};
            if (release && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                                              reinterpret_cast<LPCWSTR>(&dllAnchor), &release->module)) {
                if (TrySubmitThreadpoolCallback(ReleaseAfterFrame, release, nullptr))
                    return;
                FreeLibrary(release->module);
            }
            delete release;
        }
    }
    delete session;
}
