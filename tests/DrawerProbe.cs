using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing=System.Drawing;

internal static partial class WindowChromeProbe
{
    static bool CheckDrawerOption { get { return Environment.GetEnvironmentVariable("P5CAST_PROBE_DRAWERS")=="1"; } }
    static double SideWidth { get { return Find<ColumnDefinition>("MeterColumn").ActualWidth; } }
    static double BottomHeight { get { return Find<RowDefinition>("SettingsRow").ActualHeight; } }
    static string DrawerSample { get { return Path.Combine(Path.GetDirectoryName(report),"drawer-sizes.txt"); } }

    static async Task SetDrawer(string name,bool open) {
        var button=Find<ToggleButton>(name);
        bool side=name=="ShowStats";
        var drawer=Find<Border>(side?"SidebarDrawer":"SettingsDrawer");
        var body=(FrameworkElement)drawer.Child;
        if(button.IsChecked!=open) {
            var pattern=(IToggleProvider)Peer(button).GetPattern(PatternInterface.Toggle);
            Require(pattern!=null && button.Focusable,name+" lacks a keyboard-accessible toggle.");
            pattern.Toggle();
        }
        await Until(delegate {
            double target=open?(side?body.Width:body.Height):0;
            return button.IsChecked==open && drawer.IsVisible==open && Math.Abs((side?SideWidth:BottomHeight)-target)<1;
        },name+" did not settle into the requested state.");
    }

