using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device=SharpDX.Direct3D11.Device;

internal sealed class FsrFrameGeneration : IDisposable
{
    const string Library="P5Cast.Fsr.dll";
    const DllImportSearchPath Search=DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32;
    IntPtr state;
    readonly bool vulkan;
    public string ActiveVersion {get;private set;}

    [DefaultDllImportSearchPaths(Search), DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
    static extern int P5FsrFgOpen(IntPtr device,string loader,int w,int h,int ow,int oh,int hdr,out IntPtr state,out IntPtr version);
    [DefaultDllImportSearchPaths(Search), DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern int P5FsrFgDispatch(IntPtr state,IntPtr context,IntPtr color,IntPtr motion,IntPtr depth,IntPtr output,float ms,int reset);
    [DefaultDllImportSearchPaths(Search), DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern void P5FsrFgClose(IntPtr state);
    [DefaultDllImportSearchPaths(Search), DllImport("P5Cast.FsrVk.dll",EntryPoint="P5FsrFgOpen",ExactSpelling=true,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
    static extern int VkOpen(IntPtr device,string loader,int w,int h,int ow,int oh,int hdr,out IntPtr state,out IntPtr version);
    [DefaultDllImportSearchPaths(Search), DllImport("P5Cast.FsrVk.dll",EntryPoint="P5FsrFgDispatch",ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern int VkDispatch(IntPtr state,IntPtr context,IntPtr color,IntPtr motion,IntPtr depth,IntPtr output,float ms,int reset);
    [DefaultDllImportSearchPaths(Search), DllImport("P5Cast.FsrVk.dll",EntryPoint="P5FsrFgClose",ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern void VkClose(IntPtr state);

    public FsrFrameGeneration(Device device,int renderWidth,int renderHeight,int outputWidth,int outputHeight,bool hdr,string backend) {
        vulkan=backend=="vulkan";
        if(!vulkan && backend!="d3d12") throw new ArgumentException("AMD frame generation requires Direct3D 12 or Vulkan.","backend");
        string directory=Path.GetDirectoryName(typeof(FsrFrameGeneration).Assembly.Location);
        string loader=Path.Combine(directory,vulkan?"amd_fidelityfx_loader_vk.dll":"amd_fidelityfx_loader_dx12.dll");
        IntPtr version;
        Check(vulkan?VkOpen(device.NativePointer,loader,renderWidth,renderHeight,outputWidth,outputHeight,hdr?1:0,out state,out version)
            : P5FsrFgOpen(device.NativePointer,loader,renderWidth,renderHeight,outputWidth,outputHeight,hdr?1:0,out state,out version),"creation");
        ActiveVersion=Marshal.PtrToStringAnsi(version);
    }
    public void Evaluate(DeviceContext context, Texture2D color, Texture2D motion, Texture2D depth, Texture2D output,float frameMs,bool reset) {
        if(state==IntPtr.Zero) throw new ObjectDisposedException("FsrFrameGeneration");
        Check(vulkan?VkDispatch(state,context.NativePointer,color.NativePointer,motion.NativePointer,depth.NativePointer,output.NativePointer,frameMs,reset?1:0)
            : P5FsrFgDispatch(state,context.NativePointer,color.NativePointer,motion.NativePointer,depth.NativePointer,output.NativePointer,frameMs,reset?1:0),"dispatch");
    }
    public static string Probe(Device device,string backend,out string version) {
        const int width=384,height=224;
        version="";
        var ctx=device.ImmediateContext;
        try {
            using(var fg=new FsrFrameGeneration(device,width,height,width,height,false,backend))
            using(var color=ProbeTexture(device,width,height,Format.R8G8B8A8_UNorm,false))
            using(var motion=ProbeTexture(device,width,height,Format.R16G16_Float,false))
            using(var depth=ProbeTexture(device,width,height,Format.R32_Float,false))
            using(var output=ProbeTexture(device,width,height,Format.R8G8B8A8_UNorm,true))
            using(var inputView=new RenderTargetView(device,color))
            using(var motionView=new RenderTargetView(device,motion))
            using(var depthView=new RenderTargetView(device,depth)) {
                ctx.ClearRenderTargetView(inputView,new RawColor4(.2f,.5f,.3f,1));
                ctx.ClearRenderTargetView(motionView,new RawColor4(0,0,0,0));
                ctx.ClearRenderTargetView(depthView,new RawColor4(.5f,0,0,0));
                fg.Evaluate(ctx,color,motion,depth,output,16.67f,true);
                fg.Evaluate(ctx,color,motion,depth,output,16.67f,false);
                var desc=output.Description;
                desc.Width=desc.Height=1;desc.Usage=ResourceUsage.Staging;desc.BindFlags=BindFlags.None;desc.CpuAccessFlags=CpuAccessFlags.Read;
                using(var sample=new Texture2D(device,desc)) {
                    ctx.CopySubresourceRegion(output,0,new ResourceRegion(80,80,0,81,81,1),sample,0);
                    var map=ctx.MapSubresource(sample,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                    try {
                        if(Math.Abs(Marshal.ReadByte(map.DataPointer,1)-128)>5)
                            return "The AMD frame-generation provider produced an unexpected test pixel.";
                    } finally {ctx.UnmapSubresource(sample,0);}
                }
                version=fg.ActiveVersion;
                return null;
            }
        } catch(Exception ex) {return ex.Message;}
        finally {ctx.ClearState();}
    }
    static Texture2D ProbeTexture(Device device,int w,int h,Format format,bool uav) {
        return new Texture2D(device,new Texture2DDescription {Width=w,Height=h,ArraySize=1,MipLevels=1,
            Format=format,SampleDescription=new SampleDescription(1,0),
            BindFlags=BindFlags.ShaderResource|BindFlags.RenderTarget|(uav?BindFlags.UnorderedAccess:BindFlags.None)});
    }
    static void Check(int result,string operation) {
        if(result!=0) throw new InvalidOperationException("AMD frame generation "+operation+" failed (0x"+result.ToString("X8")+").");
    }
    public void Dispose() {
        if(state!=IntPtr.Zero) {if(vulkan) VkClose(state);else P5FsrFgClose(state);state=IntPtr.Zero;}
    }
}
