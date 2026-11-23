#pragma once
#include <d3d11.h>
#include <wrl/client.h>
#include <vulkan/vulkan.h>

using Microsoft::WRL::ComPtr;
struct DxvkDevice;
// DXVK's published IDXGIVkInteropSurface and IDXGIVkInteropDevice COM ABI.
struct __declspec(uuid("5546cf8c-77e7-4341-b05d-8d4d5000e77d")) DxvkSurface : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetDevice(DxvkDevice**) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetVulkanImageInfo(VkImage*,VkImageLayout*,VkImageCreateInfo*) = 0;
};
struct __declspec(uuid("e2ef5fa5-dc21-4af7-90c4-f67ef6a09323")) DxvkDevice : IUnknown {
    virtual void STDMETHODCALLTYPE GetVulkanHandles(VkInstance*,VkPhysicalDevice*,VkDevice*) = 0;
    virtual void STDMETHODCALLTYPE GetSubmissionQueue(VkQueue*,uint32_t*) = 0;
    virtual void STDMETHODCALLTYPE TransitionSurfaceLayout(DxvkSurface*,const VkImageSubresourceRange*,VkImageLayout,VkImageLayout) = 0;
    virtual void STDMETHODCALLTYPE FlushRenderingCommands() = 0;
    virtual void STDMETHODCALLTYPE LockSubmissionQueue() = 0;
    virtual void STDMETHODCALLTYPE ReleaseSubmissionQueue() = 0;
};
