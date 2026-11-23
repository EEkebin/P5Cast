using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        const int StatusHistoryLimit=120;
        readonly ObservableCollection<StatusEntry> statusHistory=new ObservableCollection<StatusEntry>();

        sealed class StatusEntry
        {
            public DateTime Timestamp {get; private set;}
            public string ClockTime {get {return Timestamp.ToLongTimeString();}}
            public string Message {get; private set;}
            public bool IsError {get; private set;}
            public StatusEntry(string message,bool error)
            {
                Timestamp=DateTime.Now; Message=message; IsError=error;
            }
            public override string ToString() {return ClockTime+" "+(IsError?"Error: ":"")+Message;}
        }

        void InitStatusHistory()
        {
            UI<ListBox>("StatusHistoryList").ItemsSource=statusHistory;
            var sheet=UI<Border>("StatusHistorySheet");
            UI<Button>("StatusHistoryButton").Click+=delegate {SetStatusHistory(sheet.Visibility!=Visibility.Visible);};
            UI<Button>("CloseStatusHistory").Click+=delegate {SetStatusHistory(false);};
            Show(UI<TextBlock>("Status").Text);
        }

        void SetStatusHistory(bool open)
        {
            var sheet=UI<Border>("StatusHistorySheet");
            var button=UI<Button>("StatusHistoryButton");
            sheet.Visibility=open?Visibility.Visible:Visibility.Collapsed;
            ((RotateTransform)UI<Path>("HistoryChevron").RenderTransform).Angle=open?180:0;
            button.ToolTip=open?"Hide status history":"Show status history";
            AutomationProperties.SetName(button,(string)button.ToolTip);
            if(open) {
                var list=UI<ListBox>("StatusHistoryList");
                if(statusHistory.Count>0) list.ScrollIntoView(statusHistory[0]);
                UI<Button>("CloseStatusHistory").Focus();
            } else button.Focus();
        }

        void Show(string message,bool error=false)
        {
            if(string.IsNullOrWhiteSpace(message)) return;
            var last=statusHistory.Count==0?null:statusHistory[0];
            if(last!=null && last.Message==message && last.IsError==error) return;
            var list=UI<ListBox>("StatusHistoryList");
            if(list.IsVisible) list.ApplyTemplate();
            var scroll=list.IsVisible ? list.Template.FindName("HistoryScroll",list) as ScrollViewer : null;
            bool follow=scroll!=null && scroll.VerticalOffset<=0;
            statusHistory.Insert(0,new StatusEntry(message,error));
            if(statusHistory.Count>StatusHistoryLimit) statusHistory.RemoveAt(StatusHistoryLimit);
            if(follow) list.ScrollIntoView(statusHistory[0]);

            var current=UI<TextBlock>("Status");
            var previous=UI<TextBlock>("PreviousStatus");
            var enter=(TranslateTransform)current.RenderTransform;
            var leave=(TranslateTransform)previous.RenderTransform;
            var button=UI<Button>("StatusHistoryButton");
            // A second update can arrive mid-transition. Keep the more visible outgoing line.
            if(current.Opacity>=previous.Opacity) {
                previous.Text=current.Text;
                ShiftStatus(previous,leave,current.Opacity,0,enter.Y,-10);
            } else ShiftStatus(previous,leave,previous.Opacity,0,leave.Y,-10);
            current.Text=message;
            current.ToolTip=message;
            AutomationProperties.SetItemStatus(button,message);
            UI<Ellipse>("Indicator").SetResourceReference(Shape.FillProperty,error?"Theme.Error":"Theme.Accent");
            ShiftStatus(current,enter,0,1,10,0);
        }

        void ShiftStatus(TextBlock line,TranslateTransform offset,double fromOpacity,double toOpacity,double fromY,double toY)
        {
            line.Opacity=toOpacity; offset.Y=toY;
            bool animate=window.IsLoaded && line.IsVisible && SystemParameters.ClientAreaAnimation;
            line.BeginAnimation(UIElement.OpacityProperty,animate?Motion(fromOpacity,toOpacity):null);
            offset.BeginAnimation(TranslateTransform.YProperty,animate?Motion(fromY,toY):null);
        }
    }
}
