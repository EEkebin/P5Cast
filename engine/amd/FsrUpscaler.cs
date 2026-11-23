using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

internal sealed class FsrUpscaler : IDisposable
{
    const string Library="P5Cast.Fsr.dll";
    const DllImportSearchPath Search=DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32;
    IntPtr state;
    readonly bool vulkan;
    public string ActiveVersion {get;private set;}

    [DefaultDllImportSearchPaths(Search), DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
    static extern int P5FsrOpen(IntPtr device,string loader,int w,int h,int ow,int oh,int hdr,out IntPtr state,out IntPtr version);
    [DefaultDllImportSearchPaths(Search), DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern int P5FsrDispatchHud(IntPtr state,IntPtr context,IntPtr color,IntPtr motion,IntPtr depth,IntPtr output,float ms,int reset,IntPtr composition);
    [DefaultDllImportSearchPaths(Search), DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern void P5FsrClose(IntPtr state);

    [DefaultDllImportSearchPaths(Search), DllImport("P5Cast.FsrVk.dll",EntryPoint="P5FsrOpen",ExactSpelling=true,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
    static extern int VkOpen(IntPtr device,string loader,int w,int h,int ow,int oh,int hdr,out IntPtr state,out IntPtr version);
    [DefaultDllImportSearchPaths(Search), DllImport("P5Cast.FsrVk.dll",EntryPoint="P5FsrDispatchHud",ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern int VkDispatchHud(IntPtr state,IntPtr context,IntPtr color,IntPtr motion,IntPtr depth,IntPtr output,float ms,int reset,IntPtr composition);
    [DefaultDllImportSearchPaths(Search), DllImport("P5Cast.FsrVk.dll",EntryPoint="P5FsrClose",ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern void VkClose(IntPtr state);

    public FsrUpscaler(Device device,int inputWidth,int inputHeight,int outputWidth,int outputHeight,bool hdr,string backend="d3d12") {
        vulkan=backend=="vulkan";
        if(!vulkan && backend!="d3d12") throw new ArgumentException("AMD temporal upscaling requires D3D12 or Vulkan.","backend");
        string folder=Path.GetDirectoryName(typeof(FsrUpscaler).Assembly.Location);
        string loader=Path.Combine(folder,vulkan?"amd_fidelityfx_loader_vk.dll":"amd_fidelityfx_loader_dx12.dll");
        IntPtr version;
        Check(vulkan ? VkOpen(device.NativePointer,loader,inputWidth,inputHeight,outputWidth,outputHeight,hdr?1:0,out state,out version)
            : P5FsrOpen(device.NativePointer,loader,inputWidth,inputHeight,outputWidth,outputHeight,hdr?1:0,
            out state,out version),"creation");
        ActiveVersion=Marshal.PtrToStringAnsi(version);
    }

    public void Evaluate(DeviceContext context,Texture2D color,Texture2D motion,Texture2D depth,Texture2D output,float frameMs,bool reset,Texture2D composition=null) {
        if(state==IntPtr.Zero) throw new ObjectDisposedException("FsrUpscaler");
        IntPtr hud=composition==null?IntPtr.Zero:composition.NativePointer;
        Check(vulkan ? VkDispatchHud(state,context.NativePointer,color.NativePointer,motion.NativePointer,depth.NativePointer,output.NativePointer,frameMs,reset?1:0,hud)
            : P5FsrDispatchHud(state,context.NativePointer,color.NativePointer,motion.NativePointer,depth.NativePointer,
            output.NativePointer,frameMs,reset?1:0,hud),"dispatch");
    }

    static void Check(int result,string operation) {
        if(result!=0) throw new InvalidOperationException("AMD FSR upscaler "+operation+" failed (0x"+result.ToString("X8")+").");
    }
    public void Dispose() {
        if(state!=IntPtr.Zero) { if(vulkan) VkClose(state); else P5FsrClose(state); state=IntPtr.Zero; }
    }
}
