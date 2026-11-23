using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Cadrivect
{
    internal sealed class PlaybackMeter : UserControl
    {
        static readonly string[] ModeIds={"original","sharp","fsr","rtxvideo","dlss","dlaa"};
        static readonly string[] ModeLabels={"Original","Sharpen","AMD FSR 1","RTX Video SR","DLSS","DLAA"};
        static readonly string[] Descriptions={"Display the source picture.","Sharpen edges and upscale fine detail.","AMD FidelityFX Super Resolution 1.",
            "NVIDIA RTX Video Super Resolution for SDR video.","Experimental DLSS with motion estimated from the stream.","Experimental DLAA at the source resolution."};
        static readonly Brush Ink=ColorBrush("#E9F3F1"), Muted=ColorBrush("#A5B9BB"), Line=ColorBrush("#304147"), Dark=ColorBrush("#141F24");
        static readonly Brush[] Series={ColorBrush("#ADBBC0"),ColorBrush("#8DBAF6"),ColorBrush("#8FE6D0"),ColorBrush("#EABC8E")};
        static readonly ControlTemplate ChipTemplate=MakeChipTemplate();
        static readonly ControlTemplate GroupTemplate=MakeGroupTemplate();
        static readonly Style ScrollBarStyle=(Style)XamlReader.Parse(
            @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>
                <Setter Property='Width' Value='9'/>
                <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
                  <Border Background='#141F24' Padding='2,3'>
                    <Track Name='PART_Track' Orientation='Vertical' IsDirectionReversed='True'>
                      <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0'/></Track.DecreaseRepeatButton>
                      <Track.Thumb><Thumb MinHeight='18'><Thumb.Template><ControlTemplate TargetType='Thumb'>
                        <Border Background='#52696E' CornerRadius='3' Width='5' HorizontalAlignment='Center'/>
                      </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                      <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0'/></Track.IncreaseRepeatButton>
                    </Track>
                  </Border>
                </ControlTemplate></Setter.Value></Setter>
              </Style>");
        readonly Action<int,int> change;
        readonly Action<bool> compare;
        readonly RadioButton[] modes=new RadioButton[6], factors=new RadioButton[4];
        readonly TextBlock sourceLabel,sourceValue,addedValue,targetValue;
        readonly Dictionary<string,TextBlock> values=new Dictionary<string,TextBlock>();
        readonly TextBlock submitLabel,notice;
        readonly ToggleButton compareButton;
        readonly FrameRateDial dial=new FrameRateDial();
        Dictionary<string,object> gpu;
        int selectedMode,selectedFactor=1;
        bool syncing,hdr,directSelected;
        int requestedVideoKbps;
        long lastSample=-1;
        DateTime pendingSettings,pendingCompare;

        public PlaybackMeter(Action<int,int> change,Action<bool> compare)
        {
            this.change=change; this.compare=compare;
            UseLayoutRounding=true; SnapsToDevicePixels=true; Background=Dark;
            Foreground=Ink; FontFamily=new FontFamily("Segoe UI"); FontSize=12;
            var layout=new DockPanel();
            var fixedMeter=new StackPanel {Margin=new Thickness(16,14,16,0)};
            DockPanel.SetDock(fixedMeter,Dock.Top); layout.Children.Add(fixedMeter);
            var sections=new StackPanel {Margin=new Thickness(16,0,12,14)};
            var title=Heading("Frame rate"); title.Margin=new Thickness(0);
            var heading=new DockPanel();
            targetValue=Label("Target —",Muted,11,FontWeights.SemiBold);
            targetValue.ToolTip="The white tick marks the requested output frame rate.";
            DockPanel.SetDock(targetValue,Dock.Right);
            heading.Children.Add(targetValue); heading.Children.Add(title); fixedMeter.Children.Add(heading);
            dial.Height=133; dial.Margin=new Thickness(0,2,0,6); fixedMeter.Children.Add(dial);
            var legend=new UniformGrid {Rows=1,Columns=2}; fixedMeter.Children.Add(legend);
            sourceLabel=Label("Source",Muted,11,FontWeights.Normal);
            sourceValue=RateReadout(legend,sourceLabel,Series[1],"Frames received each second from the selected source.");
            addedValue=RateReadout(legend,Label("Added",Muted,11,FontWeights.Normal),Series[3],"Extra frames P5Cast created and sent to the screen each second.");
            Separator(fixedMeter);
            var picture=AddGroup(sections,"Picture","PictureGroup");
            string group=Guid.NewGuid().ToString("N");
            var modeGrid=new UniformGrid {Rows=3,Columns=2,Margin=new Thickness(-2,1,-2,0)};
            picture.Children.Add(modeGrid);
            for(int i=0;i<6;i++)
            {
                var button=new RadioButton {GroupName="Picture"+group,Content=ModeLabels[i],Template=ChipTemplate};
                StyleChip(button); modes[i]=button; int index=i;
                button.Checked+=delegate {
                    if(syncing) return;
                    selectedMode=index; SendSettings();
                };
                modeGrid.Children.Add(button);
            }
            var interpolation=Label("Frame interpolation",Muted,11,FontWeights.SemiBold);
            interpolation.Margin=new Thickness(0,9,0,2); picture.Children.Add(interpolation);
            var factorGrid=new UniformGrid {Rows=1,Columns=4,Margin=new Thickness(-2,0,-2,0)};
            picture.Children.Add(factorGrid);
            for(int i=0;i<4;i++)
            {
                int factor=i+1;
                var button=new RadioButton {GroupName="Rate"+group,Content=i==0 ? "Off" : factor+"×",Template=ChipTemplate};
                StyleChip(button); factors[i]=button;
                button.ToolTip=i==0 ? "Present captured frames only." : "Present up to "+factor+" frames per source frame, including interpolated frames.";
                button.Checked+=delegate {if(syncing) return; selectedFactor=factor; SendSettings();};
                factorGrid.Children.Add(button);
            }
            compareButton=new ToggleButton {Content="Compare original",Template=ChipTemplate,
                ToolTip="Temporarily show the original picture with interpolation off."};
            StyleChip(compareButton); compareButton.Margin=new Thickness(0,8,0,0); picture.Children.Add(compareButton);
            compareButton.Click+=delegate {
                if(syncing) return;
                pendingCompare=DateTime.UtcNow.AddSeconds(2); if(compare!=null) compare(compareButton.IsChecked==true);
            };
            notice=Label("",Series[3],11,FontWeights.Normal); notice.TextWrapping=TextWrapping.Wrap; notice.Margin=new Thickness(0,5,0,0);
            notice.Visibility=Visibility.Collapsed; picture.Children.Add(notice);
            Separator(sections);
            var diagnostics=AddGroup(sections,"Stream diagnostics","StreamDiagnosticsGroup");
            Subheading(diagnostics,"Video");
            AddMetric(diagnostics,"size","Input → output","Incoming frame size and presentation surface size in pixels.");
            AddMetric(diagnostics,"format","Video format","Source codec and active output color mode.");
            AddMetric(diagnostics,"requested","Requested bitrate","Configured bitrate requested from the PS5 when connecting.");
            AddMetric(diagnostics,"bitrate","Received bitrate","Measured compressed video arriving from the PS5.");
            AddMetric(diagnostics,"decode","Video decode","Time spent decoding the latest video frame.");
            submitLabel=AddMetric(diagnostics,"submit","Receive → submit","Time from local frame receipt or capture to submission for presentation.");
            Subheading(diagnostics,"Audio");
            AddMetric(diagnostics,"audioFormat","Stream format","Negotiated sample rate and channel count for the incoming audio.");
            AddMetric(diagnostics,"audioBitrate","Received bitrate","Measured compressed audio arriving from the PS5.");
            AddMetric(diagnostics,"queue","Audio queue","Audio buffered locally, excluding the Windows mixer and device buffers.");
            AddMetric(diagnostics,"audioTarget","Queue target","Requested local audio preroll.");
            AddMetric(diagnostics,"underruns","Queue underruns","Observed audio queue underruns during the session.");
            Subheading(diagnostics,"Network");
            AddMetric(diagnostics,"rtt","Handshake RTT","Round-trip estimate from session setup.");
            var scrolling=new ScrollViewer {Content=sections,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            scrolling.Resources.Add(typeof(ScrollBar),ScrollBarStyle);
            layout.Children.Add(scrolling);
            Content=layout;
            Reset(0,1);
        }
        public void SetSource(string source)
        {
            directSelected=source=="Direct";
            sourceLabel.Text=source=="Direct"?"From PS5":source=="Sony"?"From Sony app":"From demo";
            submitLabel.Text=directSelected ? "Receive → submit" : source=="Sony" ? "Capture → submit" : "Frame → submit";
            ShowRequested(directSelected);
        }
        public void SetRequestedVideoKbps(int kbps) {requestedVideoKbps=Math.Max(0,kbps); ShowRequested(directSelected);}
        void ShowRequested(bool direct) {values["requested"].Text=direct && requestedVideoKbps>0 ? Metric(requestedVideoKbps/1000.0," Mb/s","0.##") : "—";}
        static TextBlock RateReadout(Panel legend,TextBlock caption,Brush color,string detail)
        {
            var cell=new DockPanel {Margin=new Thickness(0,1,9,2),ToolTip=detail};
            var chip=new Line {X1=0,X2=8,Y1=0,Y2=0,Stroke=color,StrokeThickness=3,
                StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,
                Margin=new Thickness(0,0,5,0),VerticalAlignment=VerticalAlignment.Center};
            var value=Label("—",Ink,12,FontWeights.SemiBold);
            DockPanel.SetDock(chip,Dock.Left); DockPanel.SetDock(value,Dock.Right);
            cell.Children.Add(chip); cell.Children.Add(value); cell.Children.Add(caption); legend.Children.Add(cell);
            return value;
        }
        static SolidColorBrush ColorBrush(string color)
        {
            var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
        }
        static TextBlock Label(string text,Brush foreground,double size,FontWeight weight)
        {
            return new TextBlock {Text=text,Foreground=foreground,FontSize=size,FontWeight=weight,VerticalAlignment=VerticalAlignment.Center};
        }
        static TextBlock Heading(string text)
        {
            return new TextBlock {Text=text,Foreground=Ink,FontSize=15,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,5)};
        }
        static void Separator(Panel body)
        {
            body.Children.Add(new Border {Height=1,Background=Line,Margin=new Thickness(0,10,0,9)});
        }
        static StackPanel AddGroup(Panel parent,string title,string id)
        {
            var content=new StackPanel();
            var group=new Expander {Header=title,Content=content,IsExpanded=true,Template=GroupTemplate};
            AutomationProperties.SetName(group,title); AutomationProperties.SetAutomationId(group,id);
            parent.Children.Add(group); return content;
        }
        static void Subheading(Panel parent,string title)
        {
            var label=Label(title,Series[2],12,FontWeights.SemiBold);
            label.Margin=new Thickness(0,8,0,2); parent.Children.Add(label);
        }
        static ControlTemplate MakeGroupTemplate()
        {
            var surface=new FrameworkElementFactory(typeof(Border),"Surface");
            surface.SetValue(Border.BackgroundProperty,Brushes.Transparent); surface.SetValue(Border.CornerRadiusProperty,new CornerRadius(4));
            surface.SetValue(Border.PaddingProperty,new Thickness(0,2,4,4));
            var row=new FrameworkElementFactory(typeof(DockPanel)); surface.AppendChild(row);
            var arrow=new FrameworkElementFactory(typeof(Path),"Arrow");
            arrow.SetValue(DockPanel.DockProperty,Dock.Right); arrow.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
            arrow.SetValue(Shape.StrokeProperty,Muted); arrow.SetValue(Shape.StrokeThicknessProperty,1.6);
            arrow.SetValue(Path.DataProperty,Geometry.Parse("M 3,1 L 8,6 L 3,11")); row.AppendChild(arrow);
            var caption=new FrameworkElementFactory(typeof(ContentPresenter)); row.AppendChild(caption);
            var titleTemplate=new ControlTemplate(typeof(ToggleButton)) {VisualTree=surface};
            var open=new Trigger {Property=ToggleButton.IsCheckedProperty,Value=true};
            open.Setters.Add(new Setter(Path.DataProperty,Geometry.Parse("M 1,3 L 6,8 L 11,3"),"Arrow")); titleTemplate.Triggers.Add(open);
            var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
            hover.Setters.Add(new Setter(Border.BackgroundProperty,ColorBrush("#213237"),"Surface")); titleTemplate.Triggers.Add(hover);

            var stack=new FrameworkElementFactory(typeof(StackPanel));
            var toggle=new FrameworkElementFactory(typeof(ToggleButton));
            toggle.SetValue(Control.TemplateProperty,titleTemplate); toggle.SetValue(Control.ForegroundProperty,Ink);
            toggle.SetValue(Control.FontSizeProperty,15.0); toggle.SetValue(Control.FontWeightProperty,FontWeights.SemiBold);
            toggle.SetValue(ContentControl.ContentProperty,new TemplateBindingExtension(HeaderedContentControl.HeaderProperty));
            toggle.SetBinding(ToggleButton.IsCheckedProperty,new Binding("IsExpanded") {RelativeSource=RelativeSource.TemplatedParent,Mode=BindingMode.TwoWay});
            stack.AppendChild(toggle);
            var body=new FrameworkElementFactory(typeof(ContentPresenter),"Body"); body.SetValue(ContentPresenter.ContentSourceProperty,"Content");
            stack.AppendChild(body);
            var template=new ControlTemplate(typeof(Expander)) {VisualTree=stack};
            var collapsed=new Trigger {Property=Expander.IsExpandedProperty,Value=false};
            collapsed.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed,"Body")); template.Triggers.Add(collapsed);
            return template;
        }
        TextBlock AddMetric(Panel body,string id,string name,string detail)
        {
            var row=new Grid {Margin=new Thickness(0,2,0,2),ToolTip=detail};
            row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
            var label=Label(name,Muted,11,FontWeights.Normal); row.Children.Add(label);
            var metric=Label("—",Ink,11,FontWeights.SemiBold); metric.Margin=new Thickness(5,0,0,0);
            metric.TextAlignment=TextAlignment.Right; metric.TextTrimming=TextTrimming.CharacterEllipsis;
            AutomationProperties.SetAutomationId(metric,"Metric_"+id);
            Grid.SetColumn(metric,1); row.Children.Add(metric); values.Add(id,metric); body.Children.Add(row);
            return label;
        }
        static ControlTemplate MakeChipTemplate()
        {
            var surface=new FrameworkElementFactory(typeof(Border),"Surface");
            surface.SetValue(Border.CornerRadiusProperty,new CornerRadius(5));
            surface.SetValue(Border.BorderThicknessProperty,new Thickness(1));
            surface.SetValue(Border.BackgroundProperty,ColorBrush("#1D2B31")); surface.SetValue(Border.BorderBrushProperty,Line);
            var content=new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);
            surface.AppendChild(content);
            var template=new ControlTemplate(typeof(ToggleButton)) {VisualTree=surface};
            var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
            hover.Setters.Add(new Setter(Border.BorderBrushProperty,ColorBrush("#70918E"),"Surface")); template.Triggers.Add(hover);
            var selected=new Trigger {Property=ToggleButton.IsCheckedProperty,Value=true};
            selected.Setters.Add(new Setter(Border.BackgroundProperty,ColorBrush("#263F3D"),"Surface"));
            selected.Setters.Add(new Setter(Border.BorderBrushProperty,ColorBrush("#557D74"),"Surface"));
            selected.Setters.Add(new Setter(Control.ForegroundProperty,Series[2])); template.Triggers.Add(selected);
            var focus=new Trigger {Property=UIElement.IsKeyboardFocusedProperty,Value=true};
            focus.Setters.Add(new Setter(Border.BorderBrushProperty,Series[2],"Surface")); template.Triggers.Add(focus);
            var disabled=new Trigger {Property=UIElement.IsEnabledProperty,Value=false};
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.44,"Surface")); template.Triggers.Add(disabled);
            return template;
        }
        static void StyleChip(ToggleButton chip)
        {
            chip.Height=28; chip.Margin=new Thickness(2); chip.FontSize=11; chip.Foreground=Ink;
            chip.FontWeight=FontWeights.SemiBold; ToolTipService.SetShowOnDisabled(chip,true);
            AutomationProperties.SetName(chip,chip.Content.ToString());
        }
        void SendSettings()
        {
            pendingSettings=DateTime.UtcNow.AddSeconds(2);
            if(change!=null) change(selectedMode,selectedFactor);
        }
        public void Reset(int modeIndex,int multiplier)
        {
            selectedMode=Math.Max(0,Math.Min(5,modeIndex)); selectedFactor=Math.Max(1,Math.Min(4,multiplier));
            syncing=true;
            try {modes[selectedMode].IsChecked=true; factors[selectedFactor-1].IsChecked=true; compareButton.IsChecked=false;}
            finally {syncing=false;}
            hdr=false; lastSample=-1; pendingSettings=pendingCompare=DateTime.MinValue; dial.SetRates(double.NaN,double.NaN,double.NaN,double.NaN);
            sourceValue.Text=addedValue.Text="—"; targetValue.Text="Target —";
            foreach(var label in values.Values) label.Text="—";
            ShowRequested(directSelected);
            notice.Visibility=Visibility.Collapsed; RefreshAvailability();
        }
        public void SetCapabilities(Dictionary<string,object> gpu)
        {
            this.gpu=gpu; RefreshAvailability();
        }
        void RefreshAvailability()
        {
            bool noAdapter=string.Equals(Text(gpu,"adapter"),"Unavailable",StringComparison.OrdinalIgnoreCase);
            for(int i=0;i<modes.Length;i++)
            {
                bool vendor=i>=3;
                bool available=i==0 || (!noAdapter && (!vendor || Flag(gpu,ModeIds[i])==true));
                string reason=Descriptions[i];
                if(!available) reason=gpu==null ? "Checking GPU support." : Text(gpu,ModeIds[i]+"Reason") ?? "This mode is unavailable on the active GPU.";
                if(i==3 && hdr) {available=false; reason="RTX Video SR requires SDR output.";}
                modes[i].IsEnabled=available; modes[i].ToolTip=reason;
            }
            for(int i=0;i<factors.Length;i++) factors[i].IsEnabled=i==0 || !noAdapter;
        }
        static object Field(Dictionary<string,object> data,string key)
        {
            object value; return data!=null && data.TryGetValue(key,out value) ? value : null;
        }
        static string Text(Dictionary<string,object> data,string key) {return Field(data,key) as string;}
        static bool? Flag(Dictionary<string,object> data,string key)
        {
            object value=Field(data,key);
            if(value is bool) return (bool)value;
            bool parsed; return bool.TryParse(value as string,out parsed) ? (bool?)parsed : null;
        }
        static double Number(Dictionary<string,object> data,string key)
        {
            object raw=Field(data,key); double number;
            if(raw==null || !double.TryParse(Convert.ToString(raw,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out number) ||
               number<0 || double.IsInfinity(number)) return double.NaN;
            return number;
        }
        static string Metric(double value,string unit,string format)
        {
            return double.IsNaN(value) ? "—" : value.ToString(format,CultureInfo.CurrentCulture)+unit;
        }
        static string Dimensions(Dictionary<string,object> data,string prefix)
        {
            double w=Number(data,prefix+"Width"), h=Number(data,prefix+"Height");
            return w>0 && h>0 ? w.ToString("0")+"×"+h.ToString("0") : "—";
        }
        public void Update(Dictionary<string,object> snapshot)
        {
            if(snapshot==null) return;
            double id=Number(snapshot,"sampleId");
            if(!double.IsNaN(id) && (long)id<=lastSample) return;
            if(!double.IsNaN(id)) lastSample=(long)id;
            double[] rates={Number(snapshot,"targetPresentedFps"),Number(snapshot,"sourceFps"),Number(snapshot,"submittedFps"),Number(snapshot,"generatedFps")};
            // The engine can publish telemetry while waiting for its first picture.
            if(Number(snapshot,"captured")==0) for(int i=0;i<rates.Length;i++) rates[i]=double.NaN;
            targetValue.Text="Target "+Metric(rates[0],"","0.#");
            sourceValue.Text=Metric(rates[1],"","0.#"); addedValue.Text=Metric(rates[3],"","0.#");
            dial.SetRates(rates[0],rates[1],rates[2],rates[3]);
            string input=Dimensions(snapshot,"input"),output=Dimensions(snapshot,"output");
            values["size"].Text=input=="—" && output=="—" ? "—" : input+" → "+output;
            string codec=Text(snapshot,"codec"); bool? hdrFlag=Flag(snapshot,"hdrEnabled");
            string color=hdrFlag.HasValue ? (hdrFlag.Value ? "HDR10":"SDR") : "";
            values["format"].Text=string.IsNullOrEmpty(codec) ? (color.Length==0 ? "—":color) : codec+(color.Length==0 ? "" : " · "+color);
            values["bitrate"].Text=Metric(Number(snapshot,"bitrateMbps")," Mb/s","0.0");
            values["decode"].Text=Metric(Number(snapshot,"decodeMs")," ms","0.0");
            string kind=Text(snapshot,"sourceKind");
            double requestedKbps=Number(snapshot,"requestedVideoKbps");
            if(requestedKbps>0 && requestedKbps<=int.MaxValue) requestedVideoKbps=(int)Math.Round(requestedKbps);
            ShowRequested(kind==null ? directSelected : kind=="direct");
            submitLabel.Text=kind=="window" ? "Capture → submit" : kind=="demo" ? "Frame → submit" : "Receive → submit";
            values["submit"].Text=Metric(Number(snapshot,"lastPcSubmitMs")," ms","0.0");
            values["queue"].Text=Metric(Number(snapshot,"audioQueueMs")," ms","0.#");
            values["audioBitrate"].Text=Metric(Number(snapshot,"audioBitrateKbps")," kb/s","0.#");
            double sampleRate=Number(snapshot,"audioSampleRate"),channels=Number(snapshot,"audioChannels");
            string format=sampleRate>0 ? Metric(sampleRate/1000.0," kHz","0.###") : "";
            if(channels>0) format+=(format.Length==0 ? "" : " · ")+(channels==1 ? "Mono":channels==2 ? "Stereo":Metric(channels," ch","0"));
            values["audioFormat"].Text=format.Length==0 ? "—" : format;
            values["audioTarget"].Text=Metric(Number(snapshot,"audioTargetMs")," ms","0.#");
            values["underruns"].Text=Metric(Number(snapshot,"audioUnderruns"),"","0");
            values["rtt"].Text=Metric(Number(snapshot,"handshakeRttMs")," ms","0.0");
            if(hdrFlag.HasValue && hdr!=hdrFlag.Value) {hdr=hdrFlag.Value; RefreshAvailability();}
            string requested=Text(snapshot,"mode"), effective=Text(snapshot,"effectivePictureMode");
            int mode=Array.IndexOf(ModeIds,requested); double factor=Number(snapshot,"multiplier"); bool? comparing=Flag(snapshot,"compare");
            syncing=true;
            try {
                if(DateTime.UtcNow>=pendingSettings || (mode==selectedMode && factor==selectedFactor)) {
                    if(mode>=0) {selectedMode=mode; modes[mode].IsChecked=true;}
                    if(factor>=1 && factor<=4) {selectedFactor=(int)factor; factors[selectedFactor-1].IsChecked=true;}
                    pendingSettings=DateTime.MinValue;
                }
                if(comparing.HasValue && (DateTime.UtcNow>=pendingCompare || comparing==compareButton.IsChecked)) {
                    compareButton.IsChecked=comparing; pendingCompare=DateTime.MinValue;
                }
            } finally {syncing=false;}
            string message=Text(snapshot,"effectNotice");
            int active=Array.IndexOf(ModeIds,effective);
            if(string.IsNullOrEmpty(message) && comparing!=true && active>=0 && mode>=0 && active!=mode) message="Active picture · "+ModeLabels[active];
            notice.Text=message ?? ""; notice.Visibility=string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        sealed class FrameRateDial : FrameworkElement
        {
            const double Maximum=240;
            static readonly Brush Track=ColorBrush("#2B3D43"), Tick=ColorBrush("#5C7378");
            static readonly Pen MinorTick=new Pen(Tick,1),MajorTick=new Pen(Muted,1.3);
            static readonly Typeface NumberFace=new Typeface(new FontFamily("Segoe UI"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal);
            static readonly Typeface LabelFace=new Typeface("Segoe UI");
            double target,source,presented,generated;
            internal FrameRateDial()
            {
                AutomationProperties.SetAutomationId(this,"FrameRateDial");
                AutomationProperties.SetName(this,"Frame rate dial");
                SetRates(double.NaN,double.NaN,double.NaN,double.NaN);
            }
            protected override AutomationPeer OnCreateAutomationPeer() {return new DialPeer(this);}
            sealed class DialPeer : FrameworkElementAutomationPeer
            {
                public DialPeer(FrameRateDial owner):base(owner) {}
                protected override bool IsControlElementCore() {return true;}
                protected override bool IsContentElementCore() {return true;}
                protected override string GetClassNameCore() {return "FrameRateDial";}
                protected override AutomationControlType GetAutomationControlTypeCore() {return AutomationControlType.Custom;}
            }
            static string Describe(double rate) {return double.IsNaN(rate) ? "unavailable" : Metric(rate," FPS","0.#");}
            public void SetRates(double aim,double input,double submitted,double synthetic)
            {
                target=aim; source=input; presented=submitted; generated=synthetic;
                string detail="Output "+Describe(presented)+". Source "+Describe(source)+". Added "+Describe(generated)+". Target "+Describe(target)+".";
                AutomationProperties.SetHelpText(this,detail); ToolTip=detail; InvalidateVisual();
            }
            // All tracks use the same half-circle and zero-to-240 scale, including at 30 fps.
            static Point Polar(Point hub,double radius,double angle)
            {
                return new Point(hub.X-radius*Math.Cos(angle),hub.Y-radius*Math.Sin(angle));
            }
            static double Angle(double fps) {return Math.PI*Math.Min(1,Math.Max(0,fps/Maximum));}
            static void Arc(DrawingContext dc,Point hub,double r,double angle,Brush color,double width)
            {
                var pen=new Pen(color,width){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
                var shape=new StreamGeometry();
                using(var g=shape.Open()) {
                    g.BeginFigure(Polar(hub,r,0),false,false);
                    g.ArcTo(Polar(hub,r,angle),new Size(r,r),0,false,SweepDirection.Clockwise,true,false);
                }
                dc.DrawGeometry(null,pen,shape);
            }
            static void TrackValue(DrawingContext dc,Point hub,double r,double value,Brush brush)
            {
                Arc(dc,hub,r,Math.PI,Track,4);
                if(double.IsNaN(value)) return;
                double angle=Angle(value); Point tip=Polar(hub,r,angle);
                if(value>0) {
                    Arc(dc,hub,r,angle,brush,4);
                    dc.DrawEllipse(brush,null,tip,3.2,3.2);
                } else dc.DrawEllipse(Dark,new Pen(brush,1.5),tip,2.3,2.3);
            }
            void CenterText(DrawingContext dc,string text,Point center,double size,Typeface font,Brush color)
            {
                var label=new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,font,size,color,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(label,new Point(center.X-label.Width/2,center.Y-label.Height/2));
            }
            protected override void OnRender(DrawingContext dc)
            {
                base.OnRender(dc);
                Point hub=new Point(ActualWidth/2,ActualHeight-10);
                double radius=Math.Min(hub.X-18,hub.Y-11);
                if(radius<45) return;
                // Put the scale beyond the tips so endpoint readings and target marks never cover the rates.
                for(int i=0;i<=16;i++) {
                    double angle=i*Math.PI/16;
                    bool major=i%4==0;
                    dc.DrawLine(major ? MajorTick:MinorTick,Polar(hub,radius-10,angle),Polar(hub,radius-(major?3:i%2==0?5:7),angle));
                    if(major) CenterText(dc,i==16 ? "240+" : (i*15).ToString(CultureInfo.InvariantCulture),
                        Polar(hub,radius+7,angle),10,LabelFace,Muted);
                }
                TrackValue(dc,hub,radius-18,presented,Series[2]);
                TrackValue(dc,hub,radius-27,source,Series[1]);
                TrackValue(dc,hub,radius-36,generated,Series[3]);
                if(!double.IsNaN(target)) {
                    double a=Angle(target);
                    var tip=Polar(hub,radius-16,a); var back=Polar(hub,radius-7,a);
                    double dx=3.8*Math.Sin(a),dy=-3.8*Math.Cos(a);
                    var marker=new StreamGeometry();
                    using(var g=marker.Open()) {
                        g.BeginFigure(tip,true,true);
                        g.LineTo(new Point(back.X+dx,back.Y+dy),true,false);
                        g.LineTo(new Point(back.X-dx,back.Y-dy),true,false);
                    }
                    dc.DrawGeometry(Ink,new Pen(Dark,1),marker);
                }
                CenterText(dc,Metric(presented,"","0"),new Point(hub.X,hub.Y-24),29,NumberFace,Series[2]);
                CenterText(dc,"Output FPS",new Point(hub.X,hub.Y-2),10.5,LabelFace,Muted);
            }
        }
    }
}
