using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

internal static class WindowChromeProbe
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Startup {
        public int Size; public string Reserved,Desktop,Title; public uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags;
        public short Show,ReservedSize; public IntPtr ReservedBytes,StdIn,StdOut,StdErr;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread; public uint Id,ThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct Bounds { public int Left,Top,Right,Bottom; public int Width { get {return Right-Left;} } }
    [StructLayout(LayoutKind.Sequential)] struct Monitor { public int Size; public Bounds Full,Work; public uint Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref Monitor info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window,uint attribute,out Bounds value,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr h,int index,StringBuilder text,int length,out int needed);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder cmd,IntPtr process,IntPtr thread,bool inherit,uint flags,IntPtr env,string dir,ref Startup info,out ProcessInfo result);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h,uint timeout);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h,out int exit);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h,uint code);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Bounds b);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int command);

    static string report;
    static Window window;
    static IntPtr hwnd,video;
    static Exception failure;
    static bool completed;

    static void Require(bool test,string message) { if(!test) throw new Exception(message); }
    static T Find<T>(string id) where T:class {
        T widget=window.FindName(id) as T;
        Require(widget!=null,"Missing "+typeof(T).Name+" named "+id);
        return widget;
    }
    static Bounds Rect(IntPtr h) {
        Bounds b; Require(GetWindowRect(h,out b),"Window has no native rectangle."); return b;
    }
    static bool Fits(Bounds actual,Bounds expected,int tolerance) {
        return Math.Abs(actual.Left-expected.Left)<=tolerance && Math.Abs(actual.Top-expected.Top)<=tolerance &&
            Math.Abs(actual.Right-expected.Right)<=tolerance && Math.Abs(actual.Bottom-expected.Bottom)<=tolerance;
    }
    static string Geometry(Bounds r) { return string.Format("({0},{1})–({2},{3})",r.Left,r.Top,r.Right,r.Bottom); }
    static Bounds ScreenRect(FrameworkElement element) {
        var first=element.PointToScreen(new Point(0,0));
        var last=element.PointToScreen(new Point(element.ActualWidth,element.ActualHeight));
        return new Bounds{Left=(int)Math.Round(first.X),Top=(int)Math.Round(first.Y),Right=(int)Math.Round(last.X),Bottom=(int)Math.Round(last.Y)};
    }
    static Monitor Screen() {
        var monitor=new Monitor {Size=Marshal.SizeOf(typeof(Monitor))};
        Require(GetMonitorInfo(MonitorFromWindow(hwnd,2),ref monitor),"Cannot read target monitor bounds."); return monitor;
    }
    static int Hit(int x,int y) {
        int packed=unchecked((y<<16)|(x&0xffff));
        return SendMessage(hwnd,0x84,IntPtr.Zero,new IntPtr(packed)).ToInt32();
    }
    static int CenterHit(FrameworkElement element) {
        var p=element.PointToScreen(new Point(element.ActualWidth/2,element.ActualHeight/2));
        return Hit((int)Math.Round(p.X),(int)Math.Round(p.Y));
    }
    static AutomationPeer Peer(UIElement control) {
        var peer=UIElementAutomationPeer.CreatePeerForElement(control);
        Require(peer!=null,"Missing automation peer.");
        return peer;
    }
    static void Click(string id) {
        var provider=Peer(Find<Button>(id)).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
        Require(provider!=null,id+" lacks an Invoke provider."); provider.Invoke();
    }
    static async Task Until(Func<bool> condition,string reason) {
        var time=Stopwatch.StartNew();
        while(!condition() && time.ElapsedMilliseconds<4500) await Task.Delay(15);
        Require(condition(),reason);
    }
    static void Note(string text) { File.AppendAllText(report,text+Environment.NewLine); }
    static TransformGroup Transforms(Line line) {
        var group=line.RenderTransform as TransformGroup;
        Require(group!=null,"The menu stroke has no transform group."); return group;
    }
    static RotateTransform Rotation(Line line) { return Transforms(line).Children.OfType<RotateTransform>().Single(); }
    static double Shift(Line line) { return Transforms(line).Children.OfType<TranslateTransform>().Single().Y; }
    static bool Near(double x,double y) { return Math.Abs(x-y)<0.25; }
    static IntPtr Video() { return ((HwndHost)Find<Border>("VideoHost").Child).Handle; }

    static async void CheckWindow()
    {
        try {
            window=Application.Current.MainWindow;
            await Until(delegate{return window!=null && window.IsLoaded && window.IsVisible;},"Dashboard did not open.");
            hwnd=new WindowInteropHelper(window).Handle;
            video=Video();
            Require(window.Title=="P5Cast" && video!=IntPtr.Zero,"Production dashboard or native video surface missing.");
            var header=Find<FrameworkElement>("HeaderBar");
            Require(CenterHit(header)==2,"Header center is not a native HTCAPTION drag region.");
            var rect=Rect(hwnd);
            int x=(rect.Left+rect.Right)/2, y=(rect.Top+rect.Bottom)/2;
            int[] hits={Hit(rect.Left+1,y),Hit(rect.Right-2,y),Hit(x,rect.Top+1),Hit(x,rect.Bottom-2),
                        Hit(rect.Left+1,rect.Top+1),Hit(rect.Right-2,rect.Top+1),Hit(rect.Left+1,rect.Bottom-2),Hit(rect.Right-2,rect.Bottom-2)};
            Require(hits.SequenceEqual(new[]{10,11,12,15,13,14,16,17}),"Native resize hit tests returned "+string.Join(",",hits)+".");
            var toggle=Find<ToggleButton>("ShowStats");
            Require(CenterHit(toggle)==1 && CenterHit(Find<Button>("MinimizeWindow"))==1 && CenterHit(Find<Button>("CloseWindow"))==1,
                "A header control would intercept clicks as a caption.");
            int maxHit=CenterHit(Find<Button>("MaximizeWindow"));
            Require(maxHit==1 || maxHit==9,"Maximize button does not accept clicks.");
            Note("Hit testing passed for the caption, eight frame edges/corners and all four title bar controls.");
            Click("MaximizeWindow"); await Until(delegate{return IsZoomed(hwnd);},"Maximize did not maximize the native window.");
            var work=Screen().Work;
            Bounds maximized;
            if(DwmGetWindowAttribute(hwnd,9,out maximized,Marshal.SizeOf(typeof(Bounds)))!=0) maximized=Rect(hwnd);
            // User32 can include a narrow invisible sizing border outside a maximized work area.
            Require(Fits(maximized,work,16),"Maximized frame "+Geometry(maximized)+" does not follow work area "+Geometry(work)+".");
            Note("Maximized frame "+Geometry(maximized)+", work area "+Geometry(work)+".");
            window.UpdateLayout();
            var content=ScreenRect((FrameworkElement)window.Content);
            var maximizedHeader=ScreenRect(header);
            Note("Maximized content "+Geometry(content)+", title bar "+Geometry(maximizedHeader)+".");
            var status=Find<TextBlock>("Status");
            var statusBar=ScreenRect((FrameworkElement)((FrameworkElement)status.Parent).Parent);
            Require(Math.Abs(maximizedHeader.Left-work.Left)<=2 && Math.Abs(maximizedHeader.Top-work.Top)<=2 &&
                Math.Abs(maximizedHeader.Right-work.Right)<=2 && Math.Abs(statusBar.Bottom-work.Bottom)<=2,
                "Maximized title/status bars are clipped: header "+Geometry(maximizedHeader)+", status "+Geometry(statusBar)+", work area "+Geometry(work)+".");
            Click("MaximizeWindow"); await Until(delegate{return !IsZoomed(hwnd) && window.WindowState==WindowState.Normal;},"Restore did not restore the native window.");
            Click("MinimizeWindow"); await Until(delegate{return IsIconic(hwnd);},"Minimize did not minimize the native window.");
            ShowWindow(hwnd,9);
            await Until(delegate{return !IsIconic(hwnd) && window.WindowState==WindowState.Normal;},"Native restore after minimize failed.");
            Note("Native maximize, restore and minimize passed.");
            Require(Find<ComboBox>("SourceChoice").SelectedItem.ToString()=="Offline demo","Refusing to start a network source.");
            Click("OpenSource");
            await Until(delegate{return status.Text=="Offline demo";},"The offline demo did not present its first frame.");
            var peer=Peer(toggle);
            var provider=peer.GetPattern(PatternInterface.Toggle) as IToggleProvider;
            Require(provider!=null && peer.GetName().Length>0 && toggle.Focusable,"Sidebar toggle lacks its keyboard or accessibility contract.");
            Require(provider.ToggleState==ToggleState.On,"Sidebar was not initially open.");
            var top=Find<Line>("MenuTop"); var bottom=Find<Line>("MenuBottom");
            var topRotation=Rotation(top); var bottomRotation=Rotation(bottom);
            await Until(delegate{return Near(topRotation.Angle,45) && Near(bottomRotation.Angle,-45);},"Open glyph angles incorrect.");
            Require(Near(Shift(top),0) && Near(Shift(bottom),0),"Open glyph strokes do not meet.");
            int narrow=Rect(video).Width;
            bool animate=SystemParameters.ClientAreaAnimation;
            provider.Toggle();
            if(animate) {
                await Until(delegate {
                    return (topRotation.HasAnimatedProperties && bottomRotation.HasAnimatedProperties) ||
                        (topRotation.Angle>0.25 && topRotation.Angle<44.75 && bottomRotation.Angle<-.25 && bottomRotation.Angle>-44.75);
                },"Both menu strokes should animate while closing.");
            }
            await Until(delegate{return provider.ToggleState==ToggleState.Off && Near(topRotation.Angle,0) && Near(bottomRotation.Angle,0) &&
                Near(Shift(top),-3) && Near(Shift(bottom),3) && Rect(video).Width>narrow+200;},"Closed sidebar did not reclaim video width or restore parallel strokes.");
            int wide=Rect(video).Width;
            // Reverse during several transitions, including before the previous animation completes.
            for(int i=0;i<7;i++) {provider.Toggle(); await Task.Delay(17);}
            await Until(delegate{return provider.ToggleState==ToggleState.On && Near(topRotation.Angle,45) && Near(bottomRotation.Angle,-45) &&
                Math.Abs(Rect(video).Width-narrow)<=2 && Find<Border>("MeterHost").IsVisible;},"Rapid toggling left stale sidebar geometry or glyph state.");
            Require(Video()==video,"Sidebar toggling replaced the video HWND.");
            Note(string.Format("Sidebar accessibility and {0} menu motion passed. The same video HWND grew from {1} to {2} px and returned.",animate?"animated":"reduced",narrow,wide));
            Click("Fullscreen");
            await Until(delegate{return !header.IsVisible || header.ActualHeight<1;},"Header is still visible in fullscreen.");
            var full=Screen().Full;
            await Until(delegate{return Fits(Rect(video),full,2);},"Fullscreen video "+Geometry(Rect(video))+" did not fill monitor "+Geometry(full)+".");
            SendMessage(video,0x203,IntPtr.Zero,IntPtr.Zero);
            await Until(delegate{return header.IsVisible && header.ActualHeight>5 && Math.Abs(Rect(video).Width-narrow)<=2;},"Fullscreen did not restore the original dashboard geometry.");
            Require(Video()==video && provider.ToggleState==ToggleState.On,"Fullscreen replaced video or changed the sidebar preference.");
            Require(status.Text=="Offline demo","Offline playback stopped during the chrome test.");
            Note("Offline playback filled monitor "+Geometry(full)+" in fullscreen and preserved video HWND, sidebar state and windowed size on return.");
            Click("MaximizeWindow");
            await Until(delegate{return IsZoomed(hwnd) && Fits(ScreenRect((FrameworkElement)window.Content),work,2);},
                "Windowed maximization lost its work area inset.");
            Click("Fullscreen");
            await Until(delegate{return !header.IsVisible && Fits(Rect(video),full,2);},"Fullscreen from maximized did not fill the monitor.");
            SendMessage(video,0x203,IntPtr.Zero,IntPtr.Zero);
            await Until(delegate{return IsZoomed(hwnd) && header.IsVisible && Fits(ScreenRect((FrameworkElement)window.Content),work,2);},
                "Leaving fullscreen failed to restore the maximized window and its work area inset.");
            Require(Video()==video && provider.ToggleState==ToggleState.On,"Maximized fullscreen round trip lost sidebar or video surface.");
            Note("Fullscreen entered from maximized restored the exact work area and title bar.");
            Click("OpenSource");
            await Until(delegate{return status.Text=="Disconnected";},"Offline demo did not disconnect.");
            completed=true; Click("CloseWindow");
        } catch(Exception e) {
            failure=e;
            if(Application.Current!=null) Application.Current.Shutdown(1);
        }
    }

    static int OnPrivateDesktop(string root,string stage,string name)
    {
        var desktopName=new StringBuilder(256); int needed;
        Require(GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()),2,desktopName,512,out needed) && desktopName.ToString()==name,
            "Refusing to open WPF anywhere except the disposable private desktop.");
        string local=Path.Combine(stage,"local");
        Environment.SetEnvironmentVariable("LOCALAPPDATA",local);
        string prefs=Path.Combine(local,"LumaRelay");
        Directory.CreateDirectory(prefs);
        File.WriteAllText(Path.Combine(prefs,"dashboard.json"),"{\"discordPresence\":false,\"source\":\"Demo\",\"maximized\":false}");
        var production=Assembly.LoadFrom(Path.Combine(root,"P5Cast.exe"));
        production.GetType("Cadrivect.Program",true).GetField("Root",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,root);
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,(Action)CheckWindow);
        int code=(int)production.EntryPoint.Invoke(null,new object[]{new string[0]});
        if(failure!=null) throw failure;
        Require(completed && code==0,"The production window did not close normally.");
        return 0;
    }
    static int Launch(string root,string stage,string name)
    {
        var desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0xF01FF,IntPtr.Zero);
        if(desktop==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            string exe=Assembly.GetExecutingAssembly().Location;
            var cmd=new StringBuilder("\""+exe+"\" \""+root+"\" \""+stage+"\" "+name+" --worker");
            var si=new Startup {Size=Marshal.SizeOf(typeof(Startup)),Desktop="winsta0\\"+name};
            ProcessInfo pi;
            if(!CreateProcess(exe,cmd,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,root,ref si,out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(pi.Thread);
            try {
                if(WaitForSingleObject(pi.Process,30000)!=0) {
                    TerminateProcess(pi.Process,2); WaitForSingleObject(pi.Process,3000);
                    throw new TimeoutException("The private dashboard did not close in time.");
                }
                int code; Require(GetExitCodeProcess(pi.Process,out code),"No private dashboard exit status.");
                return code;
            } finally {CloseHandle(pi.Process);}
        } finally {CloseDesktop(desktop);}
    }
    [STAThread] static int Main(string[] args) {
        report=Path.Combine(args[1],"chrome.log");
        try {
            if(args.Length==4) return OnPrivateDesktop(args[0],args[1],args[2]);
            return Launch(args[0],args[1],args[2]);
        } catch(Exception e) { Note(e.ToString()); return 1; }
        finally { if(args.Length==3 && File.Exists(report)) Console.Write(File.ReadAllText(report)); }
    }
}
