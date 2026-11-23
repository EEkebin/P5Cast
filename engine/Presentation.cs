using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

// Windows processes frames on D3D11. The portable presenter owns the selected
// API's graphics context and copies the final SDR or packed PQ image before this mapping ends.
internal sealed class Presentation : IDisposable
{
    const string Library="P5Cast.Presentation.dll";
    const DllImportSearchPath Search=DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32 |
        DllImportSearchPath.UseDllDirectoryForDependencies;
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern int p5_present_available(string driver);
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern IntPtr p5_present_create_color(IntPtr parent,string driver,int width,int height,int hdr10,[Out] byte[] error,int errorLength);
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern int p5_present_output_hdr10(IntPtr instance);
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern int p5_present_resize(IntPtr instance,int width,int height,[Out] byte[] error,int errorLength);
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern int p5_present_frame(IntPtr instance,IntPtr bgra,int pitch,int width,int height,
        int outputWidth,int outputHeight,[Out] byte[] error,int errorLength);
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern void p5_present_destroy(IntPtr instance);

    readonly byte[] error=new byte[1024];
    readonly string backend;
    IntPtr instance;
    Texture2D readback;
    Size readbackSize,windowSize;
    public bool Hdr10 { get; private set; }

    public static string Resolve(string requested)
    {
        string key=requested.ToLowerInvariant();
        switch(key) {
            case "auto": case "d3d11": return "d3d11";
            case "d3d12": case "vulkan": case "opengl": case "metal": return key;
            default: throw new ArgumentException("Choose auto, d3d11, d3d12, vulkan, opengl or metal for --renderer.");
        }
    }

    static string Driver(string key) { return key=="d3d12" ? "direct3d12" : key; }

    public static IEnumerable<string> BuiltDrivers()
    {
        var drivers=new List<string>{"d3d11"};
        try {
            foreach(string key in new[]{"d3d12","vulkan","opengl","metal"})
                if(p5_present_available(Driver(key))!=0) drivers.Add(key);
        } catch(DllNotFoundException) { } catch(EntryPointNotFoundException) { } catch(BadImageFormatException) { }
        return drivers;
    }

    public Presentation(IntPtr hwnd,string key,Size size,bool hdr10)
    {
        backend=key;
        windowSize=size;
        string driver=Driver(key);
        if(p5_present_available(driver)==0) throw new InvalidOperationException(key+" presentation is unavailable on this platform.");
        instance=p5_present_create_color(hwnd,driver,size.Width,size.Height,hdr10 ? 1:0,error,error.Length);
        if(instance==IntPtr.Zero) throw Failure("initialization");
        try {
            Hdr10=p5_present_output_hdr10(instance)==1;
            if(Hdr10!=hdr10) throw new InvalidOperationException(key+" did not create the requested presentation color space.");
        } catch { Dispose(); throw; }
    }

    InvalidOperationException Failure(string action)
    {
        int length=Array.IndexOf(error,(byte)0);
        string details=Encoding.UTF8.GetString(error,0,length<0 ? error.Length : length);
        return new InvalidOperationException(backend+" presentation "+action+" failed. "+details);
    }

    public void Present(Device device,DeviceContext context,Texture2D target,Size size)
    {
        if(readback==null || readbackSize!=size) {
            var desc=target.Description;
            desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read;
            var next=new Texture2D(device,desc);
            if(readback!=null) readback.Dispose();
            readback=next; readbackSize=size;
        }
        context.CopyResource(target,readback);
        var data=context.MapSubresource(readback,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
        try {
            if(p5_present_frame(instance,data.DataPointer,data.RowPitch,size.Width,size.Height,
                windowSize.Width,windowSize.Height,error,error.Length)==0) throw Failure("frame delivery");
        } finally { context.UnmapSubresource(readback,0); }
    }

    public void ResizeWindow(Size size)
    {
        if(size==windowSize) return;
        if(p5_present_resize(instance,size.Width,size.Height,error,error.Length)==0) throw Failure("window resize");
        windowSize=size;
    }

    public void Dispose()
    {
        if(readback!=null) { readback.Dispose(); readback=null; }
        if(instance!=IntPtr.Zero) { p5_present_destroy(instance); instance=IntPtr.Zero; }
    }
}
