using System;
using System.ComponentModel;
using System.Collections.Generic;
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

internal static partial class WindowChromeProbe
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Startup {
        public int Size; public string Reserved,Desktop,Title; public uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags;
        public short Show,ReservedSize; public IntPtr ReservedBytes,StdIn,StdOut,StdErr;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread; public uint Id,ThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct Bounds { public int Left,Top,Right,Bottom; public int Width { get {return Right-Left;} } public int Height { get {return Bottom-Top;} } }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Monitor {
        public int Size; public Bounds Full,Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
    }
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
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
    static void RequireButtonPixel(Control button,int x,int y) {
        var target=window.InputHitTest(window.PointFromScreen(new Point(x,y))) as DependencyObject;
        Require(Hit(x,y)==1 && target!=null && (target==button || button.IsAncestorOf(target)),
            button.Name+" does not own screen pixel "+x+","+y+".");
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
    static async Task Until(Func<bool> condition,string reason,int timeoutMs=4500) {
        var time=Stopwatch.StartNew();
        while(!condition() && time.ElapsedMilliseconds<timeoutMs) await Task.Delay(15);
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

    static FrameworkElement FindVisual(DependencyObject parent,string typeName,Func<FrameworkElement,bool> accept=null) {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child.GetType().Name==typeName && (accept==null || accept((FrameworkElement)child))) return (FrameworkElement)child;
            var match=FindVisual(child,typeName,accept);
            if(match!=null) return match;
        }
        return null;
    }
    struct Sizes {
        public int Header,Play,Dial;
        public override string ToString() {return Header+"/"+Play+"/"+Dial+" px";}
    }
    static Sizes Measure() {
        var dial=FindVisual(Find<Border>("MeterHost"),"FrameRateDial");
        Require(dial!=null,"FPS gauge is missing.");
        return new Sizes {Header=ScreenRect(Find<Border>("HeaderBar")).Height,
            Play=ScreenRect(Find<Button>("OpenSource")).Width,Dial=ScreenRect(dial).Height};
    }
    static bool Scaled(Sizes actual,Sizes reference,double ratio) {
        return Math.Abs(actual.Header-reference.Header*ratio)<=2.5 && Math.Abs(actual.Play-reference.Play*ratio)<=2.5 &&
               Math.Abs(actual.Dial-reference.Dial*ratio)<=2.5;
    }
    static bool Contains(Bounds outer,Bounds inner) {
        return outer.Left<=inner.Left+1 && outer.Top<=inner.Top+1 && outer.Right>=inner.Right-1 && outer.Bottom>=inner.Bottom-1;
    }
    static void CheckDashboardEdges() {
        var monitor=Screen();
        var close=Find<Button>("CloseWindow");
        RequireButtonPixel(close,monitor.Work.Right-1,monitor.Work.Top);
        Require(Contains(monitor.Work,ScreenRect(Find<Border>("HeaderBar"))),"Scaled header escaped the monitor work area.");
        var dial=FindVisual(Find<Border>("MeterHost"),"FrameRateDial");
        Require(Contains(ScreenRect(Find<Border>("MeterHost")),ScreenRect(dial)),"Scaled gauge is clipped.");
        var videoRect=Rect(video);
        Require(video==Video() && Fits(videoRect,ScreenRect(Find<Border>("VideoHost")),2) && videoRect.Width>0 && videoRect.Height>0,
            "Native video surface does not match its scaled WPF allocation: "+Geometry(videoRect)+" / "+Geometry(ScreenRect(Find<Border>("VideoHost"))));
    }
    static RadioButton ViewButton(string id) {
        return (RadioButton)FindVisual(Find<Border>("MeterHost"),"RadioButton",e=>AutomationProperties.GetAutomationId(e)==id);
    }
    static TextBlock PictureMetric(string id) {
        return (TextBlock)FindVisual(Find<Border>("MeterHost"),"TextBlock",e=>AutomationProperties.GetAutomationId(e)==id);
    }
    static async Task CheckPictureSize(Dictionary<string,object> frame) {
        string dimensions=frame["inputWidth"]+"×"+frame["inputHeight"]+" → "+frame["outputWidth"]+"×"+frame["outputHeight"];
        bool native=Equals(frame["inputWidth"],frame["outputWidth"]) && Equals(frame["inputHeight"],frame["outputHeight"]);
        string scale=native ? ((string)frame["effectivePictureMode"]=="dlaa" ? "1× · DLAA" : "1× · Native")
            : (Convert.ToDouble(frame["outputWidth"])/Convert.ToDouble(frame["inputWidth"])).ToString("0.##")+"× upscale";
        var sizeMetric=PictureMetric("Metric_size"); var scaleMetric=PictureMetric("Metric_scale");
        await Until(()=>sizeMetric.Text==dimensions && scaleMetric.Text==scale,"Picture readout did not show "+dimensions+" / "+scale+".");
        var displayed=PictureMetric("Metric_displayedSize");
        bool fitted=!Equals(frame["displayedWidth"],frame["outputWidth"]) || !Equals(frame["displayedHeight"],frame["outputHeight"]);
        string screen="On screen · "+frame["displayedWidth"]+"×"+frame["displayedHeight"];
        await Until(()=>fitted ? displayed.Visibility==Visibility.Visible && displayed.Text==screen :
            displayed.Visibility==Visibility.Collapsed,"On-screen picture dimensions did not match the renderer.");
    }
    static async Task CheckFullscreenFrame(Action toggle,Func<bool> hasGeometry,string direction) {
        var api=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Cadrivect.Engine.EmbeddedPlayback")).First(t=>t!=null);
        var read=api.GetMethod("Snapshot");
        var json=new System.Web.Script.Serialization.JavaScriptSerializer();
        Func<Dictionary<string,object>> snapshot=()=>json.Deserialize<Dictionary<string,object>>((string)read.Invoke(null,null));
        int before=Convert.ToInt32(snapshot()["presented"]);
        var timer=Stopwatch.StartNew();
        toggle();
        await Until(hasGeometry,"Video layout did not settle on "+direction+".");
        long layoutMs=timer.ElapsedMilliseconds;
        var bounds=Rect(video);
        await Until(delegate {
            var data=snapshot();
            return Convert.ToInt32(data["surfaceWidth"])==bounds.Width && Convert.ToInt32(data["surfaceHeight"])==bounds.Height &&
                Convert.ToInt32(data["presented"])>before;
        },"Video stopped presenting at the new size on "+direction+".");
        var resized=snapshot();
        await CheckPictureSize(resized);
        if((string)resized["effectivePictureMode"]=="original")
            Require(Equals(resized["inputWidth"],resized["outputWidth"]) && Equals(resized["inputHeight"],resized["outputHeight"]),
                    "Changing fullscreen resized native video output.");
        Note(direction+" layout settled in "+layoutMs+" ms. Playback telemetry confirmed "+bounds.Width+"x"+bounds.Height+
             " surface at "+timer.ElapsedMilliseconds+" ms (250 ms sampling).");
    }
    static async Task CheckComparisonViews() {
        var api=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Cadrivect.Engine.EmbeddedPlayback")).First(t=>t!=null);
        var snapshot=api.GetMethod("Snapshot");
        string[] buttons={"ViewSideBySide","ViewWipe","ViewOriginal","ViewEnhanced"};
        string[] views={"side-by-side","wipe","original","enhanced"};
        for(int i=0;i<buttons.Length;i++) {
            var button=ViewButton(buttons[i]);
            Require(button!=null && button.IsEnabled,"Missing comparison control "+buttons[i]);
            ((ISelectionItemProvider)Peer(button).GetPattern(PatternInterface.SelectionItem)).Select();
            string expected="\"comparisonView\":\""+views[i]+"\"";
            await Until(delegate{return button.IsChecked==true && ((string)snapshot.Invoke(null,null)).Contains(expected);},
                        "Dashboard selection did not reach the running engine: "+views[i]);
            await WaitForOperation("Switching comparison to "+views[i]);
            if(views[i]=="wipe") {
                var slider=FindVisual(Find<Border>("MeterHost"),"Slider",e=>AutomationProperties.GetAutomationId(e)=="WipePosition");
                var range=Peer(slider).GetPattern(PatternInterface.RangeValue) as IRangeValueProvider;
                Require(range!=null && range.Minimum==0 && range.Maximum==100,"Wipe slider needs an accessible percentage range.");
                range.SetValue(37);
                await Until(delegate{return ((string)snapshot.Invoke(null,null)).Contains("\"wipePosition\":0.37");},
                            "Drawer slider did not update the live wipe.");
                range.SetValue(50);
            }
        }
        Note("All four comparison views switched on the running offline demo.");
    }
    static async Task CheckScaling() {
        Click("InterfaceGroup");
        var scale=Find<ComboBox>("InterfaceScale"); var displays=Find<ComboBox>("PlaybackDisplay");
        await Until(delegate{return scale.IsVisible;},"Interface controls did not open.");
        Require(scale.SelectedIndex==0,"An existing profile with no scale setting should open in Auto.");
        var original=displays.SelectedItem;
        string originalDevice=Screen().Device;
        var autoSizes=new Dictionary<string,Sizes>();
        var api=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Cadrivect.Engine.EmbeddedPlayback")).First(t=>t!=null);
        var snapshot=api.GetMethod("Snapshot");
        foreach(var item in displays.Items.Cast<object>().ToArray()) {
            string device=(string)item.GetType().GetProperty("DeviceName").GetValue(item,null);
            displays.SelectedItem=item;
            await Until(delegate{return IsZoomed(hwnd) && Screen().Device==device;},"Display selector did not move the maximized player to "+device);
            scale.SelectedItem="100%";
            await Until(delegate{return scale.SelectedItem!=null && scale.SelectedItem.ToString()=="100%";},"Missing 100% scale.");
            await Task.Delay(140); window.UpdateLayout();
            var basis=Measure(); var monitor=Screen(); double dpi=GetDpiForWindow(hwnd)/96.0;
            double density=Math.Min(Math.Max(monitor.Full.Width,monitor.Full.Height)/1920.0,Math.Min(monitor.Full.Width,monitor.Full.Height)/1080.0);
            double fit=Math.Min(monitor.Work.Width/(880*dpi),monitor.Work.Height/(590*dpi));
            double baselineFit=Math.Min(1,fit);
            double autoRatio=Math.Min(fit,Math.Max(1,density/dpi))/baselineFit;
            double manualRatio=Math.Min(1.25,fit)/baselineFit;
            scale.SelectedIndex=0;
            await Until(delegate{return Scaled(Measure(),basis,autoRatio);},"Auto scale did not size the header, playback control and FPS gauge for "+device+
                        ". 100% "+basis+", Auto "+Measure()+", expected factor "+autoRatio);
            CheckDashboardEdges();
            autoSizes.Add(device,Measure());
            Note(device+" "+monitor.Full.Width+"×"+monitor.Full.Height+" at "+(dpi*96)+" DPI: header / playback button / FPS gauge at 100% "+basis+"; Auto "+Measure()+".");
            scale.SelectedItem="125%";
            await Until(delegate{return Scaled(Measure(),basis,manualRatio);},"125% manual scale did not resize all three UI regions on "+device);
            CheckDashboardEdges();
            var header=Find<Border>("HeaderBar");
            var full=Screen().Full;
            Click("Fullscreen");
            await Until(delegate{return !header.IsVisible && Fits(Rect(video),full,2);},"Scaled fullscreen failed to occupy "+device+" in physical pixels.");
            SendMessage(video,0x203,IntPtr.Zero,IntPtr.Zero);
            await Until(delegate{return IsZoomed(hwnd) && header.IsVisible && Scaled(Measure(),basis,manualRatio);},
                        "Leaving fullscreen lost the manual scale on "+device);
            Require(scale.SelectedItem.ToString()=="125%" && Find<TextBlock>("Status").Text=="Offline demo","Monitor or fullscreen change reset playback or scale.");
        }
        displays.SelectedItem=original;
        await Until(delegate{return Screen().Device==originalDevice;},"Could not restore the initial display.");
        Require(scale.SelectedItem.ToString()=="125%","Moving the player reset its manual scale.");
        scale.SelectedIndex=0;
        foreach(var item in displays.Items.Cast<object>().ToArray()) {
            string device=(string)item.GetType().GetProperty("DeviceName").GetValue(item,null);
            displays.SelectedItem=item;
            await Until(delegate{return Screen().Device==device && Scaled(Measure(),autoSizes[device],1);},"Moving in Auto did not adapt the existing layout to "+device);
            var before=(string)snapshot.Invoke(null,null);
            await Until(delegate {
                string now=(string)snapshot.Invoke(null,null);
                return now!=before && now.Contains("\"sourceKind\":\"demo\"");
            },"Live demo did not produce a new sample after moving to "+device);
            CheckDashboardEdges();
        }
        displays.SelectedItem=original;
        await Until(delegate{return Screen().Device==originalDevice && Scaled(Measure(),autoSizes[originalDevice],1);},"Auto did not restore the original display scale.");
        Note("The playing demo adapted automatically when moved between monitors; telemetry and the video surface remained live.");
        window.WindowState=WindowState.Normal;
        await Until(delegate{return !IsZoomed(hwnd) && window.WindowState==WindowState.Normal;},"Could not restore window for compact layout test.");
        scale.SelectedItem="200%"; window.Width=window.MinWidth+10; window.Height=window.MinHeight+10;
        await Task.Delay(120); window.UpdateLayout();
        Require(scale.SelectedItem.ToString()=="200%" && Find<TextBlock>("ScaleStatus").Text.Contains("fitted"),
                "The compact layout should communicate its effective scale and preserve the requested 200%.");
        var content=ScreenRect((FrameworkElement)window.Content);
        string sectionNavigation=Find<ComboBox>("PlaySectionPicker").IsVisible?"PlaySectionPicker":"InterfaceGroup";
        var controls=new[]{"OpenSource","OpenVolume","OpenSetup","Fullscreen","CloseWindow",sectionNavigation};
        foreach(string id in controls) {
            var button=Find<Control>(id); var bounds=ScreenRect(button);
            Require(button.IsVisible && bounds.Width>0 && bounds.Height>0 && Contains(content,bounds),id+" escaped the compact window: "+Geometry(bounds));
            RequireButtonPixel(button,(bounds.Left+bounds.Right)/2,(bounds.Top+bounds.Bottom)/2);
        }
        var source=ScreenRect(Find<ComboBox>("SourceChoice")); var fullscreen=ScreenRect(Find<Button>("Fullscreen"));
        Require(source.Right<=fullscreen.Left+1,"The source picker overlaps fullscreen in the compact layout.");
        Require(Rect(video).Width>=200 && Rect(video).Height>=100 && Video()==video,"The compact layout lost the playable video area.");
        Note("The compact "+Geometry(content)+" window kept visible controls reachable at requested 200%; video "+Geometry(Rect(video))+".");
        scale.SelectedItem="150%";
        Require(scale.SelectedItem.ToString()=="150%","Could not save the manual scale for relaunch.");
    }

    static async Task CheckNvidiaOutput(MethodInfo snapshot,bool hdr,bool videoSr,bool fruc,string label)
    {
        var json=new System.Web.Script.Serialization.JavaScriptSerializer();
        Func<Dictionary<string,object>> read=()=>json.Deserialize<Dictionary<string,object>>((string)snapshot.Invoke(null,null));
        // Counters can include frames submitted before the output switch. Observe new submissions.
        var before=read();
        await Until(delegate {
            var now=read();
            if(!Equals(now["presenterHdr10"],hdr) || !Equals(now["hdrEnabled"],hdr) ||
                !Equals(now["presentationColorSpace"],hdr ? "BT.2020 PQ" : "sRGB") ||
                (hdr && !Equals(now["presenterHdrFormat"],"RGB10A2_UNORM"))) return false;
            if(videoSr && (!Equals(now["effectivePictureMode"],"rtxvideo") || !Equals(now["activeNvidiaEffect"],"rtxvideo") ||
                Convert.ToInt32(now["nvidiaEvaluations"])<=Convert.ToInt32(before["nvidiaEvaluations"]))) return false;
            if(fruc && (!Equals(now["activeFrameGenerationProvider"],"fruc") ||
                Convert.ToInt32(now["generated"])<=Convert.ToInt32(before["generated"]) ||
                Convert.ToInt32(now["frucMidpoints"])<=Convert.ToInt32(before["frucMidpoints"]))) return false;
            return string.IsNullOrEmpty(Convert.ToString(now["effectNotice"])) &&
                Convert.ToInt32(now["presented"])>Convert.ToInt32(before["presented"]);
        },label+" did not present frames with the requested NVIDIA effects.",15000);
    }

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
            foreach(string id in new[]{"PlayNav","HostNav","ClientNav","ShowStats","MinimizeWindow","MaximizeWindow","CloseWindow"}) {
                var button=Find<Control>(id); var bounds=ScreenRect(button);
                int edgeX=(bounds.Left+bounds.Right)/2;
                RequireButtonPixel(button,edgeX,work.Top);
            }
            var close=Find<Button>("CloseWindow");
            RequireButtonPixel(close,work.Right-1,work.Top);
            RequireButtonPixel(close,work.Right-1,work.Top+16);
            Note("The topmost pixel reaches each title-bar action; the upper-right corner and rightmost pixel reach Close.");
            var status=Find<TextBlock>("Status");
            Require(PictureMetric("Metric_size").Text=="Waiting for video" && PictureMetric("Metric_scale").Text=="",
                "An idle dashboard should wait for actual input and output dimensions.");
            var statusBar=ScreenRect(Find<Border>("StatusFooter"));
            Require(Math.Abs(maximizedHeader.Left-work.Left)<=2 && Math.Abs(maximizedHeader.Top-work.Top)<=2 &&
                Math.Abs(maximizedHeader.Right-work.Right)<=2 && Math.Abs(statusBar.Bottom-work.Bottom)<=2,
                "Maximized title/status bars are clipped: header "+Geometry(maximizedHeader)+", status "+Geometry(statusBar)+", work area "+Geometry(work)+".");
            Click("MaximizeWindow"); await Until(delegate{return !IsZoomed(hwnd) && window.WindowState==WindowState.Normal;},"Restore did not restore the native window.");
            Click("MinimizeWindow"); await Until(delegate{return IsIconic(hwnd);},"Minimize did not minimize the native window.");
            ShowWindow(hwnd,9);
            await Until(delegate{return !IsIconic(hwnd) && window.WindowState==WindowState.Normal;},"Native restore after minimize failed.");
            Note("Native maximize, restore and minimize passed.");
            Require(Find<ComboBox>("SourceChoice").SelectedItem.ToString()=="Offline demo","Refusing to start a network source.");
            if(CheckTransportOption) {await CheckVolumeControls(); await CheckIdleRendererCancel(); await CheckCanceledStartup();}
            if(CheckTransportOption) BeginDemoWithSpinner(); else Click("OpenSource");
            await Until(delegate{return status.Text=="Offline demo";},"The offline demo did not present its first frame.");
            if(CheckTransportOption) {await WaitForOperation("Presented offline demo");await CheckLiveTransport();}
            await CheckComparisonViews();
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
            var full=Screen().Full;
            await CheckFullscreenFrame(()=>Click("Fullscreen"),()=>!header.IsVisible && Fits(Rect(video),full,2),"Enter fullscreen");
            await CheckFullscreenFrame(()=>SendMessage(video,0x203,IntPtr.Zero,IntPtr.Zero),
                ()=>header.IsVisible && header.ActualHeight>5 && Math.Abs(Rect(video).Width-narrow)<=2,"Exit fullscreen");
            Require(Video()==video && provider.ToggleState==ToggleState.On,"Fullscreen replaced video or changed the sidebar preference.");
            Require(Peer(Find<Button>("OpenSource")).GetItemStatus()=="Playing","Offline playback stopped during the chrome test.");
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
            if(CheckDrawerOption) await CheckDrawers();
            if(Environment.GetEnvironmentVariable("P5CAST_PROBE_SCALING")=="1") await CheckScaling();
            if(CheckTransportOption) await CheckCompletedStop();
            else Click("OpenSource");
            await Until(delegate{return status.Text=="Disconnected";},"Offline demo did not disconnect.");
            if(CheckDrawerOption) await CheckPairDrawer();
            string renderCheck=Environment.GetEnvironmentVariable("P5CAST_PROBE_RENDERERS");
            if(renderCheck=="1" || renderCheck=="hdr") {
                Click("DisplayGroup");
                var choice=Find<ComboBox>("VideoRenderer");
                var color=Find<ComboBox>("HdrOutput");
                if(renderCheck=="hdr") {
                    var displays=Find<ComboBox>("PlaybackDisplay");
                    var hdrDisplay=displays.Items.Cast<object>().FirstOrDefault(d=>(bool)d.GetType().GetProperty("AdvancedColorEnabled").GetValue(d,null));
                    Require(hdrDisplay!=null,"An HDR-enabled monitor is required for the explicit HDR10 check.");
                    displays.SelectedItem=hdrDisplay;
                    Note("HDR test display: "+hdrDisplay);
                }
                var api=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Cadrivect.Engine.EmbeddedPlayback")).First(t=>t!=null);
                var snapshot=api.GetMethod("Snapshot");
                string[] keys={"d3d12","vulkan","opengl","d3d11"};
                string[] labels={"Direct3D 12","Vulkan","OpenGL","Direct3D 11"};
                for(int i=0;i<keys.Length;++i) {
                    choice.SelectedItem=labels[i];
                    await WaitForOperation("Probing "+labels[i],60000);
                    Require(choice.IsEnabled && choice.SelectedItem.ToString()==labels[i],"Renderer selection failed.");
                    bool expectHdr=renderCheck=="hdr" && keys[i]!="opengl";
                    if(expectHdr) color.SelectedItem="HDR10";
                    Click("OpenSource");
                    await Until(delegate{return status.Text=="Offline demo";},"Offline demo failed to present with "+labels[i]+".");
                    string frame=(string)snapshot.Invoke(null,null);
                    Require(frame.Contains("\"renderer\":\""+keys[i]+"\"") && !choice.IsEnabled && Video()==video,
                            "Selected renderer was not presenting on the embedded surface: "+frame);
                    if(Environment.GetEnvironmentVariable("P5CAST_PROBE_WIPE_INPUT")=="1")
                        await CheckWipeInput(snapshot,keys[i]);
                    if(renderCheck=="hdr") {
                        Require(frame.Contains("\"hdrEnabled\":"+(expectHdr?"true":"false")),"Unexpected output color mode: "+frame);
                        var meter=Find<Border>("MeterHost");
                        var nis=(RadioButton)FindVisual(meter,"RadioButton",e=>AutomationProperties.GetAutomationId(e)=="Picture_nis");
                        Require(nis!=null,"The Picture drawer has no NIS selection.");
                        await Until(()=>!Equals(nis.ToolTip,"Checking GPU support."),labels[i]+" NIS probe did not finish.",45000);
                        if(keys[i]=="opengl") Require(!nis.IsEnabled,"OpenGL advertised NIS without an implementation.");
                        else if(nis.IsEnabled) {
                            await SelectPlaybackMode(nis);
                            await Until(()=>((string)snapshot.Invoke(null,null)).Contains("\"effectivePictureMode\":\"nis\"") &&
                                ((string)snapshot.Invoke(null,null)).Contains("\"activeNisFilter\":\"NVScaler\""),
                                labels[i]+" failed to run NIS from the Picture drawer.",12000);
                            await SelectPlaybackMode(((RadioButton)FindVisual(meter,"RadioButton",e=>AutomationProperties.GetAutomationId(e)=="Picture_original")));
                            await Until(()=>((string)snapshot.Invoke(null,null)).Contains("\"effectivePictureMode\":\"original\""),"Could not switch back to Original.");
                            Note(labels[i]+" ran NVIDIA Image Scaling through the Picture drawer.");
                        }
                        if(expectHdr) {
                            Require(frame.Contains("\"presenterHdr10\":true") && frame.Contains("\"presentationColorSpace\":\"BT.2020 PQ\"") &&
                                frame.Contains("\"presenterHdrFormat\":\"RGB10A2_UNORM\""),"Missing negotiated HDR10 surface: "+frame);
                            var fruc=(RadioButton)FindVisual(meter,"RadioButton",e=>AutomationProperties.GetAutomationId(e)=="NvidiaFrucFrameGeneration");
                            var vsr=(RadioButton)FindVisual(meter,"RadioButton",e=>AutomationProperties.GetAutomationId(e)=="Picture_rtxvideo");
                            var caps=(Dictionary<string,object>)meter.Child.GetType().GetProperty("Capabilities",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(meter.Child,null);
                            bool testFruc=Convert.ToBoolean(caps["frucFrameGeneration"]),testVsr=Convert.ToBoolean(caps["rtxvideo"]);
                            Require(fruc!=null && fruc.IsEnabled==testFruc && vsr!=null && vsr.IsEnabled==testVsr,
                                "The HDR Picture drawer disagrees with the GPU effect checks on "+labels[i]+".");
                            if(testVsr) await SelectPlaybackMode(vsr);
                            if(testFruc) await SelectPlaybackMode(fruc);
                            await CheckNvidiaOutput(snapshot,true,testVsr,testFruc,labels[i]+" HDR10");
                            color.SelectedItem="SDR";
                            await WaitForOperation("Applying the playback setting");
                            await CheckNvidiaOutput(snapshot,false,testVsr,testFruc,labels[i]+" SDR");
                            var amd=(RadioButton)FindVisual(meter,"RadioButton",e=>AutomationProperties.GetAutomationId(e)=="AmdFrameGeneration");
                            if(testFruc && amd.IsEnabled) {
                                await SelectPlaybackMode(amd);
                                await Until(()=>((string)snapshot.Invoke(null,null)).Contains("\"activeFrameGenerationProvider\":\"amd\""),"AMD failed before switching to FRUC.",12000);
                                await SelectPlaybackMode(fruc);
                                Require(amd.IsChecked!=true,"Both vendor providers are selected.");
                            }
                            color.SelectedItem="HDR10";
                            await WaitForOperation("Applying the playback setting");
                            await CheckNvidiaOutput(snapshot,true,testVsr,testFruc,labels[i]+" restored HDR10");
                            if(testVsr && testFruc) Note(labels[i]+" presented HDR10 with RTX Video SR and advancing NVIDIA FRUC midpoints, including after an SDR round trip.");
                            if(testFruc) await SelectPlaybackMode((RadioButton)FindVisual(meter,"RadioButton",e=>Equals(((RadioButton)e).Content,"Off")));
                            if(testVsr) await SelectPlaybackMode((RadioButton)FindVisual(meter,"RadioButton",e=>AutomationProperties.GetAutomationId(e)=="Picture_original"));
                            Note(labels[i]+" presented HDR10 and switched to SDR and back during playback.");
                            if(keys[i]=="d3d12" || keys[i]=="vulkan") {
                                var picture=Find<Border>("MeterHost");
                                var dlss=(RadioButton)FindVisual(picture,"RadioButton",e=>Equals(((RadioButton)e).Content,"DLSS"));
                                await Until(()=>!Equals(dlss.ToolTip,"Checking GPU support."),labels[i]+" capability probe did not finish.",45000);
                                var nr=(RadioButton)FindVisual(picture,"RadioButton",e=>Equals(((RadioButton)e).Content,"DLSS 5 NR"));
                                Require(nr!=null && !nr.IsEnabled,"Neural rendering must stay disabled on an HDR stream.");
                                var fsr=(RadioButton)FindVisual(picture,"RadioButton",e=>AutomationProperties.GetAutomationId(e)=="Picture_fsr-temporal");
                                var amdFrames=(RadioButton)FindVisual(picture,"RadioButton",e=>AutomationProperties.GetAutomationId(e)=="AmdFrameGeneration");
                                Func<Dictionary<string,object>> sample=()=>new System.Web.Script.Serialization.JavaScriptSerializer()
                                    .Deserialize<Dictionary<string,object>>((string)snapshot.Invoke(null,null));
                                if(fsr.IsEnabled) {
                                    await SelectPlaybackMode(fsr);
                                    await Until(()=>Convert.ToInt32(sample()["fsrEvaluations"])>0 &&
                                        !string.IsNullOrEmpty(Convert.ToString(sample()["fsrVersion"])),"AMD temporal upscaling did not process the demo.",12000);
                                    await Until(()=>Equals(fsr.Content,"FSR "+sample()["fsrVersion"]+" · Temporal"),"Temporal FSR chip did not identify the running provider.");
                                    Note(labels[i]+" dashboard AMD FSR upscaling: "+sample()["fsrVersion"]);
                                }
                                if(amdFrames.IsEnabled) {
                                    int before=Convert.ToInt32(sample()["generated"]);
                                    await SelectPlaybackMode(amdFrames);
                                    await Until(()=>Convert.ToString(sample()["frameGenerationProvider"])=="amd" &&
                                        !string.IsNullOrEmpty(Convert.ToString(sample()["activeFrameGenerationVersion"])) &&
                                        Convert.ToInt32(sample()["generated"])>before,"AMD frame generation did not submit an intermediate frame.",12000);
                                    Note(labels[i]+" dashboard AMD frame generation: "+sample()["activeFrameGenerationVersion"]);
                                    await SelectPlaybackMode(((RadioButton)FindVisual(picture,"RadioButton",e=>Equals(((RadioButton)e).Content,"Off"))));
                                }
                                await SelectPlaybackMode(((RadioButton)FindVisual(picture,"RadioButton",e=>Equals(((RadioButton)e).Content,"Original"))));
                                if(dlss.IsEnabled) {
                                    var restored=Rect(video);
                                    await SelectPlaybackMode(dlss);
                                    await Until(()=>((string)snapshot.Invoke(null,null)).Contains("\"mode\":\"dlss\""),"DLSS did not activate.");
                                    var quality=(ComboBox)FindVisual(picture,"ComboBox",e=>AutomationProperties.GetAutomationId(e)=="DlssQuality");
                                    Require(quality.IsEnabled && quality.Items.Cast<string>().SequenceEqual(new[]{"Auto","Quality","Balanced","Performance","Ultra Performance"}),
                                            "DLSS mode choices are incomplete.");
                                    var model=(ComboBox)FindVisual(picture,"ComboBox",e=>AutomationProperties.GetAutomationId(e)=="DlssModelHint");
                                    model.SelectedIndex=2;
                                    await WaitForOperation("Applying the playback setting");
                                    await Until(()=>Convert.ToString(sample()["dlssModelHint"])=="m","DLSS 4.5 model preference did not reach the player.");
                                    quality.SelectedItem="Balanced";
                                    await WaitForOperation("Applying the playback setting");
                                    await Until(()=>((string)snapshot.Invoke(null,null)).Contains("\"dlssQuality\":\"balanced\""),"The dashboard failed to apply Balanced.");
                                    await CheckFullscreenFrame(()=>Click("Fullscreen"),()=>Fits(Rect(video),Screen().Full,2),"DLSS HDR enter fullscreen");
                                    await Until(()=>((string)snapshot.Invoke(null,null)).Contains("\"activeDlssQuality\":\"balanced\"") &&
                                        PictureMetric("ActiveDlssQuality").Text=="Active · Balanced","Balanced was not reported as active.");
                                    string enlarged=(string)snapshot.Invoke(null,null);
                                    Require(enlarged.Contains("\"effectivePictureMode\":\"dlss\"") && enlarged.Contains("\"presenterHdr10\":true"),
                                            "Fullscreen lost DLSS or HDR10.");
                                    await CheckFullscreenFrame(()=>SendMessage(video,0x203,IntPtr.Zero,IntPtr.Zero),
                                        ()=>Fits(Rect(video),restored,2),"DLSS HDR exit fullscreen");
                                    var dlaa=(RadioButton)FindVisual(picture,"RadioButton",e=>Equals(((RadioButton)e).Content,"DLAA"));
                                    if(dlaa.IsEnabled) {
                                        await SelectPlaybackMode(dlaa);
                                        await Until(()=>((string)snapshot.Invoke(null,null)).Contains("\"effectivePictureMode\":\"dlaa\""),"DLAA did not activate.");
                                        await CheckPictureSize(new System.Web.Script.Serialization.JavaScriptSerializer()
                                            .Deserialize<Dictionary<string,object>>((string)snapshot.Invoke(null,null)));
                                    }
                                    await SelectPlaybackMode(((RadioButton)FindVisual(picture,"RadioButton",e=>Equals(((RadioButton)e).Content,"Original"))));
                                    Require(!quality.IsVisible && !quality.IsEnabled,"DLSS selector should only be available for DLSS.");
                                    Note("Balanced selected through the dashboard, verified by live HDR playback telemetry.");
                                }
                            }
                        }
                    }
                    Click("OpenSource");
                    await Until(delegate{return status.Text=="Disconnected" && choice.IsEnabled;},"Renderer did not release the surface.");
                    Note(labels[i]+" presented through the dashboard and released the same playback surface.");
                }
            }
            if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("P5CAST_SCREENSHOT"))) await SaveDashboardScreenshot();
            if(CheckDrawerOption) await SaveDrawerPreference();
            completed=true; Click("CloseWindow");
        } catch(Exception e) {
            failure=e;
            if(Application.Current!=null) Application.Current.Shutdown(1);
        }
    }

    static async void CheckSavedScale() {
        try {
            window=Application.Current.MainWindow;
            Require(window!=null,"The dashboard did not reopen.");
            if(Environment.GetEnvironmentVariable("P5CAST_PROBE_SCALING")=="1") {
                Require(Find<ComboBox>("InterfaceScale").SelectedItem.ToString()=="150%","Manual scale did not survive a clean process relaunch.");
                Note("A new dashboard process restored the saved 150% scale.");
            }
            if(CheckTransportOption) CheckSavedVolume();
            if(CheckDrawerOption) await CheckSavedDrawers();
            completed=true; Click("CloseWindow");
        } catch(Exception e) {failure=e; Application.Current.Shutdown(1);}
    }
    static int OnPrivateDesktop(string root,string stage,string name,bool reopen)
    {
        var desktopName=new StringBuilder(256); int needed;
        Require(GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()),2,desktopName,512,out needed) && desktopName.ToString()==name,
            "Refusing to open WPF anywhere except the disposable private desktop.");
        // An inactive desktop can block WPF's hardware composition channel for hundreds of
        // milliseconds. Measure layout here with software WPF; the native video stays on its GPU.
        if(CheckDrawerOption) RenderOptions.ProcessRenderMode=RenderMode.SoftwareOnly;
        string local=Path.Combine(stage,"local");
        if(!reopen && Environment.GetEnvironmentVariable("P5CAST_PROBE_RENDERERS")=="hdr") {
            var runtime=Path.Combine("P5Cast","runtimes","fruc");
            var installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),runtime);
            var isolated=Path.Combine(local,runtime);
            foreach(string namePart in new[]{"NvOFFRUC.dll","cudart64_110.dll"}) {
                var file=Path.Combine(installed,namePart);
                if(File.Exists(file)) {
                    Directory.CreateDirectory(isolated);
                    File.Copy(file,Path.Combine(isolated,namePart),true);
                }
            }
        }
        Environment.SetEnvironmentVariable("LOCALAPPDATA",local);
        string prefs=Path.Combine(local,"LumaRelay");
        Directory.CreateDirectory(prefs);
        bool setup=Environment.GetEnvironmentVariable("P5CAST_PROBE_SETUP")=="1";
        if(setup) IsolateSetupProfile();
        else if(!reopen) File.WriteAllText(Path.Combine(prefs,"dashboard.json"),"{\"discordPresence\":false,\"source\":\"Demo\",\"maximized\":false}");
        var production=Assembly.LoadFrom(Path.Combine(root,"P5Cast.exe"));
        production.GetType("Cadrivect.Program",true).GetField("Root",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,root);
        if(Environment.GetEnvironmentVariable("P5CAST_PROBE_STOP")=="1") return RunStopProbe(production,root);
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,setup ? (Action)CheckSetupWindow : reopen ? (Action)CheckSavedScale : CheckWindow);
        int code=(int)production.EntryPoint.Invoke(null,new object[]{new string[0]});
        if(failure!=null) throw failure;
        Require(completed && code==0,"The production window did not close normally.");
        return 0;
    }
    static int Launch(string root,string stage,string name,bool reopen)
    {
        var desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0xF01FF,IntPtr.Zero);
        if(desktop==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            string exe=Assembly.GetExecutingAssembly().Location;
            var cmd=new StringBuilder("\""+exe+"\" \""+root+"\" \""+stage+"\" "+name+(reopen ? " --reopen" : " --worker"));
            var si=new Startup {Size=Marshal.SizeOf(typeof(Startup)),Desktop="winsta0\\"+name};
            ProcessInfo pi;
            if(!CreateProcess(exe,cmd,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,root,ref si,out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(pi.Thread);
            try {
                // The HDR suite starts four backends and exercises NVIDIA fullscreen in addition to the base UI checks.
                uint timeoutMs=Environment.GetEnvironmentVariable("P5CAST_PROBE_RENDERERS")=="hdr" ? 120000u :
                    CheckTransportOption ? 90000u : 30000u;
                if(WaitForSingleObject(pi.Process,timeoutMs)!=0) {
                    TerminateProcess(pi.Process,2); WaitForSingleObject(pi.Process,3000);
                    throw new TimeoutException("The private dashboard did not close in time.");
                }
                int code; Require(GetExitCodeProcess(pi.Process,out code),"No private dashboard exit status.");
                return code;
            } finally {CloseHandle(pi.Process);}
        } finally {CloseDesktop(desktop);}
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length==1 && args[0]=="--stop-holder") {System.Threading.Thread.Sleep(45000);return 0;}
        if(args.Length==2 && args[0]=="--register") return RegisterSetupFixture(args[1]);
        report=Path.Combine(args[1],"chrome.log");
        try {
            if(args.Length==4) return OnPrivateDesktop(args[0],args[1],args[2],args[3]=="--reopen");
            if(Environment.GetEnvironmentVariable("P5CAST_PROBE_SETUP")=="1") return LaunchSetupCases(args);
            int result=Launch(args[0],args[1],args[2],false);
            if(result==0 && (CheckTransportOption || CheckDrawerOption || Environment.GetEnvironmentVariable("P5CAST_PROBE_SCALING")=="1")) result=Launch(args[0],args[1],args[2],true);
            return result;
        } catch(Exception e) { Note(e.ToString()); return 1; }
        finally { if(args.Length==3 && File.Exists(report)) Console.Write(File.ReadAllText(report)); }
    }
}
