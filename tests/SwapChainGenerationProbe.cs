using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Forms=System.Windows.Forms;

internal static partial class EmbeddedProbe
{
    static int Counter(Dictionary<string,object> state,string key) {return Convert.ToInt32(state[key]);}

    static Dictionary<string,object> VerifyProxyFrames(Type api,string provider,bool supported,string picture=null)
    {
        string label=provider=="dlss-fg" ? "DLSS" : "XeSS";
        var first=WaitSnapshot(api,s=>Equals(s["frameGenerationProvider"],provider) && Counter(s,"captured")>=4 &&
            (picture==null || Equals(s["effectivePictureMode"],picture)) &&
            (Equals(s["activeFrameGenerationProvider"],provider) || !string.IsNullOrEmpty(Convert.ToString(s["effectNotice"]))),
            provider+" activity or a specific failure reason",25000);
        int prior=Counter(first,"proxyGenerated");
        if(supported) {
            Check(Equals(first["activeFrameGenerationProvider"],provider) && string.IsNullOrEmpty(Convert.ToString(first["effectNotice"])),
                label+" passed the adapter probe but could not present: "+first["effectNotice"]);
            var next=WaitSnapshot(api,s=>Equals(s["activeFrameGenerationProvider"],provider) && Counter(s,"proxyGenerated")>=prior+3 &&
                Counter(s,"captured")>=Counter(first,"captured")+3,provider+" confirmed extra presentations",10000);
            Check(Counter(next,"generated")-Counter(first,"generated")==Counter(next,"proxyGenerated")-prior,
                label+" was credited with frames outside its presentation counter.");
            Console.WriteLine("{0} {1} proxy presentations {2} -> {3}; source frames {4} -> {5}.",
                provider,next["hdrMode"],prior,next["proxyGenerated"],first["captured"],next["captured"]);
            return next;
        }
        string reason=Convert.ToString(first["effectNotice"]);
        Check(reason.IndexOf(label,StringComparison.OrdinalIgnoreCase)>=0 && Equals(first["activeFrameGenerationProvider"],""),
            "Unavailable "+label+" lacks a specific reason or claims to be active. "+reason);
        var fallback=WaitSnapshot(api,s=>Counter(s,"captured")>=Counter(first,"captured")+9,provider+" real frames after rejection",8000);
        Check(Counter(fallback,"proxyGenerated")==prior && Counter(fallback,"generated")==Counter(first,"generated") &&
            Counter(fallback,"presented")>Counter(first,"presented") && Equals(fallback["activeFrameGenerationProvider"],""),
            "Unavailable "+label+" counted intermediate frames or stalled playback.");
        Console.WriteLine("{0} {1} preserved source playback. {2}",provider,fallback["hdrMode"],reason);
        return fallback;
    }

