using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

internal sealed class PlayerWindow : Form
{
    readonly bool overlay;
    public bool Compare;
    public bool Ended { get; private set; }
    public PlayerWindow(IntPtr source,bool windowed)
    {
        overlay=!windowed;
        Text="LumaRelay Player"; BackColor=Color.Black;
        StartPosition=FormStartPosition.Manual;
        var monitor=Screen.FromHandle(source);
        if(overlay)
        {
            FormBorderStyle=FormBorderStyle.None;
            Bounds=monitor.Bounds; TopMost=true; ShowInTaskbar=false;
        }
        else
        {
            Size=new Size(Math.Min(960,monitor.WorkingArea.Width),Math.Min(570,monitor.WorkingArea.Height));
            Location=new Point(monitor.WorkingArea.Left+(monitor.WorkingArea.Width-Width)/2,monitor.WorkingArea.Top+(monitor.WorkingArea.Height-Height)/2);
            MinimumSize=new Size(260,180);
        }
    }
    public void EnableShortcuts()
    {
        if(overlay && !WindowsApi.RegisterHotKey(Handle,1,0x4003,(int)Keys.Q))
            throw new InvalidOperationException("Ctrl+Alt+Q is already assigned. Close the other player or start in windowed mode.");
        WindowsApi.RegisterHotKey(Handle,2,0x4003,(int)Keys.B);
    }
    protected override bool ShowWithoutActivation { get { return overlay; } }
    protected override CreateParams CreateParams
    {
        get { var cp=base.CreateParams; if(overlay) cp.ExStyle |= 0x08000000 | 0x00000020; return cp; }
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
            return;
        }
        if(overlay && message.Msg==0x0084) { message.Result=new IntPtr(-1); return; }
        base.WndProc(ref message);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Ended=true; WindowsApi.UnregisterHotKey(Handle,1); WindowsApi.UnregisterHotKey(Handle,2);
        base.OnFormClosed(e);
    }
}

internal sealed class SampleWindow : Form
{
    public int Tick;
    public SampleWindow()
    {
        Text="LumaRelay capture test source"; ClientSize=new Size(480,270);
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
            bool windowed=testing || Array.IndexOf(args,"--windowed")>=0;
            string mode=Option(args,"--mode","fsr").ToLowerInvariant();
            string filename=Option(args,"--report",null);
            int multiplier=int.Parse(Option(args,"--multiplier",testing ? "3":"1"),CultureInfo.InvariantCulture);
            if(mode!="fsr" && mode!="sharp" && mode!="original") throw new ArgumentException("Mode must be fsr, sharp, or original.");
            if(multiplier<1 || multiplier>4) throw new ArgumentException("Multiplier must be between 1 and 4.");
            int seconds=int.Parse(Option(args,"--seconds",testing ? "4":"0"),CultureInfo.InvariantCulture);
            using(var sample=testing ? new SampleWindow():null)
            {
                IntPtr source;
                if(testing) { sample.Show(); sample.Update(); source=sample.Handle; }
                else source=FindSource(args);
                using(var window=new PlayerWindow(source,windowed))
                {
                    window.Show(); window.EnableShortcuts();
                    if(!testing) WindowsApi.SetForegroundWindow(source);
                    using(var renderer=new Renderer(window.Handle,window.ClientSize))
                    using(var capture=new WindowCapture(source,renderer.Device))
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
                                renderer.Resize(window.ClientSize);
                                double timestamp;
                                if(capture.Read(renderer,multiplier>1 || testing,out timestamp))
                                {
                                    // Real images always take priority if the next capture arrived early.
                                    if(step>0 && renderer.HasPrevious)
                                    {
                                        int pixel=renderer.Show(window.Compare ? "original":mode,1,testing,true);
                                        if(testing) samples.Add(pixel & 0x00ffffff);
                                        lastSubmit=clock.Elapsed.TotalMilliseconds;
                                        RecordAge(liveFrameAges,previousTime,testing || filename!=null);
                                    }
                                    double elapsed=timestamp-previousTime;
                                    bool continuous=renderer.HasPrevious && elapsed>0 && elapsed<120;
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
                            renderer.SampledFrames==0 ? 0:renderer.SampleMinLuma,renderer.SampleMaxLuma)+"}";
                        if(filename!=null) File.WriteAllText(filename,report);
                        Console.WriteLine(report);
                        if(testing && (renderer.Captured<8 || renderer.Presented<8 || samples.Count<5 ||
                            (multiplier>1 && renderer.Generated<1))) throw new InvalidOperationException("GPU capture self-test failed. "+report);
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
            if(!testing && Array.IndexOf(args,"--quiet")<0) MessageBox.Show(ex.Message,"LumaRelay Player",MessageBoxButtons.OK,MessageBoxIcon.Error);
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


