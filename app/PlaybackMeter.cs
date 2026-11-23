using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Cadrivect
{
    internal sealed class PlaybackMeter : UserControl
    {
        static readonly string[] ModeIds={"original","sharp","fsr","rtxvideo","dlss","dlaa","fsr-temporal"};
        static readonly string[] ModeLabels={"Original","Sharpen","AMD FSR 1","RTX Video SR","DLSS","DLAA","AMD FSR Temporal"};
        static readonly string[] Descriptions={"Display the source picture.","Sharpen edges at the source resolution.","AMD FidelityFX Super Resolution 1.",
            "NVIDIA RTX Video Super Resolution for SDR video.","Experimental DLSS with motion estimated from the stream.","Experimental DLAA at the source resolution.","AMD temporal upscaling. The SDK selects a provider supported by the GPU."};
        static readonly Brush Ink=ColorBrush("#E9F3F1"), Muted=ColorBrush("#A5B9BB"), Line=ColorBrush("#304147"), Dark=ColorBrush("#141F24");
        static readonly Brush Mint=ColorBrush("#8FE6D0"), Amber=ColorBrush("#EABC8E");
        static readonly ControlTemplate ChipTemplate=MakeChipTemplate();
        static readonly ControlTemplate GroupTemplate=MakeGroupTemplate();
        readonly Action<int,int> change;
        static readonly string[] ViewIds={"enhanced","original","side-by-side","wipe"};
        readonly Action<string> compare;
        readonly RadioButton[] views=new RadioButton[ViewIds.Length];
        static readonly string[] QualityIds={"auto","quality","balanced","performance","ultra-performance"};
        static readonly string[] QualityLabels={"Auto","Quality","Balanced","Performance","Ultra Performance"};
        static readonly string[] ModelIds={"auto","k","m","l"};
        readonly ComboBox qualityChoice,modelChoice;
        readonly StackPanel modelGroup;
        internal string DlssModelHint {
            get {return ModelIds[modelChoice.SelectedIndex];}
            set {modelChoice.SelectedIndex=Math.Max(0,Array.IndexOf(ModelIds,value));}
        }
        internal event Action<string> DlssModelChanged;
        readonly StackPanel qualityGroup;
        readonly TextBlock activeQuality;
        internal string DlssQuality {
            get {return QualityIds[qualityChoice.SelectedIndex];}
            set {qualityChoice.SelectedIndex=Math.Max(0,Array.IndexOf(QualityIds,value));}
        }
        internal event Action<string> DlssQualityChanged;
        readonly RadioButton amdGeneration;
        internal string FrameGenerationProvider {get;set;}
        readonly Slider divider;
        readonly ToggleButton horizontalSplit;
        internal bool WipeHorizontal {get {return horizontalSplit.IsChecked==true;} set {horizontalSplit.IsChecked=value;}}
        internal event Action<bool> WipeHorizontalChanged;
        internal event Action<double> WipePositionChanged;
        internal double WipePosition {get {return divider.Value/100;} set {divider.Value=value*100;}}
        int selectedView,previousView;
        readonly TextBlock comparisonGuide;
        public string ComparisonView {get {return ViewIds[selectedView];}}
        readonly RadioButton[] modes=new RadioButton[ModeIds.Length], factors=new RadioButton[4];
        readonly TextBlock sourceValue,generatedValue,targetValue;
        readonly Dictionary<string,TextBlock> values=new Dictionary<string,TextBlock>();
        readonly TextBlock submitLabel,notice,pictureScale,pictureSize;
        readonly FrameRateDial dial=new FrameRateDial();
        Dictionary<string,object> gpu;
        int selectedMode,selectedFactor=1;
        bool syncing,hdr,directSelected;
        int requestedVideoKbps;
        long lastSample=-1;
        DateTime pendingSettings;

        public PlaybackMeter(Action<int,int> change,Action<string> compare)
        {
            this.change=change; this.compare=compare;
            UseLayoutRounding=true; SnapsToDevicePixels=true; Background=Brushes.Transparent;
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
            sourceValue=RateReadout(legend,Label("Source",Muted,11,FontWeights.Normal),Mint,"Source frames submitted for display each second, including redraws.");
            generatedValue=RateReadout(legend,Label("Generated",Muted,11,FontWeights.Normal),Amber,"Interpolated frames submitted for display each second.");
            Separator(fixedMeter);
            var picture=AddGroup(sections,"Picture","PictureGroup");
            string group=Guid.NewGuid().ToString("N");
            var modeGrid=new UniformGrid {Rows=0,Columns=2,Margin=new Thickness(-2,1,-2,0)};
            picture.Children.Add(modeGrid);
            for(int i=0;i<modes.Length;i++)
            {
                var button=new RadioButton {GroupName="Picture"+group,Content=ModeLabels[i],Template=ChipTemplate};
                StyleChip(button); modes[i]=button; int index=i;
                button.Checked+=delegate {
                    if(syncing) return;
                    selectedMode=index; RefreshQualityChoice(); SendSettings();
                };
                modeGrid.Children.Add(button);
            }
            qualityGroup=new StackPanel {Margin=new Thickness(0,7,0,0)};
            picture.Children.Add(qualityGroup);
            var qualityHeader=new DockPanel {Margin=new Thickness(0,0,0,3)};
            activeQuality=Label("",Mint,11,FontWeights.SemiBold);
            AutomationProperties.SetAutomationId(activeQuality,"ActiveDlssQuality");
            DockPanel.SetDock(activeQuality,Dock.Right);
            qualityHeader.Children.Add(activeQuality);
            qualityHeader.Children.Add(Label("DLSS mode",Muted,11,FontWeights.Normal));
            qualityGroup.Children.Add(qualityHeader);
            qualityChoice=new ComboBox {ItemsSource=QualityLabels,SelectedIndex=0,
                ToolTip="Auto chooses a mode for the enlargement ratio. At source resolution, DLAA takes over."};
            qualityChoice.SetResourceReference(FrameworkElement.StyleProperty,typeof(ComboBox));
            AutomationProperties.SetName(qualityChoice,"DLSS mode");
            AutomationProperties.SetAutomationId(qualityChoice,"DlssQuality");
            qualityChoice.SelectionChanged+=delegate {if(DlssQualityChanged!=null) DlssQualityChanged(DlssQuality);};
            qualityGroup.Children.Add(qualityChoice);
            modelGroup=new StackPanel {Margin=new Thickness(0,5,0,0)};
            modelGroup.Children.Add(Label("DLSS model preference",Muted,11,FontWeights.Normal));
            modelChoice=new ComboBox {ItemsSource=new[]{"Driver default","DLSS 4 · K","DLSS 4.5 · M","DLSS 4.5 · L"},SelectedIndex=0,
                ToolTip="Requests an NVIDIA Super Resolution or DLAA model. Driver overrides may take precedence. M is tuned for Performance and L for Ultra Performance."};
            modelChoice.SetResourceReference(FrameworkElement.StyleProperty,typeof(ComboBox));
            AutomationProperties.SetName(modelChoice,"DLSS model preference");
            AutomationProperties.SetAutomationId(modelChoice,"DlssModelHint");
            modelChoice.SelectionChanged+=delegate {if(DlssModelChanged!=null) DlssModelChanged(DlssModelHint);};
            modelGroup.Children.Add(modelChoice); picture.Children.Add(modelGroup);
            var resolutionHeader=new DockPanel {Margin=new Thickness(0,7,0,0)};
            pictureScale=Label("",Mint,11,FontWeights.SemiBold);
            DockPanel.SetDock(pictureScale,Dock.Right);
            resolutionHeader.Children.Add(pictureScale);
            resolutionHeader.Children.Add(Label("Input → output",Muted,11,FontWeights.Normal));
            picture.Children.Add(resolutionHeader);
            pictureSize=Label("Waiting for video",Ink,13,FontWeights.SemiBold);
            pictureSize.TextWrapping=TextWrapping.Wrap;
            pictureSize.ToolTip="Incoming pixels → processed pixels before the picture is fitted to the playback area.";
            AutomationProperties.SetAutomationId(pictureSize,"Metric_size");
            AutomationProperties.SetAutomationId(pictureScale,"Metric_scale");
            picture.Children.Add(pictureSize);
            var interpolation=Label("Frame interpolation",Muted,11,FontWeights.SemiBold);
            interpolation.Margin=new Thickness(0,9,0,2); picture.Children.Add(interpolation);
            var factorGrid=new UniformGrid {Rows=1,Columns=4,Margin=new Thickness(-2,0,-2,0)};
            picture.Children.Add(factorGrid);
            for(int i=0;i<4;i++)
            {
                int factor=i+1;
                var button=new RadioButton {GroupName="Rate"+group,Content=i==0 ? "Off" : factor+"×",Template=ChipTemplate};
                StyleChip(button); factors[i]=button;
                button.ToolTip=i==0 ? "Present captured frames only." : "P5Cast uses motion estimated from the video to present up to "+factor+" frames per source frame.";
                button.Checked+=delegate {if(syncing) return; selectedFactor=factor; FrameGenerationProvider="p5cast"; SendSettings();};
                factorGrid.Children.Add(button);
            }
            FrameGenerationProvider="p5cast";
            amdGeneration=new RadioButton {GroupName="Rate"+group,Content="AMD FSR 2×",Template=ChipTemplate};
            StyleChip(amdGeneration); AutomationProperties.SetAutomationId(amdGeneration,"AmdFrameGeneration");
            amdGeneration.Checked+=delegate {
                if(syncing) return;
                FrameGenerationProvider="amd"; selectedFactor=2; SendSettings();
            };
            picture.Children.Add(amdGeneration);
            var viewLabel=Label("Comparison view",Muted,11,FontWeights.Normal);
            viewLabel.Margin=new Thickness(0,9,0,2); picture.Children.Add(viewLabel);
            var viewGrid=new UniformGrid {Rows=2,Columns=2,Margin=new Thickness(-2,0,-2,0)};
            picture.Children.Add(viewGrid);
            string[] viewLabels={"Enhanced","Original","Side by side","Draggable split"};
            string[] viewControlIds={"ViewEnhanced","ViewOriginal","ViewSideBySide","ViewWipe"};
            string[] viewTips={"Show your selected picture and interpolation settings.",
                "Show the source picture at its original frame rate.","Show the full original on the left and the enhanced picture on the right.",
                "Drag the divider across one full-size picture to reveal the original or enhanced image."};
            horizontalSplit=new ToggleButton {Content="Horizontal split",Template=ChipTemplate,
                ToolTip="Place the original above the divider and the enhanced picture below it."};
            StyleChip(horizontalSplit);
            horizontalSplit.Margin=new Thickness(0,5,0,0);
            AutomationProperties.SetAutomationId(horizontalSplit,"WipeHorizontal");
            picture.Children.Add(horizontalSplit);
            comparisonGuide=Label("",Muted,11,FontWeights.Normal);
            comparisonGuide.Margin=new Thickness(0,4,0,0); picture.Children.Add(comparisonGuide);
            divider=new Slider {Minimum=0,Maximum=100,Value=50,SmallChange=1,LargeChange=10,IsMoveToPointEnabled=true,AutoToolTipPlacement=AutoToolTipPlacement.TopLeft,
                ToolTip="Drag the divider in the picture or use the arrow keys here."};
            divider.SetResourceReference(FrameworkElement.StyleProperty,"WipeSlider");
            RoutedEventHandler rotate=delegate {
                UpdateComparisonGuide();
                if(WipeHorizontalChanged!=null) WipeHorizontalChanged(WipeHorizontal);
            };
            horizontalSplit.Checked+=rotate; horizontalSplit.Unchecked+=rotate;
            AutomationProperties.SetName(divider,"Divider position");
            AutomationProperties.SetAutomationId(divider,"WipePosition");
            divider.ValueChanged+=delegate {if(WipePositionChanged!=null) WipePositionChanged(WipePosition);};
            picture.Children.Add(divider);
            for(int i=0;i<views.Length;i++) {
                int index=i;
                var button=new RadioButton {GroupName="View"+group,Content=viewLabels[i],Template=ChipTemplate,ToolTip=viewTips[i]};
                StyleChip(button); views[i]=button;
                AutomationProperties.SetName(button,viewLabels[i]+" view");
                AutomationProperties.SetAutomationId(button,viewControlIds[i]);
                button.Checked+=delegate {
                    if(syncing) return;
                    SelectView(index);
                    if(compare!=null) compare(ComparisonView);
                };
                viewGrid.Children.Add(button);
            }
            var loupeHint=Label("Hold the picture or drag the split to inspect pixels. Scroll to zoom.",Muted,11,FontWeights.Normal);
            loupeHint.TextWrapping=TextWrapping.Wrap;
            loupeHint.Margin=new Thickness(0,5,0,0);
            picture.Children.Add(loupeHint);
            notice=Label("",Amber,11,FontWeights.Normal); notice.TextWrapping=TextWrapping.Wrap; notice.Margin=new Thickness(0,5,0,0);
            notice.Visibility=Visibility.Collapsed; picture.Children.Add(notice);
            Separator(sections);
            var diagnostics=AddGroup(sections,"Stream diagnostics","StreamDiagnosticsGroup");
            Subheading(diagnostics,"Video");
            AddMetric(diagnostics,"format","Video format","Source codec and active output color mode.");
            AddMetric(diagnostics,"surface","Playback area","Size of the player surface in screen pixels, including any black bars.");
            AddMetric(diagnostics,"refresh","Display refresh","Active Windows display mode in Hz. The FPS dial counts submitted frames.");
            AddMetric(diagnostics,"renderer","Effects and output API","Graphics API running picture effects and presenting the video.");
            AddMetric(diagnostics,"incomingFps","Incoming FPS","Frames received from the source each second.");
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
            layout.Children.Add(scrolling);
            Content=layout;
            Reset(0,1);
        }
        public void SetSource(string source)
        {
            directSelected=source=="Direct";
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
            var label=Label(title,Mint,12,FontWeights.SemiBold);
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
            selected.Setters.Add(new Setter(Control.ForegroundProperty,Mint)); template.Triggers.Add(selected);
            var focus=new Trigger {Property=UIElement.IsKeyboardFocusedProperty,Value=true};
            focus.Setters.Add(new Setter(Border.BorderBrushProperty,Mint,"Surface")); template.Triggers.Add(focus);
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
        void SelectView(int index)
        {
            if(index==1 && selectedView!=1) previousView=selectedView;
            selectedView=index; views[index].IsChecked=true;
            horizontalSplit.Visibility=index==3 ? Visibility.Visible : Visibility.Collapsed;
            UpdateComparisonGuide();
            comparisonGuide.Visibility=index>=2 ? Visibility.Visible : Visibility.Collapsed;
            divider.Visibility=index==3 ? Visibility.Visible : Visibility.Collapsed;
        }
        void UpdateComparisonGuide()
        {
            comparisonGuide.Text=selectedView==3 && WipeHorizontal ? "Top: Original    ·    Bottom: Enhanced" : "Left: Original    ·    Right: Enhanced";
        }
        public void ToggleOriginal()
        {
            views[selectedView==1 ? previousView : 1].IsChecked=true;
        }
        public void Reset(int modeIndex,int multiplier)
        {
            selectedMode=Math.Max(0,Math.Min(modes.Length-1,modeIndex)); selectedFactor=Math.Max(1,Math.Min(4,multiplier));
            syncing=true;
            try {modes[selectedMode].IsChecked=true; (FrameGenerationProvider=="amd" && selectedFactor==2 ? amdGeneration : factors[selectedFactor-1]).IsChecked=true; SelectView(selectedView);}
            finally {syncing=false;}
            hdr=false; lastSample=-1; pendingSettings=DateTime.MinValue; dial.SetRates(double.NaN,double.NaN,double.NaN,double.NaN);
            sourceValue.Text=generatedValue.Text="—"; targetValue.Text="Target —";
            pictureSize.Text="Waiting for video"; pictureScale.Text=""; activeQuality.Text="";
            foreach(var label in values.Values) label.Text="—";
            values["format"].ToolTip=null;
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
                if(!available) reason=gpu==null ? "Checking GPU support." : Text(gpu,ModeIds[i]+"Reason") ?? Text(gpu,"probeFailure") ?? "This mode is unavailable on the active GPU.";
                if(i==3 && hdr) {available=false; reason="RTX Video SR requires SDR output.";}
                if(ModeIds[i]=="fsr-temporal" && available && !string.IsNullOrEmpty(Text(gpu,"fsrVersion")))
                    reason="AMD temporal upscaling · "+Text(gpu,"fsrVersion");
                modes[i].IsEnabled=available; modes[i].ToolTip=reason;
            }
            amdGeneration.IsEnabled=Flag(gpu,"amdFrameGeneration")==true;
            amdGeneration.ToolTip=amdGeneration.IsEnabled ? "AMD FSR frame generation "+Text(gpu,"amdFrameGenerationVersion")+" · one generated frame between source frames." :
                Text(gpu,"amdFrameGenerationReason") ?? Text(gpu,"probeFailure") ?? "Checking GPU support.";
            for(int i=0;i<factors.Length;i++) factors[i].IsEnabled=i==0 || !noAdapter;
            RefreshQualityChoice();
        }
        void RefreshQualityChoice()
        {
            modelGroup.Visibility=selectedMode==4 || selectedMode==5 ? Visibility.Visible : Visibility.Collapsed;
            modelChoice.IsEnabled=(selectedMode==4 || selectedMode==5) && modes[selectedMode].IsEnabled;
            qualityGroup.Visibility=selectedMode==4 ? Visibility.Visible : Visibility.Collapsed;
            qualityChoice.IsEnabled=selectedMode==4 && modes[4].IsEnabled;
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
        internal static double Number(Dictionary<string,object> data,string key)
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
        static string Dimensions(double w,double h)
        {
            return w>0 && h>0 ? w.ToString("0")+"×"+h.ToString("0") : "—";
        }
        void UpdatePictureSize(Dictionary<string,object> data)
        {
            double iw=Number(data,"inputWidth"),ih=Number(data,"inputHeight");
            double ow=Number(data,"outputWidth"),oh=Number(data,"outputHeight");
            if(!(iw>0 && ih>0 && ow>0 && oh>0)) {
                pictureSize.Text="Waiting for video"; pictureScale.Text=""; return;
            }
            pictureSize.Text=Dimensions(iw,ih)+" → "+Dimensions(ow,oh);
            bool native=iw==ow && ih==oh;
            if(native) pictureScale.Text=Text(data,"effectivePictureMode")=="dlaa" ? "1× · DLAA" : "1× · Native";
            else pictureScale.Text=Metric(ow/iw,"×","0.##")+(ow>iw ? " upscale" : " downscale");
        }
        public void Update(Dictionary<string,object> snapshot)
        {
            if(snapshot==null) return;
            double id=Number(snapshot,"sampleId");
            if(!double.IsNaN(id) && (long)id<=lastSample) return;
            if(!double.IsNaN(id)) lastSample=(long)id;
            bool waiting=Number(snapshot,"captured")==0;
            double output=waiting ? double.NaN : Number(snapshot,"submittedFps");
            double generated=Math.Min(output,Number(snapshot,"generatedFps"));
            double originals=output-generated;
            double target=Number(snapshot,"targetPresentedFps");
            targetValue.Text="Target "+Metric(target,"","0.#");
            sourceValue.Text=Metric(originals,"","0.#");
            generatedValue.Text=Metric(generated,"","0.#");
            dial.SetRates(target,originals,output,generated);
            values["incomingFps"].Text=Metric(waiting ? double.NaN : Number(snapshot,"sourceFps"),"","0.#");
            UpdatePictureSize(snapshot);
            values["surface"].Text=Dimensions(Number(snapshot,"surfaceWidth"),Number(snapshot,"surfaceHeight"));
            values["refresh"].Text=Metric(Number(snapshot,"displayRefreshHz")," Hz","0.##");
            values["renderer"].Text=Dashboard.RendererName(Text(snapshot,"processingRenderer") ?? Text(snapshot,"renderer"));
            string codec=Text(snapshot,"codec"); bool? hdrFlag=Flag(snapshot,"presenterHdr10") ?? Flag(snapshot,"hdrEnabled");
            string color=hdrFlag.HasValue ? (hdrFlag.Value ? "HDR10":"SDR") : "";
            values["format"].Text=string.IsNullOrEmpty(codec) ? (color.Length==0 ? "—":color) : codec+(color.Length==0 ? "" : " · "+color);
            string space=Text(snapshot,"presentationColorSpace");
            double displayBits=Number(snapshot,"displayBitsPerColor");
            values["format"].ToolTip=space==null ? null : "Player surface · "+(hdrFlag==true ? "10-bit " : "")+space+
                (displayBits>0 ? "\nWindows display output · "+displayBits.ToString("0")+" bits per color" : "");
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
                if(DateTime.UtcNow>=pendingSettings || (mode==selectedMode && factor==selectedFactor && Text(snapshot,"frameGenerationProvider")==FrameGenerationProvider)) {
                    if(mode>=0 && mode!=selectedMode) {selectedMode=mode; modes[mode].IsChecked=true; RefreshQualityChoice();}
                    if(factor>=1 && factor<=4) {
                        selectedFactor=(int)factor;
                        FrameGenerationProvider=Text(snapshot,"frameGenerationProvider")=="amd" && selectedFactor==2 ? "amd" : "p5cast";
                        (FrameGenerationProvider=="amd" ? amdGeneration : factors[selectedFactor-1]).IsChecked=true;
                    }
                    pendingSettings=DateTime.MinValue;
                }
            } finally {syncing=false;}
            int quality=Array.IndexOf(QualityIds,Text(snapshot,"activeDlssQuality"));
            activeQuality.Text=effective=="dlss" && quality>0 ? "Active · "+QualityLabels[quality] : "";
            string message=Text(snapshot,"effectNotice");
            if(string.IsNullOrEmpty(message) && effective=="fsr-temporal" && !string.IsNullOrEmpty(Text(snapshot,"fsrVersion")))
                message="AMD FSR "+Text(snapshot,"fsrVersion");
            string fgVersion=Text(snapshot,"activeFrameGenerationVersion");
            if(!string.IsNullOrEmpty(fgVersion) && string.IsNullOrEmpty(Text(snapshot,"effectNotice")))
                message=(string.IsNullOrEmpty(message) ? "" : message+" · ")+"AMD frame generation "+fgVersion;
            int active=Array.IndexOf(ModeIds,effective);
            if(string.IsNullOrEmpty(message) && comparing!=true && active>=0 && mode>=0 && active!=mode && effective!="dlaa") message="Active picture · "+ModeLabels[active];
            notice.Text=message ?? ""; notice.Visibility=string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        sealed class FrameRateDial : FrameworkElement
        {
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
            public void SetRates(double aim,double originals,double submitted,double synthetic)
            {
                target=aim; source=originals; presented=submitted; generated=synthetic;
                string detail="Output "+Describe(presented)+". Source "+Describe(source)+". Generated "+Describe(generated)+". Target "+Describe(target)+".";
                AutomationProperties.SetHelpText(this,detail); ToolTip=detail; InvalidateVisual();
            }
            static Point Polar(Point hub,double radius,double angle)
            {
                return new Point(hub.X-radius*Math.Cos(angle),hub.Y-radius*Math.Sin(angle));
            }
            static void Arc(DrawingContext dc,Point hub,double r,double start,double end,Brush color,bool roundStart,bool roundEnd)
            {
                if(!(end>start)) return;
                var pen=new Pen(color,8){StartLineCap=roundStart?PenLineCap.Round:PenLineCap.Flat,EndLineCap=roundEnd?PenLineCap.Round:PenLineCap.Flat};
                var shape=new StreamGeometry();
                using(var g=shape.Open()) {
                    g.BeginFigure(Polar(hub,r,start),false,false);
                    g.ArcTo(Polar(hub,r,end),new Size(r,r),0,false,SweepDirection.Clockwise,true,false);
                }
                dc.DrawGeometry(null,pen,shape);
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
                double peak=Math.Max(double.IsNaN(target)?0:target,double.IsNaN(presented)?0:presented);
                double maximum=Math.Max(240,240*Math.Ceiling(peak/240));
                for(int i=0;i<=16;i++) {
                    double angle=i*Math.PI/16;
                    bool major=i%4==0;
                    dc.DrawLine(major ? MajorTick:MinorTick,Polar(hub,radius-10,angle),Polar(hub,radius-(major?3:i%2==0?5:7),angle));
                    if(major) CenterText(dc,(maximum*i/16).ToString("0",CultureInfo.InvariantCulture),
                        Polar(hub,radius+7,angle),10,LabelFace,Muted);
                }
                double ring=radius-18,split=Math.PI*source/maximum,end=Math.PI*presented/maximum;
                Arc(dc,hub,ring,0,Math.PI,Track,true,true);
                Arc(dc,hub,ring,0,split,Mint,true,generated==0);
                Arc(dc,hub,ring,split,end,Amber,source==0,true);
                if(!double.IsNaN(target)) {
                    double a=Math.PI*target/maximum;
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
                CenterText(dc,Metric(presented,"","0.#"),new Point(hub.X,hub.Y-24),29,NumberFace,Ink);
                CenterText(dc,"Output FPS",new Point(hub.X,hub.Y-2),10.5,LabelFace,Muted);
            }
        }
    }
}