    static void VerifyProxyPicturePairs(Type api,string provider,string[] pictures)
    {
        foreach(string mode in pictures) {
            if(mode=="none") continue;
            int index=mode=="dlss"?4:mode=="dlaa"?5:3;
            api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{index,1,"p5cast"});
            var paused=WaitSnapshot(api,s=>Equals(s["frameGenerationProvider"],"p5cast") && Equals(s["activeFrameGenerationProvider"],"") &&
                Equals(s["effectivePictureMode"],mode) && Equals(s["activeNvidiaEffect"],mode),mode+" with frame generation off",12000);
            api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{index,2,provider});
            var paired=VerifyProxyFrames(api,provider,true,mode);
            Check(Equals(paired["activeNvidiaEffect"],mode) && Counter(paired,"nvidiaEvaluations")>Counter(paused,"nvidiaEvaluations"),
                "Selecting "+provider+" interrupted "+mode+" picture evaluations.");
            Console.WriteLine("Confirmed "+mode+" active with "+provider+".");
        }
        if(Array.IndexOf(pictures,"rtxvideo")>=0) {
            api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{3,1,"p5cast"});
            VerifyProxyIdle(api,"enhanced","p5cast");
            var stable=WaitSnapshot(api,s=>Equals(s["activeNvidiaEffect"],"rtxvideo"),"VSR retained after disabling "+provider);
            int evaluations=Counter(stable,"nvidiaEvaluations");
            WaitSnapshot(api,s=>Equals(s["activeNvidiaEffect"],"rtxvideo") && Counter(s,"nvidiaEvaluations")>=evaluations+4 &&
                string.IsNullOrEmpty(Convert.ToString(s["effectNotice"])),"continued RTX Video SR evaluations without "+provider);
            api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{3,2,provider});
            VerifyProxyFrames(api,provider,true,"rtxvideo");
            if(Array.IndexOf(pictures,"dlss")>=0) {
                api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{4,2,provider});
                VerifyProxyFrames(api,provider,true,"dlss");
            }
        }
    }

    static void VerifyProxyIdle(Type api,string view,string selected)
    {
        var idle=WaitSnapshot(api,s=>Equals(s["comparisonView"],view) && Equals(s["frameGenerationProvider"],selected) &&
            Equals(s["activeFrameGenerationProvider"],"") && string.IsNullOrEmpty(Convert.ToString(s["effectNotice"])),
            "inactive frame generation in "+view);
        var next=WaitSnapshot(api,s=>Counter(s,"captured")>=Counter(idle,"captured")+9 && Counter(s,"presented")>Counter(idle,"presented"),
            "continued real pictures with frame generation suspended");
        Check(Counter(next,"proxyGenerated")==Counter(idle,"proxyGenerated") && Counter(next,"generated")==Counter(idle,"generated"),
            "Extra presentations continued after frame generation was suspended.");
    }

    internal static int CheckSwapChainGeneration(string[] args)
    {
        // Both SDKs suppress generation on an occluded HWND. A nonactivating
        // window on the current desktop allows actual DXGI presentation counts.
        if(args.Length!=7) return 2;
        try {
            var api=Assembly.LoadFrom(args[0]).GetType("Cadrivect.Engine.EmbeddedPlayback",true);
            string backend=args[1],initial=args[2],other=initial=="dlss-fg" ? "xess-fg" : "dlss-fg";
            string display=args[5];
            using(var host=new VideoHost()) {
                var window=new Window {Content=host,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None,Width=1440,Height=900};
                window.Show();
                if(display!="sdr") {
                    if(display.Equals("auto",StringComparison.OrdinalIgnoreCase)) {
                        var targets=api.Assembly.GetType("DisplayTarget",true);
                        foreach(var output in (Array)targets.GetMethod("Enumerate").Invoke(null,null))
                            if((bool)targets.GetProperty("AdvancedColorEnabled").GetValue(output,null)) {
                                display=(string)targets.GetProperty("DeviceName").GetValue(output,null);break;
                            }
                    }
                    var target=Array.Find(Forms.Screen.AllScreens,s=>Equals(s.DeviceName,display));
                    Check(target!=null,"The selected HDR monitor could not be found.");
                    MoveToDisplay(window,target);
                }
                IntPtr hwnd=new WindowInteropHelper(window).Handle;
                Check(SetWindowPos(hwnd,IntPtr.Zero,0,0,1440,900,0x12),"Could not size the initial playback surface.");
                window.UpdateLayout();Pump();
                string surface=host.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
                int result=-1;
                var worker=Start(api,new[]{"--surface",surface,"--demo","--renderer",backend,"--hdr",display=="sdr"?"off":"hdr10",
                    "--mode","original","--comparison","enhanced","--frame-generation",initial,"--multiplier","2","--target-fps","30"},
                    v=>result=v);
                bool rejectedApi=backend!="d3d12";
                try {
                    if(rejectedApi) {
                        var timeout=Stopwatch.StartNew();while(worker.IsAlive && timeout.ElapsedMilliseconds<5000) Pump();
                        string reason=Convert.ToString(api.GetProperty("Error").GetValue(null,null));
                        Check(args[3]=="no" && !worker.IsAlive && result!=0 && reason.Contains("Direct3D 12") &&
                            reason.Contains(initial=="dlss-fg"?"DLSS":"XeSS"),"Incompatible graphics API accepted or lacked a clear rejection. "+reason);
                        Console.WriteLine("{0} on {1} returned: {2}",initial,backend,reason);
                        return 0;
                    }
                    var first=VerifyProxyFrames(api,initial,args[3]=="yes");
                    CheckRenderer(first,backend);
                    Check(Equals(first["presenterHdr10"],display!="sdr"),"Wrong HDR state for proxy frame generation.");
                    int previousWidth=Counter(first,"surfaceWidth");
                    Check(SetWindowPos(hwnd,IntPtr.Zero,0,0,1360,800,0x16),"Could not resize playback.");
                    window.UpdateLayout();Pump();
                    WaitSnapshot(api,s=>Counter(s,"surfaceWidth")!=previousWidth,"resized proxy presentation surface",10000);
                    VerifyProxyFrames(api,initial,args[3]=="yes");
                    api.GetMethod("SetComparison").Invoke(null,new object[]{"original"});
                    VerifyProxyIdle(api,"original",initial);
                    api.GetMethod("SetComparison").Invoke(null,new object[]{"enhanced"});
                    VerifyProxyFrames(api,initial,args[3]=="yes");
                    if(args[3]=="yes") VerifyProxyPicturePairs(api,initial,args[6].Split(','));
                    api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{1,2,other});
                    var switched=VerifyProxyFrames(api,other,args[4]=="yes");
                    Check(Equals(switched["effectivePictureMode"],"sharp") && Equals(switched["presenterHdr10"],display!="sdr"),
                        "A live provider switch dropped the picture effect or HDR mode.");
                    api.GetMethod("ConfigureWithGeneration").Invoke(null,new object[]{0,1,"p5cast"});
                    VerifyProxyIdle(api,"enhanced","p5cast");
                } finally {
                    api.GetMethod("Stop").Invoke(null,null);
                    var stop=Stopwatch.StartNew();while(worker.IsAlive && stop.ElapsedMilliseconds<8000) Pump();
                    window.Close();
                    Check(!worker.IsAlive && (result==0 || rejectedApi),"Frame generation left an unclosed playback session. "+api.GetProperty("Error").GetValue(null,null));
                }
            }
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}

internal static class SwapChainGenerationRunner
{
    [STAThread] static int Main(string[] args) {return EmbeddedProbe.CheckSwapChainGeneration(args);}
}
