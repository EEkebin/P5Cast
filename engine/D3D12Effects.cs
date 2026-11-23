using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

internal static class SelectedEffectDevice
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("Cadrivect.Ngx.dll",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern int P5CreateD3D12Effects(IntPtr adapter,out IntPtr device);

    public static Device CreateD3D12(IntPtr adapter=default(IntPtr))
    {
        IntPtr device;
        int result=P5CreateD3D12Effects(adapter,out device);
        Marshal.ThrowExceptionForHR(result);
        return new Device(device);
    }
}
