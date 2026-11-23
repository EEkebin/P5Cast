using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;

internal static partial class EmbeddedProbe
{
    internal static int CheckFrucPlayback(string[] args)
    {
        if(args.Length==4) return RunOnDesktop(args,"P5CastFruc"+Guid.NewGuid().ToString("N"),60000);
        if(args.Length!=5 || args[4]!="--inactive-desktop") return 2;
        var api=Assembly.LoadFrom(args[0]).GetType("Cadrivect.Engine.EmbeddedPlayback",true);
        var features=args[1].Split(',');
        bool supported=features[0]=="available";
        try {
            using(var host=new VideoHost()) {
                var window=new Window {Content=host,Width=1280,Height=800,ShowInTaskbar=false};
                window.Show();window.UpdateLayout();Pump();
                string surface=host.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
                int result=-1;
                var worker=Start(api,new[]{"--surface",surface,"--demo","--renderer",args[2],"--hdr","off","--mode","original",
                    "--frame-generation","fruc","--multiplier","2","--target-fps","30"},v=>result=v);
                try {
                    var ready=WaitSnapshot(api,s=>Convert.ToInt32(s["presented"])>=10 &&
                        (supported ? (string)s["activeFrameGenerationProvider"]=="fruc" :
                            Convert.ToString(s["effectNotice"]).IndexOf("FRUC",StringComparison.OrdinalIgnoreCase)>=0),"FRUC result",10000);
                    Check((string)ready["frameGenerationProvider"]=="fruc","Requested FRUC provider was lost.");
                    int before=Convert.ToInt32(ready["generated"]),midpoints=Convert.ToInt32(ready["frucMidpoints"]);
                    if(supported) {
                        WaitSnapshot(api,s=>Convert.ToInt32(s["frucMidpoints"])>midpoints && Convert.ToInt32(s["generated"])>before,
                            "frames generated and presented by NVIDIA FRUC");
                        foreach(string mode in features) {
                            if(mode=="available") continue;
                            int index=mode=="nis"?8:mode=="dlaa"?5:3;
                            api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{index,2,"fruc"});
                            var activated=WaitSnapshot(api,s=>Equals(s["effectivePictureMode"],mode) && Equals(s["activeFrameGenerationProvider"],"fruc"),
                                mode+" with NVIDIA FRUC");
                            int prior=Convert.ToInt32(activated["frucMidpoints"]);
                            WaitSnapshot(api,s=>Equals(s["effectivePictureMode"],mode) && Convert.ToInt32(s["frucMidpoints"])>=prior+2 &&
                                string.IsNullOrEmpty(Convert.ToString(s["effectNotice"])),mode+" with advancing FRUC midpoints");
                            Console.WriteLine("Confirmed NVIDIA FRUC with "+mode+".");
                        }
                        api.GetMethod("SetComparison").Invoke(null,new object[]{"original"});
                        WaitSnapshot(api,s=>(string)s["comparisonView"]=="original" && (string)s["activeFrameGenerationProvider"]=="",
                            "FRUC suspended in the Original view");
                        api.GetMethod("SetComparison").Invoke(null,new object[]{"enhanced"});
                        WaitSnapshot(api,s=>(string)s["activeFrameGenerationProvider"]=="fruc","FRUC resuming");
                        if(args[3]=="amd") {
                            api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{0,2,"amd"});
                            var switched=WaitSnapshot(api,s=>(string)s["activeFrameGenerationProvider"]=="amd" &&
                                !string.IsNullOrEmpty((string)s["activeFrameGenerationVersion"]),"switching to AMD");
                            int amdInitial=Convert.ToInt32(switched["generated"]);
                            WaitSnapshot(api,s=>Convert.ToInt32(s["generated"])>amdInitial,"AMD-generated frame");
                            api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{0,2,"fruc"});
                            WaitSnapshot(api,s=>(string)s["activeFrameGenerationProvider"]=="fruc" &&
                                Convert.ToInt32(s["frucMidpoints"])>Convert.ToInt32(switched["frucMidpoints"]),"returning from AMD to FRUC");
                            Console.WriteLine("Switched live from NVIDIA FRUC to AMD FSR and back.");
                        }
                    } else {
                        Check(before==0 && midpoints==0 && (string)ready["activeFrameGenerationProvider"]=="","An unavailable FRUC provider recorded generated frames.");
                        var next=WaitSnapshot(api,s=>Convert.ToInt32(s["presented"])>=Convert.ToInt32(ready["presented"])+7,"source continuing after FRUC failure");
                        Check(Convert.ToInt32(next["generated"])==0,"FRUC silently fell back to another interpolator.");
                    }
                    api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{0,2,"p5cast"});
                    WaitSnapshot(api,s=>(string)s["frameGenerationProvider"]=="p5cast" && Convert.ToInt32(s["generated"])>before &&
                        string.IsNullOrEmpty(Convert.ToString(s["effectNotice"])),"switching back to P5Cast interpolation");
                    Console.WriteLine(supported?"NVIDIA FRUC generated frames, suspended for Original and resumed.":"Unavailable NVIDIA FRUC kept real video running without generating frames.");
                } finally {
                    api.GetMethod("Stop").Invoke(null,null);
                    var timeout=Stopwatch.StartNew();while(worker.IsAlive && timeout.ElapsedMilliseconds<5000) Pump();
                    window.Close();
                    Check(!worker.IsAlive && result==0,"Embedded playback did not close cleanly.");
                }
            }
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}

internal static class FrucPlaybackRunner
{
    [STAThread] static int Main(string[] args) {return EmbeddedProbe.CheckFrucPlayback(args);}
}
