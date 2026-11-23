using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using SharpDX.Direct3D11;

internal static class WindowsApi
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICaptureWindow
    {
        void CreateForWindow(IntPtr hwnd, ref Guid iid, out IntPtr item);
        void CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISurfaceAccess { void GetInterface(ref Guid iid, out IntPtr resource); }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr winrtDevice);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, int mods, int key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rectangle);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd,int name,out Rect rect,int size);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }

    public static GraphicsCaptureItem CaptureItem(IntPtr hwnd)
    {
        var factory=(ICaptureWindow)WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
        Guid iid=new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        IntPtr raw;
        factory.CreateForWindow(hwnd,ref iid,out raw);
        try { return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(raw); }
        finally { Marshal.Release(raw); }
    }
    public static IDirect3DDevice WinRTDevice(Device device)
    {
        using(var dxgi=device.QueryInterface<SharpDX.DXGI.Device>())
        {
            IntPtr raw;
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer,out raw));
            try { return (IDirect3DDevice)Marshal.GetObjectForIUnknown(raw); }
            finally { Marshal.Release(raw); }
        }
    }
    public static Texture2D Texture(IDirect3DSurface surface)
    {
        Guid iid=typeof(Texture2D).GUID;
        IntPtr texture;
        ((ISurfaceAccess)surface).GetInterface(ref iid,out texture);
        return new Texture2D(texture);
    }
    public static Rectangle ClientCrop(IntPtr hwnd, int width, int height)
    {
        var whole=new Rectangle(0,0,width,height);
        Rect frame,client;
        var start=new Point();
        if (DwmGetWindowAttribute(hwnd,9,out frame,16)!=0 || !GetClientRect(hwnd,out client) || !ClientToScreen(hwnd,ref start))
            return whole;
        var crop=Rectangle.Intersect(whole,new Rectangle(start.X-frame.Left,start.Y-frame.Top,client.Right,client.Bottom));
        return crop.Width>0 && crop.Height>0 ? crop : whole;
    }
}

internal sealed class WindowCapture : IDisposable
{
    readonly IntPtr window;
    readonly Device captureDevice;
    Texture2D staging,uploaded;
    readonly IDirect3DDevice device;
    readonly GraphicsCaptureItem item;
    readonly Direct3D11CaptureFramePool pool;
    readonly GraphicsCaptureSession session;
    Windows.Graphics.SizeInt32 size;
    DirectXPixelFormat format;
    static DirectXPixelFormat CaptureFormat(bool hdr) { return hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized; }
    public WindowCapture(IntPtr source, Device renderDevice,bool hdr,bool transfer=false)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new InvalidOperationException("This Windows session does not support window capture.");
        window=source;
        // Windows Graphics Capture needs a system D3D11 device. An image from
        // that device cannot be submitted directly to a different API's context.
        if(transfer) captureDevice=new Device(SharpDX.Direct3D.DriverType.Hardware,DeviceCreationFlags.BgraSupport);
        try {
            device=WindowsApi.WinRTDevice(captureDevice ?? renderDevice);
            item=WindowsApi.CaptureItem(window);
            size=item.Size;
            format=CaptureFormat(hdr);
            pool=Direct3D11CaptureFramePool.CreateFreeThreaded(device,format,2,size);
            session=pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled=false;
            session.StartCapture();
        } catch { Dispose(); throw; }
    }
    public bool Read(Renderer renderer, bool deduplicate, out double timestamp)
    {
        timestamp=0;
        if(format!=CaptureFormat(renderer.HdrEnabled))
        {
            format=CaptureFormat(renderer.HdrEnabled);
            pool.Recreate(device,format,2,size);
            return false;
        }
        using(var frame=pool.TryGetNextFrame())
        {
            if(frame==null) return false;
            if(frame.ContentSize.Width<=0 || frame.ContentSize.Height<=0) return false;
            if(frame.ContentSize.Width==size.Width && frame.ContentSize.Height==size.Height)
            {
                bool fresh;
                using(var texture=WindowsApi.Texture(frame.Surface))
                {
                    // WGC may deliver queued surfaces from before Recreate. Their DXGI format
                    // must match the new pool before the renderer allocates its history textures.
                    if ((int)texture.Description.Format!=(int)format) return false;
                    fresh=renderer.Receive(captureDevice==null ? texture : Transfer(renderer.Device,texture),WindowsApi.ClientCrop(window,size.Width,size.Height),deduplicate);
                }
                timestamp=frame.SystemRelativeTime.TotalMilliseconds;
                return fresh;
            }
            size=frame.ContentSize;
        }
        pool.Recreate(device,format,2,size);
        return false;
    }
    Texture2D Transfer(Device destination,Texture2D input)
    {
        var desc=input.Description;
        if(staging==null || staging.Description.Width!=desc.Width || staging.Description.Height!=desc.Height || staging.Description.Format!=desc.Format) {
            if(staging!=null) staging.Dispose();
            if(uploaded!=null) uploaded.Dispose();
            desc.OptionFlags=ResourceOptionFlags.None;
            desc.BindFlags=BindFlags.None;
            desc.Usage=ResourceUsage.Staging;
            desc.CpuAccessFlags=CpuAccessFlags.Read;
            staging=new Texture2D(captureDevice,desc);
            desc.Usage=ResourceUsage.Default;
            desc.CpuAccessFlags=CpuAccessFlags.None;
            uploaded=new Texture2D(destination,desc);
        }
        var ctx=captureDevice.ImmediateContext;
        ctx.CopyResource(input,staging);
        var data=ctx.MapSubresource(staging,0,MapMode.Read,MapFlags.None);
        try {destination.ImmediateContext.UpdateSubresource(data,uploaded,0);}
        finally {ctx.UnmapSubresource(staging,0);}
        return uploaded;
    }
    public void Dispose()
    {
        if(session!=null) session.Dispose();
        if(pool!=null) pool.Dispose();
        if(device!=null) device.Dispose();
        if(uploaded!=null) uploaded.Dispose();
        if(staging!=null) staging.Dispose();
        if(captureDevice!=null) captureDevice.Dispose();
    }
}
