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

internal interface ICaptureBridge : IDisposable
{
    Device CaptureDevice {get;}
    bool Receive(Renderer renderer,Texture2D input,Rectangle crop,bool deduplicate);
}

internal sealed class WindowCapture : IDisposable
{
    readonly IntPtr window;
    readonly ICaptureBridge bridge;
    readonly IDirect3DDevice device;
    readonly GraphicsCaptureItem item;
    readonly Direct3D11CaptureFramePool pool;
    readonly GraphicsCaptureSession session;
    Windows.Graphics.SizeInt32 size;
    DirectXPixelFormat format;
    static DirectXPixelFormat CaptureFormat(bool hdr) { return hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized; }
    public WindowCapture(IntPtr source,Renderer renderer)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new InvalidOperationException("This Windows session does not support window capture.");
        window=source;
        try {
            if(renderer.Backend=="d3d12") bridge=new SharedCapture(renderer.Device);
            else if(renderer.Backend=="vulkan") bridge=new VulkanCapture(renderer.Device);
            device=WindowsApi.WinRTDevice(bridge==null ? renderer.Device : bridge.CaptureDevice);
            item=WindowsApi.CaptureItem(window);
            size=item.Size;
            format=CaptureFormat(renderer.HdrEnabled);
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
                    var desc=texture.Description;
                    if((int)desc.Format!=(int)format || desc.Width<size.Width || desc.Height<size.Height) return false;
                    var crop=WindowsApi.ClientCrop(window,size.Width,size.Height);
                    fresh=bridge==null ? renderer.Receive(texture,crop,deduplicate) : bridge.Receive(renderer,texture,crop,deduplicate);
                }
                timestamp=frame.SystemRelativeTime.TotalMilliseconds;
                return fresh;
            }
            size=frame.ContentSize;
        }
        pool.Recreate(device,format,2,size);
        return false;
    }
    public void Dispose()
    {
        if(session!=null) session.Dispose();
        if(pool!=null) pool.Dispose();
        if(device!=null) device.Dispose();
        if(bridge!=null) bridge.Dispose();
    }
}
