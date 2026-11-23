using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        const double HeaderHeight=52, MinimumLayoutWidth=880, MinimumLayoutHeight=590;
        static readonly int[] ScaleChoices={0,75,100,125,150,175,200};
        readonly ScaleTransform interfaceTransform=new ScaleTransform();
        bool scalePending;

        int PreferredScalePercent {
            get {return ScaleChoices[Math.Max(0,UI<ComboBox>("InterfaceScale").SelectedIndex)];}
        }

        void InitInterfaceScale(Dictionary<string,object> saved)
        {
            var choice=UI<ComboBox>("InterfaceScale");
            choice.ItemsSource=ScaleChoices.Select(p=>p==0?"Auto":p+"%").ToArray();
            int percent;
            choice.SelectedIndex=int.TryParse(Field(saved,"uiScale"),out percent)?
                Math.Max(0,Array.IndexOf(ScaleChoices,percent)):0;
            UI<Grid>("InterfaceRoot").LayoutTransform=interfaceTransform;
            choice.SelectionChanged+=delegate {
                QueueInterfaceScale();
                if(window.IsLoaded) SaveSettings();
            };
            window.LocationChanged+=delegate {QueueInterfaceScale();};
            window.SizeChanged+=delegate {QueueInterfaceScale();};
            window.Loaded+=delegate {QueueInterfaceScale();};
        }

        void QueueInterfaceScale()
        {
            if(scalePending || closing) return;
            scalePending=true;
            // WPF commits the new native DPI and work-area insets before measuring.
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(delegate {
                scalePending=false; ApplyInterfaceScale();
            }));
        }

        void ApplyInterfaceScale()
        {
            var source=PresentationSource.FromVisual(window) as HwndSource;
            if(source==null || source.CompositionTarget==null || window.WindowState==WindowState.Minimized) return;
            var info=new FrameMonitor {Size=Marshal.SizeOf(typeof(FrameMonitor))};
            FrameRect client;
            if(!GetMonitorInfo(MonitorFromWindow(source.Handle,2),ref info) || !GetClientRect(source.Handle,out client)) return;
            double dpi=source.CompositionTarget.TransformToDevice.M11;
            if(dpi<=0) return;
            double workWidth=(info.Work.Right-info.Work.Left)/dpi,workHeight=(info.Work.Bottom-info.Work.Top)/dpi;
            window.MinWidth=Math.Min(MinimumLayoutWidth,workWidth);
            window.MinHeight=Math.Min(MinimumLayoutHeight,workHeight);
            if(!fullscreen && window.WindowState==WindowState.Normal) {
                if(window.Width>workWidth) window.Width=workWidth;
                if(window.Height>workHeight) window.Height=workHeight;
            }
            double width=info.Bounds.Right-info.Bounds.Left,height=info.Bounds.Bottom-info.Bounds.Top;
            double monitorScale=Math.Min(Math.Max(width,height)/1920.0,Math.Min(width,height)/1080.0);
            double requested=PreferredScalePercent==0?Math.Max(1,monitorScale/dpi):PreferredScalePercent/100.0;
            var inset=UI<Grid>("WindowFrame").Margin;
            double availableWidth=(client.Right-client.Left)/dpi-inset.Left-inset.Right;
            double availableHeight=(client.Bottom-client.Top)/dpi-inset.Top-inset.Bottom;
            if(availableWidth<=0 || availableHeight<=0) return;
            double fit=Math.Min(availableWidth/MinimumLayoutWidth,availableHeight/MinimumLayoutHeight);
            double zoom=fullscreen?1:Math.Min(requested,fit);
            if(Math.Abs(interfaceTransform.ScaleX-zoom)>0.00001) {
                interfaceTransform.ScaleX=zoom; interfaceTransform.ScaleY=zoom;
                titleChrome.CaptionHeight=HeaderHeight*zoom;
            }
            int shown=(int)Math.Round(zoom*100),windows=(int)Math.Round(dpi*100);
            UI<TextBlock>("ScaleStatus").Text=shown+"% interface scale · "+windows+"% Windows scale"+
                (zoom+0.005<requested && !fullscreen?" · fitted to this window":"");
        }
    }
}
