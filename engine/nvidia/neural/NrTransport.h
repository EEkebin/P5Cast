#pragma once
#include "../../vulkan/DxvkInterop.h"
#include <vector>
#include <stdexcept>
#include <string>
#include <cstring>
#include <algorithm>

inline void VkCheck(VkResult result,const char* operation) {
    if(result!=VK_SUCCESS) throw std::runtime_error(std::string(operation)+" (Vulkan "+std::to_string(result)+")");
}
inline void DxCheck(HRESULT result,const char* operation) {
    if(FAILED(result)) throw std::runtime_error(std::string(operation)+" (HRESULT "+std::to_string(result)+")");
}
struct NrPlane {
    VkFormat vkFormat;
    DXGI_FORMAT dxFormat;
    UINT bytes;
    VkImage image{};
    VkImageView view{};
    VkDeviceMemory memory{};
    VkDeviceSize offset{};
    ComPtr<ID3D11Texture2D> staging;
};

// A second device lets NGX request its own Vulkan extensions, including those
// stock DXVK does not enable. Staging keeps both devices on the selected GPU
// and requires no driver-specific memory or queue-ownership assumptions.
struct NrTransport {
    ComPtr<ID3D11Device> owner;
    ComPtr<ID3D11DeviceContext> context;
    VkInstance instance{};
    VkPhysicalDevice physical{};
    VkDevice device{};
    VkQueue queue{};
    uint32_t family{},width{},height{};
    VkCommandPool pool{};
    VkCommandBuffer cmd{};
    VkFence fence{};
    VkBuffer buffer{};
    VkDeviceMemory bufferMemory{};
    unsigned char* mapped{};
    VkPhysicalDeviceMemoryProperties memoryTypes{};
    NrPlane planes[5]={{VK_FORMAT_R16G16B16A16_SFLOAT,DXGI_FORMAT_R16G16B16A16_FLOAT,8},
        {VK_FORMAT_R16G16_SFLOAT,DXGI_FORMAT_R16G16_FLOAT,4}, {VK_FORMAT_R32_SFLOAT,DXGI_FORMAT_R32_FLOAT,4},
        {VK_FORMAT_R8G8B8A8_UNORM,DXGI_FORMAT_R8G8B8A8_UNORM,4}, {VK_FORMAT_R16G16B16A16_SFLOAT,DXGI_FORMAT_R16G16B16A16_FLOAT,8}};
    bool initializedLayouts{};
    static VkPhysicalDevice Selected(ID3D11Device* owner) {
        ComPtr<DxvkDevice> interop;
        DxCheck(owner->QueryInterface(IID_PPV_ARGS(&interop)),"Select the Vulkan renderer for neural rendering");
        VkInstance instance{};VkPhysicalDevice selected{};VkDevice device{};
        interop->GetVulkanHandles(&instance,&selected,&device);
        if(!selected) throw std::runtime_error("Vulkan did not expose the selected GPU");
        return selected;
    }
    static VkPhysicalDeviceIDProperties Identity(VkPhysicalDevice gpu) {
        VkPhysicalDeviceIDProperties id{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
        VkPhysicalDeviceProperties2 properties{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2,&id};
        vkGetPhysicalDeviceProperties2(gpu,&properties);
        return id;
    }
    void OpenInstance(ID3D11Device* source,const std::vector<const char*>& extensions) {
        owner=source;
        owner->GetImmediateContext(&context);
        auto selected=Identity(Selected(source));
        VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
        app.pApplicationName="P5Cast"; app.apiVersion=VK_API_VERSION_1_2;
        VkInstanceCreateInfo info{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
        info.pApplicationInfo=&app; info.enabledExtensionCount=static_cast<uint32_t>(extensions.size());
        info.ppEnabledExtensionNames=extensions.data();
        VkCheck(vkCreateInstance(&info,nullptr,&instance),"Create neural rendering Vulkan instance");
        uint32_t count=0; VkCheck(vkEnumeratePhysicalDevices(instance,&count,nullptr),"Enumerate Vulkan GPUs");
        std::vector<VkPhysicalDevice> devices(count);
        VkCheck(vkEnumeratePhysicalDevices(instance,&count,devices.data()),"Enumerate Vulkan GPUs");
        for(auto gpu:devices) {
            auto id=Identity(gpu);
            if(!memcmp(selected.deviceUUID,id.deviceUUID,VK_UUID_SIZE)) {physical=gpu;return;}
        }
        throw std::runtime_error("Selected Vulkan GPU could not be matched by device UUID");
    }
    void OpenDevice(const std::vector<const char*>& extensions) {
        uint32_t count=0;vkGetPhysicalDeviceQueueFamilyProperties(physical,&count,nullptr);
        std::vector<VkQueueFamilyProperties> families(count);
        vkGetPhysicalDeviceQueueFamilyProperties(physical,&count,families.data());
        for(family=0;family<count;++family) if((families[family].queueFlags & (VK_QUEUE_COMPUTE_BIT|VK_QUEUE_GRAPHICS_BIT))==
            (VK_QUEUE_COMPUTE_BIT|VK_QUEUE_GRAPHICS_BIT)) break;
        if(family==count) throw std::runtime_error("No Vulkan graphics and compute queue");
        float priority=1.f;
        VkDeviceQueueCreateInfo q{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
        q.queueFamilyIndex=family;q.queueCount=1;q.pQueuePriorities=&priority;
        VkPhysicalDeviceVulkan12Features f12{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES};
        VkPhysicalDeviceVulkan11Features f11{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_1_FEATURES,&f12};
        VkPhysicalDeviceFeatures2 features{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2,&f11};
        vkGetPhysicalDeviceFeatures2(physical,&features);
        VkDeviceCreateInfo info{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,&features};
        info.queueCreateInfoCount=1; info.pQueueCreateInfos=&q;
        info.enabledExtensionCount=static_cast<uint32_t>(extensions.size());info.ppEnabledExtensionNames=extensions.data();
        VkCheck(vkCreateDevice(physical,&info,nullptr,&device),"Create Vulkan device with provider-required extensions");
        vkGetDeviceQueue(device,family,0,&queue);
        vkGetPhysicalDeviceMemoryProperties(physical,&memoryTypes);
        VkCommandPoolCreateInfo pci{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
        pci.flags=VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT; pci.queueFamilyIndex=family;
        VkCheck(vkCreateCommandPool(device,&pci,nullptr,&pool),"Create Vulkan command pool");
        VkCommandBufferAllocateInfo ai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
        ai.commandPool=pool; ai.level=VK_COMMAND_BUFFER_LEVEL_PRIMARY; ai.commandBufferCount=1;
        VkCheck(vkAllocateCommandBuffers(device,&ai,&cmd),"Allocate Vulkan command buffer");
        VkFenceCreateInfo fi{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        VkCheck(vkCreateFence(device,&fi,nullptr,&fence),"Create Vulkan completion fence");
    }
    void Allocate(const VkMemoryRequirements& req,VkMemoryPropertyFlags flags,VkDeviceMemory* result) {
        VkMemoryAllocateInfo a{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
        for(uint32_t i=0;i<memoryTypes.memoryTypeCount;++i) if((req.memoryTypeBits & (1u<<i)) &&
            (memoryTypes.memoryTypes[i].propertyFlags & flags)==flags) {
            a.allocationSize=req.size;a.memoryTypeIndex=i;
            VkCheck(vkAllocateMemory(device,&a,nullptr,result),"Allocate graphics memory");return;
        }
        throw std::runtime_error("No compatible Vulkan memory type");
    }
    static VkImageSubresourceRange Range() { return {VK_IMAGE_ASPECT_COLOR_BIT,0,1,0,1}; }
    void AllocateFrame(UINT w,UINT h) {
        width=w;height=h;VkDeviceSize total=0;
        for(unsigned i=0;i<5;++i) {
            auto& plane=planes[i];plane.offset=(total+255)&~VkDeviceSize(255);
            total=plane.offset+VkDeviceSize(w)*h*plane.bytes;
            VkImageCreateInfo ci{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
            ci.imageType=VK_IMAGE_TYPE_2D;ci.format=plane.vkFormat;ci.extent={w,h,1};
            ci.mipLevels=1;ci.arrayLayers=1;ci.samples=VK_SAMPLE_COUNT_1_BIT;ci.tiling=VK_IMAGE_TILING_OPTIMAL;
            ci.usage=VK_IMAGE_USAGE_TRANSFER_SRC_BIT|VK_IMAGE_USAGE_TRANSFER_DST_BIT|VK_IMAGE_USAGE_SAMPLED_BIT;
            if(i==4) ci.usage|=VK_IMAGE_USAGE_STORAGE_BIT;
            VkCheck(vkCreateImage(device,&ci,nullptr,&plane.image),"Create neural rendering image");
            VkMemoryRequirements req{};vkGetImageMemoryRequirements(device,plane.image,&req);
            Allocate(req,VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT,&plane.memory);
            VkCheck(vkBindImageMemory(device,plane.image,plane.memory,0),"Bind neural rendering image");
            VkImageViewCreateInfo vi{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
            vi.image=plane.image;vi.viewType=VK_IMAGE_VIEW_TYPE_2D;vi.format=plane.vkFormat;vi.subresourceRange=Range();
            VkCheck(vkCreateImageView(device,&vi,nullptr,&plane.view),"Create neural rendering image view");
            if(i<4) {
                D3D11_TEXTURE2D_DESC desc{};desc.Width=w;desc.Height=h;desc.MipLevels=1;desc.ArraySize=1;
                desc.Format=plane.dxFormat;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                DxCheck(owner->CreateTexture2D(&desc,nullptr,&plane.staging),"Create stream input staging");
            }
        }
        VkBufferCreateInfo bi{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};bi.size=total;bi.usage=VK_BUFFER_USAGE_TRANSFER_SRC_BIT|VK_BUFFER_USAGE_TRANSFER_DST_BIT;
        VkCheck(vkCreateBuffer(device,&bi,nullptr,&buffer),"Create frame transfer buffer");
        VkMemoryRequirements req{};vkGetBufferMemoryRequirements(device,buffer,&req);
        Allocate(req,VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT|VK_MEMORY_PROPERTY_HOST_COHERENT_BIT,&bufferMemory);
        VkCheck(vkBindBufferMemory(device,buffer,bufferMemory,0),"Bind frame transfer buffer");
        void* ptr=nullptr;VkCheck(vkMapMemory(device,bufferMemory,0,VK_WHOLE_SIZE,0,&ptr),"Map frame transfer buffer");
        mapped=static_cast<unsigned char*>(ptr);
    }
    void Begin() {
        VkCheck(vkResetCommandBuffer(cmd,0),"Reset Vulkan commands");
        VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        bi.flags=VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        VkCheck(vkBeginCommandBuffer(cmd,&bi),"Begin Vulkan commands");
    }
    void Finish() {
        VkCheck(vkEndCommandBuffer(cmd),"End Vulkan commands");
        VkCheck(vkResetFences(device,1,&fence),"Reset frame fence");
        VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO};si.commandBufferCount=1;si.pCommandBuffers=&cmd;
        VkCheck(vkQueueSubmit(queue,1,&si,fence),"Submit neural rendering GPU work");
        VkCheck(vkWaitForFences(device,1,&fence,VK_TRUE,UINT64_MAX),"Wait for neural rendering GPU work");
    }
    void Layout(unsigned i,VkImageLayout old,VkImageLayout next,VkAccessFlags src,VkAccessFlags dst) {
        VkImageMemoryBarrier b{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
        b.srcAccessMask=src;b.dstAccessMask=dst;b.oldLayout=old;b.newLayout=next;
        b.srcQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED;b.dstQueueFamilyIndex=VK_QUEUE_FAMILY_IGNORED;
        b.image=planes[i].image;b.subresourceRange=Range();
        vkCmdPipelineBarrier(cmd,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,0,0,nullptr,0,nullptr,1,&b);
    }
    VkBufferImageCopy Region(unsigned i) const {
        VkBufferImageCopy copy{};copy.bufferOffset=planes[i].offset;copy.imageExtent={width,height,1};
        copy.imageSubresource={VK_IMAGE_ASPECT_COLOR_BIT,0,0,1};return copy;
    }
    template<typename Evaluate> void Frame(ID3D11Texture2D* (&textures)[5],Evaluate eval) {
        for(unsigned i=0;i<5;++i) {
            if(!textures[i]) throw std::runtime_error("Missing neural rendering frame plane");
            D3D11_TEXTURE2D_DESC d{}; textures[i]->GetDesc(&d);
            if(d.Width!=width || d.Height!=height || d.Format!=planes[i].dxFormat || d.SampleDesc.Count!=1 ||
                d.MipLevels!=1 || d.ArraySize!=1) throw std::runtime_error("Neural rendering texture dimensions or format do not match");
            if(i<4) context->CopyResource(planes[i].staging.Get(),textures[i]);
        }
        for(unsigned i=0;i<4;++i){
            auto& p=planes[i];D3D11_MAPPED_SUBRESOURCE src{};
            DxCheck(context->Map(p.staging.Get(),0,D3D11_MAP_READ,0,&src),"Read neural rendering input");
            const UINT pitch=width*p.bytes;
            for(UINT y=0;y<height;++y)
                memcpy(mapped+p.offset+size_t(y)*pitch,static_cast<unsigned char*>(src.pData)+size_t(y)*src.RowPitch,pitch);
            context->Unmap(p.staging.Get(),0);
        }
        Begin();
        for(unsigned i=0;i<4;++i){
            Layout(i,initializedLayouts?VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL:VK_IMAGE_LAYOUT_UNDEFINED,
                VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,initializedLayouts?VK_ACCESS_SHADER_READ_BIT:0,VK_ACCESS_TRANSFER_WRITE_BIT);
            auto region=Region(i);
            vkCmdCopyBufferToImage(cmd,buffer,planes[i].image,VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,1,&region);
            Layout(i,VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,VK_ACCESS_TRANSFER_WRITE_BIT,VK_ACCESS_SHADER_READ_BIT);
        }
        Layout(4,initializedLayouts?VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL:VK_IMAGE_LAYOUT_UNDEFINED,VK_IMAGE_LAYOUT_GENERAL,
            initializedLayouts?VK_ACCESS_TRANSFER_READ_BIT:0,VK_ACCESS_SHADER_WRITE_BIT);
        eval();
        Layout(4,VK_IMAGE_LAYOUT_GENERAL,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,VK_ACCESS_SHADER_WRITE_BIT|VK_ACCESS_TRANSFER_WRITE_BIT,VK_ACCESS_TRANSFER_READ_BIT);
        auto output=Region(4);
        vkCmdCopyImageToBuffer(cmd,planes[4].image,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,buffer,1,&output);
        VkMemoryBarrier b{VK_STRUCTURE_TYPE_MEMORY_BARRIER};b.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;b.dstAccessMask=VK_ACCESS_HOST_READ_BIT;
        vkCmdPipelineBarrier(cmd,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_HOST_BIT,0,1,&b,0,nullptr,0,nullptr);
        Finish();initializedLayouts=true;
        context->UpdateSubresource(textures[4],0,nullptr,mapped+planes[4].offset,width*planes[4].bytes,0);
        DxCheck(owner->GetDeviceRemovedReason(),"Copy neural rendering output");
    }
    ~NrTransport() {
        if(device) vkDeviceWaitIdle(device);
        if(mapped) vkUnmapMemory(device,bufferMemory);
        if(buffer) vkDestroyBuffer(device,buffer,nullptr);
        if(bufferMemory) vkFreeMemory(device,bufferMemory,nullptr);
        for(auto& p:planes){
            if(p.view) vkDestroyImageView(device,p.view,nullptr);
            if(p.image) vkDestroyImage(device,p.image,nullptr);
            if(p.memory) vkFreeMemory(device,p.memory,nullptr);
        }
        if(fence)vkDestroyFence(device,fence,nullptr);
        if(pool)vkDestroyCommandPool(device,pool,nullptr);
        if(device)vkDestroyDevice(device,nullptr);
        if(instance)vkDestroyInstance(instance,nullptr);
    }
};
