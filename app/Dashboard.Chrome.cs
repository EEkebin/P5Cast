using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        WindowChrome titleChrome;
        int sidebarTransition;
        [StructLayout(LayoutKind.Sequential)] struct FramePoint {public int X,Y;}
        [StructLayout(LayoutKind.Sequential)] struct FrameRect {public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)] struct FrameMonitor {public int Size; public FrameRect Bounds,Work; public uint Flags;}
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd,out FrameRect rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd,ref FramePoint point);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref FrameMonitor info);
        const int DwmTransitionsForceDisabled=3;
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int bytes);

        void SetFullscreenFrame(bool enter)
        {
            // Fullscreen uses a borderless maximized window. Suppress the native
            // restore/maximize animation only while changing this window's frame.
            var hwnd=new WindowInteropHelper(window).Handle;
            int disabled=1;
            bool restore=DwmSetWindowAttribute(hwnd,DwmTransitionsForceDisabled,ref disabled,sizeof(int))==0;
            try {
                if(enter) {
                    previousWindowState=window.WindowState;
                    if(previousWindowState!=WindowState.Normal) window.WindowState=WindowState.Normal;
                    WindowChrome.SetWindowChrome(window,null);
                    window.WindowStyle=WindowStyle.None;
                    window.WindowState=WindowState.Maximized;
                } else {
                    window.WindowState=WindowState.Normal;
                    window.WindowStyle=WindowStyle.SingleBorderWindow;
                    WindowChrome.SetWindowChrome(window,titleChrome);
                    if(previousWindowState!=WindowState.Normal) window.WindowState=previousWindowState;
                }
            } finally {
                disabled=0;
                if(restore) DwmSetWindowAttribute(hwnd,DwmTransitionsForceDisabled,ref disabled,sizeof(int));
            }
        }

        void UpdateFrameInsets()
        {
            var frame=UI<Grid>("WindowFrame"); var inset=new Thickness(0);
            if(!fullscreen && window.WindowState==WindowState.Maximized) {
                var source=PresentationSource.FromVisual(window) as HwndSource;
                if(source!=null && source.CompositionTarget!=null) {
                    var hwnd=source.Handle; var top=new FramePoint(); FrameRect client;
                    var monitor=new FrameMonitor {Size=Marshal.SizeOf(typeof(FrameMonitor))};
                    if(GetClientRect(hwnd,out client) && ClientToScreen(hwnd,ref top) && GetMonitorInfo(MonitorFromWindow(hwnd,2),ref monitor)) {
                        // A maximized custom frame can extend past the work area on any edge.
                        var dip=source.CompositionTarget.TransformFromDevice; var work=monitor.Work;
                        inset=new Thickness(Math.Max(0,work.Left-top.X)*dip.M11,Math.Max(0,work.Top-top.Y)*dip.M22,
                            Math.Max(0,top.X+client.Right-work.Right)*dip.M11,Math.Max(0,top.Y+client.Bottom-work.Bottom)*dip.M22);
                    }
                }
            }
            if(frame.Margin!=inset) frame.Margin=inset;
        }
        void InitTitleBar(bool sidebarOpen)
        {
            titleChrome=WindowChrome.GetWindowChrome(window);
            UI<Button>("MinimizeWindow").Click+=delegate {SystemCommands.MinimizeWindow(window);};
            UI<Button>("MaximizeWindow").Click+=delegate {
                if(window.WindowState==WindowState.Maximized) SystemCommands.RestoreWindow(window);
                else SystemCommands.MaximizeWindow(window);
            };
            UI<Button>("CloseWindow").Click+=delegate {window.Close();};
            window.StateChanged+=delegate {UpdateMaximizeButton(); UpdateFrameInsets();};
            window.LocationChanged+=delegate {UpdateFrameInsets();};
            window.SizeChanged+=delegate {UpdateFrameInsets();};
            window.Loaded+=delegate {UpdateFrameInsets();};
            UpdateMaximizeButton();
            stats=sidebarOpen; ApplySidebar(false);
            var toggle=UI<ToggleButton>("ShowStats");
            RoutedEventHandler change=delegate {
                bool open=toggle.IsChecked==true;
                if(stats==open) return;
                stats=open; ApplySidebar(true);
                if(window.IsLoaded) SaveSettings();
            };
            toggle.Checked+=change;
            toggle.Unchecked+=change;
        }
        void UpdateMaximizeButton()
        {
            bool maximized=window.WindowState==WindowState.Maximized;
            var button=UI<Button>("MaximizeWindow");
            string label=maximized?"Restore down":"Maximize";
            button.ToolTip=label; AutomationProperties.SetName(button,label);
            UI<Path>("MaximizeGlyph").Data=Geometry.Parse(maximized?"M2,1 V0 H10 V8 H9 M0,3 H7 V10 H0 Z":"M0,0 H10 V10 H0 Z");
        }
        static DoubleAnimation Motion(double from,double to)
        {
            return new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(170)) {
                EasingFunction=new QuadraticEase {EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop
            };
        }
        static void MoveStroke(Line line,int direction,bool open,bool animate)
        {
            var group=(TransformGroup)line.RenderTransform;
            var rotation=(RotateTransform)group.Children[0]; var offset=(TranslateTransform)group.Children[1];
            double angle=rotation.Angle,y=offset.Y;
            rotation.Angle=open?direction*45:0; offset.Y=open?0:-direction*3;
            rotation.BeginAnimation(RotateTransform.AngleProperty,animate?Motion(angle,open?direction*45:0):null);
            offset.BeginAnimation(TranslateTransform.YProperty,animate?Motion(y,open?0:-direction*3):null);
        }
        void ApplySidebar(bool animate)
        {
            animate=animate && window.IsLoaded && SystemParameters.ClientAreaAnimation && !fullscreen;
            var toggle=UI<ToggleButton>("ShowStats"); toggle.IsChecked=stats;
            toggle.ToolTip=(stats?"Hide":"Show")+" performance sidebar (Ctrl+Alt+O)";
            MoveStroke(UI<Line>("MenuTop"),1,stats,animate);
            MoveStroke(UI<Line>("MenuBottom"),-1,stats,animate);
            var column=UI<ColumnDefinition>("MeterColumn"); var panel=UI<Border>("MeterHost");
            var slide=(TranslateTransform)panel.RenderTransform;
            bool visible=stats && !fullscreen;
            int transition=++sidebarTransition;
            double x=slide.X,opacity=panel.Opacity;
            UI<Thumb>("SidebarResize").Visibility=visible?Visibility.Visible:Visibility.Collapsed;
            if(visible) {RefreshDrawerSizes(); panel.Visibility=Visibility.Visible;}
            panel.IsHitTestVisible=visible;
            double endX=visible?0:42,endOpacity=visible?1:0;
            slide.X=endX; panel.Opacity=endOpacity;
            slide.BeginAnimation(TranslateTransform.XProperty,animate?Motion(x,endX):null);
            var fade=animate?Motion(opacity,endOpacity):null;
            // Resize the D3D child once per toggle. The WPF sidebar handles the animation.
            if(!visible && animate) fade.Completed+=delegate {
                if(transition==sidebarTransition) {panel.Visibility=Visibility.Collapsed; column.Width=new GridLength(0);}
            };
            panel.BeginAnimation(UIElement.OpacityProperty,fade);
            if(!visible && !animate) {panel.Visibility=Visibility.Collapsed; column.Width=new GridLength(0);}
        }
    }
}
