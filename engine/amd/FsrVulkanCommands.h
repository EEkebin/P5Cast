#pragma once
#include "../vulkan/DxvkInterop.h"
#include "api/include/vk/ffx_api_vk.h"

// DXVK owns the graphics queue and textures. Keep input texture references until
// the frame fence completes, and return each image to its normal DXVK layout.
struct FsrVulkanCommands {
    ComPtr<DxvkDevice> interop;
    VkInstance instance{};
    VkPhysicalDevice physical{};
    VkDevice device{};
    VkQueue queue{};
    VkCommandPool pool{};
    struct Slot {
        VkCommandBuffer cmd{};
        VkFence fence{};
        bool submitted{};
        ComPtr<DxvkSurface> images[4];
    } slots[3];
    unsigned next{};

    bool Open(ID3D11Device* owner) {
        if (FAILED(owner->QueryInterface(IID_PPV_ARGS(&interop)))) return false;
        interop->GetVulkanHandles(&instance,&physical,&device);
        uint32_t family;
        interop->GetSubmissionQueue(&queue,&family);
        if (!device || !queue) return false;
        VkCommandPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
        poolInfo.flags=VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT; poolInfo.queueFamilyIndex=family;
        if (vkCreateCommandPool(device,&poolInfo,nullptr,&pool)!=VK_SUCCESS) return false;
        VkCommandBufferAllocateInfo allocate{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
        allocate.commandPool=pool; allocate.level=VK_COMMAND_BUFFER_LEVEL_PRIMARY; allocate.commandBufferCount=1;
        VkFenceCreateInfo fenceInfo{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        for (auto& slot : slots) {
            if (vkAllocateCommandBuffers(device,&allocate,&slot.cmd)!=VK_SUCCESS ||
                vkCreateFence(device,&fenceInfo,nullptr,&slot.fence)!=VK_SUCCESS) return false;
        }
        return true;
    }
    bool Wait(Slot& slot) {
        if (!slot.submitted) return true;
        if (vkWaitForFences(device,1,&slot.fence,VK_TRUE,UINT64_MAX)!=VK_SUCCESS) return false;
        slot.submitted=false; return true;
    }
    void Drain() { for (auto& slot : slots) Wait(slot); }
    ~FsrVulkanCommands() {
        Drain();
        for (auto& slot : slots) if (slot.fence) vkDestroyFence(device,slot.fence,nullptr);
        if (pool) vkDestroyCommandPool(device,pool,nullptr);
    }

    template<class Record> bool WithResources(ID3D11Resource* (&textures)[4],Record record) {
        auto& slot=slots[next++%3];
        if (!Wait(slot) || vkResetCommandBuffer(slot.cmd,0)!=VK_SUCCESS) return false;
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        begin.flags=VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        if (vkBeginCommandBuffer(slot.cmd,&begin)!=VK_SUCCESS) return false;
        FfxApiResource resources[4]{};
        VkImageLayout normal[4]{},compute[4]{};
        for(unsigned i=0;i<4;i++){
            ComPtr<DxvkSurface> surface;
            if (FAILED(textures[i]->QueryInterface(IID_PPV_ARGS(&surface)))) return false;
            slot.images[i]=surface;
            VkImage image{};
            VkImageCreateInfo info{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
            if (FAILED(surface->GetVulkanImageInfo(&image,&normal[i],&info)) || !image) return false;
            auto description=ffxApiGetImageResourceDescriptionVK(image,info,0);
            // DXVK permits additional compatible views; honor the actual mapped format.
            description.format=ffxApiGetSurfaceFormatVK(info.format);
            resources[i]=ffxApiGetResourceVK(image,description,i==3?FFX_API_RESOURCE_STATE_UNORDERED_ACCESS:FFX_API_RESOURCE_STATE_COMPUTE_READ);
            compute[i]=i==3 ? VK_IMAGE_LAYOUT_GENERAL : VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
        }
        VkMemoryBarrier memory{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
        memory.srcAccessMask=VK_ACCESS_MEMORY_WRITE_BIT;
        memory.dstAccessMask=VK_ACCESS_MEMORY_READ_BIT|VK_ACCESS_MEMORY_WRITE_BIT;
        vkCmdPipelineBarrier(slot.cmd,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,0,1,&memory,0,nullptr,0,nullptr);
        record(slot.cmd,resources);
        vkCmdPipelineBarrier(slot.cmd,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,0,1,&memory,0,nullptr,0,nullptr);
        if(vkEndCommandBuffer(slot.cmd)!=VK_SUCCESS || vkResetFences(device,1,&slot.fence)!=VK_SUCCESS) return false;
        const VkImageSubresourceRange range={VK_IMAGE_ASPECT_COLOR_BIT,0,1,0,1};
        for(unsigned i=0;i<4;i++) if(normal[i]!=compute[i]) interop->TransitionSurfaceLayout(slot.images[i].Get(),&range,normal[i],compute[i]);
        interop->FlushRenderingCommands();
        VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
        submit.commandBufferCount=1; submit.pCommandBuffers=&slot.cmd;
        interop->LockSubmissionQueue();
        slot.submitted=vkQueueSubmit(queue,1,&submit,slot.fence)==VK_SUCCESS;
        interop->ReleaseSubmissionQueue();
        for(unsigned i=0;i<4;i++) if(normal[i]!=compute[i]) interop->TransitionSurfaceLayout(slot.images[i].Get(),&range,compute[i],normal[i]);
        return slot.submitted;
    }
};
