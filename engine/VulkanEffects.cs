// A private copy of DXVK translates the existing DXBC shaders to Vulkan. Its
// unique library name keeps WinRT, WPF and native D3D users on the Windows DLLs.
using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using Device=SharpDX.Direct3D11.Device;

internal static class VulkanEffects
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("P5Cast.Dxvk.dll",ExactSpelling=true)]
    static extern int D3D11CreateDevice(IntPtr adapter,int driverType,IntPtr software,DeviceCreationFlags flags,
        ref int featureLevel,int featureLevelCount,int sdkVersion,out IntPtr result,out int selected,IntPtr context);

    // DXVK's IDXGIVkInteropDevice exposes the specific Vulkan driver handles backing this device.
    [ComImport,Guid("e2ef5fa5-dc21-4af7-90c4-f67ef6a09323"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVulkanDevice
    {
        [PreserveSig] void GetVulkanHandles(out IntPtr instance,out IntPtr physical,out IntPtr device);
    }

    // Vulkan writes the full VkPhysicalDeviceProperties. The leading fields and
    // name are all we consume, with space reserved for its trailing device limits.
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Ansi,Size=4096)]
    struct PhysicalProperties
    {
        public uint ApiVersion,DriverVersion,VendorId,DeviceId,DeviceType;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string DeviceName;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("vulkan-1.dll",ExactSpelling=true)]
    static extern void vkGetPhysicalDeviceProperties(IntPtr physical,out PhysicalProperties properties);

    public static Device Create(out string adapterName)
    {
        // DXVK otherwise writes a .log in the application's working directory.
        if(Environment.GetEnvironmentVariable("DXVK_LOG_PATH")==null)
            Environment.SetEnvironmentVariable("DXVK_LOG_PATH","none");
        int requested=0xb000,selected;
        IntPtr pointer;
        int result=D3D11CreateDevice(IntPtr.Zero,1,IntPtr.Zero,DeviceCreationFlags.BgraSupport,
            ref requested,1,7,out pointer,out selected,IntPtr.Zero);
        if(result<0)
            throw new InvalidOperationException("Vulkan effects device creation failed (0x"+result.ToString("X8")+").");
        var device=new Device(pointer);
        try {
            var interop=(IVulkanDevice)Marshal.GetObjectForIUnknown(pointer);
            try {
                IntPtr instance,physical,logical;
                interop.GetVulkanHandles(out instance,out physical,out logical);
                if(instance==IntPtr.Zero || physical==IntPtr.Zero || logical==IntPtr.Zero)
                    throw new InvalidOperationException("The effects device did not expose its Vulkan handles.");
                PhysicalProperties properties;
                vkGetPhysicalDeviceProperties(physical,out properties);
                if(properties.DeviceType==4) throw new InvalidOperationException("Vulkan effects require a hardware GPU.");
                adapterName=properties.DeviceName;
            } finally { Marshal.ReleaseComObject(interop); }
            return device;
        } catch { device.Dispose(); throw; }
    }
}
