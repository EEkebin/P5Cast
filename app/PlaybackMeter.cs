using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Cadrivect
{
    internal sealed class PlaybackMeter : UserControl
    {
        static readonly string[] ModeIds=PictureLabels.Ids;
        static readonly string[] ModeLabels=PictureLabels.Modes;
        static readonly int NeuralMode=Array.IndexOf(ModeIds,"dlss-nr"),OpenNeuralMode=Array.IndexOf(ModeIds,"open-nr");
        static readonly string[] Descriptions={"Display the source picture.","Sharpen edges at the source resolution.","AMD FSR 1 enlarges and sharpens each decoded frame independently.",
            "NVIDIA RTX Video Super Resolution for SDR and HDR10 playback.","Experimental DLSS with motion estimated from the stream.","Experimental DLAA at the source resolution.","AMD temporal upscaling. The SDK selects a provider supported by the GPU.","Experimental DLSS 5 neural rendering at source resolution. Uses motion estimated from the PS5 video. Requires Vulkan, SDR and a compatible RTX GPU.",
            "NVIDIA Image Scaling. Enlarges and sharpens each frame independently, or sharpens at native resolution."};
        const string Ink="Theme.Text", Muted="Theme.Muted", Line="Theme.Border";
        const string Mint="Theme.Accent", Amber="Theme.Generated";
        static readonly ControlTemplate ChipTemplate=MakeChipTemplate(false);
        static readonly ControlTemplate PictureTemplate=MakeChipTemplate(true);
        static readonly ControlTemplate GroupTemplate=MakeGroupTemplate();
        readonly Action<int,int> change;
        static readonly string[] ViewIds={"enhanced","original","side-by-side","wipe","hdr-sdr"};
        const int HdrSdrView=4;
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
        readonly StackPanel neuralSetup;
        readonly StackPanel pictureControls;
        readonly TextBlock networkPictureNotice;
        readonly Button neuralDownload;
        readonly CheckBox useOpenNr;
        readonly CheckBox nrPrepass;
        readonly ToggleButton nrModelSettings;
        internal bool PreferOpenNrPrepass {get;set;}
        readonly StackPanel openNrSetup;
        readonly TextBlock openNrStatus;
        readonly Button openNrModel,openNrDownload;
        bool openNrChecking;
        internal event Action OpenNrProviderChanged,OpenNrModelRequested,OpenNrDownloadRequested;
        internal bool UseOpenNr {get {return useOpenNr.IsChecked==true;} set {useOpenNr.IsChecked=value;}}
        bool idleHdr;
        internal void SetIdleHdr(bool enabled)
        {
            idleHdr=enabled;
            if(hdr!=enabled) {
                int before=selectedView;
                hdr=enabled; RefreshAvailability(); RefreshComparison();
                if(before!=selectedView && compare!=null) compare(ComparisonView);
            }
            if(hdr && selectedMode==NeuralMode)
                modes[0].IsChecked=true;
        }
        internal bool SupportsSelectedColor(int picture)
        {
            return !hdr || picture!=NeuralMode;
        }
        internal void SetOpenNrChecking(bool checking)
        {
            openNrChecking=checking;
            UpdateNeuralSetup();
        }
        bool neuralSetupBusy;
        internal event Action NeuralDownloadRequested,NeuralLicenseRequested;
        internal bool CanDownloadNeural {get {return Flag(gpu,"dlssNeuralRuntimeDownloadAvailable")==true;}}
        internal bool NeuralReady {get {return Flag(gpu,"dlss-nr")==true;}}
        internal string NeuralReason {get {return Text(gpu,"dlss-nrReason") ?? Text(gpu,"probeFailure");}}
        internal void SetNeuralSetupBusy(bool busy)
        {
            neuralSetupBusy=busy;
            neuralDownload.Content=busy ? "Downloading and verifying…" : "Download runtime · 368 MB";
            UpdateNeuralSetup();
        }
        void UpdateNeuralSetup()
        {
            neuralSetup.Visibility=!UseOpenNr && (CanDownloadNeural || neuralSetupBusy) ? Visibility.Visible : Visibility.Collapsed;
            neuralDownload.IsEnabled=CanDownloadNeural && !neuralSetupBusy;
            nrModelSettings.Visibility=UseOpenNr ? Visibility.Collapsed : Visibility.Visible;
            openNrSetup.Visibility=UseOpenNr || nrModelSettings.IsChecked==true ? Visibility.Visible : Visibility.Collapsed;
            openNrModel.IsEnabled=!openNrChecking;
            openNrModel.Content=openNrChecking ? "Checking model…" : Flag(gpu,"open-nr")==true ? "Change model…" : "Choose model…";
            openNrDownload.IsEnabled=!openNrChecking;
            openNrDownload.Visibility=Flag(gpu,"openNrModelImportAvailable")==true ? Visibility.Visible : Visibility.Collapsed;
            openNrStatus.Text=Flag(gpu,"open-nr")==true
                  ? (hdr ? "OpenDLSS-NR · HDR10 color and detail" : "OpenDLSS-NR · Model ready · Native resolution")
                : Text(gpu,"open-nrReason") ?? Text(gpu,"probeFailure") ?? "Checking OpenDLSS-NR support.";
        }
        readonly StackPanel qualityGroup;
        readonly TextBlock activeQuality;
        internal string DlssQuality {
            get {return QualityIds[qualityChoice.SelectedIndex];}
            set {qualityChoice.SelectedIndex=Math.Max(0,Array.IndexOf(QualityIds,value));}
        }
        internal event Action<string> DlssQualityChanged;
        readonly RadioButton amdGeneration,frucGeneration,dlssGeneration,xessGeneration;
        readonly Panel aiPictures,standardPictures,pendingPicture,aiGeneration,standardGeneration,pendingGeneration;
        readonly StackPanel frucSetup;
        internal event Action FrucImportRequested,FrucSdkRequested;
        internal string FrucAvailabilityReason {get {return (string)frucGeneration.ToolTip;}}
        internal string FrameGenerationProvider {get;set;}
        internal int GenerationSelectionRevision {get;private set;}
        internal static string SavedProvider(string provider,int factor)
        {
            return factor==2 && (provider=="amd" || provider=="fruc" || provider=="dlss-fg" || provider=="xess-fg") ? provider : "p5cast";
        }
        internal bool ProviderAvailable(string provider)
        {
            switch(provider) {
                case "dlss-fg": return dlssGeneration.IsEnabled;
                case "xess-fg": return xessGeneration.IsEnabled;
                case "fruc": return frucGeneration.IsEnabled;
                case "amd": return amdGeneration.IsEnabled;
                default: return false;
            }
        }
        readonly Slider divider;
        readonly ToggleButton horizontalSplit;
        internal bool WipeHorizontal {get {return horizontalSplit.IsChecked==true;} set {horizontalSplit.IsChecked=value;}}
        internal event Action<bool> WipeHorizontalChanged;
        internal event Action<double> WipePositionChanged;
        internal double WipePosition {get {return divider.Value/100;} set {divider.Value=value*100;}}
        int selectedView,previousView;
        bool comparisonsAvailable=true,hdrComparisonAvailable;
        readonly TextBlock comparisonGuide;
        public string ComparisonView {get {return ViewIds[selectedView];}}
        // The two neural providers share one visible picture choice.
        readonly RadioButton[] modes=new RadioButton[Descriptions.Length], factors=new RadioButton[4];
        readonly TextBlock sourceValue,generatedValue,targetValue;
        readonly Dictionary<string,TextBlock> values=new Dictionary<string,TextBlock>();
        readonly TextBlock submitLabel,notice,pictureScale,pictureSize,displayedSize;
        readonly FrameRateDial dial=new FrameRateDial();
        internal const int DefaultDialMaximum=240;
        const int MinDialMaximum=120,MaxDialMaximum=1000;
        readonly Slider dialScale;
        readonly TextBox dialScaleInput;
        internal event Action DialMaximumChanged;
        readonly CheckBox estimateGameFps=new CheckBox {Content="Estimate game FPS",Margin=new Thickness(0,5,0,2),
            ToolTip="Estimate game FPS by comparing pixels in the Direct PS5 video. This uses no AI. Still scenes and uncertain readings show a dash."};
        internal bool EstimateGameFps {get {return estimateGameFps.IsChecked==true;} set {estimateGameFps.IsChecked=value;}}
        internal event Action EstimateGameFpsChanged;
        internal int DialMaximum {
            get {return (int)dialScale.Value;}
            set {dialScale.Value=Math.Max(MinDialMaximum,Math.Min(MaxDialMaximum,value));}
        }
        Dictionary<string,object> gpu;
        int selectedMode,selectedBase,selectedFactor=1;
        bool syncing,hdr,directSelected;
        int requestedVideoKbps;
        string liveFsrVersion, liveFsrGenerationVersion;
        long lastSample=-1;

        public PlaybackMeter(Action<int,int> change,Action<string> compare)
        {
            this.change=change; this.compare=compare;
            UseLayoutRounding=true; SnapsToDevicePixels=true; Background=Brushes.Transparent;
            SetResourceReference(ForegroundProperty,Ink); FontFamily=new FontFamily("Segoe UI"); FontSize=12;
            var layout=new DockPanel();
            var fixedMeter=new StackPanel {Margin=new Thickness(16,14,16,0)};
            DockPanel.SetDock(fixedMeter,Dock.Top); layout.Children.Add(fixedMeter);
            var sections=new StackPanel {Margin=new Thickness(16,0,12,14)};
            var title=Heading("Frame rate"); title.Margin=new Thickness(0);
            var heading=new DockPanel();
            targetValue=Label("Target —",Muted,11,FontWeights.SemiBold);
            targetValue.ToolTip="The triangular marker shows the requested output frame rate.";
            DockPanel.SetDock(targetValue,Dock.Right);
            heading.Children.Add(targetValue); heading.Children.Add(title); fixedMeter.Children.Add(heading);
            dial.Height=133; dial.Margin=new Thickness(0,2,0,6); fixedMeter.Children.Add(dial);
            var legend=new UniformGrid {Rows=1,Columns=2}; fixedMeter.Children.Add(legend);
            sourceValue=RateReadout(legend,Label("Source",Muted,11,FontWeights.Normal),"Theme.Source","Presentations of real source pictures per second. Moving the comparison divider does not count.");
            generatedValue=RateReadout(legend,Label("Generated",Muted,11,FontWeights.Normal),Amber,"Interpolated frames submitted for display each second.");
            AutomationProperties.SetAutomationId(estimateGameFps,"EstimateGameFps");
            RoutedEventHandler cadenceChanged=delegate {
                values["gameFps"].Text=EstimateGameFps ? "—" : "Off";
                if(EstimateGameFpsChanged!=null) EstimateGameFpsChanged();
            };
            fixedMeter.Children.Add(estimateGameFps);
            AddMetric(fixedMeter,"gameFps","Game FPS (est.)","Visible picture changes per second in Direct PS5 video. Frame generation is excluded.");
            values["gameFps"].Text="Off";
            estimateGameFps.Checked+=cadenceChanged; estimateGameFps.Unchecked+=cadenceChanged;
            var scaleRow=new DockPanel {Margin=new Thickness(0,6,0,0)};
            var scaleTitle=Label("Dial max",Muted,11,FontWeights.SemiBold);
            DockPanel.SetDock(scaleTitle,Dock.Left); scaleRow.Children.Add(scaleTitle);
            dialScaleInput=new TextBox {Text=DefaultDialMaximum.ToString(CultureInfo.InvariantCulture),Width=45,MinHeight=24,
                MaxLength=4,Padding=new Thickness(2,1,2,1),TextAlignment=TextAlignment.Center,Margin=new Thickness(4,0,0,0),
                ToolTip="Maximum on the FPS dial (120–1,000 FPS)."};
            AutomationProperties.SetName(dialScaleInput,"FPS dial maximum"); AutomationProperties.SetAutomationId(dialScaleInput,"DialMaximumInput");
            DockPanel.SetDock(dialScaleInput,Dock.Right); scaleRow.Children.Add(dialScaleInput);
            dialScale=new Slider {Minimum=MinDialMaximum,Maximum=MaxDialMaximum,Value=DefaultDialMaximum,TickFrequency=1,
                IsSnapToTickEnabled=true,IsMoveToPointEnabled=true,SmallChange=1,LargeChange=10,Margin=new Thickness(7,0,3,0),
                ToolTip="Adjust the FPS dial scale. Arrow keys change it by one FPS."};
            dialScale.SetResourceReference(StyleProperty,"WipeSlider");
            AutomationProperties.SetName(dialScale,"FPS dial maximum"); AutomationProperties.SetAutomationId(dialScale,"DialMaximumSlider");
            dialScale.ValueChanged+=delegate {
                string text=DialMaximum.ToString(CultureInfo.InvariantCulture);
                if(dialScaleInput.Text!=text) dialScaleInput.Text=text;
                dial.SetMaximum(DialMaximum);
                if(DialMaximumChanged!=null) DialMaximumChanged();
            };
            dialScaleInput.TextChanged+=delegate {
                int entered;
                if(int.TryParse(dialScaleInput.Text,NumberStyles.None,CultureInfo.InvariantCulture,out entered)
                    && entered>=MinDialMaximum && entered<=MaxDialMaximum) DialMaximum=entered;
            };
            dialScaleInput.LostKeyboardFocus+=delegate {CommitDialMaximum();};
            dialScaleInput.PreviewKeyDown+=delegate(object sender,KeyEventArgs e) {
                if(e.Key==Key.Enter) {CommitDialMaximum();dialScaleInput.SelectAll();e.Handled=true;}
            };
            scaleRow.Children.Add(dialScale); fixedMeter.Children.Add(scaleRow);
            Separator(fixedMeter);
            var pictureGroup=AddGroup(sections,"Picture","PictureGroup");
            networkPictureNotice=Label("Network playback uses Moonlight. Local picture effects, comparisons and frame generation are available in Direct, Sony Remote Play and Demo.",Muted,12,FontWeights.Normal);
            networkPictureNotice.TextWrapping=TextWrapping.Wrap;
            networkPictureNotice.Visibility=Visibility.Collapsed;
            AutomationProperties.SetAutomationId(networkPictureNotice,"NetworkPictureNotice");
            pictureGroup.Children.Add(networkPictureNotice);
            var picture=pictureControls=new StackPanel();
            pictureGroup.Children.Add(picture);
            string group=Guid.NewGuid().ToString("N");
            for(int i=0;i<modes.Length;i++)
            {
                var button=new RadioButton {GroupName="Picture"+group,Content=ModeLabels[i],Template=PictureTemplate};
                StyleChip(button); button.ClearValue(HeightProperty); button.MinHeight=28;
                modes[i]=button; int index=i;
                AutomationProperties.SetAutomationId(button,"Picture_"+ModeIds[i]);
                if(index!=0) button.Click+=PreviewSelectedEffect;
                button.Checked+=delegate {
                    if(syncing) return;
                    if(index==NeuralMode) PreferOpenNrPrepass=false;
                    selectedBase=index;
                    selectedMode=PictureWithPreference(index);
                    RefreshQualityChoice(); SendSettings(index!=0);
                };
            }
            var modeGroup=new StackPanel();
            var modeHeading=Label("Picture mode · Choose one",Ink,12,FontWeights.SemiBold);
            modeHeading.Margin=new Thickness(0,4,0,3);
            AutomationProperties.SetLabeledBy(modeGroup,modeHeading);
            picture.Children.Add(modeHeading);
            picture.Children.Add(modeGroup);
            standardPictures=AddPictureRow(modeGroup,"Standard · no AI","original","sharp","nis","fsr");
            aiPictures=AddPictureRow(modeGroup,"AI enhancement","rtxvideo","dlss","dlaa","dlss-nr");
            pendingPicture=AddPictureRow(modeGroup,"AMD · AI status unknown","fsr-temporal");
            useOpenNr=new CheckBox {Content="Use open source for DLSS 5 NR",Margin=new Thickness(4,5,0,2),
                ToolTip="Use OpenDLSS-NR with SDR or HDR10 on Direct3D 11, Direct3D 12 or Vulkan. HDR playback applies the model’s color and shading changes to the original HDR picture."};
            AutomationProperties.SetAutomationId(useOpenNr,"UseOpenNr");
            useOpenNr.Checked+=ChangeNeuralProvider; useOpenNr.Unchecked+=ChangeNeuralProvider;
            modeGroup.Children.Add(useOpenNr);
            var nrOptions=new DockPanel {Margin=new Thickness(0,8,0,2)};
            nrModelSettings=new ToggleButton {Content="Model…",Template=ChipTemplate,MinWidth=56,Padding=new Thickness(6,2,6,2),
                ToolTip="Show or hide the OpenDLSS-NR model settings."};
            AutomationProperties.SetName(nrModelSettings,"OpenDLSS-NR model settings");
            AutomationProperties.SetAutomationId(nrModelSettings,"OpenNrModelSettings");
            nrModelSettings.Checked+=delegate {UpdateNeuralSetup();};
            nrModelSettings.Unchecked+=delegate {UpdateNeuralSetup();};
            DockPanel.SetDock(nrModelSettings,Dock.Right); nrOptions.Children.Add(nrModelSettings);
            nrPrepass=new CheckBox {Content="Apply OpenDLSS-NR first (AI)",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,4,0)};
            ToolTipService.SetShowOnDisabled(nrPrepass,true);
            AutomationProperties.SetAutomationId(nrPrepass,"OpenNrPrepass");
            nrPrepass.Checked+=ChangePrepass; nrPrepass.Unchecked+=ChangePrepass;
            nrOptions.Children.Add(nrPrepass); modeGroup.Children.Add(nrOptions);
            openNrSetup=new StackPanel {Margin=new Thickness(4,2,0,3),Visibility=Visibility.Collapsed};
            openNrStatus=Label("",Muted,11,FontWeights.Normal); openNrStatus.TextWrapping=TextWrapping.Wrap;
            openNrSetup.Children.Add(openNrStatus);
            openNrModel=new Button {Content="Choose model…",Margin=new Thickness(0,4,0,0),
                ToolTip="Select manifest.json from an OpenDLSS-NR model folder."};
            AutomationProperties.SetAutomationId(openNrModel,"ChooseOpenNrModel");
            openNrModel.Click+=delegate {if(OpenNrModelRequested!=null) OpenNrModelRequested();};
            openNrDownload=new Button {Content="Download model",Margin=new Thickness(0,4,4,0),
                ToolTip="Download the NVIDIA package (368 MB) and prepare the compatible model in your user folder."};
            AutomationProperties.SetAutomationId(openNrDownload,"DownloadOpenNrModel");
            openNrDownload.Click+=delegate {if(OpenNrDownloadRequested!=null) OpenNrDownloadRequested();};
            var modelActions=new WrapPanel();
            modelActions.Children.Add(openNrDownload); modelActions.Children.Add(openNrModel);
            openNrSetup.Children.Add(modelActions); modeGroup.Children.Add(openNrSetup);
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
            neuralSetup=new StackPanel {Margin=new Thickness(0,7,0,0),Visibility=Visibility.Collapsed};
            neuralSetup.Children.Add(Label("DLSS Neural Rendering",Ink,12,FontWeights.SemiBold));
            var licenseLink=new Hyperlink(new Run("NVIDIA SDK license"));
            licenseLink.SetResourceReference(TextElement.ForegroundProperty,Mint);
            licenseLink.Click+=delegate {if(NeuralLicenseRequested!=null) NeuralLicenseRequested();};
            var licenseText=Label("",Muted,11,FontWeights.Normal);licenseText.TextWrapping=TextWrapping.Wrap;
            licenseText.Inlines.Add(new Run("Optional runtime · "));licenseText.Inlines.Add(licenseLink);
            neuralSetup.Children.Add(licenseText);
            neuralDownload=new Button {Content="Download runtime · 368 MB",Margin=new Thickness(0,4,0,0)};
            AutomationProperties.SetAutomationId(neuralDownload,"DownloadNeuralRuntime");
            neuralDownload.Click+=delegate {if(CanDownloadNeural && !neuralSetupBusy && NeuralDownloadRequested!=null) NeuralDownloadRequested();};
            neuralSetup.Children.Add(neuralDownload);picture.Children.Add(neuralSetup);
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
            displayedSize=Label("",Muted,11,FontWeights.Normal);
            displayedSize.Visibility=Visibility.Collapsed;
            AutomationProperties.SetAutomationId(displayedSize,"Metric_displayedSize");
            picture.Children.Add(displayedSize);
            Subheading(picture,"Frame generation");
            picture.Children.Add(Label("P5Cast interpolation · no AI",Muted,11,FontWeights.Normal));
            var factorGrid=new UniformGrid {Rows=1,Columns=4,Margin=new Thickness(-2,0,-2,0)};
            picture.Children.Add(factorGrid);
            for(int i=0;i<4;i++)
            {
                int factor=i+1;
                var button=new RadioButton {GroupName="Rate"+group,Content=i==0 ? "Off" : factor+"×",Template=ChipTemplate};
                StyleChip(button); factors[i]=button;
                AutomationProperties.SetName(button,i==0 ? "Frame generation off" : "P5Cast interpolation "+factor+"×");
                button.ToolTip=i==0 ? "Present captured frames only." : "P5Cast uses motion estimated from the video to present up to "+factor+" frames per source frame.";
                if(factor>1) button.Click+=PreviewSelectedEffect;
                // During a hardware probe Off is temporarily checked. Clicking it
                // still means the user wants to keep generation off.
                if(factor==1) button.Click+=delegate {GenerationSelectionRevision++;};
                button.Checked+=delegate {
                    if(syncing) return;
                    GenerationSelectionRevision++;
                    selectedFactor=factor; FrameGenerationProvider="p5cast"; SendSettings(factor>1);
                };
                factorGrid.Children.Add(button);
            }
            FrameGenerationProvider="p5cast";
            standardGeneration=AddPictureRow(picture,"Optical-flow interpolation · no AI");
            aiGeneration=AddPictureRow(picture,"AI frame generation");
            pendingGeneration=AddPictureRow(picture,"AMD · AI status unknown");
            amdGeneration=AddGenerationProvider(pendingGeneration,group,"amd","AMD FSR 2×","AmdFrameGeneration");
            frucGeneration=AddGenerationProvider(standardGeneration,group,"fruc","NVIDIA FRUC 2×","NvidiaFrucFrameGeneration");
            dlssGeneration=AddGenerationProvider(aiGeneration,group,"dlss-fg","NVIDIA DLSS FG 2×","DlssFrameGeneration");
            xessGeneration=AddGenerationProvider(aiGeneration,group,"xess-fg","Intel XeSS FG 2×","XessFrameGeneration");
            frucSetup=new StackPanel {Margin=new Thickness(0,3,0,0),Visibility=Visibility.Collapsed};
            var sdkLink=new Hyperlink(new Run("Get NVIDIA Optical Flow SDK"));
            sdkLink.SetResourceReference(TextElement.ForegroundProperty,Mint);
            sdkLink.Click+=delegate {if(FrucSdkRequested!=null) FrucSdkRequested();};
            var sdkText=Label("",Muted,11,FontWeights.Normal);sdkText.Inlines.Add(sdkLink);
            frucSetup.Children.Add(sdkText);
            var import=new Button {Content="Import FRUC runtime…",Margin=new Thickness(0,3,0,0),
                ToolTip="Choose the NVIDIA Optical Flow SDK ZIP or its 64-bit FRUC runtime."};
            AutomationProperties.SetAutomationId(import,"ImportFrucRuntime");
            import.Click+=delegate {if(FrucImportRequested!=null) FrucImportRequested();};
            frucSetup.Children.Add(import);picture.Children.Add(frucSetup);
            var viewLabel=Label("Comparison view",Muted,11,FontWeights.Normal);
            viewLabel.Margin=new Thickness(0,9,0,2); picture.Children.Add(viewLabel);
            var viewGrid=new UniformGrid {Columns=2,Margin=new Thickness(-2,0,-2,0)};
            picture.Children.Add(viewGrid);
            string[] viewLabels={"Enhanced","Original","Side by side","Draggable split","HDR / SDR split"};
            string[] viewControlIds={"ViewEnhanced","ViewOriginal","ViewSideBySide","ViewWipe","ViewHdrSdr"};
            string[] viewTips={"Show your selected picture and interpolation settings.",
                "Show the source picture at its original frame rate.","Show the full original on the left and the enhanced picture on the right.",
                "Drag the divider across one full-size picture to reveal the original or enhanced image.",
                "On an HDR output, compare an SDR preview with HDR10 using the same frame and picture settings."};
            horizontalSplit=new ToggleButton {Content="Horizontal split",Template=ChipTemplate,
                ToolTip="Drag the divider up and down."};
            StyleChip(horizontalSplit);
            horizontalSplit.Margin=new Thickness(0,5,0,0);
            AutomationProperties.SetAutomationId(horizontalSplit,"WipeHorizontal");
            picture.Children.Add(horizontalSplit);
            comparisonGuide=Label("",Muted,11,FontWeights.Normal);
            comparisonGuide.TextWrapping=TextWrapping.Wrap;
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
                if(index==HdrSdrView) {button.IsEnabled=false; ToolTipService.SetShowOnDisabled(button,true);}
                StyleChip(button); views[i]=button;
                AutomationProperties.SetName(button,viewLabels[i]+" view");
                AutomationProperties.SetAutomationId(button,viewControlIds[i]);
                button.Checked+=delegate {
                    if(syncing) return;
                    int before=selectedView;
                    RefreshComparison(index);
                    if(before!=selectedView && compare!=null) compare(ComparisonView);
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
            AddMetric(diagnostics,"incomingFps","Stream FPS","Decoded PS5 pictures per second, including repeated images. Measured before local enhancement or frame generation.");
            AddMetric(diagnostics,"videoLoss","Lost video frames","Cumulative video frames the Direct receiver reports missing before decode.");
            AddMetric(diagnostics,"localSkips","Skipped after decode","Decoded pictures replaced by newer pictures before the player could render them.");
            AddMetric(diagnostics,"slowHandoffs","Slow decoder handoffs","Video callbacks that spent at least 15 ms handing compressed video to the local decoder. These can also delay audio delivery.");
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
            AddMetric(diagnostics,"concealments","Concealed audio frames","Missing Opus frames for which the Direct receiver requested concealment.");
            AddMetric(diagnostics,"queueTrims","Audio queue trims","Local audio bursts trimmed to keep playback latency bounded.");
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
            estimateGameFps.IsEnabled=directSelected;
            submitLabel.Text=directSelected ? "Receive → submit" : source=="Sony" ? "Capture → submit" : "Frame → submit";
            ShowRequested(directSelected);
        }
        internal void SetNetworkPlayback(bool network)
        {
            pictureControls.IsEnabled=!network;
            networkPictureNotice.Visibility=network ? Visibility.Visible : Visibility.Collapsed;
        }
        public void SetRequestedVideoKbps(int kbps) {requestedVideoKbps=Math.Max(0,kbps); ShowRequested(directSelected);}
        void ShowRequested(bool direct) {values["requested"].Text=direct && requestedVideoKbps>0 ? Metric(requestedVideoKbps/1000.0," Mb/s","0.##") : "—";}
        static TextBlock RateReadout(Panel legend,TextBlock caption,string color,string detail)
        {
            var cell=new DockPanel {Margin=new Thickness(0,1,9,2),ToolTip=detail};
            var chip=new Line {X1=0,X2=8,Y1=0,Y2=0,StrokeThickness=3,
                StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,
                Margin=new Thickness(0,0,5,0),VerticalAlignment=VerticalAlignment.Center};
            chip.SetResourceReference(Shape.StrokeProperty,color);
            var value=Label("—",Ink,12,FontWeights.SemiBold);
            DockPanel.SetDock(chip,Dock.Left); DockPanel.SetDock(value,Dock.Right);
            cell.Children.Add(chip); cell.Children.Add(value); cell.Children.Add(caption); legend.Children.Add(cell);
            return value;
        }
        void CommitDialMaximum()
        {
            int entered;
            if(int.TryParse(dialScaleInput.Text,NumberStyles.None,CultureInfo.InvariantCulture,out entered)) DialMaximum=entered;
            dialScaleInput.Text=DialMaximum.ToString(CultureInfo.InvariantCulture);
        }
        internal void RefreshTheme() {dial.InvalidateVisual();}
        static TextBlock Label(string text,string role,double size,FontWeight weight)
        {
            var label=new TextBlock {Text=text,FontSize=size,FontWeight=weight,VerticalAlignment=VerticalAlignment.Center};
            label.SetResourceReference(TextBlock.ForegroundProperty,role);
            return label;
        }
        static TextBlock Heading(string text)
        {
            var label=Label(text,Ink,15,FontWeights.SemiBold);
            label.Margin=new Thickness(0,0,0,5);
            return label;
        }
        static void Separator(Panel body)
        {
            var line=new Border {Height=1,Margin=new Thickness(0,10,0,9)};
            line.SetResourceReference(Border.BackgroundProperty,Line);
            body.Children.Add(line);
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
        Panel AddPictureRow(Panel parent,string title,params string[] ids)
        {
            var section=new StackPanel();
            Subheading(section,title);
            var grid=new UniformGrid {Columns=2,Margin=new Thickness(-2,1,-2,0)};
            AutomationProperties.SetName(grid,title);
            foreach(string id in ids) grid.Children.Add(modes[Array.IndexOf(ModeIds,id)]);
            section.Children.Add(grid); parent.Children.Add(section);
            return grid;
        }
        static void ClassifyFsr(RadioButton control,string version,Panel ai,Panel standard,Panel unknown)
        {
            bool? usesAi=PictureLabels.FsrUsesAi(version);
            var row=usesAi==true ? ai : usesAi==false ? standard : unknown;
            if(control.Parent!=row) {
                ((Panel)control.Parent).Children.Remove(control);
                row.Children.Add(control);
            }
            ((FrameworkElement)unknown.Parent).Visibility=row==unknown ? Visibility.Visible : Visibility.Collapsed;
        }
        static ControlTemplate MakeGroupTemplate()
        {
            var surface=new FrameworkElementFactory(typeof(Border),"Surface");
            surface.SetValue(Border.BackgroundProperty,Brushes.Transparent); surface.SetValue(Border.CornerRadiusProperty,new CornerRadius(4));
            surface.SetValue(Border.PaddingProperty,new Thickness(0,2,4,4));
            var row=new FrameworkElementFactory(typeof(DockPanel)); surface.AppendChild(row);
            var arrow=new FrameworkElementFactory(typeof(Path),"Arrow");
            arrow.SetValue(DockPanel.DockProperty,Dock.Right); arrow.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
            arrow.SetResourceReference(Shape.StrokeProperty,Muted); arrow.SetValue(Shape.StrokeThicknessProperty,1.6);
            arrow.SetValue(Path.DataProperty,Geometry.Parse("M 3,1 L 8,6 L 3,11")); row.AppendChild(arrow);
            var caption=new FrameworkElementFactory(typeof(ContentPresenter)); row.AppendChild(caption);
            var titleTemplate=new ControlTemplate(typeof(ToggleButton)) {VisualTree=surface};
            var open=new Trigger {Property=ToggleButton.IsCheckedProperty,Value=true};
            open.Setters.Add(new Setter(Path.DataProperty,Geometry.Parse("M 1,3 L 6,8 L 11,3"),"Arrow")); titleTemplate.Triggers.Add(open);
            var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
            hover.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Theme.Hover"),"Surface")); titleTemplate.Triggers.Add(hover);

            var stack=new FrameworkElementFactory(typeof(StackPanel));
            var toggle=new FrameworkElementFactory(typeof(ToggleButton));
            toggle.SetValue(Control.TemplateProperty,titleTemplate); toggle.SetResourceReference(Control.ForegroundProperty,Ink);
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
        static ControlTemplate MakeChipTemplate(bool pictureMode)
        {
            var surface=new FrameworkElementFactory(typeof(Border),"Surface");
            surface.SetValue(Border.CornerRadiusProperty,new CornerRadius(5));
            surface.SetValue(Border.BorderThicknessProperty,new Thickness(1));
            surface.SetResourceReference(Border.BackgroundProperty,"Theme.Control"); surface.SetResourceReference(Border.BorderBrushProperty,Line);
            var content=new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);
            if(pictureMode) {
                var row=new FrameworkElementFactory(typeof(DockPanel));
                row.SetValue(FrameworkElement.MarginProperty,new Thickness(6,0,4,0));
                var indicator=new FrameworkElementFactory(typeof(Grid));
                indicator.SetValue(DockPanel.DockProperty,Dock.Left);
                indicator.SetValue(FrameworkElement.HeightProperty,12.0); indicator.SetValue(FrameworkElement.WidthProperty,12.0);
                indicator.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
                indicator.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,5,0));
                var ring=new FrameworkElementFactory(typeof(Ellipse),"RadioRing");
                ring.SetResourceReference(Shape.StrokeProperty,Muted);
                var dot=new FrameworkElementFactory(typeof(Ellipse),"RadioDot");
                dot.SetValue(FrameworkElement.MarginProperty,new Thickness(3));
                dot.SetResourceReference(Shape.FillProperty,Mint); dot.SetValue(UIElement.VisibilityProperty,Visibility.Hidden);
                indicator.AppendChild(ring); indicator.AppendChild(dot); row.AppendChild(indicator);
                var label=new FrameworkElementFactory(typeof(TextBlock));
                label.SetValue(TextBlock.TextProperty,new TemplateBindingExtension(ContentControl.ContentProperty));
                label.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);
                label.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
                row.AppendChild(label); surface.AppendChild(row);
            } else surface.AppendChild(content);
            var template=new ControlTemplate(typeof(ToggleButton)) {VisualTree=surface};
            var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
            hover.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("Theme.StrongBorder"),"Surface")); template.Triggers.Add(hover);
            var selected=new Trigger {Property=ToggleButton.IsCheckedProperty,Value=true};
            selected.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Theme.Selected"),"Surface"));
            selected.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("Theme.StrongBorder"),"Surface"));
            if(pictureMode) {
                selected.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Visible,"RadioDot"));
                selected.Setters.Add(new Setter(Shape.StrokeProperty,new DynamicResourceExtension(Mint),"RadioRing"));
            }
            selected.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension(Mint))); template.Triggers.Add(selected);
            var focus=new Trigger {Property=UIElement.IsKeyboardFocusedProperty,Value=true};
            focus.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension(Mint),"Surface")); template.Triggers.Add(focus);
            var disabled=new Trigger {Property=UIElement.IsEnabledProperty,Value=false};
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.44,"Surface")); template.Triggers.Add(disabled);
            return template;
        }
        static void StyleChip(ToggleButton chip)
        {
            chip.Height=28; chip.Margin=new Thickness(2); chip.FontSize=11; chip.SetResourceReference(Control.ForegroundProperty,Ink);
            chip.FontWeight=FontWeights.SemiBold; ToolTipService.SetShowOnDisabled(chip,true);
            AutomationProperties.SetName(chip,chip.Content.ToString());
        }
        void SendSettings(bool previewEffect)
        {
            int before=selectedView;
            RefreshComparison(previewEffect && selectedView==1 ? (int?)0 : null);
            if(change!=null) change(selectedMode,selectedFactor);
            if(before!=selectedView && compare!=null) compare(ComparisonView);
        }
        void PreviewSelectedEffect(object sender,RoutedEventArgs e)
        {
            // Clicking the already checked effect doesn't raise Checked.
            if(!syncing && selectedView==1) views[0].IsChecked=true;
        }
        void ChangeNeuralProvider(object sender,RoutedEventArgs e)
        {
            if(syncing) return;
            RefreshAvailability();
            if(selectedBase==NeuralMode) {
                int next=modes[NeuralMode].IsEnabled ? (UseOpenNr ? OpenNeuralMode : NeuralMode) : 0;
                syncing=true;
                try {SyncPictureChoice(next);}
                finally {syncing=false;}
                SendSettings(next!=0);
            }
            if(OpenNrProviderChanged!=null) OpenNrProviderChanged();
        }
        void ChangePrepass(object sender,RoutedEventArgs e)
        {
            if(syncing) return;
            PreferOpenNrPrepass=nrPrepass.IsChecked==true;
            selectedMode=PictureWithPreference(selectedBase);
            RefreshQualityChoice(); SendSettings(selectedMode!=0);
        }
        void SyncPictureChoice(int index)
        {
            selectedMode=index;
            if(index==NeuralMode || (index==OpenNeuralMode && !PreferOpenNrPrepass)) {
                selectedBase=NeuralMode;
                bool open=index==OpenNeuralMode;
                if(UseOpenNr!=open) {UseOpenNr=open;RefreshAvailability();}
            } else {
                selectedBase=Array.IndexOf(ModeIds,PictureLabels.BaseMode(ModeIds[index]));
                if(PictureLabels.HasOpenNr(ModeIds[index])) PreferOpenNrPrepass=true;
            }
            modes[selectedBase].IsChecked=true;
            RefreshQualityChoice();
        }
        int PictureWithPreference(int baseIndex)
        {
            if(baseIndex==NeuralMode) return UseOpenNr ? OpenNeuralMode : NeuralMode;
            string combined=PictureLabels.WithOpenNr(ModeIds[baseIndex]);
            return PreferOpenNrPrepass && Flag(gpu,combined)==true ? Array.IndexOf(ModeIds,combined) : baseIndex;
        }
        internal int AvailablePicture(int requested)
        {
            if(requested==NeuralMode || (requested==OpenNeuralMode && !PreferOpenNrPrepass))
                return SupportsSelectedColor(requested) && Flag(gpu,ModeIds[requested])==true ? requested : 0;
            int baseIndex=Array.IndexOf(ModeIds,PictureLabels.BaseMode(ModeIds[requested]));
            if(baseIndex>=3 && Flag(gpu,ModeIds[baseIndex])!=true) return 0;
            return PictureWithPreference(baseIndex);
        }
        RadioButton AddGenerationProvider(Panel parent,string group,string provider,string label,string id)
        {
            var button=new RadioButton {GroupName="Rate"+group,Content=label,Template=ChipTemplate};
            StyleChip(button);AutomationProperties.SetAutomationId(button,id);
            button.Click+=PreviewSelectedEffect;
            button.Checked+=delegate {
                if(syncing) return;
                GenerationSelectionRevision++;
                FrameGenerationProvider=provider; selectedFactor=2; SendSettings(true);
            };
            parent.Children.Add(button); return button;
        }
        void SyncGenerationChoice()
        {
            FrameGenerationProvider=SavedProvider(FrameGenerationProvider,selectedFactor);
            var choice=factors[selectedFactor-1];
            switch(FrameGenerationProvider) {
                case "fruc":choice=frucGeneration;break;
                case "amd":choice=amdGeneration;break;
                case "dlss-fg":choice=dlssGeneration;break;
                case "xess-fg":choice=xessGeneration;break;
            }
            choice.IsChecked=true;
        }
        void RefreshComparison(int? requested=null)
        {
            bool available=selectedMode!=0 || selectedFactor>1;
            if(available==comparisonsAvailable && hdr==hdrComparisonAvailable && !requested.HasValue) return;
            int index=requested ?? (!comparisonsAvailable && available && selectedView==1 ? 0 : selectedView);
            if(index==HdrSdrView && !hdr) index=available ? 0 : 1;
            if(!available && index!=HdrSdrView) index=1;
            comparisonsAvailable=available;
            hdrComparisonAvailable=hdr;
            for(int i=0;i<views.Length;i++)
                views[i].IsEnabled=i==HdrSdrView ? hdr : i==1 || available;
            if(index==1 && selectedView!=1) previousView=selectedView;
            selectedView=index;
            bool wasSyncing=syncing; syncing=true;
            try {views[index].IsChecked=true;}
            finally {syncing=wasSyncing;}
            horizontalSplit.Visibility=WipeLayout.IsSplit(ComparisonView) ? Visibility.Visible : Visibility.Collapsed;
            UpdateComparisonGuide();
            comparisonGuide.Visibility=index==0 ? Visibility.Collapsed : Visibility.Visible;
            divider.Visibility=horizontalSplit.Visibility;
        }
        internal void RestorePlaybackControls(int picture,int multiplier,string provider,string view)
        {
            syncing=true;
            try {
                selectedFactor=multiplier; FrameGenerationProvider=provider;
                SyncPictureChoice(picture>=0 && picture<ModeIds.Length ? picture : 0); SyncGenerationChoice();
                int index=Array.IndexOf(ViewIds,view);
                RefreshComparison(index>=0 ? (int?)index : null);
            } finally {syncing=false;}
        }
        internal Dictionary<string,object> Capabilities { get {return gpu;} }
        void UpdateComparisonGuide()
        {
            comparisonGuide.Text=selectedView==HdrSdrView ? (WipeHorizontal ? "Top · SDR preview     Bottom · HDR10" : "Left · SDR preview     Right · HDR10") :
                !comparisonsAvailable ? (hdr ? "Choose HDR / SDR split to compare color output." : "Choose a picture effect or enable frame generation to compare.") :
                selectedView==1 ? "Original shows the source picture at its incoming frame rate. Select Enhanced to resume your effects." :
                selectedView==3 && WipeHorizontal ? "Top: Original    ·    Bottom: Enhanced" : "Left: Original    ·    Right: Enhanced";
        }
        public void ToggleOriginal()
        {
            int next=selectedView==1 ? previousView : 1;
            if(!views[next].IsEnabled && views[0].IsEnabled) next=0;
            if(views[next].IsEnabled) views[next].IsChecked=true;
        }
        public void Reset(int modeIndex,int multiplier)
        {
            hdr=idleHdr;
            selectedFactor=Math.Max(1,Math.Min(4,multiplier));
            syncing=true;
            try {SyncPictureChoice(Math.Max(0,Math.Min(ModeIds.Length-1,modeIndex))); SyncGenerationChoice(); RefreshComparison();}
            finally {syncing=false;}
            liveFsrVersion=liveFsrGenerationVersion=null; lastSample=-1; dial.SetRates(double.NaN,double.NaN,double.NaN,double.NaN);
            sourceValue.Text=generatedValue.Text="—"; targetValue.Text="Target —";
            pictureSize.Text="Waiting for video"; pictureScale.Text=""; activeQuality.Text="";
            displayedSize.Visibility=Visibility.Collapsed;
            foreach(var label in values.Values) label.Text="—";
            if(!EstimateGameFps) values["gameFps"].Text="Off";
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
            UpdateNeuralSetup();
            bool noAdapter=string.Equals(Text(gpu,"adapter"),"Unavailable",StringComparison.OrdinalIgnoreCase);
            for(int i=0;i<modes.Length;i++)
            {
                bool vendor=i>=3;
                string id=i==NeuralMode && UseOpenNr ? "open-nr" : ModeIds[i];
                bool available=i==0 || (!noAdapter && (!vendor || Flag(gpu,id)==true));
                string reason=id=="open-nr" ? "OpenDLSS-NR at source resolution using the selected model." : Descriptions[i];
                if(!available) reason=gpu==null ? "Checking GPU support." : Text(gpu,id+"Reason") ?? Text(gpu,"probeFailure") ?? "This mode is unavailable on the active GPU.";
                if(hdr && id=="dlss-nr" && available) {
                    available=false; reason=ModeLabels[i]+" requires SDR output.";
                }
                if(ModeIds[i]=="fsr-temporal" && available && !string.IsNullOrEmpty(Text(gpu,"fsrVersion")))
                    reason="AMD temporal upscaling · "+Text(gpu,"fsrVersion");
                modes[i].IsEnabled=available; modes[i].ToolTip=reason;
            }
            modes[6].Content=PictureLabels.TemporalFsr(liveFsrVersion ?? Text(gpu,"fsrVersion"));
            ClassifyFsr(modes[6],liveFsrVersion ?? Text(gpu,"fsrVersion"),aiPictures,standardPictures,pendingPicture);
            RefreshGenerationAvailability();
            for(int i=0;i<factors.Length;i++) factors[i].IsEnabled=i==0 || !noAdapter;
            RefreshQualityChoice();
        }
        void RefreshGenerationAvailability()
        {
            SetSwapChainAvailability(dlssGeneration,"dlssFrameGeneration");
            SetSwapChainAvailability(xessGeneration,"xessFrameGeneration");
            string version=liveFsrGenerationVersion ?? Text(gpu,"amdFrameGenerationVersion");
            ClassifyFsr(amdGeneration,version,aiGeneration,standardGeneration,pendingGeneration);
            amdGeneration.IsEnabled=Flag(gpu,"amdFrameGeneration")==true;
            amdGeneration.ToolTip=amdGeneration.IsEnabled ? "AMD FSR frame generation "+version+" · one generated frame between source frames." :
                Text(gpu,"amdFrameGenerationReason") ?? Text(gpu,"probeFailure") ?? "Checking GPU support.";
            frucGeneration.IsEnabled=Flag(gpu,"frucFrameGeneration")==true;
            frucGeneration.ToolTip=frucGeneration.IsEnabled ? "NVIDIA FRUC generates one intermediate frame between two source frames in SDR or HDR10." :
                Text(gpu,"frucFrameGenerationReason") ?? Text(gpu,"probeFailure") ?? "Checking NVIDIA FRUC support.";
            AutomationProperties.SetHelpText(frucGeneration,(string)frucGeneration.ToolTip);
            frucSetup.Visibility=Flag(gpu,"frucRuntimeImportAvailable")==true ? Visibility.Visible : Visibility.Collapsed;
        }
        void SetSwapChainAvailability(RadioButton button,string key)
        {
            button.IsEnabled=Flag(gpu,key)==true;
            button.ToolTip=button.IsEnabled ? "Experimental Direct3D 12 interpolation with motion estimated from video." :
                Text(gpu,key+"Reason") ?? Text(gpu,"probeFailure") ?? "Checking frame generation support.";
            AutomationProperties.SetHelpText(button,(string)button.ToolTip);
        }
        void RefreshQualityChoice()
        {
            bool hasBase=selectedBase!=NeuralMode;
            string combined=hasBase ? PictureLabels.WithOpenNr(ModeIds[selectedBase]) : null;
            bool wasSyncing=syncing; syncing=true;
            try {nrPrepass.IsChecked=hasBase && PictureLabels.HasOpenNr(ModeIds[selectedMode]);}
            finally {syncing=wasSyncing;}
            bool supported=hasBase && Flag(gpu,combined)==true;
            nrPrepass.IsEnabled=supported || nrPrepass.IsChecked==true;
            string detail=!hasBase ? "The selected picture mode already applies neural rendering." :
                supported ? "Apply neural rendering at the stream resolution before the selected picture effect." :
                Text(gpu,combined+"Reason") ?? Text(gpu,"open-nrReason") ?? Text(gpu,"probeFailure") ?? "Checking GPU and model support.";
            nrPrepass.ToolTip=detail; AutomationProperties.SetHelpText(nrPrepass,detail);
            modelGroup.Visibility=selectedBase==4 || selectedBase==5 ? Visibility.Visible : Visibility.Collapsed;
            modelChoice.IsEnabled=(selectedBase==4 || selectedBase==5) && modes[selectedBase].IsEnabled;
            qualityGroup.Visibility=selectedBase==4 ? Visibility.Visible : Visibility.Collapsed;
            qualityChoice.IsEnabled=selectedBase==4 && modes[4].IsEnabled;
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
            double dw=Number(data,"displayedWidth"),dh=Number(data,"displayedHeight");
            bool fitted=ow>0 && oh>0 && dw>0 && dh>0 && (dw!=ow || dh!=oh);
            displayedSize.Visibility=fitted ? Visibility.Visible : Visibility.Collapsed;
            if(fitted) displayedSize.Text="On screen · "+Dimensions(dw,dh);
            if(!(iw>0 && ih>0 && ow>0 && oh>0)) {
                pictureSize.Text="Waiting for video"; pictureScale.Text=""; return;
            }
            pictureSize.Text=Dimensions(iw,ih)+" → "+Dimensions(ow,oh);
            bool native=iw==ow && ih==oh;
            if(native) {
                string mode=PictureLabels.BaseMode(Text(data,"effectivePictureMode"));
                pictureScale.Text=mode=="dlaa" ? "1× · DLAA" : mode=="nis" ? "1× · NIS sharpen" : "1× · Native";
            }
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
            values["incomingFps"].Text=Metric(Number(snapshot,"streamFps"),"","0.#");
            values["gameFps"].Text=EstimateGameFps ? Metric(Number(snapshot,"estimatedGameFps"),"","0") : "Off";
            UpdatePictureSize(snapshot);
            values["surface"].Text=Dimensions(Number(snapshot,"surfaceWidth"),Number(snapshot,"surfaceHeight"));
            values["refresh"].Text=Metric(Number(snapshot,"displayRefreshHz")," Hz","0.##");
            string renderer=Text(snapshot,"renderer"),processing=Text(snapshot,"processingRenderer");
            values["renderer"].Text=Dashboard.RendererName(renderer ?? processing);
            if(processing!=null && renderer!=null && processing!=renderer)
                values["renderer"].Text+=" · "+Dashboard.RendererName(processing)+" processing";
            if(PictureLabels.IsCombined(Text(snapshot,"effectivePictureMode")) && renderer!="vulkan")
                values["renderer"].Text+=" · Vulkan NR";
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
            values["videoLoss"].Text=Metric(Number(snapshot,"videoFramesLost"),"","0");
            values["localSkips"].Text=Metric(Number(snapshot,"replacedFrames"),"","0");
            values["slowHandoffs"].Text=Metric(Number(snapshot,"slowVideoHandoffs"),"","0");
            values["concealments"].Text=Metric(Number(snapshot,"audioConcealments"),"","0");
            values["queueTrims"].Text=Metric(Number(snapshot,"audioQueueTrims"),"","0");
            values["rtt"].Text=Metric(Number(snapshot,"handshakeRttMs")," ms","0.0");
            if(hdrFlag.HasValue && hdr!=hdrFlag.Value) {
                hdr=hdrFlag.Value;
                RefreshAvailability();
            }
            string requested=Text(snapshot,"mode"), effective=Text(snapshot,"effectivePictureMode");
            string fsrVersion=PictureLabels.BaseMode(effective)=="fsr-temporal" ? Text(snapshot,"fsrVersion") : null;
            if(fsrVersion!=liveFsrVersion) {
                liveFsrVersion=fsrVersion;
                modes[6].Content=PictureLabels.TemporalFsr(liveFsrVersion ?? Text(gpu,"fsrVersion"));
                ClassifyFsr(modes[6],liveFsrVersion ?? Text(gpu,"fsrVersion"),aiPictures,standardPictures,pendingPicture);
            }
            int mode=Array.IndexOf(ModeIds,requested); bool? comparing=Flag(snapshot,"compare");
            int priorView=selectedView;
            // The dashboard owns the requested settings. Only a rejected, acknowledged
            // switch restores those controls; older telemetry can arrive after a new click.
            RefreshComparison();
            if(priorView!=selectedView && compare!=null) compare(ComparisonView);
            int quality=Array.IndexOf(QualityIds,Text(snapshot,"activeDlssQuality"));
            string requestedBase=PictureLabels.BaseMode(requested),effectiveBase=PictureLabels.BaseMode(effective);
            activeQuality.Text=requestedBase=="dlss" && effectiveBase=="dlaa" ? "Native · DLAA" :
                effectiveBase=="dlss" && quality>0 ? "Active · "+QualityLabels[quality] : "";
            string message=Text(snapshot,"effectNotice");
            string chosenProvider=Text(snapshot,"frameGenerationProvider");
            if((chosenProvider=="dlss-fg" || chosenProvider=="xess-fg") && comparing!=true && string.IsNullOrEmpty(message)) {
                string label=chosenProvider=="dlss-fg" ? "NVIDIA DLSS FG" : "Intel XeSS FG";
                double proxyRate=Number(snapshot,"proxyGeneratedFps");
                message=Text(snapshot,"activeFrameGenerationProvider")==chosenProvider && proxyRate>0
                    ? label+" · "+Metric(proxyRate," generated FPS","0.#")
                    : label+" selected · waiting for a generated presentation";
            }
            string fgVersion=Text(snapshot,"activeFrameGenerationVersion");
            string activeAmdVersion=chosenProvider=="amd" && !string.IsNullOrWhiteSpace(fgVersion) ? fgVersion : null;
            if(liveFsrGenerationVersion!=activeAmdVersion) {
                liveFsrGenerationVersion=activeAmdVersion;
                RefreshGenerationAvailability();
            }
            if(Text(snapshot,"frameGenerationProvider")=="fruc" && comparing!=true && string.IsNullOrEmpty(message)) {
                double rate=Number(snapshot,"frucPresentedFps");
                message=Text(snapshot,"activeFrameGenerationProvider")=="fruc" && rate>0
                    ? "NVIDIA FRUC · "+Metric(rate," generated FPS","0.#")
                    : "NVIDIA FRUC selected · waiting for a generated frame to present";
            }
            if(comparing!=true && activeAmdVersion!=null && string.IsNullOrEmpty(Text(snapshot,"effectNotice")))
                message=(string.IsNullOrEmpty(message) ? "" : message+" · ")+"AMD frame generation "+fgVersion;
            int active=Array.IndexOf(ModeIds,effective);
            if(string.IsNullOrEmpty(message) && comparing!=true && active>=0 && mode>=0 && active!=mode && effective!="dlaa") message="Active picture · "+ModeLabels[active];
            notice.Text=message ?? ""; notice.Visibility=string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        sealed class FrameRateDial : FrameworkElement
        {
            Brush ThemeBrush(string role) {return (Brush)FindResource("Theme."+role);}
            static readonly Typeface NumberFace=new Typeface(new FontFamily("Segoe UI"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal);
            static readonly Typeface LabelFace=new Typeface("Segoe UI");
            double target,source,presented,generated;
            int maximum=DefaultDialMaximum;
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
            internal void SetMaximum(int limit)
            {
                maximum=limit;
                SetRates(target,source,presented,generated);
            }
            public void SetRates(double aim,double originals,double submitted,double synthetic)
            {
                target=aim; source=originals; presented=submitted; generated=synthetic;
                string detail="Output "+Describe(presented)+". Source "+Describe(source)+". Generated "+Describe(generated)+". Target "+Describe(target)+". Dial maximum "+maximum+" FPS.";
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
                var ink=ThemeBrush("Text");
                var muted=ThemeBrush("Muted");
                var minorTick=new Pen(ThemeBrush("StrongBorder"),1);
                var majorTick=new Pen(muted,1.3);
                for(int i=0;i<=16;i++) {
                    double angle=i*Math.PI/16;
                    bool major=i%4==0;
                    dc.DrawLine(major ? majorTick:minorTick,Polar(hub,radius-10,angle),Polar(hub,radius-(major?3:i%2==0?5:7),angle));
                    if(major) CenterText(dc,(maximum*i/16.0).ToString("0.##",CultureInfo.InvariantCulture),
                        Polar(hub,radius+7,angle),10,LabelFace,muted);
                }
                double ring=radius-18,end=Math.PI*Math.Min(1,Math.Max(0,presented/maximum));
                // At saturation the two segments still show their share of submitted frames.
                double split=presented>0 ? end*source/presented : 0;
                Arc(dc,hub,ring,0,Math.PI,ThemeBrush("Track"),true,true);
                Arc(dc,hub,ring,0,split,ThemeBrush("Source"),true,generated==0);
                Arc(dc,hub,ring,split,end,ThemeBrush("Generated"),source==0,true);
                if(!double.IsNaN(target)) {
                    double a=Math.PI*Math.Min(1,Math.Max(0,target/maximum));
                    var tip=Polar(hub,radius-16,a); var back=Polar(hub,radius-7,a);
                    double dx=3.8*Math.Sin(a),dy=-3.8*Math.Cos(a);
                    var marker=new StreamGeometry();
                    using(var g=marker.Open()) {
                        g.BeginFigure(tip,true,true);
                        g.LineTo(new Point(back.X+dx,back.Y+dy),true,false);
                        g.LineTo(new Point(back.X-dx,back.Y-dy),true,false);
                    }
                    dc.DrawGeometry(ink,new Pen(ThemeBrush("Panel"),1),marker);
                }
                CenterText(dc,Metric(presented,"","0.#"),new Point(hub.X,hub.Y-24),29,NumberFace,ink);
                CenterText(dc,"Output FPS",new Point(hub.X,hub.Y-2),10.5,LabelFace,muted);
            }
        }
    }
}
