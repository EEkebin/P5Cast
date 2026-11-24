using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device=SharpDX.Direct3D11.Device;

// Native frame-generation presenters share the video renderer's D3D12 queue.
internal sealed class SwapChainFrameGeneration : IDisposable
{
    const string Nvidia="P5Cast.DlssFg.dll", Intel="P5Cast.XessFg.dll";
    const CallingConvention C=CallingConvention.Cdecl;
    const CharSet Wide=CharSet.Unicode;
    const DllImportSearchPath Paths=DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32;
    [DefaultDllImportSearchPaths(Paths),DllImport(Nvidia,EntryPoint="P5FgSupport",ExactSpelling=true,CallingConvention=C,CharSet=Wide)]
    static extern int DlssSupport(IntPtr device,StringBuilder reason,int chars);
    [DefaultDllImportSearchPaths(Paths),DllImport(Intel,EntryPoint="P5FgSupport",ExactSpelling=true,CallingConvention=C,CharSet=Wide)]
    static extern int XessSupport(IntPtr device,StringBuilder reason,int chars);
    [DefaultDllImportSearchPaths(Paths),DllImport(Nvidia,EntryPoint="P5FgCreate",ExactSpelling=true,CallingConvention=C,CharSet=Wide)]
    static extern int DlssCreate(IntPtr parent,IntPtr device,int w,int h,int hdr,out IntPtr state,StringBuilder reason,int chars);
    [DefaultDllImportSearchPaths(Paths),DllImport(Intel,EntryPoint="P5FgCreate",ExactSpelling=true,CallingConvention=C,CharSet=Wide)]
    static extern int XessCreate(IntPtr parent,IntPtr device,int w,int h,int hdr,out IntPtr state,StringBuilder reason,int chars);
    [DefaultDllImportSearchPaths(Paths),DllImport(Nvidia,EntryPoint="P5FgPresent",ExactSpelling=true,CallingConvention=C,CharSet=Wide)]
    static extern int DlssPresent(IntPtr state,IntPtr image,IntPtr motion,IntPtr depth,int reset,float ms,out int extra,StringBuilder reason,int chars);
    [DefaultDllImportSearchPaths(Paths),DllImport(Intel,EntryPoint="P5FgPresent",ExactSpelling=true,CallingConvention=C,CharSet=Wide)]
    static extern int XessPresent(IntPtr state,IntPtr image,IntPtr motion,IntPtr depth,int reset,float ms,out int extra,StringBuilder reason,int chars);
    [DefaultDllImportSearchPaths(Paths),DllImport(Nvidia,EntryPoint="P5FgClose",ExactSpelling=true,CallingConvention=C)]
    static extern void DlssClose(IntPtr state);
    [DefaultDllImportSearchPaths(Paths),DllImport(Intel,EntryPoint="P5FgClose",ExactSpelling=true,CallingConvention=C)]
    static extern void XessClose(IntPtr state);

    readonly bool nvidia;
    readonly StringBuilder reason=new StringBuilder(512);
    IntPtr state;
    internal static bool Uses(string provider) {return provider=="dlss-fg" || provider=="xess-fg";}
    internal static string Label(string provider) {return provider=="dlss-fg" ? "NVIDIA DLSS Frame Generation" : "Intel XeSS Frame Generation";}
    internal static string Probe(Device device,string provider)
    {
        var reason=new StringBuilder(512);
        try {
            int result=provider=="dlss-fg" ? DlssSupport(device.NativePointer,reason,reason.Capacity) : XessSupport(device.NativePointer,reason,reason.Capacity);
            return result==0 ? null : Diagnostic(result,reason);
        } catch(DllNotFoundException) {return Label(provider)+" runtime is unavailable.";}
        catch(EntryPointNotFoundException) {return Label(provider)+" bridge needs updating.";}
        catch(BadImageFormatException error) {return error.Message;}
    }
    static string Diagnostic(int result,StringBuilder reason)
    {
        return reason.Length>0 ? reason.ToString() : "Frame generation provider returned 0x"+result.ToString("X8")+".";
    }
    internal SwapChainFrameGeneration(string provider,IntPtr parent,Device device,Size size,bool hdr)
    {
        nvidia=provider=="dlss-fg";
        int result=nvidia ? DlssCreate(parent,device.NativePointer,size.Width,size.Height,hdr?1:0,out state,reason,reason.Capacity)
            : XessCreate(parent,device.NativePointer,size.Width,size.Height,hdr?1:0,out state,reason,reason.Capacity);
        if(result!=0 || state==IntPtr.Zero) {Dispose();throw new InvalidOperationException(Diagnostic(result,reason));}
    }
    internal int Present(Texture2D image,Texture2D motion,Texture2D depth,bool reset,float ms)
    {
        reason.Clear();
        int extra;
        int result=nvidia ? DlssPresent(state,image.NativePointer,motion.NativePointer,depth.NativePointer,reset?1:0,ms,out extra,reason,reason.Capacity)
            : XessPresent(state,image.NativePointer,motion.NativePointer,depth.NativePointer,reset?1:0,ms,out extra,reason,reason.Capacity);
        if(result!=0) throw new InvalidOperationException(Diagnostic(result,reason));
        if(extra<0) throw new InvalidOperationException("The provider returned an invalid presentation count.");
        return extra;
    }
    public void Dispose()
    {
        if(state==IntPtr.Zero) return;
        if(nvidia) DlssClose(state); else XessClose(state);
        state=IntPtr.Zero;
    }
}

