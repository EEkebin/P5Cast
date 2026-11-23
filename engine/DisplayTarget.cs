using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Windows uses GDI device names for desktop rectangles and CCD target paths for monitor names.
// Pair them by source name to keep identical monitor models and mixed-DPI setups distinct.
public sealed class DisplayTarget
{
    public string DeviceName { get; private set; }
    public string FriendlyName { get; private set; }
    public bool Primary { get; private set; }
    public bool AdvancedColorEnabled { get; private set; }
    public Rectangle Bounds { get; private set; }
    public Rectangle WorkingArea { get; private set; }
    public string MonitorPath { get; private set; }
    IntPtr monitorHandle;
    public string Label { get { return FriendlyName+" · "+DeviceName.Replace(@"\\.\DISPLAY","Display ")+" · "+Bounds.Width+"×"+Bounds.Height+(Primary?" · Primary":""); } }
    public override string ToString() { return Label; }
    public static Size ResolutionLimit(string id)
    {
        switch ((id ?? "native").ToLowerInvariant()) {
            case "native": return Size.Empty;
            case "720": return new Size(1280,720);
            case "1080": return new Size(1920,1080);
            case "1440": return new Size(2560,1440);
            case "4k": return new Size(3840,2160);
            default: throw new ArgumentException("Choose native, 720, 1080, 1440, or 4k.");
        }
    }
    public string[] AvailableResolutions {
        get {
            var fit=new List<string>{"native"};
            foreach(var id in new[]{"720","1080","1440","4k"}) {
                Size s=ResolutionLimit(id);
                if(s.Width<=Bounds.Width && s.Height<=Bounds.Height) fit.Add(id);
            }
            return fit.ToArray();
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct Rect {
        public int Left,Top,Right,Bottom;
        public Rectangle Value { get { return Rectangle.FromLTRB(Left,Top,Right,Bottom); } }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct MonitorInfo {
        public int Size; public Rect Bounds,Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct SourcePath {
        public Luid Adapter; public uint Id,ModeIndex,Status;
    }
    [StructLayout(LayoutKind.Sequential)] struct TargetPath {
        public Luid Adapter;
        public uint Id,ModeIndex,Technology,Rotation,Scaling,Numerator,Denominator,Scan,Available,Status;
    }
    [StructLayout(LayoutKind.Sequential)] struct Path { public SourcePath Source; public TargetPath Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct Header { public uint Type,Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct SourceName {
        public Header H;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct TargetName {
        public Header H; public uint Flags,Technology; public ushort Manufacturer,Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Friendly;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Path;
    }
    [StructLayout(LayoutKind.Sequential)] struct ColorInfo { public Header H; public uint Value,Encoding,Bits; }
    delegate bool MonitorCallback(IntPtr monitor,IntPtr dc,ref Rect rect,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorCallback callback,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpi);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags,ref uint pathCount,[Out] Path[] paths,ref uint modeCount,IntPtr modes,IntPtr topology);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetSourceName(ref SourceName source);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetTargetName(ref TargetName target);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetColor(ref ColorInfo color);
    [DllImport("user32.dll",SetLastError=true)] static extern bool GetWindowRect(IntPtr window,out Rect rectangle);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int w,int h,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint msg,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr response);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetProp(IntPtr hwnd,string key);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool SetProp(IntPtr hwnd,string key,IntPtr value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr hwnd,string key);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    delegate bool WindowCallback(IntPtr hwnd,IntPtr unused);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback each,IntPtr unused);
    const string playerProperty="Cadrivect.PlaybackWindow.v1";
    internal static readonly uint MoveMessage=RegisterWindowMessage("Cadrivect.MoveToDisplay.v1");
    internal static void MarkPlayer(IntPtr hwnd,bool active)
    {
        if(active) SetProp(hwnd,playerProperty,new IntPtr(1)); else RemoveProp(hwnd,playerProperty);
    }
    internal static DisplayTarget FromMonitor(IntPtr monitor)
    {
        foreach(var screen in Enumerate()) if(screen.monitorHandle==monitor) return screen;
        return null;
    }
    public static bool MovePlayer(int pid,string deviceName)
    {
        if(pid<=0) return false;
        DisplayTarget target=null;
        foreach(var screen in Enumerate())
            if(String.Equals(screen.DeviceName,deviceName,StringComparison.OrdinalIgnoreCase)) target=screen;
        if(target==null) return false;
        IntPtr player=IntPtr.Zero;
        WindowCallback locate=delegate(IntPtr hwnd,IntPtr unused) {
            uint owner;
            GetWindowThreadProcessId(hwnd,out owner);
            if(owner!=(uint)pid || GetProp(hwnd,playerProperty)!=new IntPtr(1)) return true;
            player=hwnd; return false;
        };
        EnumWindows(locate,IntPtr.Zero);
        if(player==IntPtr.Zero) return false;
        IntPtr result;
        return SendMessageTimeout(player,MoveMessage,target.monitorHandle,IntPtr.Zero,2,2000,out result)!=IntPtr.Zero && result!=IntPtr.Zero;
    }
    public static bool IsPlaybackSource(IntPtr hwnd)
    {
        uint pid;
        if(GetWindowThreadProcessId(hwnd,out pid)==0) return false;
        try {
            using(var process=Process.GetProcessById((int)pid)) {
                string name=process.ProcessName;
                return name.Equals("RemotePlay",StringComparison.OrdinalIgnoreCase) || name.Equals("chiaki",StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("chiaki-ng",StringComparison.OrdinalIgnoreCase) || name.Equals("SignalRun",StringComparison.OrdinalIgnoreCase);
            }
        } catch(ArgumentException) { return false; }
    }
    readonly static IntPtr physicalDpi=new IntPtr(-4);
    static MonitorInfo GetMonitor(IntPtr handle)
    {
        var info=new MonitorInfo {Size=Marshal.SizeOf(typeof(MonitorInfo))};
        if(!GetMonitorInfo(handle,ref info)) throw new InvalidOperationException("The display is no longer connected.");
        return info;
    }
    static void AddNames(Dictionary<string,DisplayTarget> targets)
    {
        const uint active=2;
        for(int attempt=0;attempt<3;attempt++) {
            uint pathCount,modeCount;
            if(GetDisplayConfigBufferSizes(active,out pathCount,out modeCount)!=0 || pathCount==0 || modeCount==0) return;
            var paths=new Path[pathCount];
            var modes=Marshal.AllocHGlobal(checked((int)modeCount*64)); // sizeof(DISPLAYCONFIG_MODE_INFO)
            try {
                int status=QueryDisplayConfig(active,ref pathCount,paths,ref modeCount,modes,IntPtr.Zero);
                if(status==122) continue; // topology changed while querying
                if(status!=0) return;
                for(int i=0;i<pathCount;i++) {
                    var path=paths[i];
                    var src=new SourceName { H=new Header {Type=1,Size=(uint)Marshal.SizeOf(typeof(SourceName)),Adapter=path.Source.Adapter,Id=path.Source.Id} };
                    DisplayTarget target;
                    if(GetSourceName(ref src)!=0 || !targets.TryGetValue(src.Device,out target)) continue;
                    var name=new TargetName { H=new Header {Type=2,Size=(uint)Marshal.SizeOf(typeof(TargetName)),Adapter=path.Target.Adapter,Id=path.Target.Id} };
                    if(GetTargetName(ref name)==0 && !String.IsNullOrWhiteSpace(name.Friendly)) {
                        target.FriendlyName=name.Friendly; target.MonitorPath=name.Path;
                    }
                    var color=new ColorInfo { H=new Header {Type=9,Size=(uint)Marshal.SizeOf(typeof(ColorInfo)),Adapter=path.Target.Adapter,Id=path.Target.Id} };
                    if(GetColor(ref color)==0) target.AdvancedColorEnabled=(color.Value&2)!=0;
                }
                return;
            } finally { Marshal.FreeHGlobal(modes); }
        }
    }
    public static DisplayTarget[] Enumerate()
    {
        var old=SetThreadDpiAwarenessContext(physicalDpi);
        try {
            var targets=new Dictionary<string,DisplayTarget>(StringComparer.OrdinalIgnoreCase);
            MonitorCallback each=delegate(IntPtr monitor,IntPtr dc,ref Rect rect,IntPtr data) {
                var info=GetMonitor(monitor);
                targets[info.Device]=new DisplayTarget {monitorHandle=monitor,DeviceName=info.Device,FriendlyName="Monitor",Bounds=info.Bounds.Value,
                    WorkingArea=info.Work.Value,Primary=(info.Flags&1)!=0,MonitorPath=String.Empty};
                return true;
            };
            if(!EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,each,IntPtr.Zero) || targets.Count==0)
                throw new InvalidOperationException("No active Windows display could be enumerated.");
            AddNames(targets);
            var ordered=new List<DisplayTarget>(targets.Values);
            ordered.Sort(delegate(DisplayTarget a,DisplayTarget b) {
                return a.Primary==b.Primary ? StringComparer.OrdinalIgnoreCase.Compare(a.DeviceName,b.DeviceName) : (a.Primary ? -1:1);
            });
            return ordered.ToArray();
        } finally { if(old!=IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }
    public static string DeviceForWindow(IntPtr window)
    {
        return GetMonitor(MonitorFromWindow(window,2)).Device;
    }
    public static DisplayTarget Select(string deviceName,IntPtr source)
    {
        var targets=Enumerate();
        if(String.Equals(deviceName,"source",StringComparison.OrdinalIgnoreCase) && source!=IntPtr.Zero)
            deviceName=DeviceForWindow(source);
        foreach(var target in targets)
            if(String.Equals(deviceName,target.DeviceName,StringComparison.OrdinalIgnoreCase)) return target;
        return targets[0]; // default and unplugged saved display both resolve to the primary
    }
    public void MoveSourceWindow(IntPtr window)
    {
        if(window==IntPtr.Zero || IsIconic(window) || DeviceForWindow(window)==DeviceName) return;
        var old=SetThreadDpiAwarenessContext(physicalDpi);
        bool maximized=IsZoomed(window);
        try {
            if(maximized) ShowWindow(window,9);
            Rect rect;
            if(!GetWindowRect(window,out rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
            int w=Math.Min(rect.Value.Width,WorkingArea.Width), h=Math.Min(rect.Value.Height,WorkingArea.Height);
            int x=WorkingArea.Left+(WorkingArea.Width-w)/2, y=WorkingArea.Top+(WorkingArea.Height-h)/2;
            if(!SetWindowPos(window,IntPtr.Zero,x,y,w,h,0x14)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally {
            if(maximized) ShowWindow(window,3);
            if(old!=IntPtr.Zero) SetThreadDpiAwarenessContext(old);
        }
    }
}
