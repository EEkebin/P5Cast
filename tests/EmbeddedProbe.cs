using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Forms=System.Windows.Forms;
using System.Windows.Threading;

internal static partial class EmbeddedProbe
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern IntPtr CreateWindowEx(int ex,string cls,string title,int style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr data);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    delegate bool Visit(IntPtr hwnd,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr desktop,Visit visit,IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint id);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartInfo {
        public int Size; public string Reserved,Desktop,Title; public uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags;
        public short Show,ReservedSize; public IntPtr ReservedBytes,StdIn,StdOut,StdErr;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread; public int Id,ThreadId; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr process,IntPtr thread,bool inherit,uint flags,IntPtr env,string dir,ref StartInfo info,out ProcessInfo output);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr handle,out int code);
    static int RunOnDesktop(string[] args,string id)
    {
        IntPtr desktop=CreateDesktop(id,IntPtr.Zero,IntPtr.Zero,0,0xf01ff,IntPtr.Zero);
        if(desktop==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            string exe=Assembly.GetExecutingAssembly().Location;
            var command=new StringBuilder("\""+exe+"\"");
            foreach(var arg in args) command.Append(" \"").Append(arg).Append('"');
            command.Append(" --inactive-desktop");
            var startup=new StartInfo { Size=Marshal.SizeOf(typeof(StartInfo)),Desktop="winsta0\\"+id };
            ProcessInfo process;
            if(!CreateProcess(exe,command,IntPtr.Zero,IntPtr.Zero,false,0,IntPtr.Zero,Path.GetDirectoryName(exe),ref startup,out process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(process.Thread);
            try {
                Check(WaitForSingleObject(process.Process,30000)==0,"Inactive-desktop test timed out.");
                int code; GetExitCodeProcess(process.Process,out code); return code;
            } finally { CloseHandle(process.Process); }
        } finally { CloseDesktop(desktop); }
    }
    sealed class VideoHost : HwndHost
    {
        protected override HandleRef BuildWindowCore(HandleRef parent) {
            IntPtr child=CreateWindowEx(0,"STATIC","Embedded test surface",0x50000000,0,0,1,1,parent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(child==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new HandleRef(this,child);
        }
        protected override void DestroyWindowCore(HandleRef child) { DestroyWindow(child.Handle); }
    }
    sealed class PatternWindow : Forms.Form
    {
        public System.Drawing.Color Color;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaint(Forms.PaintEventArgs e) {
            e.Graphics.Clear(Color); e.Graphics.FillRectangle(System.Drawing.Brushes.White,24,24,110,55);
        }
    }
    static void Check(bool ok,string reason) { if(!ok) throw new Exception(reason); }
    static HashSet<IntPtr> VisibleWindows(IntPtr desktop) {
        var result=new HashSet<IntPtr>();
        using(var process=Process.GetCurrentProcess())
            EnumDesktopWindows(desktop,delegate(IntPtr hwnd,IntPtr ignored) {
                uint id; GetWindowThreadProcessId(hwnd,out id);
                if(id==(uint)process.Id && IsWindowVisible(hwnd)) result.Add(hwnd);
                return true;
            },IntPtr.Zero);
        return result;
    }
    static void Pump() {
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,(Action)delegate {frame.Continue=false;});
        Dispatcher.PushFrame(frame); Thread.Sleep(4);
    }
    static void SendFixture(string name,byte[][] packets,ManualResetEvent pause,ManualResetEvent end,int codec=1)
    {
        try {
            using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.Out)) {
                pipe.Connect(10000);
                using(var writer=new BinaryWriter(pipe)) {
                    writer.Write(0x32564443); writer.Write(codec); WriteAudio(writer,192750,48000,2);
                    bool unavailable=false;
                    int frame=0; while(!end.WaitOne(33)) {
                        if(pause.WaitOne(0)) {
                            if(!unavailable) { WriteAudio(writer,-1,-1,-1); writer.Flush(); unavailable=true; }
                            continue;
                        }
                        var packet=packets[frame++%packets.Length]; writer.Write(packet.Length); writer.Write(packet); writer.Flush();
                    }
                }
            }
        } catch(IOException) { } catch(TimeoutException) { }
    }
    static void WriteAudio(BinaryWriter writer,int bitrate,int rate,int channels) {
        writer.Write(0); writer.Write(17000); writer.Write(0); writer.Write(4100); writer.Write(20000);
        writer.Write(bitrate); writer.Write(rate); writer.Write(channels);
    }
    static Thread Start(Type api,string[] options,Action<int> completed) {
        var thread=new Thread(delegate() { completed((int)api.GetMethod("Run").Invoke(null,new object[]{options})); });
        thread.IsBackground=true; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return thread;
    }
    static void CheckRenderer(Dictionary<string,object> report,string renderer) {
        string active=renderer=="auto" ? "d3d11" : renderer;
        Check((string)report["renderer"]==active && (string)report["processingRenderer"]=="d3d11",
              "The selected presentation API was not used.");
        Check((bool)report["hdrEnabled"]==(bool)report["presenterHdr10"],"HDR processing and presenter color space disagree.");
        if(active=="opengl") Check(!(bool)report["hdrEnabled"],"The SDR presenter reported HDR output.");
    }
    static int CheckCapture(Type api,string report,string renderer) {
        var clock=Stopwatch.StartNew(); int result=-1; var serializer=new JavaScriptSerializer();
        IntPtr desktop=GetThreadDesktop(GetCurrentThreadId()); var host=new VideoHost();
        var window=new Window {Title="WPF capture test",Content=host,Width=650,Height=390,Left=-17800,Top=-14000,
            WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false,ShowInTaskbar=false};
        Thread worker=null;
        try {
            window.Show(); window.UpdateLayout(); Pump();
            string surface=host.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
            // DWM stops painting a wholly offscreen GDI source. Use a tiny, non-activating
            // sample under the taskbar at the far corner; leave the actual WPF player offscreen.
            var screen=Forms.Screen.PrimaryScreen;
            foreach(var other in Forms.Screen.AllScreens) if(other.Bounds.Right>screen.Bounds.Right) screen=other;
            var source=new PatternWindow {Left=screen.Bounds.Right-40,Top=screen.Bounds.Bottom-32,FormBorderStyle=Forms.FormBorderStyle.None,
                StartPosition=Forms.FormStartPosition.Manual,Text="Synthetic capture source",Width=40,Height=32,ShowInTaskbar=false};
            try {
                source.Show(); source.Refresh(); Pump();
                var withSource=VisibleWindows(desktop);
                string capturedReport=Path.ChangeExtension(report,".window.json");
                worker=Start(api,new[]{"--surface",surface,"--hwnd",source.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),
                    "--renderer",renderer,"--hdr","off","--seconds","3","--verify-output","--report",capturedReport},delegate(int value){result=value;});
                clock.Restart(); int lastColor=-1;
                while(worker.IsAlive && clock.ElapsedMilliseconds<8000) {
                    int color=(int)(clock.ElapsedMilliseconds/90)%2;
                    if(color!=lastColor) { source.Color=color==1 ? System.Drawing.Color.MidnightBlue:System.Drawing.Color.SeaGreen; source.Refresh(); lastColor=color; }
                    Pump(); Check(withSource.SetEquals(VisibleWindows(desktop)),"Capture player created another top-level window.");
                }
                Check(!worker.IsAlive && result==0,"Embedded WGC failed: "+api.GetProperty("Error").GetValue(null,null));
                var wgc=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(capturedReport));
                CheckRenderer(wgc,renderer);
                Check((string)wgc["sourceKind"]=="window" && Convert.ToInt32(wgc["captured"])>=3 && Convert.ToInt32(wgc["sampleMaxLuma"])>Convert.ToInt32(wgc["sampleMinLuma"]),
                      "Embedded WGC captured no changing pixels.");
                Console.WriteLine("Passed existing window capture into WPF child. "+capturedReport);
            } finally { source.Dispose(); }
            return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { api.GetMethod("Stop").Invoke(null,null); window.Close(); host.Dispose(); }
    }
    static void MoveToDisplay(Window window,Forms.Screen screen)
    {
        var hwnd=new WindowInteropHelper(window).Handle;
        Check(SetWindowPos(hwnd,IntPtr.Zero,screen.WorkingArea.Left+80,screen.WorkingArea.Top+80,760,410,0x14),
              "Could not move test surface to "+screen.DeviceName);
    }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int w,int h,uint flags);

    static int CheckDirectHdr(Type api,string[] args,string displayName)
    {
        var serializer=new JavaScriptSerializer();
        Forms.Screen hdrScreen=Array.Find(Forms.Screen.AllScreens,x=>x.DeviceName.Equals(displayName,StringComparison.OrdinalIgnoreCase));
        Check(hdrScreen!=null,"HDR test display is missing: "+displayName);
        string renderer=args[5];
        var end=new ManualResetEvent(false); var pause=new ManualResetEvent(false);
        string pipe="P5CastHdr"+Guid.NewGuid().ToString("N");
        var host=new VideoHost(); var window=new Window {Title="P5Cast HDR10 test",Width=760,Height=410,Content=host,ShowActivated=false};
        Thread worker=null; int result=-1; var clock=Stopwatch.StartNew();
        try {
            window.Show(); MoveToDisplay(window,hdrScreen); window.UpdateLayout(); Pump();
            string surface=host.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
            worker=Start(api,new[]{"--surface",surface,"--direct-pipe",pipe,"--direct-runtime",args[1],
                "--renderer",renderer,"--hdr","hdr10","--verify-output","--report",args[4]},delegate(int value){ result=value; });
            byte[][] frames={File.ReadAllBytes(args[2])};
            new Thread(delegate(){SendFixture(pipe,frames,pause,end,3);}) {IsBackground=true}.Start();
            int lastPresented=0;
            Action<Forms.Screen,string,bool> verify=delegate(Forms.Screen screen,string mode,bool hdr) {
                clock.Restart();
                while(worker.IsAlive && clock.ElapsedMilliseconds<7000) {
                    Pump();
                    var data=serializer.Deserialize<Dictionary<string,object>>((string)api.GetMethod("Snapshot").Invoke(null,null));
                    if(!data.ContainsKey("presented") || (string)data["display"]!=screen.DeviceName || (string)data["hdrMode"]!=mode ||
                       (bool)data["hdrEnabled"]!=hdr || Convert.ToInt32(data["presented"])<lastPresented+10) continue;
                    CheckRenderer(data,renderer);
                    Check((string)data["codec"]=="HEVC HDR" && (string)data["presentationColorSpace"]==(hdr ? "BT.2020 PQ":"sRGB") &&
                          (string)data["presenterHdrFormat"]==(hdr ? "RGB10A2_UNORM":null),"Wrong swap chain or codec: "+serializer.Serialize(data));
                    lastPresented=Convert.ToInt32(data["presented"]);
                    Console.WriteLine(renderer+" on "+screen.DeviceName+" presented "+data["codec"]+" via "+data["presentationColorSpace"]+" "+data["presenterHdrFormat"]);
                    return;
                }
                throw new Exception("Expected "+mode+" (HDR "+hdr+") on "+screen.DeviceName+". "+api.GetProperty("Error").GetValue(null,null)+" "+api.GetMethod("Snapshot").Invoke(null,null));
            };
            verify(hdrScreen,"hdr10",true);
            api.GetMethod("SetComparison").Invoke(null,new object[]{"side-by-side"});
            var split=WaitSnapshot(api,s=>(string)s["comparisonView"]=="side-by-side" && (bool)s["presenterHdr10"] &&
                Convert.ToInt32(s["presented"])>lastPresented+5,"HDR10 side-by-side");
            CheckRenderer(split,renderer);
            api.GetMethod("SetComparison").Invoke(null,new object[]{"wipe"});
            api.GetMethod("Configure").Invoke(null,new object[]{1,1});
            WaitSnapshot(api,s=>(string)s["comparisonView"]=="wipe" && (string)s["effectivePictureMode"]=="sharp","sharpened HDR wipe");
            var wiped=SetWipePosition(api,.33f);
            Check((bool)wiped["presenterHdr10"] && (string)wiped["presenterHdrFormat"]=="RGB10A2_UNORM","Wipe lost HDR10 output.");
            SetWipePosition(api,.77f);
            window.Width+=37; window.UpdateLayout();
            api.GetMethod("ConfigureOutput").Invoke(null,new object[]{null,"off"});
            verify(hdrScreen,"off",false);
            api.GetMethod("ConfigureOutput").Invoke(null,new object[]{null,"hdr10"});
            verify(hdrScreen,"hdr10",true);

            // Moving an explicitly HDR session to an SDR monitor should keep it
            // playing and restore PQ when its video surface returns to the HDR monitor.
            var displayType=api.Assembly.GetType("DisplayTarget",true);
            var displays=(Array)displayType.GetMethod("Enumerate").Invoke(null,null);
            foreach(var display in displays) {
                if((bool)displayType.GetProperty("AdvancedColorEnabled").GetValue(display,null)) continue;
                string name=(string)displayType.GetProperty("DeviceName").GetValue(display,null);
                var sdr=Array.Find(Forms.Screen.AllScreens,x=>x.DeviceName==name && x.DeviceName!=hdrScreen.DeviceName);
                if(sdr==null) continue;
                MoveToDisplay(window,sdr); verify(sdr,"hdr10",false);
                api.GetMethod("ConfigureOutput").Invoke(null,new object[]{null,"hdr10"});
                verify(sdr,"hdr10",false);
                var rejected=serializer.Deserialize<Dictionary<string,object>>((string)api.GetMethod("Snapshot").Invoke(null,null));
                Check(((string)rejected["effectNotice"]).Contains("active Windows HDR display"),"The explicit HDR10 request on SDR was not rejected.");
                MoveToDisplay(window,hdrScreen); verify(hdrScreen,"hdr10",true);
                break;
            }
            api.GetMethod("Stop").Invoke(null,null);
            var stop=Stopwatch.StartNew(); while(worker.IsAlive && stop.ElapsedMilliseconds<3500) Pump();
            Check(!worker.IsAlive && result==0,"Engine did not stop after HDR test.");
            var final=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(args[4]));
            Check((bool)final["presenterHdr10"] && Convert.ToInt32(final["sampledFrames"])>0 &&
                  Convert.ToInt32(final["sampleMaxLuma"])>Convert.ToInt32(final["sampleMinLuma"]),"No varying PQ fixture pixels.");
            return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            api.GetMethod("Stop").Invoke(null,null); end.Set();
            var stop=Stopwatch.StartNew(); while(worker!=null && worker.IsAlive && stop.ElapsedMilliseconds<3500) Pump();
            window.Close(); host.Dispose();
        }
    }

    [STAThread] static int Main(string[] args)
    {
        string id="CadrivectEmbedded"+Guid.NewGuid().ToString("N");
        bool hdrProbe=args.Length>=7 && args[6].StartsWith("--hdr-display=",StringComparison.Ordinal);
        bool compareProbe=args.Length>=7 && args[6]=="--comparison";
        if(args.Length==6 || (args.Length==7 && (hdrProbe || compareProbe))) return RunOnDesktop(args,id);
        if(args.Length==8 && compareProbe && args[7]=="--inactive-desktop") {
            var compareApi=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Cadrivect.Engine.EmbeddedPlayback",true);
            return CheckComparison(compareApi,args);
        }
        if(args.Length==8 && hdrProbe && args[7]=="--inactive-desktop") {
            var hdrApi=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Cadrivect.Engine.EmbeddedPlayback",true);
            return CheckDirectHdr(hdrApi,args,args[6].Substring("--hdr-display=".Length));
        }
        if(args.Length!=7 || (args[6]!="--inactive-desktop" && args[6]!="--offscreen-capture")) return 2;
        string renderer=args[5];
        IntPtr desktop=GetThreadDesktop(GetCurrentThreadId());
        var serializer=new JavaScriptSerializer();
        Exception failure=null; int result=-1,exitCode=1;
        var api=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Cadrivect.Engine.EmbeddedPlayback",true);
        if(args[6]=="--offscreen-capture") return CheckCapture(api,args[4],renderer);
        var host=new VideoHost(); var window=new Window { Title="WPF host test",Width=700,Height=420,Content=host };
        var end=new ManualResetEvent(false); var pause=new ManualResetEvent(false); Thread worker=null;
        try {
            window.Show(); window.UpdateLayout(); Pump();
            var initial=VisibleWindows(desktop);
            Check(initial.Contains(new WindowInteropHelper(window).Handle) && host.Handle!=IntPtr.Zero,"WPF dashboard/child not ready, desktop visible count="+initial.Count+", child="+host.Handle);
            string[] options={"--surface",host.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),"--direct-pipe",id,
                "--direct-runtime",args[1],"--renderer",renderer,"--hdr","off","--target-fps","30","--verify-output","--report",args[4]};
            worker=new Thread(delegate() {
                try {
                    result=(int)api.GetMethod("Run").Invoke(null,new object[]{options});
                } catch(Exception e) { failure=e; }
            });
            worker.IsBackground=true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
            byte[][] frames={File.ReadAllBytes(args[2]),File.ReadAllBytes(args[3])};
            new Thread(delegate() { SendFixture(id,frames,pause,end); }) {IsBackground=true}.Start();
            int stage=0,width=0,start=0,beforeRepaint=0; long pauseStart=0; var clock=Stopwatch.StartNew();
            while(worker.IsAlive && clock.ElapsedMilliseconds<15000 && stage<6) {
                Pump(); var data=serializer.Deserialize<Dictionary<string,object>>((string)api.GetMethod("Snapshot").Invoke(null,null));
                Check(initial.SetEquals(VisibleWindows(desktop)),"Playback created another visible top-level window.");
                if(!data.ContainsKey("captured")) continue;
                int captured=Convert.ToInt32(data["captured"]); long elapsed=Convert.ToInt64(data["elapsedMs"]);
                if(stage==0 && captured>5) {
                    Check((string)data["mode"]=="original" && Convert.ToInt32(data["multiplier"])==1,"Enhancement defaults changed.");
                    width=Convert.ToInt32(data["outputWidth"]); window.Width+=170; window.Height+=50;
                    api.GetMethod("Configure").Invoke(null,new object[]{2,2}); stage=1;
                } else if(stage==1 && (string)data["effectivePictureMode"]=="fsr" && Convert.ToInt32(data["generated"])>2) {
                    Check(Convert.ToInt32(data["outputWidth"])>width,"Surface did not resize.");
                    api.GetMethod("Compare").Invoke(null,new object[]{true}); stage=2;
                } else if(stage==2 && (bool)data["compare"] && (string)data["effectivePictureMode"]=="original") {
                    api.GetMethod("Compare").Invoke(null,new object[]{false}); start=captured; stage=3;
                } else if(stage==3 && !(bool)data["compare"] && captured>start+8 && elapsed>2300) {
                    Check(Convert.ToInt32(data["targetPresentedFps"])==60 && Convert.ToInt32(data["audioQueueMs"])==17 &&
                          Convert.ToDouble(data["audioBitrateKbps"])==192.75 && Convert.ToInt32(data["audioSampleRate"])==48000 && Convert.ToInt32(data["audioChannels"])==2,
                          "Embedded target or audio telemetry missing.");
                    pause.Set(); pauseStart=elapsed; stage=4;
                } else if(stage==4 && elapsed>pauseStart+650) {
                    Check(data["audioBitrateKbps"]==null && data["audioSampleRate"]==null && data["audioChannels"]==null,
                          "The CDV2 unavailable fields retained stale audio metrics.");
                    start=captured; beforeRepaint=Convert.ToInt32(data["presented"]); window.Width+=53; stage=5;
                } else if(stage==5 && Convert.ToInt32(data["presented"])>beforeRepaint) {
                    Check(captured==start,"Paused source unexpectedly supplied a new frame during resize.");
                    stage=6; api.GetMethod("Stop").Invoke(null,null);
                }
            }
            if(failure!=null) throw failure;
            Check(stage==6,"Missing video, resize, paused repaint, input, or comparison transition: "+api.GetProperty("Error").GetValue(null,null)+" "+api.GetMethod("Snapshot").Invoke(null,null));
            while(worker.IsAlive && clock.ElapsedMilliseconds<19000) Pump();
            Check(!worker.IsAlive && result==0 && !(bool)api.GetProperty("Running").GetValue(null,null),"Embedded worker failed to stop.");
            var report=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(args[4]));
            CheckRenderer(report,renderer);
            Check((bool)report["embedded"] && Convert.ToInt32(report["sampledFrames"])>1 && Convert.ToInt32(report["sampleMaxLuma"])>Convert.ToInt32(report["sampleMinLuma"]),
                  "GPU output had no changing sampled luminance.");
            Console.WriteLine("Passed WPF child-HWND playback, GPU pixel readback, live resize/configuration, comparison and stop with unchanged visible top-level HWNDs. "+args[4]);
            end.Set();
            window.Width=1510; window.Height=925; window.UpdateLayout(); Pump();
            string demoReport=Path.ChangeExtension(args[4],".demo.json");
            string surface=host.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
            worker=Start(api,new[]{"--surface",surface,"--demo","--renderer",renderer,"--hdr","off","--verify-output","--report",demoReport},delegate(int value){result=value;});
            stage=0; clock.Restart();
            while(worker.IsAlive && clock.ElapsedMilliseconds<10000) {
                Pump(); var data=serializer.Deserialize<Dictionary<string,object>>((string)api.GetMethod("Snapshot").Invoke(null,null));
                Check(initial.SetEquals(VisibleWindows(desktop)),"Offline demo created another visible window.");
                if(!data.ContainsKey("sourceKind") || (string)data["sourceKind"]!="demo") continue;
                if(stage==0 && Convert.ToInt32(data["captured"])>5) {
                    api.GetMethod("ConfigureOutput").Invoke(null,new object[]{"720","auto"}); stage=1;
                } else if(stage==1 && (string)data["resolution"]=="720" && (string)data["hdrMode"]=="auto" && Convert.ToInt32(data["elapsedMs"])>2200) {
                    Check(Convert.ToInt32(data["inputWidth"])==960 && Convert.ToInt32(data["renderWidth"])<=1280 && data["audioQueueMs"]==null &&
                          data["audioBitrateKbps"]==null && data["audioSampleRate"]==null && data["audioChannels"]==null,
                          "Offline demo sizing or source metadata is wrong.");
                    api.GetMethod("Stop").Invoke(null,null); stage=2;
                }
            }
            Check(stage==2 && result==0,"Embedded demo/live output change failed. "+api.GetProperty("Error").GetValue(null,null));
            var measured=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(demoReport));
            CheckRenderer(measured,renderer);
            Check(Convert.ToInt32(measured["sampledFrames"])>=2 && Convert.ToInt32(measured["sampleMaxLuma"])>Convert.ToInt32(measured["sampleMinLuma"]),
                  "Offline motion scene did not produce measurable changing samples.");
            Console.WriteLine("Passed offline GPU scene and live output settings. "+demoReport);
            worker=Start(api,new[]{"--surface",surface,"--direct-pipe",id+"Pending","--direct-runtime",args[1],"--renderer",renderer,"--hdr","off"},delegate(int value){result=value;});
            clock.Restart();
            while(clock.ElapsedMilliseconds<450) Pump();
            api.GetMethod("Stop").Invoke(null,null);
            while(worker.IsAlive && clock.ElapsedMilliseconds<2400) Pump();
            Check(!worker.IsAlive && result==0,"Stopping before a Direct pipe connection did not complete.");
            Console.WriteLine("Passed cancellation before the first Direct connection.");
            worker=Start(api,new[]{"--surface",surface,"--demo","--renderer","metal","--hdr","off"},delegate(int value){result=value;});
            clock.Restart(); while(worker.IsAlive && clock.ElapsedMilliseconds<3000) Pump();
            Check(!worker.IsAlive && result!=0 && ((string)api.GetProperty("Error").GetValue(null,null)).Contains("metal"),
                  "A Windows request for Metal should fail explicitly.");
            exitCode=0;
        } catch(Exception e) { Console.Error.WriteLine(e); File.WriteAllText(args[4]+".error.txt",e.ToString()); }
        finally {
            api.GetMethod("Stop").Invoke(null,null); end.Set();
            var deadline=Stopwatch.StartNew(); while(worker!=null && worker.IsAlive && deadline.ElapsedMilliseconds<3000) Pump();
            window.Close(); host.Dispose();
        }
        return exitCode;
    }
}