internal sealed partial class Renderer
{
    SwapChainFrameGeneration proxyFrames;
    GpuImage proxyMotion,proxyDepth;
    Size proxySize,failedProxySize;
    int lastProxyFrame=-1;
    long lastProxyExtra;
    public int DlssFgPresented {get;private set;}
    public int XessFgPresented {get;private set;}
    public int ProxyGenerated {get {return DlssFgPresented+XessFgPresented;}}
    void ReleaseProxyFrames()
    {
        if(proxyFrames!=null) {proxyFrames.Dispose();proxyFrames=null;}
        if(proxyMotion!=null) {proxyMotion.Dispose();proxyMotion=null;}
        if(proxyDepth!=null) {proxyDepth.Dispose();proxyDepth=null;}
        proxySize=failedProxySize=Size.Empty;lastProxyFrame=-1;lastProxyExtra=0;
    }
    bool PresentProxyFrame()
    {
        if(!SwapChainFrameGeneration.Uses(FrameGenerationProvider)) return false;
        if(!string.IsNullOrEmpty(FrameGenerationNotice)) {
            if(screenSize==failedProxySize) return false;
            FrameGenerationNotice="";
        }
        try {
            if(Backend!="d3d12") throw new InvalidOperationException("Select Direct3D 12 for this frame generator.");
            if(proxySize!=screenSize) {
                ReleaseProxyFrames();
                proxyMotion=new GpuImage(Device,screenSize.Width,screenSize.Height,Format.R16G16_Float,true);
                proxyDepth=new GpuImage(Device,screenSize.Width,screenSize.Height,Format.R32_Float,true);
                context.ClearRenderTargetView(proxyDepth.Write,new RawColor4(.5f,0,0,0));
                proxyFrames=new SwapChainFrameGeneration(FrameGenerationProvider,hwnd,Device,screenSize,HdrEnabled);
                proxySize=screenSize;
            }
            bool reset=sdkNeedsReset || !HasPrevious || Captured!=lastProxyFrame+1;
            context.ClearRenderTargetView(proxyMotion.Write,new RawColor4(0,0,0,0));
            if(!reset) {
                if(HdrEnabled) PrepareHdrMotion(); else PrepareMotion();
                Draw("PresentationMotion",proxyMotion.Write,viewport,curr.Read,1,prev.Read,motion.Read);
                if(!originalViewport.IsEmpty) Draw("PresentationMotion",proxyMotion.Write,originalViewport,
                    (originalCurrent ?? curr).Read,1,(originalPrevious ?? prev).Read,motion.Read);
            }
            int extra=proxyFrames.Present(backBuffer,proxyMotion.Texture,proxyDepth.Texture,reset,frameMilliseconds);
            lastProxyFrame=Captured; sdkNeedsReset=false;
            if(extra>0) {
                Generated+=extra; Presented+=extra;
                if(FrameGenerationProvider=="dlss-fg") DlssFgPresented+=extra; else XessFgPresented+=extra;
                lastProxyExtra=System.Diagnostics.Stopwatch.GetTimestamp();
            }
            return true;
        } catch(Exception e) {
            if(!IsEffectFailure(e)) throw;
            ReleaseProxyFrames(); RestoreDrawState();
            failedProxySize=screenSize;
            FrameGenerationNotice=SwapChainFrameGeneration.Label(FrameGenerationProvider)+" paused. "+e.Message;
            return false;
        }
    }
}
