using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        Window playerWindow;
        bool playerFullscreen;
        WindowState playerRestoreState;
        WindowChrome playerChrome;
        KeyEventHandler playbackKey;

        void InitPopout()
        {
            UI<Button>("PopoutVideo").Click+=delegate {
                if(playerWindow==null) OpenPlayerWindow(); else playerWindow.Close();
            };
            UI<Button>("DockPlaceholder").Click+=delegate {if(playerWindow!=null) playerWindow.Close();};
            window.Closed+=delegate {if(playerWindow!=null) playerWindow.Close();};
        }

        void OpenPlayerWindow()
        {
            if(fullscreen) ToggleFullscreen();
            Window player;
            using(var xaml=typeof(Dashboard).Assembly.GetManifestResourceStream("P5Cast.PlayerWindow.xaml"))
                player=(Window)XamlReader.Load(xaml);
            player.Resources.MergedDictionaries.Add(window.Resources);
            player.Icon=window.Icon;
            player.FontFamily=window.FontFamily;
            player.FontSize=window.FontSize;
            var destination=(Border)player.FindName("PlayerSurface");
            var home=UI<Border>("VideoHost");
            ((Button)player.FindName("DockVideo")).Click+=delegate {player.Close();};
            ((Button)player.FindName("PlayerClose")).Click+=delegate {player.Close();};
            ((Button)player.FindName("PlayerMinimize")).Click+=delegate {SystemCommands.MinimizeWindow(player);};
            ((Button)player.FindName("PlayerFullscreen")).Click+=delegate {TogglePlayerFullscreen();};
            player.Deactivated+=delegate {surface.EndPointerGesture();};
            player.LocationChanged+=delegate {QueueInterfaceScale();};
            player.PreviewKeyDown+=playbackKey;
            player.PreviewMouseWheel+=delegate(object sender,MouseWheelEventArgs e) {
                if(surface.ZoomWheel(e.Delta)) e.Handled=true;
            };
            player.SourceInitialized+=delegate {
                HwndSource.FromHwnd(new WindowInteropHelper(player).Handle).AddHook(DisplayChanged);
                MoveDashboard();
            };
            player.Closing+=delegate {
                surface.EndPointerGesture();
                if(destination.Child==surface) destination.Child=null;
                playerWindow=null;
                playerFullscreen=false;
                if(closing || !window.IsVisible) return;
                home.Child=surface;
                if(window.WindowState==WindowState.Minimized) SystemCommands.RestoreWindow(window);
                SetPopoutControls(false);
                surface.ClipForModal(HistoryOpen);
                surface.ClipForProgress(OperationOpen ? UI<Border>("OperationCard") : null);
                SyncPlaybackDisplay(window);
                QueueInterfaceScale();
                window.Activate();
            };
            playerChrome=WindowChrome.GetWindowChrome(player);
            playerWindow=player;
            try {
                // Create the destination HWND before reparenting the existing HwndHost.
                player.Show();
                surface.EndPointerGesture();
                surface.ClipForProgress(null);
                surface.ClipForModal(false);
                home.Child=null;
                destination.Child=surface;
                player.UpdateLayout();
                SetPopoutControls(true);
                SyncPlaybackDisplay(player);
                UpdatePlayerScale();
            } catch {
                player.Close();
                throw;
            }
        }

        void SetPopoutControls(bool detached)
        {
            UI<Button>("DockPlaceholder").Visibility=detached ? Visibility.Visible : Visibility.Collapsed;
            var button=UI<Button>("PopoutVideo");
            string action=detached ? "Dock video" : "Pop out video";
            button.ToolTip=action;
            AutomationProperties.SetName(button,action);
            UI<System.Windows.Shapes.Path>("PopoutGlyph").Data=Geometry.Parse(detached ?
                "M8,3 H2 V15 H15 V9 M8,4 V10 H14 M8,10 L17,1" : "M8,3 H2 V15 H15 V9 M10,1 H17 V8 M17,1 L8,10");
            UI<Button>("Fullscreen").Content="Fullscreen";
        }

        void SyncPlaybackDisplay(Window host)
        {
            var hwnd=new WindowInteropHelper(host).Handle;
            if(hwnd!=IntPtr.Zero) RefreshDisplays(DisplayTarget.DeviceForWindow(hwnd));
            if(rendering!=null) engine.InvalidateDisplay();
        }

        void UpdatePlayerScale()
        {
            var source=PresentationSource.FromVisual(playerWindow) as HwndSource;
            var info=new FrameMonitor {Size=Marshal.SizeOf(typeof(FrameMonitor))};
            if(source==null || source.CompositionTarget==null || !GetMonitorInfo(MonitorFromWindow(source.Handle,2),ref info)) return;
            double dpi=source.CompositionTarget.TransformToDevice.M11;
            int width=info.Bounds.Right-info.Bounds.Left,height=info.Bounds.Bottom-info.Bounds.Top;
            double auto=Math.Min(Math.Max(width,height)/1920.0,Math.Min(width,height)/1080.0);
            double zoom=PreferredScalePercent==0 ? Math.Max(1,auto/dpi) : PreferredScalePercent/100.0;
            // Keep hit testing aligned to the physical pixels of the player monitor.
            surface.SetWipeScale((float)(dpi*(playerFullscreen ? 1 : zoom)));
            var header=(DockPanel)playerWindow.FindName("PlayerHeader");
            var scale=header.LayoutTransform as ScaleTransform;
            if(scale==null || scale.ScaleX!=zoom) header.LayoutTransform=new ScaleTransform(zoom,zoom);
            playerChrome.CaptionHeight=40*zoom;
        }

        void TogglePlayerFullscreen()
        {
            var player=playerWindow;
            if(player==null) return;
            surface.EndPointerGesture();
            var header=(DockPanel)player.FindName("PlayerHeader");
            if(!playerFullscreen) {
                playerRestoreState=player.WindowState==WindowState.Minimized ? WindowState.Normal : player.WindowState;
                player.WindowState=WindowState.Normal;
                WindowChrome.SetWindowChrome(player,null);
                player.WindowStyle=WindowStyle.None;
                header.Visibility=Visibility.Collapsed;
                player.WindowState=WindowState.Maximized;
            } else {
                player.WindowState=WindowState.Normal;
                player.WindowStyle=WindowStyle.SingleBorderWindow;
                WindowChrome.SetWindowChrome(player,playerChrome);
                header.Visibility=Visibility.Visible;
                player.WindowState=playerRestoreState;
            }
            playerFullscreen=!playerFullscreen;
            UI<Button>("Fullscreen").Content=playerFullscreen ? "Windowed" : "Fullscreen";
            UpdatePlayerScale();
        }
    }
}
