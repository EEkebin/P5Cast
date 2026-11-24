using System;
using System.Runtime.InteropServices;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

// The matching DXVK factory asks the effects device for its Vulkan swapchain interface.
// Its backbuffer is a GPU texture on that same device and queue.
internal static class VulkanSwapChain
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("P5Cast.DxgiVk.dll", ExactSpelling=true)]
    static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    public static SwapChain Create(Device device, SwapChainDescription description)
    {
        // Expose the GPU's HDR color spaces to DXGI. DisplayHdr chooses the actual
        // swapchain space for each display and explicit SDR/HDR setting.
        if (Environment.GetEnvironmentVariable("DXVK_HDR")==null)
            Environment.SetEnvironmentVariable("DXVK_HDR","1");
        Guid iid=typeof(Factory1).GUID;
        IntPtr pointer;
        int result=CreateDXGIFactory1(ref iid,out pointer);
        if (result<0 || pointer==IntPtr.Zero)
            throw new InvalidOperationException("Vulkan presentation factory creation failed (0x"+result.ToString("X8")+").");
        using (var factory=new Factory1(pointer))
            return new SwapChain(factory,device,description);
    }
}
