using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Forms;

internal static partial class EmbeddedProbe
{
    public static int RunAmdFrames(string[] args) {
        if(args.Length==3) return RunOnDesktop(args,"P5CastAmdFg"+Guid.NewGuid().ToString("N"));
        if(args.Length!=4 || args[3]!="--inactive-desktop") return 2;
        string backend=args[2];
        var api=Assembly.LoadFrom(args[0]).GetType("Cadrivect.Engine.EmbeddedPlayback",true);
        try {
            var screen=Screen.AllScreens.First(s=>s.DeviceName==args[1]);
            using(var host=new VideoHost()) {
                var window=new Window {Content=host,Width=900,Height=506,Left=screen.Bounds.Left+8,Top=screen.Bounds.Top+8,
                    ResizeMode=ResizeMode.NoResize,WindowStyle=WindowStyle.None,ShowInTaskbar=false};
                window.Show();window.UpdateLayout();Pump();
                try {
                    foreach(string color in new[]{"off","hdr10"}) {
                        string mode=color=="off" ? "sharp" : "fsr-temporal";
                        string surface=host.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
                        string report=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"amd-"+color+".json");
                        int result=-1;
                        var worker=Start(api,new[]{"--surface",surface,"--demo","--renderer",backend,"--hdr",color,"--mode",mode,
                            "--frame-generation","amd","--multiplier","2","--target-fps","30","--verify-output","--report",report},v=>result=v);
                        try {
                            var start=WaitSnapshot(api,data=>Convert.ToInt32(data["generated"])>=3 &&
                                (string)data["activeFrameGenerationVersion"]!="" && (string)data["effectivePictureMode"]==mode,"AMD generated midpoint frames");
                            int generated=Convert.ToInt32(start["generated"]),submitted=Convert.ToInt32(start["presented"]);
                            var end=WaitSnapshot(api,data=>Convert.ToInt32(data["generated"])>=generated+3 &&
                                Convert.ToInt32(data["presented"])>=submitted+7,"advancing real and AMD-generated cadence");
                            Check((string)end["frameGenerationProvider"]=="amd" && (string)end["processingRenderer"]==backend &&
                                  Convert.ToInt32(end["targetPresentedFps"])==60 &&
                                  Convert.ToBoolean(end["presenterHdr10"])==(color=="hdr10") &&
                                  string.IsNullOrEmpty((string)end["effectNotice"]),"AMD provider, cadence target, or color output failed.");
                            string version=(string)end["activeFrameGenerationVersion"];
                            api.GetMethod("SetComparison").Invoke(null,new object[]{"original"});
                            var paused=WaitSnapshot(api,data=>(string)data["comparisonView"]=="original" &&
                                (string)data["activeFrameGenerationVersion"]=="","reference-only mode releases AMD FG");
                            int before=Convert.ToInt32(paused["generated"]);
                            var reference=WaitSnapshot(api,data=>Convert.ToInt32(data["presented"])>=Convert.ToInt32(paused["presented"])+3,"live reference cadence");
                            Check(Convert.ToInt32(reference["generated"])==before,"Generated frame recorded in original-only view.");
                            api.GetMethod("SetComparison").Invoke(null,new object[]{"enhanced"});
                            WaitSnapshot(api,data=>(string)data["activeFrameGenerationVersion"]==version &&
                                Convert.ToInt32(data["generated"])>before,"resuming AMD FG after comparison");
                            Console.WriteLine("AMD FG {0} on {1} with {2} ({3}) advanced real/generated frames, disabled for Original and resumed on Enhanced.",version,backend,mode,color);
                        } finally {
                            api.GetMethod("Stop").Invoke(null,null);
                            var timeout=Stopwatch.StartNew();while(worker.IsAlive && timeout.ElapsedMilliseconds<5000) Pump();
                            Check(!worker.IsAlive && result==0,"AMD footage did not close cleanly: "+api.GetProperty("Error").GetValue(null,null));
                        }
                        var measured=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(report));
                        Check(Convert.ToInt32(measured["sampledFrames"])>0 &&
                              Convert.ToInt32(measured["sampleMaxLuma"])-Convert.ToInt32(measured["sampleMinLuma"])>40,
                              "Embedded AMD playback produced a blank or low-contrast image.");
                    }
                } finally {window.Close();}
            }
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
internal static class AmdFgRunner { [STAThread] static int Main(string[] args) {return EmbeddedProbe.RunAmdFrames(args);} }
