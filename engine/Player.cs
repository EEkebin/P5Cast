using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal sealed class PlayerWindow : Form
{
    const int Style=-16, ExtendedStyle=-20;
    const int OverlappedWindow=0x00cf0000, Popup=unchecked((int)0x80000000);
    const int OverlayStyles=0x08000000 | 0x20 | 0x80, AppWindow=0x40000;
    const int NoActivateAndFrameChanged=0x30;
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetStyle(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")] static extern int SetStyle(IntPtr hwnd,int index,int value);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,int flags);
    bool fullscreen, quitRegistered, displayChanged;
    readonly IntPtr source;
    readonly bool moveSource;
    readonly Size chosenSize;
    DisplayTarget target;
    Rectangle restored;
    public bool Compare;
    public bool Fullscreen { get { return fullscreen; } }
    public bool Ended { get; private set; }
    public PlayerWindow(IntPtr source,DisplayTarget target,Size chosenSize,bool windowed)
    {
        this.source=source; this.target=target; this.chosenSize=chosenSize;
        moveSource=DisplayTarget.IsPlaybackSource(source);
        Text="Cadrivect Player"; BackColor=Color.Black; KeyPreview=true;
        AutoScaleMode=AutoScaleMode.None;
        StartPosition=FormStartPosition.Manual;
        MinimumSize=new Size(240,150);
        restored=InitialWindow(target);
        fullscreen=!windowed;
        Bounds=fullscreen ? target.Bounds : restored;
        if(moveSource) target.MoveSourceWindow(source);
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
        if(!SetWindowPos(Handle,new IntPtr(fullscreen ? -1 : -2),rect.X,rect.Y,rect.Width,rect.Height,NoActivateAndFrameChanged))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public void EnableShortcuts()
    {
        DisplayTarget.MarkPlayer(Handle,true);
        quitRegistered=WindowsApi.RegisterHotKey(Handle,1,0x4003,(int)Keys.Q);
        if(fullscreen && !quitRegistered)
            throw new InvalidOperationException("Ctrl+Alt+Q is already assigned. Close the other player or start in windowed mode.");
        WindowsApi.RegisterHotKey(Handle,2,0x4003,(int)Keys.B);
        WindowsApi.RegisterHotKey(Handle,3,0x4003,(int)Keys.Enter);
        WindowsApi.RegisterHotKey(Handle,4,0x4003,(int)Keys.Right);
        ApplyBounds(fullscreen ? target.Bounds : restored);
    }
    public void ToggleFullscreen()
    {
        if(!fullscreen && !quitRegistered) return;
        target=DisplayTarget.Select(DisplayTarget.DeviceForWindow(Handle),source);
        if(!fullscreen) {
            restored=WindowState==FormWindowState.Normal ? Bounds : RestoreBounds;
            if(WindowState!=FormWindowState.Normal) WindowState=FormWindowState.Normal;
        }
        fullscreen=!fullscreen;
        var style=GetStyle(Handle,Style);
        var extended=GetStyle(Handle,ExtendedStyle);
        SetStyle(Handle,Style,fullscreen ? (style & ~OverlappedWindow)|Popup : (style & ~Popup)|OverlappedWindow);
        SetStyle(Handle,ExtendedStyle,fullscreen ? (extended & ~AppWindow)|OverlayStyles : (extended & ~OverlayStyles)|AppWindow);
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
    protected override bool ShowWithoutActivation { get { return fullscreen; } }
    protected override CreateParams CreateParams
    {
        get { var cp=base.CreateParams;
            if(fullscreen) { cp.Style=(cp.Style & ~OverlappedWindow)|Popup; cp.ExStyle=(cp.ExStyle & ~AppWindow)|OverlayStyles; }
            return cp;
        }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.KeyCode==Keys.Escape) Close(); else if(e.KeyCode==Keys.B) Compare=!Compare;
        base.OnKeyDown(e);
    }
    protected override void WndProc(ref Message message)
    {
        if(message.Msg==0x0312)
        {
            if(message.WParam.ToInt32()==1) Close();
            if(message.WParam.ToInt32()==2) Compare=!Compare;
            if(message.WParam.ToInt32()==3) ToggleFullscreen();
            if(message.WParam.ToInt32()==4) CycleDisplay();
            return;
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
        if(fullscreen && message.Msg==0x0084) { message.Result=new IntPtr(-1); return; }
        base.WndProc(ref message);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Ended=true; DisplayTarget.MarkPlayer(Handle,false);
        for(int id=1;id<=4;id++) WindowsApi.UnregisterHotKey(Handle,id);
        base.OnFormClosed(e);
    }
}

internal sealed class SampleWindow : Form
{
    public int Tick;
    public SampleWindow()
    {
        Text="Cadrivect capture test source"; ClientSize=new Size(480,270);
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
    static int Run(string[] args)
    {
        bool testing=Array.IndexOf(args,"--self-test")>=0;
        var samples=new HashSet<int>();
        var liveFrameAges=new List<double>();
        try
        {
            WindowsApi.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            bool windowed=Array.IndexOf(args,"--windowed")>=0 || (testing && Array.IndexOf(args,"--fullscreen")<0);
            string displayName=Option(args,"--display","primary");
            string resolution=Option(args,"--resolution","native").ToLowerInvariant();
            Size raster=DisplayTarget.ResolutionLimit(resolution);
            string hdr=Option(args,"--hdr","auto").ToLowerInvariant();
            if(hdr!="auto" && hdr!="off" && hdr!="hdr10") throw new ArgumentException("Choose auto, off, or hdr10 for --hdr.");
            string mode=Option(args,"--mode","fsr").ToLowerInvariant();
            string filename=Option(args,"--report",null);
            int multiplier=int.Parse(Option(args,"--multiplier",testing ? "3":"1"),CultureInfo.InvariantCulture);
            bool vendor=mode=="dlss" || mode=="dlaa" || mode=="rtxvideo";
            if(!vendor && mode!="fsr" && mode!="sharp" && mode!="original") throw new ArgumentException("Unknown picture mode: "+mode);
            if(multiplier<1 || multiplier>4) throw new ArgumentException("Multiplier must be between 1 and 4.");
            int seconds=int.Parse(Option(args,"--seconds",testing ? "4":"0"),CultureInfo.InvariantCulture);
            using(var sample=testing ? new SampleWindow():null)
            {
                IntPtr source;
                if(testing) { sample.Show(); sample.Update(); source=sample.Handle; }
                else source=FindSource(args);
                var destination=DisplayTarget.Select(displayName,source);
                using(var window=new PlayerWindow(source,destination,raster,windowed))
                {
                    window.Show(); window.EnableShortcuts();
                    if(!testing) WindowsApi.SetForegroundWindow(source);
                    using(var renderer=new Renderer(window.Handle,window.ClientSize,raster,hdr,mode))
                    using(var capture=new WindowCapture(source,renderer.Device,renderer.HdrEnabled))
                    {
                        var clock=Stopwatch.StartNew();
                        double nextSample=0, previousTime=0, period=16.667, due=0, pairStart=0, lastSubmit=-1000;
                        bool verifyOutput=Array.IndexOf(args,"--verify-output")>=0;
                        double nextOutputSample=0;
                        int step=0, count=1, drawTick=0;
                        double firstInput=0,lastInput=0;
                        WindowsApi.timeBeginPeriod(1);
                        try
                        {
                            while(!window.Ended && WindowsApi.IsWindow(source))
                            {
                                Application.DoEvents();
                                if(window.Ended || (seconds>0 && clock.Elapsed.TotalSeconds>=seconds)) break;
                                double now=clock.Elapsed.TotalMilliseconds;
                                if(verifyOutput && now>=nextOutputSample)
                                {
                                    renderer.VerifyNextOutput=true; nextOutputSample=now+1000;
                                }
                                if(testing && now>=nextSample)
                                {
                                    sample.Tick=drawTick++/2; sample.Refresh(); nextSample=Math.Max(nextSample+16.667,now+1);
                                }
                                if(window.WindowState==FormWindowState.Minimized || WindowsApi.IsIconic(source))
                                {
                                    Thread.Sleep(30); step=0; previousTime=0; continue;
                                }
                                if(window.ConsumeDisplayChange()) renderer.InvalidateDisplay();
                                renderer.Resize(window.ClientSize);
                                double timestamp;
                                if(capture.Read(renderer,multiplier>1 || testing || vendor,out timestamp))
                                {
                                    double elapsed=timestamp-previousTime;
                                    bool continuous=renderer.HasPrevious && elapsed>0 && elapsed<120;
                                    renderer.SetFrameTiming(elapsed,continuous);
                                    // Real images always take priority if the next capture arrived early.
                                    if(step>0 && renderer.HasPrevious)
                                    {
                                        int pixel=renderer.Show(window.Compare ? "original":mode,1,testing,true);
                                        if(testing) samples.Add(pixel & 0x00ffffff);
                                        lastSubmit=clock.Elapsed.TotalMilliseconds;
                                        RecordAge(liveFrameAges,previousTime,testing || filename!=null);
                                    }
                                    if(continuous) period=period*.66 + Math.Max(4,Math.Min(100,elapsed))*.34;
                                    previousTime=timestamp;
                                    if(firstInput==0) firstInput=timestamp;
                                    lastInput=timestamp;
                                    count=continuous && !window.Compare ? multiplier:1;
                                    step=1; pairStart=now;
                                    due=count==1 ? now:Math.Max(now,lastSubmit+period/count);
                                }
                                if(step>0 && now>=due)
                                {
                                    // When the GPU misses a deadline skip obsolete intermediates.
                                    step=Math.Min(count,Math.Max(step,1+(int)((now-pairStart)/(period/count))));
                                    int color=renderer.Show(window.Compare ? "original":mode,(float)step/count,testing);
                                    if(testing) samples.Add(color & 0x00ffffff);
                                    if(step==count) RecordAge(liveFrameAges,previousTime,testing || filename!=null);
                                    step++;
                                    if(step>count) step=0;
                                    lastSubmit=clock.Elapsed.TotalMilliseconds;
                                    due=pairStart+(step-1)*period/count;
                                }
                                if(renderer.Captured==0 && now>15000)
                                    throw new InvalidOperationException("No frames arrived from the selected window. Restore it, start playback, and retry.");
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
                            +",\"activeNvidiaEffect\":\""+renderer.ActiveEffect+"\",\"nvidiaEvaluations\":"+renderer.VendorEvaluations
                            +",\"hardwareMotionPairs\":"+renderer.HardwareMotionPairs
                            +",\"display\":"+JsonString(renderer.DisplayName)+",\"resolution\":"+JsonString(resolution)
                            +",\"fullscreen\":"+(window.Fullscreen ? "true":"false")+",\"hdrEnabled\":"+(renderer.HdrEnabled ? "true":"false")
                            +",\"outputWidth\":"+renderer.OutputSize.Width+",\"outputHeight\":"+renderer.OutputSize.Height
                            +",\"renderWidth\":"+renderer.RenderSize.Width+",\"renderHeight\":"+renderer.RenderSize.Height
                            +",\"outputStatus\":"+JsonString(renderer.OutputStatus)+"}";
                        if(filename!=null) File.WriteAllText(filename,report);
                        Console.WriteLine(report);
                        if(testing && (renderer.Captured<8 || renderer.Presented<8 || samples.Count<5 ||
                            (multiplier>1 && renderer.Generated<1) || (vendor && renderer.VendorEvaluations<5))) throw new InvalidOperationException("GPU capture self-test failed. "+report);
                    }
                }
            }
            return 0;
        }
        catch(Exception ex)
        {
            string log=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LumaRelay","player-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)); File.WriteAllText(log,DateTime.Now+Environment.NewLine+ex);
            Console.Error.WriteLine(ex.Message);
            if(!testing && Array.IndexOf(args,"--quiet")<0) MessageBox.Show(ex.Message,"Cadrivect Player",MessageBoxButtons.OK,MessageBoxIcon.Error);
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
    static void RecordAge(List<double> samples,double timestamp,bool measuring)
    {
        if(!measuring || samples.Count>=20000) return;
        double age=Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency-timestamp;
        if(age>=0 && age<1000) samples.Add(age);
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


