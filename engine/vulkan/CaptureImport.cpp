#define VK_USE_PLATFORM_WIN32_KHR
#include <windows.h>
#include <dxgi1_2.h>
#include "DxvkInterop.h"
#include <cstring>
#include <memory>
#include <stdexcept>
#include <vector>

namespace {
void Require(bool valid, const char* reason) {
    if (!valid) throw std::runtime_error(reason);
}
void Check(VkResult result, const char* operation) {
    if (result != VK_SUCCESS) {
        char error[160];
        sprintf_s(error, "%s (Vulkan %d)", operation, static_cast<int>(result));
        throw std::runtime_error(error);
    }
}
void CopyReason(char* reason, int size, const char* message) {
    if (reason && size > 0) strncpy_s(reason, static_cast<size_t>(size), message, _TRUNCATE);
}
struct SharedHandle {
    HANDLE handle{};
    ~SharedHandle() { if (handle) CloseHandle(handle); }
};
struct CaptureSlot {
    ComPtr<ID3D11Texture2D> shared;
    ComPtr<IDXGIKeyedMutex> mutex;
    VkImage image{};
    VkDeviceMemory memory{};
    VkFence fence{};
    VkCommandBuffer cmd{};
    bool pending{};
};
bool AdapterLuid(VkPhysicalDevice physical, LUID& luid) {
    if (!physical) return false;
    VkPhysicalDeviceIDProperties identity{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
    VkPhysicalDeviceProperties2 properties{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2,&identity};
    vkGetPhysicalDeviceProperties2(physical,&properties);
    if (!identity.deviceLUIDValid) return false;
    static_assert(sizeof(luid)==VK_LUID_SIZE);
    memcpy(&luid,identity.deviceLUID,sizeof(luid));
    return true;
}
struct Capture {
    ComPtr<ID3D11Device> producer;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<DxvkDevice> dxvk;
    ComPtr<DxvkSurface> target;
    VkDevice device{};
    VkPhysicalDevice physical{};
    VkQueue queue{};
    uint32_t family{};
    VkCommandPool pool{};
    VkImage targetImage{};
    VkImageLayout targetLayout{};
    VkImageCreateInfo info{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    D3D11_TEXTURE2D_DESC desc{};
    PFN_vkGetMemoryWin32HandlePropertiesKHR handleProperties{};
    CaptureSlot slots[2];
    unsigned next{};
    HMODULE deferredModule{};

    void Open(ID3D11Device* capture, ID3D11Texture2D* dest) {
        Require(capture && dest, "Missing Windows capture or Vulkan destination device.");
        producer=capture;
        capture->GetImmediateContext(&context);
        dest->GetDesc(&desc);
        Require(desc.Width && desc.Height && desc.MipLevels==1 && desc.ArraySize==1 && desc.SampleDesc.Count==1 &&
            (desc.Format==DXGI_FORMAT_B8G8R8A8_UNORM || desc.Format==DXGI_FORMAT_R16G16B16A16_FLOAT),
            "Unsupported Windows capture texture.");
        Require(SUCCEEDED(dest->QueryInterface(IID_PPV_ARGS(&target))) && SUCCEEDED(target->GetDevice(&dxvk)),
            "The destination is not a Vulkan effects texture.");
        VkInstance instance{};
        dxvk->GetVulkanHandles(&instance,&physical,&device);
        dxvk->GetSubmissionQueue(&queue,&family);
        Require(physical && device && queue, "The Vulkan effects queue is unavailable.");
        ComPtr<IDXGIDevice> captureDxgi;
        ComPtr<IDXGIAdapter> adapter;
        DXGI_ADAPTER_DESC adapterDesc{};
        Require(SUCCEEDED(capture->QueryInterface(IID_PPV_ARGS(&captureDxgi))) &&
                SUCCEEDED(captureDxgi->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&adapterDesc)),
            "Cannot identify the Windows capture GPU.");
        LUID vulkanLuid{};
        Require(AdapterLuid(physical,vulkanLuid) && !memcmp(&vulkanLuid,&adapterDesc.AdapterLuid,sizeof(LUID)),
            "Windows capture and Vulkan are using different GPUs.");
        // The pinned DXVK device enables win32_keyed_mutex whenever advertised.
        // This extension adds a submit structure rather than a queryable command.
        uint32_t extensionCount=0;
        Check(vkEnumerateDeviceExtensionProperties(physical,nullptr,&extensionCount,nullptr),"Listing Vulkan capture extensions failed");
        std::vector<VkExtensionProperties> extensions(extensionCount);
        Check(vkEnumerateDeviceExtensionProperties(physical,nullptr,&extensionCount,extensions.data()),"Reading Vulkan capture extensions failed");
        bool keyed=false;
        for(const auto& ext : extensions) if(!strcmp(ext.extensionName,VK_KHR_WIN32_KEYED_MUTEX_EXTENSION_NAME)) keyed=true;
        Require(keyed, "This Vulkan driver does not support Windows keyed mutex synchronization.");
        handleProperties=reinterpret_cast<PFN_vkGetMemoryWin32HandlePropertiesKHR>(
            vkGetDeviceProcAddr(device,"vkGetMemoryWin32HandlePropertiesKHR"));
        Require(handleProperties, "Vulkan Win32 external memory is unavailable.");
        Require(SUCCEEDED(target->GetVulkanImageInfo(&targetImage,&targetLayout,&info)) &&
            targetImage && (info.usage & VK_IMAGE_USAGE_TRANSFER_DST_BIT), "The Vulkan texture cannot receive capture frames.");
        VkPhysicalDeviceExternalImageFormatInfo external{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO};
        external.handleType=VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
        VkPhysicalDeviceImageFormatInfo2 query{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2,&external};
        query.type=VK_IMAGE_TYPE_2D; query.format=info.format; query.tiling=VK_IMAGE_TILING_OPTIMAL; query.usage=VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
        VkExternalImageFormatProperties externalFormat{VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES};
        VkImageFormatProperties2 format{VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2,&externalFormat};
        Check(vkGetPhysicalDeviceImageFormatProperties2(physical,&query,&format), "Vulkan capture format query failed");
        Require((externalFormat.externalMemoryProperties.externalMemoryFeatures & VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT)!=0,
            "The Vulkan driver cannot import this Windows capture format.");
        VkCommandPoolCreateInfo create{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
        create.queueFamilyIndex=family;
        Check(vkCreateCommandPool(device,&create,nullptr,&pool),"Creating the Vulkan capture command pool failed");
        for (auto& slot : slots) CreateSlot(slot);
    }

    void CreateSlot(CaptureSlot& slot) {
        D3D11_TEXTURE2D_DESC share=desc;
        share.BindFlags=D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        share.Usage=D3D11_USAGE_DEFAULT; share.CPUAccessFlags=0;
        share.MiscFlags=D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        Require(SUCCEEDED(producer->CreateTexture2D(&share,nullptr,&slot.shared)) && SUCCEEDED(slot.shared.As(&slot.mutex)),
            "Windows could not create a shareable capture texture.");
        ComPtr<IDXGIResource1> resource;
        SharedHandle shareHandle;
        Require(SUCCEEDED(slot.shared.As(&resource)) && SUCCEEDED(resource->CreateSharedHandle(nullptr,
            DXGI_SHARED_RESOURCE_READ|DXGI_SHARED_RESOURCE_WRITE,nullptr,&shareHandle.handle)), "Could not export the Windows capture texture.");
        VkExternalMemoryImageCreateInfo externalImage{VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO};
        externalImage.handleTypes=VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
        VkImageCreateInfo imported{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,&externalImage};
        imported.imageType=VK_IMAGE_TYPE_2D; imported.format=info.format;
        imported.extent=info.extent; imported.mipLevels=1; imported.arrayLayers=1; imported.samples=VK_SAMPLE_COUNT_1_BIT;
        imported.tiling=VK_IMAGE_TILING_OPTIMAL; imported.usage=VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
        Check(vkCreateImage(device,&imported,nullptr,&slot.image), "Creating the Vulkan capture import failed");
        VkMemoryRequirements req{};
        vkGetImageMemoryRequirements(device,slot.image,&req);
        VkMemoryWin32HandlePropertiesKHR properties{VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR};
        Check(handleProperties(device,VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT,shareHandle.handle,&properties),
            "Reading the Windows capture memory handle failed");
        const uint32_t types=req.memoryTypeBits & properties.memoryTypeBits;
        Require(types!=0, "Vulkan and Windows do not share a memory type for this capture texture.");
        VkMemoryDedicatedAllocateInfo dedicated{VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO};
        dedicated.image=slot.image;
        VkImportMemoryWin32HandleInfoKHR import{VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR,&dedicated};
        import.handleType=VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT; import.handle=shareHandle.handle;
        VkMemoryAllocateInfo memory{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,&import};
        memory.allocationSize=req.size;
        while (!(types & (1u<<memory.memoryTypeIndex))) ++memory.memoryTypeIndex;
        Check(vkAllocateMemory(device,&memory,nullptr,&slot.memory),"Importing Windows capture memory failed");
        Check(vkBindImageMemory(device,slot.image,slot.memory,0),"Binding the Windows capture import failed");
        VkFenceCreateInfo fence{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        Check(vkCreateFence(device,&fence,nullptr,&slot.fence),"Creating the capture fence failed");
        VkCommandBufferAllocateInfo cmd{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
        cmd.commandPool=pool; cmd.commandBufferCount=1; cmd.level=VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        Check(vkAllocateCommandBuffers(device,&cmd,&slot.cmd),"Allocating the capture command buffer failed");
        Record(slot);
    }
    void Record(CaptureSlot& slot) {
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        Check(vkBeginCommandBuffer(slot.cmd,&begin),"Recording the capture command buffer failed");
        VkImageMemoryBarrier barriers[2]{};
        for(auto& barrier : barriers) {
            barrier.sType=VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
            barrier.subresourceRange={VK_IMAGE_ASPECT_COLOR_BIT,0,1,0,1};
        }
        auto& input=barriers[0]; auto& output=barriers[1];
        input.image=slot.image; input.srcQueueFamilyIndex=VK_QUEUE_FAMILY_EXTERNAL; input.dstQueueFamilyIndex=family;
        input.oldLayout=VK_IMAGE_LAYOUT_GENERAL; input.newLayout=VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        input.dstAccessMask=VK_ACCESS_TRANSFER_READ_BIT;
        output.image=targetImage; output.srcQueueFamilyIndex=output.dstQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED;
        output.oldLayout=targetLayout; output.newLayout=VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        output.srcAccessMask=VK_ACCESS_MEMORY_READ_BIT|VK_ACCESS_MEMORY_WRITE_BIT; output.dstAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;
        vkCmdPipelineBarrier(slot.cmd,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,0,nullptr,0,nullptr,2,barriers);
        VkImageCopy region{}; region.srcSubresource=region.dstSubresource={VK_IMAGE_ASPECT_COLOR_BIT,0,0,1}; region.extent=info.extent;
        vkCmdCopyImage(slot.cmd,slot.image,input.newLayout,targetImage,output.newLayout,1,&region);
        input.oldLayout=VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL; input.newLayout=VK_IMAGE_LAYOUT_GENERAL;
        input.srcQueueFamilyIndex=family;input.dstQueueFamilyIndex=VK_QUEUE_FAMILY_EXTERNAL;
        input.srcAccessMask=VK_ACCESS_TRANSFER_READ_BIT;input.dstAccessMask=0;
        output.oldLayout=VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL; output.newLayout=targetLayout;
        output.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;output.dstAccessMask=VK_ACCESS_MEMORY_READ_BIT|VK_ACCESS_MEMORY_WRITE_BIT;
        vkCmdPipelineBarrier(slot.cmd,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,0,0,nullptr,0,nullptr,2,barriers);
        Check(vkEndCommandBuffer(slot.cmd),"Finishing the capture command buffer failed");
    }

    void Transfer(ID3D11Texture2D* image) {
        Require(image, "Missing Windows capture frame.");
        D3D11_TEXTURE2D_DESC frame{};
        image->GetDesc(&frame);
        ComPtr<ID3D11Device> owner; image->GetDevice(&owner);
        Require(owner.Get()==producer.Get() && frame.Width==desc.Width && frame.Height==desc.Height &&
            frame.MipLevels==1 && frame.ArraySize==1 && frame.SampleDesc.Count==1 && frame.Format==desc.Format,
            "Windows capture format or device changed.");
        auto& slot=slots[next++%2];
        if (slot.pending) {
            Check(vkWaitForFences(device,1,&slot.fence,VK_TRUE,2000000000ull),"Waiting for a capture transfer failed");
            slot.pending=false;
            Check(vkResetFences(device,1,&slot.fence),"Resetting the capture fence failed");
        }
        Require(slot.mutex->AcquireSync(0,1000)==S_OK, "Timed out acquiring the Windows capture texture.");
        context->CopyResource(slot.shared.Get(),image);
        Require(SUCCEEDED(slot.mutex->ReleaseSync(1)), "Windows could not release a capture texture to Vulkan.");
        context->Flush();
        dxvk->FlushRenderingCommands();
        const uint64_t readKey=1,writeKey=0;
        const uint32_t timeout=2000;
        VkWin32KeyedMutexAcquireReleaseInfoKHR keys{VK_STRUCTURE_TYPE_WIN32_KEYED_MUTEX_ACQUIRE_RELEASE_INFO_KHR};
        keys.acquireCount=keys.releaseCount=1; keys.pAcquireSyncs=keys.pReleaseSyncs=&slot.memory;
        keys.pAcquireKeys=&readKey;keys.pReleaseKeys=&writeKey;keys.pAcquireTimeouts=&timeout;
        VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO,&keys};
        submit.commandBufferCount=1;submit.pCommandBuffers=&slot.cmd;
        dxvk->LockSubmissionQueue();
        VkResult result=vkQueueSubmit(queue,1,&submit,slot.fence);
        dxvk->ReleaseSubmissionQueue();
        Check(result,"Submitting the Vulkan capture transfer failed");
        slot.pending=true;
    }
    ~Capture() {
        for (auto& slot : slots) if(slot.pending) vkWaitForFences(device,1,&slot.fence,VK_TRUE,UINT64_MAX);
        if(pool) vkDestroyCommandPool(device,pool,nullptr);
        for (auto& slot : slots) {
            if(slot.fence) vkDestroyFence(device,slot.fence,nullptr);
            if(slot.image) vkDestroyImage(device,slot.image,nullptr);
            if(slot.memory) vkFreeMemory(device,slot.memory,nullptr);
        }
    }
    bool HasQueuedTransfer() const {
        for(const auto& slot : slots)
            if(slot.pending && vkGetFenceStatus(device,slot.fence)==VK_NOT_READY) return true;
        return false;
    }
};
int moduleAnchor;
void CALLBACK ReleaseAfterGpu(PTP_CALLBACK_INSTANCE callback, void* parameter) {
    auto* bridge=static_cast<Capture*>(parameter);
    FreeLibraryWhenCallbackReturns(callback,bridge->deferredModule);
    delete bridge;
}
}
extern "C" __declspec(dllexport) int P5VkCaptureGetAdapterLuid(ID3D11Device* destination,LUID* luid) {
    ComPtr<DxvkDevice> interop;
    if (!destination || !luid || FAILED(destination->QueryInterface(IID_PPV_ARGS(&interop)))) return 0;
    VkInstance instance{};
    VkDevice device{};
    VkPhysicalDevice physical{};
    interop->GetVulkanHandles(&instance,&physical,&device);
    return AdapterLuid(physical,*luid);
}
extern "C" __declspec(dllexport) Capture* P5VkCaptureOpen(ID3D11Device* capture,ID3D11Texture2D* output,char* error,int length) {
    try {
        auto bridge=std::make_unique<Capture>();
        bridge->Open(capture,output);
        return bridge.release();
    } catch(const std::exception& failure) {CopyReason(error,length,failure.what());return nullptr;}
}
extern "C" __declspec(dllexport) int P5VkCaptureFrame(Capture* bridge, ID3D11Texture2D* input,char* error,int length) {
    try {
        Require(bridge,"Missing Vulkan capture bridge.");
        bridge->Transfer(input);
        return 1;
    } catch(const std::exception& failure) {CopyReason(error,length,failure.what());return 0;}
}
extern "C" __declspec(dllexport) void P5VkCaptureClose(Capture* bridge) {
    if (!bridge) return;
    // Stop/resize and a previously timed-out transfer must not wait for a blocked GPU
    // on the playback thread. Retain the device, textures, and this DLL until Vulkan
    // signals the submissions or reports device loss.
    if (bridge->HasQueuedTransfer() && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            reinterpret_cast<LPCWSTR>(&moduleAnchor),&bridge->deferredModule)) {
        if (TrySubmitThreadpoolCallback(ReleaseAfterGpu,bridge,nullptr)) return;
        FreeLibrary(bridge->deferredModule);
    }
    delete bridge;
}
