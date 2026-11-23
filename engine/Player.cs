using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Cadrivect.Engine;

internal sealed class PlayerWindow : Form
{
    const int Style=-16, ExtendedStyle=-20;
    const int OverlappedWindow=0x00cf0000, Popup=unchecked((int)0x80000000);
    const int OverlayStyles=0x08000000 | 0x20 | 0x80, AppWindow=0x40000;
    const int NoActivateAndFrameChanged=0x30;
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetStyle(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")] static extern int SetStyle(IntPtr hwnd,int index,int value);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,int flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    static readonly int ControlMessage=RegisterWindowMessage("Cadrivect.Player.Controls.0.3");
    bool fullscreen, quitRegistered, displayChanged;
    readonly IntPtr source;
    readonly bool moveSource;
    readonly Size chosenSize;
    DisplayTarget target;
    Rectangle restored;
    public string ComparisonView="enhanced";
    string previousView="enhanced";
    void ToggleOriginal()
    {
        if(ComparisonView=="original") ComparisonView=previousView;
        else {previousView=ComparisonView; ComparisonView="original";}
    }
    public PlaybackControls ControlPanel;
    public event Action<string,int> SettingsRequested;
    public bool Fullscreen { get { return fullscreen; } }
    public bool Ended { get; private set; }
    public PlayerWindow(IntPtr source,DisplayTarget target,Size chosenSize,bool windowed)
    {
        this.source=source; this.target=target; this.chosenSize=chosenSize;
        moveSource=DisplayTarget.IsPlaybackSource(source);
        Text="P5Cast Player"; BackColor=Color.Black; KeyPreview=true;
        AutoScaleMode=AutoScaleMode.None;
        StartPosition=FormStartPosition.Manual;
        MinimumSize=new Size(240,150);
        restored=InitialWindow(target);
        fullscreen=!windowed;
        Bounds=fullscreen ? target.Bounds : restored;
        if(moveSource) target.MoveSourceWindow(source);
        ContextMenuStrip=new ContextMenuStrip();
        ContextMenuStrip.Items.Add("Playback controls    Ctrl+Alt+O",null,delegate { if(ControlPanel!=null) ControlPanel.Open(); });
    }
    Rectangle InitialWindow(DisplayTarget display)
    {
        var area=display.WorkingArea;
        var client=chosenSize.IsEmpty ? new Size(960,540):chosenSize;
        Size border=SizeFromClientSize(new Size(0,0));
        double fit=Math.Min(1,Math.Min((double)Math.Max(1,area.Width-border.Width)/client.Width,
                                      (double)Math.Max(1,area.Height-border.Height)/client.Height));
        var outer=new Size(Math.Min(area.Width,(int)(client.Width*fit)+border.Width),
                           Math.Min(area.Height,(int)(client.Height*fit)+border.Height));
        return Centered(outer,area);
    }
    static Rectangle Centered(Size size,Rectangle area)
    {
        int w=Math.Min(size.Width,area.Width), h=Math.Min(size.Height,area.Height);
        return new Rectangle(area.Left+(area.Width-w)/2,area.Top+(area.Height-h)/2,w,h);
    }
    void ApplyBounds(Rectangle rect)
    {
        if(!SetWindowPos(Handle,new IntPtr(fullscreen && source!=IntPtr.Zero ? -1 : -2),rect.X,rect.Y,rect.Width,rect.Height,NoActivateAndFrameChanged))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public void EnableShortcuts()
    {
        DisplayTarget.MarkPlayer(Handle,true);
        quitRegistered=WindowsApi.RegisterHotKey(Handle,1,0x4003,(int)Keys.Q);
        if(fullscreen && !quitRegistered && source!=IntPtr.Zero)
            throw new InvalidOperationException("Ctrl+Alt+Q is already assigned. Close the other player or start in windowed mode.");
        WindowsApi.RegisterHotKey(Handle,2,0x4003,(int)Keys.B);
        WindowsApi.RegisterHotKey(Handle,3,0x4003,(int)Keys.Enter);
        WindowsApi.RegisterHotKey(Handle,4,0x4003,(int)Keys.Right);
        WindowsApi.RegisterHotKey(Handle,5,0x4003,(int)Keys.O);
        ApplyBounds(fullscreen ? target.Bounds : restored);
    }
    public void ToggleFullscreen()
    {
        if(!fullscreen && !quitRegistered && source!=IntPtr.Zero) return;
        target=DisplayTarget.Select(DisplayTarget.DeviceForWindow(Handle),source);
        if(!fullscreen) {
            restored=WindowState==FormWindowState.Normal ? Bounds : RestoreBounds;
            if(WindowState!=FormWindowState.Normal) WindowState=FormWindowState.Normal;
        }
        fullscreen=!fullscreen;
        var style=GetStyle(Handle,Style);
        var extended=GetStyle(Handle,ExtendedStyle);
        SetStyle(Handle,Style,fullscreen ? (style & ~OverlappedWindow)|Popup : (style & ~Popup)|OverlappedWindow);
        SetStyle(Handle,ExtendedStyle,fullscreen && source!=IntPtr.Zero ? (extended & ~AppWindow)|OverlayStyles : (extended & ~OverlayStyles)|AppWindow);
        ApplyBounds(fullscreen ? target.Bounds : FitRestored(target));
        if(moveSource && fullscreen) WindowsApi.SetForegroundWindow(source);
    }
    Rectangle FitRestored(DisplayTarget destination)
    {
        return destination.WorkingArea.Contains(restored) ? restored : Centered(restored.Size,destination.WorkingArea);
    }
    void MoveTo(DisplayTarget destination)
    {
        target=destination;
        if(!fullscreen) {
            var size=WindowState==FormWindowState.Normal ? Bounds.Size : RestoreBounds.Size;
            if(WindowState!=FormWindowState.Normal) WindowState=FormWindowState.Normal;
            restored=Centered(size,target.WorkingArea);
        }
        if(moveSource) target.MoveSourceWindow(source);
        ApplyBounds(fullscreen ? target.Bounds : restored);
    }
    void CycleDisplay()
    {
        var displays=DisplayTarget.Enumerate();
        string current=DisplayTarget.DeviceForWindow(Handle);
        for(int i=0;i<displays.Length;i++) if(displays[i].DeviceName==current) {
            if(displays.Length>1) MoveTo(displays[(i+1)%displays.Length]);
            return;
        }
    }
    public bool ConsumeDisplayChange()
    {
        if(!displayChanged) return false;
        displayChanged=false;
        target=DisplayTarget.Select(DisplayTarget.DeviceForWindow(Handle),source);
        if(fullscreen) ApplyBounds(target.Bounds);
        return true;
    }
    protected override bool ShowWithoutActivation { get { return fullscreen && source!=IntPtr.Zero; } }
    protected override CreateParams CreateParams
    {
        get { var cp=base.CreateParams;
            if(fullscreen) {
                cp.Style=(cp.Style & ~OverlappedWindow)|Popup;
                cp.ExStyle=source==IntPtr.Zero ? cp.ExStyle|AppWindow : (cp.ExStyle & ~AppWindow)|OverlayStyles;
            }
            return cp;
        }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.KeyCode==Keys.Escape) Close(); else if(e.KeyCode==Keys.B) ToggleOriginal();
        base.OnKeyDown(e);
    }
    protected override bool ProcessCmdKey(ref Message message,Keys keys)
    {
        if(source==IntPtr.Zero && (keys & Keys.Modifiers)==(Keys.Control|Keys.Alt)) {
            switch(keys & Keys.KeyCode) {
                case Keys.Enter: ToggleFullscreen(); return true;
                case Keys.O: if(ControlPanel!=null) ControlPanel.Toggle(); return true;
                case Keys.Q: Close(); return true;
                case Keys.Right: CycleDisplay(); return true;
                case Keys.B: ToggleOriginal(); return true;
            }
        }
        return base.ProcessCmdKey(ref message,keys);
    }
    protected override void WndProc(ref Message message)
    {
        if(message.Msg==0x0312)
        {
            if(message.WParam.ToInt32()==1) Close();
            if(message.WParam.ToInt32()==2) ToggleOriginal();
            if(message.WParam.ToInt32()==3) ToggleFullscreen();
            if(message.WParam.ToInt32()==4) CycleDisplay();
            if(message.WParam.ToInt32()==5 && ControlPanel!=null) ControlPanel.Toggle();
            return;
        }
        if(message.Msg==ControlMessage) {
            long mode=message.WParam.ToInt64(), factor=message.LParam.ToInt64();
            if(ControlPanel==null || mode< -1 || mode>=PlaybackControls.Modes.Length || (factor!= -1 && (factor<1 || factor>4))) return;
            if(mode== -1 && factor== -1) ControlPanel.Open();
            else if(SettingsRequested!=null) SettingsRequested(mode== -1 ? null : PlaybackControls.Modes[(int)mode],(int)factor);
            message.Result=new IntPtr(1); return;
        }
        if(message.Msg==DisplayTarget.MoveMessage) {
            var next=DisplayTarget.FromMonitor(message.WParam);
            if(next!=null) { MoveTo(next); message.Result=new IntPtr(1); }
            return;
        }
        if(message.Msg==0x007e) displayChanged=true; // WM_DISPLAYCHANGE
        if(message.Msg==0x02e0) displayChanged=true; // WM_DPICHANGED
        if(message.Msg==0x0232 && !fullscreen && moveSource) { // after a finished title-bar drag
            target=DisplayTarget.Select(DisplayTarget.DeviceForWindow(Handle),source);
            target.MoveSourceWindow(source);
        }
        if(fullscreen && source!=IntPtr.Zero && message.Msg==0x0084) { message.Result=new IntPtr(-1); return; }
        base.WndProc(ref message);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Ended=true; DisplayTarget.MarkPlayer(Handle,false);
        for(int id=1;id<=5;id++) WindowsApi.UnregisterHotKey(Handle,id);
        base.OnFormClosed(e);
    }
}

internal sealed class SampleWindow : Form
{
    public int Tick;
    public SampleWindow()
    {
        Text="P5Cast capture test source"; ClientSize=new Size(480,270);
        StartPosition=FormStartPosition.Manual; Location=new Point(50,60);
        DoubleBuffered=true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(32+(Tick*3)%150,55,120));
        e.Graphics.FillRectangle(Brushes.Gold,(Tick*7)%ClientSize.Width,30,55,70);
    }
}
internal static class Player
{
    static int Main(string[] args)
    {
        // WinForms and WindowsRuntimeMarshal require an STA thread for this desktop integration.
        int result=1;
        var thread=new Thread(delegate() { result=Run(args); });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        return result;
    }
    internal static int Run(string[] args,HostedSurface host=null)
    {
        bool testing=Array.IndexOf(args,"--self-test")>=0;
        var samples=new HashSet<int>();
        var liveFrameAges=new List<double>();
        try
        {
            if(Array.IndexOf(args,"--capabilities")>=0) {
                string json=GpuCapabilities.ProbeForBackend(Presentation.Resolve(Option(args,"--renderer","auto"))).ToJson();
                string output=Option(args,"--report",null);
                if(output!=null) File.WriteAllText(output,json);
                Console.WriteLine(json); return 0;
            }
            if(host==null) {
                WindowsApi.SetProcessDpiAwarenessContext(new IntPtr(-4));
                Application.EnableVisualStyles();
                if(Array.IndexOf(args,"--surface")>=0) throw new ArgumentException("Use EmbeddedPlayback.Run in the process that owns the playback surface.");
            }
            bool windowed=Array.IndexOf(args,"--windowed")>=0 || (testing && Array.IndexOf(args,"--fullscreen")<0);
            string displayName=Option(args,"--display","primary");
            string resolution=Option(args,"--resolution","native").ToLowerInvariant();
            Size raster=DisplayTarget.ResolutionLimit(resolution);
            string backend=Presentation.Resolve(Option(args,"--renderer","auto"));
            string hdr=Option(args,"--hdr","auto").ToLowerInvariant();
            if(hdr!="auto" && hdr!="off" && hdr!="hdr10") throw new ArgumentException("Choose auto, off, or hdr10 for --hdr.");
            string mode=Option(args,"--mode",testing ? "fsr":"original").ToLowerInvariant();
            string dlssQuality=EmbeddedPlayback.ValidateDlssQuality(Option(args,"--dlss-quality","auto").ToLowerInvariant());
            string filename=Option(args,"--report",null);
            string pipe=Option(args,"--direct-pipe",null), runtime=Option(args,"--direct-runtime",null);
            bool direct=pipe!=null;
            bool demo=Array.IndexOf(args,"--demo")>=0;
            if(demo && (host==null || direct || Array.IndexOf(args,"--hwnd")>=0)) throw new ArgumentException("The dashboard demo uses only its playback surface.");
            bool captureWindow=!demo && !direct;
            if(direct && (testing || string.IsNullOrWhiteSpace(runtime))) throw new ArgumentException("Direct playback requires --direct-runtime and a live source.");
            string targetOption=Option(args,"--target-fps",direct ? "60":demo ? "30":null);
            int targetFps=targetOption==null ? 0 : int.Parse(targetOption,CultureInfo.InvariantCulture);
            if(targetOption!=null && targetFps!=30 && targetFps!=60) throw new ArgumentException("Choose 30 or 60 for --target-fps.");
            int multiplier=int.Parse(Option(args,"--multiplier",testing ? "3":"1"),CultureInfo.InvariantCulture);
            bool vendor=Renderer.IsVendor(mode);
            if(Array.IndexOf(PictureLabels.Ids,mode)<0) throw new ArgumentException("Unknown picture mode: "+mode);
            if(mode=="nis" && !NisScaler.SupportsBackend(backend)) throw new ArgumentException(NisScaler.BackendReason);
            string generation=Option(args,"--frame-generation","p5cast").ToLowerInvariant();
            Renderer.ValidateFrameGeneration(generation,multiplier);
            if(host==null && generation=="amd" && backend!="d3d12" && backend!="vulkan") throw new ArgumentException("AMD frame generation requires Direct3D 12 or Vulkan.");
            if(host==null && generation=="fruc" && !Renderer.SupportsFrucBackend(backend)) throw new ArgumentException(Renderer.FrucBackendReason);
            if(multiplier<1 || multiplier>4) throw new ArgumentException("Multiplier must be between 1 and 4.");
            int seconds=int.Parse(Option(args,"--seconds",testing ? "4":"0"),CultureInfo.InvariantCulture);
            using(var sample=testing ? new SampleWindow():null)
            {
                IntPtr source;
                if(testing) { sample.Show(); sample.Update(); source=sample.Handle; }
                else source=captureWindow ? FindSource(args):IntPtr.Zero;
                using(var window=host==null ? new PlayerWindow(source,DisplayTarget.Select(displayName,source),raster,windowed):null)
                {
                    if(window!=null) {
                        window.ComparisonView=EmbeddedPlayback.ValidateComparison(Option(args,"--comparison","enhanced"));
                        window.Show(); window.EnableShortcuts();
                        if(!testing && !direct) WindowsApi.SetForegroundWindow(source);
                    }
                    using(var renderer=new Renderer(host==null ? window.Handle:host.Handle,host==null ? window.ClientSize:host.ClientSize,raster,hdr,mode,host!=null,backend))
                    using(var capture=captureWindow ? new WindowCapture(source,renderer.Device,renderer.HdrEnabled,backend=="vulkan" || backend=="d3d12"):null)
                    using(var decoded=direct ? new DirectVideo(pipe,runtime):null)
                    using(var offline=demo ? new DemoVideo(renderer,host.Handle,targetFps):null)
                    using(var controls=host==null ? new PlaybackControls(window,source,mode,multiplier,targetFps,backend):null)
                    using(var live=filename==null ? null:new LiveReport(filename))
                    {
                        renderer.SetDlssQuality(dlssQuality);
                        renderer.SetDlssModel(Option(args,"--dlss-model","auto").ToLowerInvariant());
                        string requestedMode=null, settingsNotice=null;
                        int requestedMultiplier=multiplier;
                        bool changeRequested=false,compare=false;
                        float initialPosition=host!=null ? host.WipePosition : EmbeddedPlayback.ParseWipePosition(Option(args,"--wipe-position","0.5"));
                        float wipePosition=initialPosition;
                        bool initialHorizontal=Array.IndexOf(args,"--wipe-horizontal")>=0;
                        bool wipeHorizontal=host!=null ? host.WipeHorizontal : initialHorizontal;
                        int gripState=0;
                        float gripScale=1;
                        LoupeState loupe=null;
                        string previousView="enhanced",comparisonView="enhanced";
                        Action<string,int> change=delegate(string picture,int factor) {
                            if(picture!=null) requestedMode=picture;
                            if(factor>0) requestedMultiplier=factor;
                            changeRequested=true;
                        };
                        Action<string,int,string> hostedChange=delegate(string picture,int factor,string provider) {
                            if(factor>0) {
                                try {Renderer.ValidateFrameGeneration(provider ?? generation,factor);}
                                catch(ArgumentException error) {settingsNotice=error.Message;return;}
                            }
                            if(provider!=null) generation=provider;
                            settingsNotice=null;
                            change(picture,factor);
                        };
                        if(controls!=null) {
                            Action<string,int> standaloneChange=delegate(string picture,int factor) {
                                if(factor>0) generation="p5cast";
                                change(picture,factor);
                            };
                            window.ControlPanel=controls; controls.SettingChanged+=standaloneChange; window.SettingsRequested+=standaloneChange;
                            if(Array.IndexOf(args,"--controls")>=0) controls.Open();
                        }
                        var clock=Stopwatch.StartNew();
                        double nextSample=0, previousTime=0, period=16.667, due=0, pairStart=0, lastSubmit=-1000;
                        bool repaintPending=false;
                        double lastRepaint=-1000;
                        bool verifyOutput=Array.IndexOf(args,"--verify-output")>=0;
                        double nextOutputSample=0;
                        int step=0, count=1, drawTick=0;
                        double firstInput=0,lastInput=0,lastRealAge=-1;
                        double lastSnapshot=0,lastFile=0; int sampleId=0,snapshotInput=0,snapshotPresented=0,snapshotGenerated=0;
                        int presentedSettingsRevision=0;
                        WindowsApi.timeBeginPeriod(1);
                        try
                        {
                            while((host==null ? !window.Ended : host.Exists) && (!captureWindow || WindowsApi.IsWindow(source)))
                            {
                                // The portable presenter owns a child HWND on this playback thread.
                                if(host==null || renderer.Backend!="d3d11") Application.DoEvents();
                                // Read the marker before draining requests. A later marker can only be acknowledged
                                // by a later iteration, even if a cancel and a switch arrive in the same frame.
                                int settingsRevision=host==null ? 0 : Volatile.Read(ref host.SettingsRevision);
                                if(host!=null) host.TakeChanges(hostedChange);
                                if((window!=null && window.Ended) || (seconds>0 && clock.Elapsed.TotalSeconds>=seconds)) break;
                                comparisonView=host==null ? window.ComparisonView:host.ComparisonView;
                                compare=comparisonView=="original";
                                double now=clock.Elapsed.TotalMilliseconds;
                                float requestedPosition=host!=null ? host.WipePosition : initialPosition;
                                bool horizontal=host!=null ? host.WipeHorizontal : initialHorizontal;
                                int requestedGrip=host!=null ? host.WipeGripState : 0;
                                float requestedScale=host!=null ? host.WipeGripScale : 1;
                                bool redraw=comparisonView=="wipe" && (requestedPosition!=wipePosition || horizontal!=wipeHorizontal ||
                                                                      requestedGrip!=gripState || requestedScale!=gripScale);
                                gripState=requestedGrip; gripScale=requestedScale;
                                wipeHorizontal=horizontal;
                                wipePosition=requestedPosition;
                                if(redraw || previousView!=comparisonView) renderer.SetWipe(comparisonView=="wipe",wipePosition,wipeHorizontal,gripState,gripScale);
                                var requestedLoupe=host==null ? null : host.Loupe;
                                if(requestedLoupe!=loupe) {
                                    loupe=requestedLoupe;
                                    if(loupe==null) renderer.SetLoupe(false,0,0,0,1);
                                    else renderer.SetLoupe(true,loupe.X,loupe.Y,loupe.Level,loupe.Scale);
                                    redraw=true;
                                }
                                string newModel=host==null ? null : Interlocked.Exchange(ref host.PendingDlssModel,null);
                                if(newModel!=null && newModel!=renderer.DlssModelHint) {
                                    renderer.SetDlssModel(newModel);
                                    if(mode=="dlss" || mode=="dlaa") {step=0; previousTime=0; redraw=true;}
                                }
                                string newQuality=host==null ? null : Interlocked.Exchange(ref host.PendingDlssQuality,null);
                                if(newQuality!=null && newQuality!=renderer.DlssQuality) {
                                    renderer.SetDlssQuality(newQuality);
                                    if(mode=="dlss") {step=0; previousTime=0; redraw=true;}
                                }
                                string newSize,newHdr;
                                if(host!=null && host.TakeOutput(out newSize,out newHdr)) {
                                    try {
                                        renderer.SetOutput(DisplayTarget.ResolutionLimit(newSize ?? resolution),newHdr ?? hdr);
                                        resolution=newSize ?? resolution; hdr=newHdr ?? hdr; settingsNotice=null;
                                        step=0; previousTime=0; redraw=true;
                                    } catch(InvalidOperationException ex) { settingsNotice=ex.Message; }
                                }
                                if(direct && decoded.Closed) {
                                    if(!string.IsNullOrEmpty(decoded.Error)) throw new IOException(decoded.Error);
                                    if(renderer.Captured==0) throw new IOException("The Direct session closed before the first video frame.");
                                    break;
                                }
                                if(changeRequested || previousView!=comparisonView) {
                                    if(requestedMode!=null) {
                                        try {
                                            renderer.SetPictureMode(requestedMode);
                                            mode=requestedMode; settingsNotice=null;
                                        } catch(InvalidOperationException ex) { settingsNotice=ex.Message; }
                                    }
                                    multiplier=requestedMultiplier; vendor=Renderer.IsVendor(mode);
                                    renderer.ResetFrameHistory(); if(controls!=null) controls.Sync(mode,multiplier);
                                    renderer.SetSideBySide(comparisonView=="side-by-side");
                                    step=0; previousTime=0; previousView=comparisonView;
                                    redraw=true;
                                    requestedMode=null; changeRequested=false;
                                }
                                renderer.SetFrameGenerationProvider(compare ? "p5cast" : generation);
                                if(controls!=null) {
                                    controls.SetNotice(settingsNotice ?? (!string.IsNullOrEmpty(renderer.FrameGenerationNotice) ? renderer.FrameGenerationNotice : renderer.EffectNotice));
                                    controls.UpdateLive(now,renderer,compare,lastRealAge,decoded);
                                }
                                if((host!=null || live!=null) && now-lastSnapshot>=(host==null ? 1000:250)) {
                                    double perSecond=1000/(now-lastSnapshot);
                                    string snapshot=string.Format(CultureInfo.InvariantCulture,
                                        "{{\"captured\":{0},\"presented\":{1},\"generated\":{2},\"sourceFps\":{3:F1},\"submittedFps\":{4:F1},\"generatedFps\":{5:F1}",
                                        renderer.Captured,renderer.Presented,renderer.Generated,(renderer.Captured-snapshotInput)*perSecond,
                                        (renderer.Presented-snapshotPresented)*perSecond,(renderer.Generated-snapshotGenerated)*perSecond)
                                        +",\"repaints\":"+renderer.Repaints+",\"mode\":"+JsonString(mode)+",\"multiplier\":"+multiplier+",\"effectivePictureMode\":"+JsonString(renderer.EffectivePictureMode)
                                        +",\"settingsRevision\":"+presentedSettingsRevision
                                        +",\"settingsError\":"+JsonString(settingsNotice)
                                        +",\"loupeActive\":"+(loupe!=null ? "true":"false")+",\"loupeLevel\":"+(loupe==null ? 0 : loupe.Level)
                                        +",\"loupeX\":"+(loupe==null ? "0" : loupe.X.ToString("R",CultureInfo.InvariantCulture))
                                        +",\"loupeY\":"+(loupe==null ? "0" : loupe.Y.ToString("R",CultureInfo.InvariantCulture))
                                        +",\"loupeScale\":"+Number(loupe==null ? 1 : loupe.Scale)
                                        +",\"effectNotice\":"+JsonString(settingsNotice ?? (!string.IsNullOrEmpty(renderer.FrameGenerationNotice) ? renderer.FrameGenerationNotice : string.IsNullOrEmpty(renderer.EffectNotice) ? renderer.OutputNotice : renderer.EffectNotice))+",\"compare\":"+(compare ? "true":"false")
                                        +",\"wipeGripState\":"+gripState+",\"wipeGripScale\":"+Number(gripScale)+",\"wipeHorizontal\":"+(wipeHorizontal ? "true" : "false")+",\"wipePosition\":"+wipePosition.ToString("R",CultureInfo.InvariantCulture)+",\"comparisonView\":"+JsonString(comparisonView)+",\"sampleId\":"+(++sampleId)+",\"elapsedMs\":"+Number(now)+",\"display\":"+JsonString(renderer.DisplayName)
                                        +",\"hdrEnabled\":"+(renderer.HdrEnabled ? "true":"false")
                                        +",\"frameGenerationProvider\":"+JsonString(generation)+",\"resolution\":"+JsonString(resolution)+",\"hdrMode\":"+JsonString(hdr)
                                        +Telemetry(renderer,decoded,targetFps,compare ? 1:multiplier,lastRealAge,demo)+"}";
                                    if(host!=null) EmbeddedPlayback.Publish(snapshot);
                                    if(live!=null && now-lastFile>=1000) { live.Update(snapshot); lastFile=now; }
                                    lastSnapshot=now; snapshotInput=renderer.Captured;
                                    snapshotPresented=renderer.Presented; snapshotGenerated=renderer.Generated;
                                }
                                if(verifyOutput && now>=nextOutputSample)
                                {
                                    renderer.VerifyNextOutput=true; nextOutputSample=now+1000;
                                }
                                if(testing && now>=nextSample)
                                {
                                    sample.Tick=drawTick++/2; sample.Refresh(); nextSample=Math.Max(nextSample+16.667,now+1);
                                }
                                if((host==null ? window.WindowState==FormWindowState.Minimized:host.Hidden) || (captureWindow && WindowsApi.IsIconic(source)))
                                {
                                    Thread.Sleep(30); step=0; previousTime=0; continue;
                                }
                                if(host==null ? window.ConsumeDisplayChange():host.ConsumeDisplayChange()) { renderer.InvalidateDisplay(); redraw=true; }
                                Size surfaceSize=host==null ? window.ClientSize:host.ClientSize;
                                if(renderer.UpdateWindowSize(surfaceSize,true)) redraw=true;
                                double timestamp;
                                bool deduplicate=multiplier>1 || testing || vendor;
                                bool fresh=demo ? offline.Read(renderer,out timestamp) : direct ? decoded.Read(renderer,deduplicate,out timestamp) : capture.Read(renderer,deduplicate,out timestamp);
                                repaintPending|=redraw || presentedSettingsRevision!=settingsRevision;
                                if(fresh)
                                {
                                    double elapsed=timestamp-previousTime;
                                    bool continuous=renderer.HasPrevious && elapsed>0 && elapsed<120;
                                    renderer.SetFrameTiming(elapsed,continuous);
                                    // Real images always take priority if the next capture arrived early.
                                    if(step>0 && renderer.HasPrevious)
                                    {
                                        int pixel=renderer.Show(compare ? "original":mode,1,testing,true);
                                        if(testing) samples.Add(pixel & 0x00ffffff);
                                        lastSubmit=clock.Elapsed.TotalMilliseconds;
                                        lastRealAge=RecordAge(liveFrameAges,previousTime,testing || filename!=null);
                                    }
                                    if(continuous) period=period*.66 + Math.Max(4,Math.Min(100,elapsed))*.34;
                                    previousTime=timestamp;
                                    if(firstInput==0) firstInput=timestamp;
                                    lastInput=timestamp;
                                    count=continuous && !compare && string.IsNullOrEmpty(renderer.FrameGenerationNotice) ? multiplier:1;
                                    step=1; pairStart=now;
                                    due=count==1 ? now:Math.Max(now,lastSubmit+period/count);
                                }
                                if(step>0 && now>=due)
                                {
                                    // When the GPU misses a deadline skip obsolete intermediates.
                                    step=Math.Min(count,Math.Max(step,1+(int)((now-pairStart)/(period/count))));
                                    int color=renderer.Show(compare ? "original":mode,(float)step/count,testing);
                                    presentedSettingsRevision=settingsRevision;
                                    if(testing) samples.Add(color & 0x00ffffff);
                                    if(step==count) lastRealAge=RecordAge(liveFrameAges,previousTime,testing || filename!=null);
                                    step++;
                                    if(step>count) step=0;
                                    lastSubmit=clock.Elapsed.TotalMilliseconds;
                                    repaintPending=false;
                                    due=pairStart+(step-1)*period/count;
                                }
                                // Moving video picks up the latest divider on its next submission. A stalled
                                // source still responds to dragging, with at most 60 UI-only repaints per second.
                                if(repaintPending && step==0 && renderer.Captured>0 &&
                                   now-lastSubmit>=period*1.25 && now-lastRepaint>=1000.0/60)
                                {
                                    renderer.Repaint(compare ? "original":mode);
                                    presentedSettingsRevision=settingsRevision;
                                    lastRepaint=clock.Elapsed.TotalMilliseconds;
                                    repaintPending=false;
                                }
                                if(renderer.Captured==0 && now>(direct ? 30000:15000))
                                    throw new InvalidOperationException(direct ? "No video frames arrived from the Direct backend within 30 seconds. Check the console connection and retry."
                                                                              : "No frames arrived from the selected window. Restore it, start playback, and retry.");
                                Thread.Sleep(step>0 ? 1:2);
                            }
                        }
                        finally { WindowsApi.timeEndPeriod(1); }
                        liveFrameAges.Sort();
                        int median=liveFrameAges.Count/2;
                        double testSeconds=Math.Max(.001,clock.Elapsed.TotalSeconds);
                        double inputFps=lastInput>firstInput ? (renderer.Captured-1)*1000.0/(lastInput-firstInput):0;
                        string report=string.Format(CultureInfo.InvariantCulture,
                            "{{\"captured\":{0},\"presented\":{1},\"generated\":{2},\"distinctSampleColors\":{3},\"mode\":\"{4}\",\"multiplier\":{5},"+
                            "\"duplicatesDropped\":{6},\"sourceFps\":{7:F1},\"submittedFps\":{8:F1},\"medianCaptureToRealFrameSubmitMs\":{9:F1},"+
                            "\"sampledFrames\":{10},\"sampleMinLuma\":{11},\"sampleMaxLuma\":{12}",
                            renderer.Captured,renderer.Presented,renderer.Generated,samples.Count,mode,multiplier,renderer.Duplicates,
                            inputFps,renderer.Presented/testSeconds,liveFrameAges.Count==0 ? 0:liveFrameAges[median],renderer.SampledFrames,
                            renderer.SampledFrames==0 ? 0:renderer.SampleMinLuma,renderer.SampleMaxLuma)
                            +",\"frameGenerationProvider\":"+JsonString(generation)
                            +",\"hardwareMotionPairs\":"+renderer.HardwareMotionPairs
                            +",\"display\":"+JsonString(renderer.DisplayName)+",\"resolution\":"+JsonString(resolution)
                            +",\"embedded\":"+(host!=null ? "true":"false")+",\"fullscreen\":"+(window!=null && window.Fullscreen ? "true":"false")+",\"hdrEnabled\":"+(renderer.HdrEnabled ? "true":"false")
                            +",\"wipeHorizontal\":"+(wipeHorizontal ? "true" : "false")+",\"wipePosition\":"+wipePosition.ToString("R",CultureInfo.InvariantCulture)+",\"comparisonView\":"+JsonString(comparisonView)+",\"outputStatus\":"+JsonString(renderer.OutputStatus)+",\"effectivePictureMode\":"+JsonString(renderer.EffectivePictureMode)
                            +",\"effectNotice\":"+JsonString(string.IsNullOrEmpty(renderer.FrameGenerationNotice) ? renderer.EffectNotice : renderer.FrameGenerationNotice)
                            +",\"medianPcSubmitMs\":"+Number(liveFrameAges.Count==0 ? -1:liveFrameAges[median])
                            +Telemetry(renderer,decoded,targetFps,compare ? 1:multiplier,lastRealAge,demo)+"}";
                        if(filename!=null) File.WriteAllText(filename,report);
                        Console.WriteLine(report);
                        if(testing && (renderer.Captured<8 || renderer.Presented<8 || samples.Count<5 ||
                            (multiplier>1 && renderer.Generated<1) || (vendor && renderer.VendorEvaluations+renderer.FsrEvaluations<5))) throw new InvalidOperationException("GPU capture self-test failed. "+report);
                    }
                }
            }
            return 0;
        }
        catch(Exception ex)
        {
            if(host!=null) { EmbeddedPlayback.Failed(ex.Message); return 1; }
            string log=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LumaRelay","player-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)); File.WriteAllText(log,DateTime.Now+Environment.NewLine+ex);
            Console.Error.WriteLine(ex.Message);
            if(!testing && Array.IndexOf(args,"--quiet")<0) MessageBox.Show(ex.Message,"P5Cast Player",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return 1;
        }
    }
    static string Option(string[] args,string key,string fallback)
    {
        int index=Array.IndexOf(args,key);
        if(index<0) return fallback;
        if(index+1>=args.Length) throw new ArgumentException("Missing value for "+key);
        return args[index+1];
    }
    static string JsonString(string value)
    {
        var text=new System.Text.StringBuilder("\"");
        foreach(char c in value ?? "") {
            if(c=='\\' || c=='"') text.Append('\\').Append(c);
            else if(c<32) text.Append("\\u").Append(((int)c).ToString("X4",CultureInfo.InvariantCulture));
            else text.Append(c);
        }
        return text.Append('"').ToString();
    }
    static string Number(double value)
    {
        return value<0 || double.IsNaN(value) || double.IsInfinity(value) ? "null":value.ToString("0.##",CultureInfo.InvariantCulture);
    }
    static string Telemetry(Renderer renderer,DirectVideo direct,int targetFps,int factor,double age,bool demo)
    {
        Size input=renderer.InputSize;
        Size output=renderer.RenderSize;
        Size surface=renderer.OutputSize;
        var picture=renderer.PictureViewport;
        var audio=direct==null ? null:direct.Audio;
        return ",\"presenterHdr10\":"+(renderer.PresenterHdr10 ? "true":"false")
               +",\"outputWidth\":"+output.Width+",\"outputHeight\":"+output.Height
               +",\"renderWidth\":"+output.Width+",\"renderHeight\":"+output.Height
               +",\"surfaceWidth\":"+surface.Width+",\"surfaceHeight\":"+surface.Height
               +",\"displayRefreshHz\":"+Number(renderer.DisplayRefreshHz>0 ? renderer.DisplayRefreshHz : -1)
               +",\"pictureX\":"+picture.X+",\"pictureY\":"+picture.Y+",\"pictureWidth\":"+picture.Width+",\"pictureHeight\":"+picture.Height
               +",\"displayBitsPerColor\":"+Number(renderer.DisplayBitsPerColor>0 ? renderer.DisplayBitsPerColor : -1)
               +",\"presentationColorSpace\":"+JsonString(renderer.PresenterHdr10 ? "BT.2020 PQ":"sRGB")
               +",\"presenterHdrFormat\":"+(renderer.PresenterHdr10 ? "\"RGB10A2_UNORM\"":"null")
               +",\"outputNotice\":"+JsonString(renderer.OutputNotice)
               +",\"activeFrameGenerationVersion\":"+JsonString(renderer.ActiveFrameGenerationVersion)
               +",\"activeFrameGenerationProvider\":"+JsonString(renderer.ActiveFrameGenerationProvider)
               +",\"activeNvidiaEffect\":"+JsonString(renderer.ActiveEffect)+",\"nvidiaEvaluations\":"+renderer.VendorEvaluations
               +",\"frucEvaluations\":"+renderer.FrucEvaluations+",\"frucMidpoints\":"+renderer.FrucMidpoints
               +",\"activeNisFilter\":"+JsonString(renderer.ActiveNisFilter)+",\"nisEvaluations\":"+renderer.NisEvaluations
               +",\"fsrVersion\":"+JsonString(renderer.ActiveFsrVersion)+",\"fsrEvaluations\":"+renderer.FsrEvaluations
               +",\"dlssModelHint\":"+JsonString(renderer.DlssModelHint)+",\"dlssQuality\":"+JsonString(renderer.DlssQuality)+",\"activeDlssQuality\":"+JsonString(renderer.ActiveDlssQuality)
               +",\"renderer\":"+JsonString(renderer.Backend)+",\"processingRenderer\":"+JsonString(renderer.Backend)+",\"inputWidth\":"+input.Width+",\"inputHeight\":"+input.Height+",\"targetFps\":"+Number(targetFps==0 ? -1:targetFps)
               +",\"targetPresentedFps\":"+Number(targetFps==0 ? -1:targetFps*factor)+",\"lastPcSubmitMs\":"+Number(age)
               +",\"sourceKind\":"+JsonString(demo ? "demo":direct==null ? "window":"direct")
               +",\"audioQueueMs\":"+Number(audio==null ? -1:audio.QueueMs)+",\"audioTargetMs\":"+Number(audio==null ? -1:audio.TargetMs)
               +",\"audioBitrateKbps\":"+Number(audio==null ? -1:audio.BitrateKbps)+",\"audioSampleRate\":"+Number(audio==null ? -1:audio.SampleRate)
               +",\"audioChannels\":"+Number(audio==null ? -1:audio.Channels)
               +",\"audioUnderruns\":"+Number(audio==null ? -1:audio.Underruns)+",\"handshakeRttMs\":"+Number(audio==null ? -1:audio.RttMs)
               +",\"decodeMs\":"+Number(direct==null ? -1:direct.DecodeMs)
               +(direct==null ? "" : ",\"codec\":"+JsonString(direct.CodecName)+",\"bitrateMbps\":"+Number(direct.BitrateMbps));
    }
    sealed class LiveReport : IDisposable
    {
        readonly string path,temp;
        Task writer;
        bool wrote;
        public LiveReport(string filename)
        {
            path=Path.ChangeExtension(filename,".live.json"); temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        }
        public void Update(string snapshot)
        {
            if(writer!=null && !writer.IsCompleted) return;
            writer=Task.Run(delegate {
                try {
                    File.WriteAllText(temp,snapshot);
                    if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
                    wrote=true;
                } catch(IOException) { } catch(UnauthorizedAccessException) { }
            });
        }
        public void Dispose()
        {
            if(writer!=null) writer.Wait();
            try { if(wrote) File.Delete(path); File.Delete(temp); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
    }
    static double RecordAge(List<double> samples,double timestamp,bool measuring)
    {
        double age=Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency-timestamp;
        if(age<0 || age>=1000) return -1;
        if(measuring && samples.Count<20000) samples.Add(age);
        return age;
    }
    static IntPtr FindSource(string[] args)
    {
        string handle=Option(args,"--hwnd",null);
        IntPtr hwnd=IntPtr.Zero;
        if(handle!=null) hwnd=new IntPtr(long.Parse(handle,CultureInfo.InvariantCulture));
        else
        {
            string name=Path.GetFileNameWithoutExtension(Option(args,"--process","RemotePlay"));
            foreach(var process in Process.GetProcessesByName(name))
                using(process) if(process.MainWindowHandle!=IntPtr.Zero) { hwnd=process.MainWindowHandle; break; }
        }
        if(hwnd==IntPtr.Zero || !WindowsApi.IsWindow(hwnd)) throw new InvalidOperationException("Open and connect the source window, then start the player again.");
        if(WindowsApi.IsIconic(hwnd)) throw new InvalidOperationException("Restore the source window before starting the player.");
        return hwnd;
    }
}


