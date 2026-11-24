using System;
using System.Drawing;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

internal sealed class NativeD3D12Presentation : IFramePresenter
{
    const string Library="P5Cast.Presentation.dll";
    const DllImportSearchPath Search=DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32 |
        DllImportSearchPath.UseDllDirectoryForDependencies;
    IntPtr presenter;
    Size childSize;
    public bool Hdr10 { get; private set; }

    [DefaultDllImportSearchPaths(Search),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern int p5_d3d12_display_create(IntPtr parent,IntPtr device,int width,int height,int hdr,out IntPtr handle);
    [DefaultDllImportSearchPaths(Search),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern int p5_d3d12_display_frame(IntPtr handle,IntPtr texture);
    [DefaultDllImportSearchPaths(Search),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern int p5_d3d12_display_resize(IntPtr handle,int width,int height);
    [DefaultDllImportSearchPaths(Search),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern void p5_d3d12_display_destroy(IntPtr handle);

    public NativeD3D12Presentation(IntPtr hwnd,Device device,Size size,bool hdr10)
    {
        Check(p5_d3d12_display_create(hwnd,device.NativePointer,size.Width,size.Height,hdr10 ? 1 : 0,out presenter),
            hdr10 ? "HDR10 setup" : "setup");
        Hdr10=hdr10; childSize=size;
    }
    public void Present(Texture2D target)
    {
        Check(p5_d3d12_display_frame(presenter,target.NativePointer),"frame delivery");
    }
    public void ResizeWindow(Size size)
    {
        if(size==childSize) return;
        Check(p5_d3d12_display_resize(presenter,size.Width,size.Height),"window resize");
        childSize=size;
    }
    static void Check(int hr,string operation)
    {
        if(hr<0) throw new InvalidOperationException(string.Format("Direct3D 12 presentation {0} failed (0x{1:X8}). {2}",
            operation,hr,Marshal.GetExceptionForHR(hr).Message));
    }
    public void Dispose()
    {
        if(presenter!=IntPtr.Zero) {p5_d3d12_display_destroy(presenter); presenter=IntPtr.Zero;}
    }
}
