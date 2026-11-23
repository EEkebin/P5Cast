using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

internal static class DlssModel
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("Cadrivect.Ngx.dll",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern int P5SetDlssModelHint(IntPtr device,uint preset);
    public static uint Preset(string key) {
        switch(key) {
            case "auto": return 0;
            case "k": return 11;
            case "l": return 12;
            case "m": return 13;
            default: throw new ArgumentException("Choose auto, k, l or m for the DLSS model hint.");
        }
    }
    public static void Apply(Device device,string key) {
        Marshal.ThrowExceptionForHR(P5SetDlssModelHint(device.NativePointer,Preset(key)));
    }
}
