using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using SharpDX.Direct3D11;

internal sealed class VulkanCapture : ICaptureBridge
{
    const string Library="P5Cast.VulkanCapture.dll";
    const DllImportSearchPath Search=DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32;
    [DefaultDllImportSearchPaths(Search),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern IntPtr P5VkCaptureOpen(IntPtr capture,IntPtr output,[Out] byte[] error,int size);
    [DefaultDllImportSearchPaths(Search),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern int P5VkCaptureFrame(IntPtr bridge,IntPtr input,[Out] byte[] error,int size);
    [DefaultDllImportSearchPaths(Search),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern void P5VkCaptureClose(IntPtr bridge);
    readonly byte[] error=new byte[256];
    readonly Device destination;
    Texture2D uploaded;
    IntPtr bridge;
    public Device CaptureDevice {get;private set;}

    public VulkanCapture(Device destination)
    {
        this.destination=destination;
        // VulkanEffects obtained this adapter from system DXGI when creating
        // the DXVK device. WGC creates its own system D3D11 device on that GPU.
        using(var dxgi=destination.QueryInterface<SharpDX.DXGI.Device>())
        using(var adapter=dxgi.Adapter)
            CaptureDevice=new Device(adapter,DeviceCreationFlags.BgraSupport);
    }
    void Allocate(Texture2DDescription desc)
    {
        desc.BindFlags=BindFlags.ShaderResource;
        desc.CpuAccessFlags=CpuAccessFlags.None;
        desc.OptionFlags=ResourceOptionFlags.None;
        desc.Usage=ResourceUsage.Default;
        var next=new Texture2D(destination,desc);
        try {
            var nextBridge=P5VkCaptureOpen(CaptureDevice.NativePointer,next.NativePointer,error,error.Length);
            if(nextBridge==IntPtr.Zero) throw Failure();
            ReleaseFrame();
            uploaded=next; bridge=nextBridge;
        } catch { next.Dispose(); throw; }
    }
    InvalidOperationException Failure()
    {
        int end=Array.IndexOf(error,(byte)0);
        return new InvalidOperationException(Encoding.UTF8.GetString(error,0,end<0 ? error.Length : end)+
            " Choose Direct3D 11 for window capture.");
    }
    internal Texture2D Transfer(Texture2D input)
    {
        var desc=input.Description;
        if(uploaded==null || uploaded.Description.Width!=desc.Width || uploaded.Description.Height!=desc.Height ||
            uploaded.Description.Format!=desc.Format) Allocate(desc);
        if(P5VkCaptureFrame(bridge,input.NativePointer,error,error.Length)==0) throw Failure();
        return uploaded;
    }
    public bool Receive(Renderer renderer,Texture2D input,Rectangle crop,bool deduplicate)
    {
        return renderer.Receive(Transfer(input),crop,deduplicate);
    }
    void ReleaseFrame()
    {
        if(bridge!=IntPtr.Zero) {P5VkCaptureClose(bridge);bridge=IntPtr.Zero;}
        if(uploaded!=null) {uploaded.Dispose();uploaded=null;}
    }
    public void Dispose()
    {
        ReleaseFrame();
        if(CaptureDevice!=null) {CaptureDevice.Dispose();CaptureDevice=null;}
    }
}