    static async Task CheckDrawerMotion(bool side) {
        string name=side?"ShowStats":"ShowSettings";
        var drawer=Find<Border>(side?"SidebarDrawer":"SettingsDrawer");
        var grip=Find<Thumb>(side?"SidebarResize":"SettingsResize");
        var toggle=(IToggleProvider)Peer(Find<ToggleButton>(name)).GetPattern(PatternInterface.Toggle);
        Func<int> videoEdge=()=>side?Rect(video).Right:Rect(video).Bottom;
        Func<int> gripEdge=()=>side?ScreenRect(grip).Left:ScreenRect(grip).Top;
        int openEdge=videoEdge(),gap=gripEdge()-openEdge;
        var bounds=ScreenRect(drawer);
        int extent=side?bounds.Width:bounds.Height;
        int closedEdge=openEdge+extent;
        Func<string> drawInfo=()=>string.Format("{0}:{1:0.#}/{2:0.#}({3})",videoEdge(),
            side?drawer.Width:drawer.Height,side?drawer.ActualWidth:drawer.ActualHeight,drawer.Visibility);
        Require(extent>50,name+" is too small to test its motion.");
        foreach(bool opening in new[]{false,true}) {
            int previous=videoEdge(),target=opening?openEdge:closedEdge;
            int distinctMiddle=0;
            var timer=Stopwatch.StartNew();
            var trace=new System.Text.StringBuilder();
            var nativeTrace=new System.Text.StringBuilder();
            int nativeMiddle=0; bool nativeReversed=false;
            var stopTracking=new System.Threading.CancellationTokenSource();
            var nativeSampler=Task.Run(delegate {
                int last=videoEdge();
                while(!stopTracking.IsCancellationRequested) {
                    int edge=videoEdge();
                    if(edge!=last) {
                        nativeTrace.Append(" ").Append(timer.ElapsedMilliseconds).Append("=").Append(edge);
                        if(edge>openEdge+3 && edge<closedEdge-3) nativeMiddle++;
                        if(opening?edge>last+2:edge<last-2) nativeReversed=true;
                        last=edge;
                    }
                    System.Threading.Thread.Sleep(3);
                }
            });
            string failure=null;
            EventHandler observe=delegate {
                int edge=videoEdge();
                if(edge==previous) return;
                if(trace.Length<1200) trace.Append(" ").Append(timer.ElapsedMilliseconds).Append("=").Append(drawInfo());
                if(opening?edge>previous+2:edge<previous-2) failure="reversed direction";
                if(edge>openEdge+3 && edge<closedEdge-3) {
                    distinctMiddle++;
                    if(!drawer.IsVisible || Math.Abs(gripEdge()-edge-gap)>2) failure="detached from the native video";
                }
                previous=edge;
            };
            // HwndHost applies the new HWND bounds in its own LayoutUpdated handler.
            // Compare the two screen rectangles after every handler for the frame has returned.
            bool measuring=true,queued=false;
            EventHandler afterLayout=delegate {
                if(queued) return;
                queued=true;
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,new Action(delegate {
                    queued=false;
                    if(measuring) observe(null,EventArgs.Empty);
                }));
            };
            window.LayoutUpdated+=afterLayout;
            try {
                toggle.Toggle(); window.UpdateLayout(); observe(null,EventArgs.Empty);
                await Until(()=>Math.Abs(videoEdge()-target)<=2 && drawer.IsVisible==opening,name+" failed to reach its endpoint.");
            } finally {
                measuring=false; window.LayoutUpdated-=afterLayout;
                stopTracking.Cancel(); nativeSampler.Wait(); stopTracking.Dispose();
            }
            Require(!nativeReversed,name+" reversed the HWND trajectory."+nativeTrace);
            Require(failure==null,name+" "+failure+" while "+(opening?"opening.":"closing.")+trace);
            if(SystemParameters.ClientAreaAnimation)
                Require(Math.Max(distinctMiddle,nativeMiddle)>=2 && distinctMiddle>=1,name+" used "+distinctMiddle+" intermediate layout and "+
                        nativeMiddle+" native video sizes over "+timer.ElapsedMilliseconds+" ms while "+(opening?"opening.":"closing.")+trace+" HWND:"+nativeTrace);
            else
                Require(distinctMiddle==0,name+" animated despite the Windows reduced-motion preference.");
            Note(name+" "+(opening?"opened":"closed")+" across "+extent+" px with "+Math.Max(distinctMiddle,nativeMiddle)+" distinct intermediate native video positions.");
        }
        if(SystemParameters.ClientAreaAnimation) {
            bool requested=false,reversed=false,snapped=false;
            EventHandler reverse=delegate {
                int edge=videoEdge();
                if(requested || edge<=openEdge+3 || edge>=closedEdge-3) return;
                requested=true;
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,new Action(delegate {
                    int before=videoEdge();
                    toggle.Toggle();
                    window.UpdateLayout();
                    snapped=Math.Abs(videoEdge()-before)>=extent/8;
                    reversed=true;
                }));
            };
            window.LayoutUpdated+=reverse;
            try {
                toggle.Toggle();
                await Until(()=>reversed,name+" did not expose an intermediate edge to reverse.");
            } finally {window.LayoutUpdated-=reverse;}
            Require(!snapped,name+" snapped to an endpoint on reversal.");
            await SetDrawer(name,true);
            Require(Math.Abs(videoEdge()-openEdge)<=2,name+" did not return to its saved size after reversal.");
        }
        Note(name+" and the native video edge stayed aligned while closing and reopening.");
    }
    static void RouteKey(Thumb grip,Key key) {
        grip.Focus();
        var keyEvent=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(grip),Environment.TickCount,key);
        keyEvent.RoutedEvent=Keyboard.PreviewKeyDownEvent; grip.RaiseEvent(keyEvent);
        if(!keyEvent.Handled) {keyEvent.RoutedEvent=Keyboard.KeyDownEvent; grip.RaiseEvent(keyEvent);}
    }
    static void Drag(Thumb grip,double dx,double dy,bool cancel=false) {
        grip.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
        grip.RaiseEvent(new DragDeltaEventArgs(dx,dy){RoutedEvent=Thumb.DragDeltaEvent});
        grip.RaiseEvent(new DragCompletedEventArgs(dx,dy,cancel){RoutedEvent=Thumb.DragCompletedEvent});
        window.UpdateLayout();
    }
    static void CheckGrip(Thumb grip) {
        var b=ScreenRect(grip);
        Require(grip.IsVisible && grip.Focusable && Peer(grip).GetName().Length>0 && b.Width>4 && b.Height>4,
                grip.Name+" has no accessible resize target.");
        RequireButtonPixel(grip,(b.Left+b.Right)/2,(b.Top+b.Bottom)/2);
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X,Y; }
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h,ref NativePoint point);
    static IntPtr Packed(int x,int y) {return new IntPtr(unchecked((y<<16)|(x&0xffff)));}
    static async Task<bool> TryNativeDrag(Thumb grip) {
        // Messages go only to the verified private dashboard HWND. Never redirect the
        // system cursor or use SendInput, which would operate the user's active desktop.
        var b=ScreenRect(grip); var p=new NativePoint {X=(b.Left+b.Right)/2,Y=(b.Top+b.Bottom)/2};
        Require(ScreenToClient(hwnd,ref p),"Cannot address grip in the private window.");
        double original=SideWidth;
        SendMessage(hwnd,0x200,IntPtr.Zero,Packed(p.X,p.Y));
        SendMessage(hwnd,0x201,new IntPtr(1),Packed(p.X,p.Y));
        try {
            SendMessage(hwnd,0x200,new IntPtr(1),Packed(p.X-42,p.Y));
            await Task.Delay(40);
        } finally {SendMessage(hwnd,0x202,IntPtr.Zero,Packed(p.X-42,p.Y));}
        window.UpdateLayout(); return Math.Abs(SideWidth-original)>4;
    }

    static async Task CheckDrawers() {
        var side=Find<Thumb>("SidebarResize"); var bottom=Find<Thumb>("SettingsResize");
        var settings=Find<Border>("SettingsDrawer");
        var sideToggle=Find<ToggleButton>("ShowStats"); var bottomToggle=Find<ToggleButton>("ShowSettings");
        Require(sideToggle.IsChecked==true && bottomToggle.IsChecked==true && settings.IsVisible,"Legacy preferences should open both drawers.");
        CheckGrip(side); CheckGrip(bottom);
        await CheckDrawerMotion(true); await CheckDrawerMotion(false);
        Note(await TryNativeDrag(side) ? "Private HWND mouse messages resized the performance drawer."
                                      : "Inactive desktop did not route a native pointer drag; grip hit tests and WPF drag events are checked.");
        double initialSide=SideWidth,initialBottom=BottomHeight; var initialVideo=Rect(video);
        Drag(side,-58,0); Drag(bottom,0,-48);
        await Until(delegate{return SideWidth>initialSide+35 && BottomHeight>initialBottom+30 && Rect(video).Width<initialVideo.Width-25 && Rect(video).Height<initialVideo.Height-20;},
                    "Dragging both edges did not resize the playback viewport.");
        CheckGrip(side); CheckGrip(bottom); CheckDashboardEdges();
        double sideSize=SideWidth,bottomSize=BottomHeight;
        Drag(side,28,0,true); Drag(bottom,0,22,true);
        await Until(delegate{return Near(SideWidth,sideSize)&&Near(BottomHeight,bottomSize);},"Canceled drags changed the saved drawer geometry.");
        RouteKey(side,Key.Right); RouteKey(bottom,Key.Down);
        await Until(delegate{return SideWidth<sideSize-5 && BottomHeight<bottomSize-5;},"Arrow keys did not shrink the focused drawers.");
        sideSize=SideWidth; bottomSize=BottomHeight;
        RouteKey(side,Key.Space); RouteKey(bottom,Key.Enter);
        await Until(delegate{return SideWidth<1 && BottomHeight<1 && !settings.IsVisible;},"Keyboard collapse did not reclaim the drawer space.");
        await SetDrawer("ShowStats",true); await SetDrawer("ShowSettings",true);
        Require(Near(SideWidth,sideSize)&&Near(BottomHeight,bottomSize),"Reopening drawers lost their last size.");
        Click("Fullscreen"); var full=Screen().Full;
        await Until(delegate{return Fits(Rect(video),full,2) && !side.IsVisible && !bottom.IsVisible;},"Fullscreen retained a drawer or grip.");
        SendMessage(video,0x203,IntPtr.Zero,IntPtr.Zero);
        await Until(delegate{return IsZoomed(hwnd) && Near(SideWidth,sideSize) && Near(BottomHeight,bottomSize);},"Fullscreen did not restore both drawer sizes.");
        Require(Video()==video && Find<TextBlock>("Status").Text=="Offline demo","Drawer changes interrupted playback or replaced the video HWND.");
        await SetDrawer("ShowSettings",false); Click("HostNav");
        await Until(delegate{return settings.IsVisible && Find<StackPanel>("HostPage").IsVisible;},"Host navigation did not reveal its settings.");
        await SetDrawer("ShowSettings",false); Click("ClientNav");
        await Until(delegate{return settings.IsVisible && Find<StackPanel>("ClientPage").IsVisible;},"Client navigation did not reveal its settings.");
        await SetDrawer("ShowSettings",false); Click("PlayNav");
        await Until(delegate{return settings.IsVisible && Find<StackPanel>("PlayPage").IsVisible;},"Play navigation did not reveal its settings.");
        Click("MaximizeWindow");
        await Until(delegate{return !IsZoomed(hwnd);},"Cannot restore window for compact drawer test.");
        window.Width=window.MinWidth+10; window.Height=window.MinHeight+10; await Task.Delay(100);
        RouteKey(side,Key.End); RouteKey(bottom,Key.End); window.UpdateLayout();
        var small=Rect(video);
        Require(small.Width>=180 && small.Height>=90 && Video()==video,"A maximum-size drawer consumed the compact video viewport: "+Geometry(small));
        CheckGrip(side); CheckGrip(bottom);
        var compactPicker=Find<ComboBox>("PlaySectionPicker");
        Require(compactPicker.IsVisible && !Find<Grid>("SettingsSections").IsVisible,
                "A narrow drawer must reserve field width by using the compact section selector.");
        Require(compactPicker.Items.Count==3,"Offline demo must expose only its three applicable settings sections.");
        compactPicker.SelectedIndex=1;
        await Until(()=>Find<ComboBox>("InterfaceScale").IsVisible,"Compact section selector did not open Interface.");
        foreach(string id in new[]{"OpenSource","OpenVolume","OpenSetup","ShowSettings","Fullscreen"}) {
            var control=Find<Control>(id); var b=ScreenRect(control);
            RequireButtonPixel(control,(b.Left+b.Right)/2,(b.Top+b.Bottom)/2);
        }
        RouteKey(side,Key.Home); RouteKey(bottom,Key.Home); window.UpdateLayout();
        Require(Rect(video).Width>small.Width && Rect(video).Height>small.Height,"Home/End did not bound the two drawer sizes.");
        Click("MaximizeWindow"); await Until(delegate{return IsZoomed(hwnd);},"Unable to return to maximized playback.");
        Drag(side,SideWidth-initialSide,0); Drag(bottom,0,BottomHeight-initialBottom);
        await Until(()=>Find<Grid>("SettingsSections").IsVisible && !compactPicker.IsVisible,
            "A wide drawer did not restore its section rail.");
        await CheckSettingsControls();
        Note("Both drawers resized and restored across drag, keyboard, hide/show, fullscreen, navigation and compact layout with an uninterrupted native video surface.");
    }

    static async Task CheckPairDrawer() {
        var source=Find<ComboBox>("SourceChoice"); source.SelectedItem="Direct to PS5";
        Click("ConsoleGroup"); await Until(delegate{return Find<Button>("AddConsole").IsVisible;},"Add console entry is unavailable.");
        Click("AddConsole"); await Until(delegate{return Find<StackPanel>("PairPanel").IsVisible;},"Pairing did not open inline.");
        Require(Find<TextBox>("PairCode").IsVisible && Find<Button>("CancelPair").IsEnabled,"Pairing controls are unreachable.");
        Click("CancelPair"); await Until(delegate{return !Find<StackPanel>("PairPanel").IsVisible;},"Pair cancellation failed.");
        source.SelectedItem="Offline demo";
        Note("The inline pairing form opened and canceled without a network request.");
    }

    static async Task SaveDrawerPreference() {
        window.WindowState=WindowState.Maximized; ShowWindow(hwnd,3);
        await Until(delegate{return IsZoomed(hwnd);},"Cannot maximize drawer fixture.");
        await SetDrawer("ShowStats",true); await SetDrawer("ShowSettings",true);
        Drag(Find<Thumb>("SidebarResize"),SideWidth-372,0);
        Drag(Find<Thumb>("SettingsResize"),0,BottomHeight-264);
        Require(Math.Abs(SideWidth-372)<2 && Math.Abs(BottomHeight-264)<2,"Cannot set distinct drawer preferences for relaunch.");
        File.WriteAllLines(DrawerSample,new[]{SideWidth.ToString("R",CultureInfo.InvariantCulture),BottomHeight.ToString("R",CultureInfo.InvariantCulture)});
        await SetDrawer("ShowStats",false); await SetDrawer("ShowSettings",false);
    }
    static async Task CheckSavedDrawers() {
        hwnd=new WindowInteropHelper(window).Handle;
        Require(Find<ToggleButton>("ShowStats").IsChecked==false && Find<ToggleButton>("ShowSettings").IsChecked==false,
                "A fresh process lost drawer visibility preferences.");
        var expected=File.ReadAllLines(DrawerSample);
        await SetDrawer("ShowStats",true); await SetDrawer("ShowSettings",true);
        await Until(delegate{return Math.Abs(SideWidth-double.Parse(expected[0],CultureInfo.InvariantCulture))<2 &&
                                    Math.Abs(BottomHeight-double.Parse(expected[1],CultureInfo.InvariantCulture))<2;},
                    "Reopening in a new process lost the saved drawer extents.");
        Note("A fresh process restored both collapsed drawers and their distinct expanded sizes.");
    }

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window,out Bounds bounds);
    static Drawing.Bitmap WindowImage() {
        Bounds b; Require(GetClientRect(hwnd,out b),"Cannot measure screenshot.");
        var image=new Drawing.Bitmap(b.Width,b.Height);
        try {
            using(var graphics=Drawing.Graphics.FromImage(image)) {
                var dc=graphics.GetHdc();
                try {Require(PrintWindow(hwnd,dc,3),"Could not capture the private WPF dashboard.");}
                finally {graphics.ReleaseHdc(dc);}
            }
            return image;
        } catch {image.Dispose(); throw;}
    }
    static bool HasDetail(Drawing.Bitmap image,Bounds area,int minimumColors) {
        var colors=new System.Collections.Generic.HashSet<int>();
        for(int y=area.Top+5;y<area.Bottom-5;y+=11)
            for(int x=area.Left+5;x<area.Right-5;x+=13) colors.Add(image.GetPixel(x,y).ToArgb());
        return colors.Count>=minimumColors;
    }
    static async Task SaveDashboardScreenshot() {
        var status=Find<TextBlock>("Status");
        Require(status.Text=="Disconnected" && Find<ComboBox>("SourceChoice").SelectedItem.ToString()=="Offline demo","Screenshot fixture must be offline.");
        window.WindowState=WindowState.Maximized; ShowWindow(hwnd,3);
        await Until(delegate{return IsZoomed(hwnd);},"Cannot maximize screenshot.");
        await SetDrawer("ShowStats",true); await SetDrawer("ShowSettings",true); Click("DisplayGroup");
        await Until(delegate{return Find<ComboBox>("VideoRenderer").IsVisible;},"Display settings did not open.");
        Find<ComboBox>("VideoRenderer").SelectedItem="Direct3D 11"; Find<ComboBox>("HdrOutput").SelectedItem="SDR";
        Click("OpenSource"); await Until(delegate{return status.Text=="Offline demo";},"Offline scene did not start for preview.");
        var meter=Find<Border>("MeterHost");
        var twice=(RadioButton)FindVisual(meter,"RadioButton",e=>Equals(((RadioButton)e).Content,"2×"));
        Require(twice!=null && twice.IsVisible && twice.IsEnabled,"Cannot select 2× interpolation for the demo screenshot.");
        twice.IsChecked=true;
        var sharp=(RadioButton)FindVisual(meter,"RadioButton",e=>Equals(((RadioButton)e).Content,"Sharpen"));
        sharp.IsChecked=true;
        string view=Environment.GetEnvironmentVariable("P5CAST_PROBE_WIPE_INPUT")=="1" ? "ViewWipe" : "ViewSideBySide";
        ViewButton(view).IsChecked=true;
        if(view=="ViewWipe") {
            var orientation=(ToggleButton)FindVisual(meter,"ToggleButton",e=>AutomationProperties.GetAutomationId(e)=="WipeHorizontal");
            orientation.IsChecked=true;
        }
        var dial=FindVisual(meter,"FrameRateDial");
        await Until(delegate {
            var match=System.Text.RegularExpressions.Regex.Match(AutomationProperties.GetHelpText(dial),@"Generated (\d+)");
            return match.Success && int.Parse(match.Groups[1].Value)>10;
        },"The demo did not generate enough frames to show both measured FPS segments.");
        if(CheckTransportOption) {
            Click("StatusHistoryButton");
            await Until(()=>Find<Border>("StatusHistorySheet").IsVisible,"History did not open for preview.");
            await Task.Delay(280);
        }
        window.UpdateLayout();
        string path=Environment.GetEnvironmentVariable("P5CAST_SCREENSHOT");
        var work=Screen().Work; var picture=Rect(video);
        var origin=new NativePoint {X=work.Left,Y=work.Top}; ScreenToClient(hwnd,ref origin);
        var pix=new NativePoint {X=picture.Left,Y=picture.Top}; ScreenToClient(hwnd,ref pix);
        bool live;
        var drawing=new DrawingVisual();
        using(var image=WindowImage()) {
            var inner=new Bounds {Left=pix.X,Top=pix.Y,Right=pix.X+picture.Width,Bottom=pix.Y+picture.Height};
            live=HasDetail(image,inner,40);
            if(!live) {
                Click("OpenSource"); await Until(delegate{return status.Text=="Disconnected";},"Preview did not stop.");
                await Task.Delay(150); window.UpdateLayout();
            }
            // On an inactive desktop PrintWindow captures the swap chain and WPF's
            // render bitmap captures the dashboard. Composite the two real surfaces
            // using their physical client bounds, including any video letterboxing.
            var content=(FrameworkElement)window.Content;
            using(var ctx=drawing.RenderOpen()) {
                var bounds=new Rect(0,0,work.Width,work.Height);
                ctx.DrawRectangle(window.Background,null,bounds);
                ctx.DrawRectangle(new VisualBrush(content),null,bounds);
                if(live) {
                    using(var native=image.Clone(new Drawing.Rectangle(pix.X,pix.Y,picture.Width,picture.Height),image.PixelFormat))
                    using(var stream=new MemoryStream()) {
                        native.Save(stream,Drawing.Imaging.ImageFormat.Png); stream.Position=0;
                        var pixels=BitmapFrame.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
                        ctx.DrawImage(pixels,new Rect(pix.X-origin.X,pix.Y-origin.Y,picture.Width,picture.Height));
                    }
                }
            }
        }
        var bitmap=new RenderTargetBitmap(work.Width,work.Height,96,96,PixelFormats.Pbgra32); bitmap.Render(drawing);
        var enc=new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(path)) enc.Save(stream);
        if(CheckTransportOption && Find<Border>("StatusHistorySheet").IsVisible) Click("CloseStatusHistory");
        if(live) {
            Click("OpenSource"); await Until(delegate{return status.Text=="Disconnected";},"Preview did not stop.");
        }
        Note("Screenshot captured from the "+(live?"playing offline demo":"disconnected dashboard")+" at "+path);
    }
}
