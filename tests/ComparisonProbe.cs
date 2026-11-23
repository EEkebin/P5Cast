using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using Rectangle=System.Drawing.Rectangle;

internal static partial class EmbeddedProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct ClientBounds { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd,out ClientBounds bounds);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);

    static Bitmap CaptureComparison(Window window) {
        var hwnd=new WindowInteropHelper(window).Handle;
        ClientBounds bounds;
        Check(GetClientRect(hwnd,out bounds),"Cannot measure comparison window.");
        var bitmap=new Bitmap(bounds.Right,bounds.Bottom);
        try {
            using(var graphics=Graphics.FromImage(bitmap)) {
                var dc=graphics.GetHdc();
                try { Check(PrintWindow(hwnd,dc,3),"Cannot capture the fixture on the isolated desktop."); }
                finally { graphics.ReleaseHdc(dc); }
            }
            return bitmap;
        } catch { bitmap.Dispose(); throw; }
    }

    static Dictionary<string,object> WaitSnapshot(Type api,Func<Dictionary<string,object>,bool> accept,string condition) {
        var clock=Stopwatch.StartNew(); var serializer=new JavaScriptSerializer(); string last="";
        while(clock.ElapsedMilliseconds<5000) {
            Pump(); last=(string)api.GetMethod("Snapshot").Invoke(null,null);
            var sample=serializer.Deserialize<Dictionary<string,object>>(last);
            if(sample.ContainsKey("comparisonView") && accept(sample)) return sample;
        }
        throw new Exception("Timed out waiting for "+condition+". "+api.GetProperty("Error").GetValue(null,null)+" "+last);
    }

    // The 128x64 lossless fixture contains three 5x7 glyphs, each cell 5x5 pixels.
    static readonly string[] ComparisonGlyphs={
        "11110100011000111110100001000010000","01111100001000001110000010000111110","11111100001000011110000010000111110"
    };
    static Rectangle CheckFixture(Bitmap image,int origin,int paneWidth) {
        double scale=Math.Min(paneWidth/128.0,image.Height/64.0);
        int w=(int)Math.Round(128*scale),h=(int)Math.Round(64*scale);
        var frame=new Rectangle(origin+(paneWidth-w)/2,(image.Height-h)/2,w,h);
        Func<int,int,int> luma=(x,y)=>image.GetPixel(frame.X+(int)((x+.5)*scale),frame.Y+(int)((y+.5)*scale)).R;
        bool inverse=luma(6,5)>120;
        int matches=0;
        for(int g=0;g<3;g++) for(int row=0;row<7;row++) for(int col=0;col<5;col++) {
            bool expected=(ComparisonGlyphs[g][row*5+col]=='1')!=inverse;
            if((luma(18+g*30+col*5,14+row*5)>120)==expected) matches++;
        }
        Check(matches>=102,"The complete, aspect-fitted PS5 fixture was not visible in pane at x="+origin+": "+matches+"/105 glyph cells matched.");
        if(frame.Top>4) Check(image.GetPixel(frame.X+frame.Width/2,frame.Top/2).R<8,"Upper letterbox is not black.");
        return frame;
    }
    static int ChangedPixels(Bitmap before,Bitmap after,Rectangle area) {
        int changed=0;
        // Sample both uniform glyph interiors and their antialiased boundaries.
        for(int y=area.Top+2;y<area.Bottom-2;y+=2) for(int x=area.Left+2;x<area.Right-2;x+=2)
            if(Math.Abs(before.GetPixel(x,y).R-after.GetPixel(x,y).R)>3) changed++;
        return changed;
    }

    static void CheckWipePixels(Bitmap actual,Bitmap original,Bitmap enhanced,Rectangle frame,float position) {
        Check(original.Size==enhanced.Size && actual.Size==original.Size,"Wipe changed the output size.");
        bool interior=position>0 && position<1;
        int cut=frame.Left+(int)Math.Round(position*frame.Width);
        int wrong=0,samples=0;
        // Use screen coordinates so the test also checks that the letterbox and pillarbox stay put.
        for(int y=0;y<actual.Height;y+=3) for(int x=0;x<actual.Width;x+=3) {
            if(interior && y>=frame.Top && y<frame.Bottom && Math.Abs(x-cut)<=1) continue;
            var reference=x<cut ? original : enhanced;
            if(Math.Abs(reference.GetPixel(x,y).R-actual.GetPixel(x,y).R)>2) wrong++;
            samples++;
        }
        Check(wrong==0,wrong+" of "+samples+" physical pixel samples differed from their full-view references at divider "+position+".");
        if(interior) {
            bool visible=false;
            for(int x=cut-1;x<=cut;x++) {
                int y=frame.Top+6;
                if(Math.Abs(actual.GetPixel(x,y).R-original.GetPixel(x,y).R)>8) visible=true;
            }
            Check(visible,"Missing visible divider at the requested picture position "+position);
        }
    }

    static Dictionary<string,object> SetWipePosition(Type api,float position) {
        api.GetMethod("SetWipePosition").Invoke(null,new object[]{position});
        return WaitSnapshot(api,s=>(string)s["comparisonView"]=="wipe" &&
            Math.Abs(Convert.ToDouble(s["wipePosition"])-position)<.0001,"wipe at "+position);
    }

    static void CheckFullSizeWipe(Window window,Type api,Action<string> setView,
                                  Func<string,string,Dictionary<string,object>> waitView,float[] positions) {
        setView("original"); waitView("original","original");
        using(var original=CaptureComparison(window)) {
            var frame=CheckFixture(original,0,original.Width);
            setView("enhanced"); var single=waitView("enhanced","sharp");
            using(var enhanced=CaptureComparison(window)) {
                Check(ChangedPixels(original,enhanced,frame)>20,"Original and enhanced reference frames did not distinguish sharpening.");
                setView("wipe");
                foreach(float position in positions) {
                    var data=SetWipePosition(api,position);
                    var reported=new Rectangle(Convert.ToInt32(data["pictureX"]),Convert.ToInt32(data["pictureY"]),
                        Convert.ToInt32(data["pictureWidth"]),Convert.ToInt32(data["pictureHeight"]));
                    Check(reported==frame,"The divider hit bounds differ from the physical picture.");
                    Check(Convert.ToInt32(data["renderWidth"])==Convert.ToInt32(single["renderWidth"]) &&
                          Convert.ToInt32(data["renderHeight"])==Convert.ToInt32(single["renderHeight"]),
                          "Wipe changed the enhancement resolution.");
                    using(var wipe=CaptureComparison(window)) CheckWipePixels(wipe,original,enhanced,frame,position);
                }
                Console.WriteLine("Wipe matched the original/enhanced full-size references in "+frame.Width+"x"+frame.Height+
                    " picture pixels within a "+enhanced.Width+"x"+enhanced.Height+" surface.");
            }
        }
    }

    static int CheckComparison(Type api,string[] args) {
        string id="P5CastCompare"+Guid.NewGuid().ToString("N");
        var host=new VideoHost();
        var window=new Window {Title="Comparison fixture",Content=host,Width=1024,Height=384,WindowStyle=WindowStyle.None,
            ResizeMode=ResizeMode.NoResize,ShowActivated=false,ShowInTaskbar=false,Left=80,Top=80,WindowStartupLocation=WindowStartupLocation.Manual};
        var pause=new ManualResetEvent(false); var end=new ManualResetEvent(false);
        Thread worker=null; int result=-1;
        try {
            window.Show(); window.UpdateLayout(); Pump();
            var desktop=GetThreadDesktop(GetCurrentThreadId()); var visible=VisibleWindows(desktop);
            string[] options={"--surface",host.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),"--direct-pipe",id,"--direct-runtime",args[1],
                "--renderer",args[5],"--verify-output","--target-fps","30","--hdr","off","--report",args[4]};
            worker=Start(api,options,delegate(int value){result=value;});
            byte[][] frames={File.ReadAllBytes(args[2]),File.ReadAllBytes(args[3])};
            new Thread(delegate(){SendFixture(id,frames,pause,end);}) {IsBackground=true}.Start();
            Func<string,string,Dictionary<string,object>> waitView=(view,picture)=>WaitSnapshot(api,s=>
                (string)s["comparisonView"]==view && (string)s["effectivePictureMode"]==picture && Convert.ToInt32(s["captured"])>4,view+" / "+picture);
            var first=waitView("enhanced","original"); CheckRenderer(first,args[5]);
            pause.Set();
            // Drain in-flight packets; subsequent transitions must repaint the same static source.
            var clock=Stopwatch.StartNew(); while(clock.ElapsedMilliseconds<350) Pump();
            Action<string> setView=view=>api.GetMethod("SetComparison").Invoke(null,new object[]{view});
            // The inactive desktop compositor exposes D3D swap chains to PrintWindow.
            // SDL Vulkan and OpenGL present successfully, but their captures are black.
            bool supportsCapture=args[5]!="vulkan" && args[5]!="opengl";
            if(supportsCapture) {
                using(var original=CaptureComparison(window)) {
                    var full=CheckFixture(original,0,original.Width);
                    setView("side-by-side"); waitView("side-by-side","original");
                    using(var split=CaptureComparison(window)) {
                        int half=split.Width/2;
                        var left=CheckFixture(split,0,half); var right=CheckFixture(split,split.Width-half,half);
                        api.GetMethod("Configure").Invoke(null,new object[]{1,2});
                        waitView("side-by-side","sharp");
                        using(var sharp=CaptureComparison(window)) {
                            CheckFixture(sharp,0,half); CheckFixture(sharp,sharp.Width-half,half);
                            int leftChanged=ChangedPixels(split,sharp,left),rightChanged=ChangedPixels(split,sharp,right);
                            Check(leftChanged==0 && rightChanged>20,"Sharpen changed "+leftChanged+" original pixels and "+rightChanged+" enhanced pixels.");
                            Console.WriteLine("Split shows both complete fixtures. Sharpen changed "+rightChanged+" sampled pixels on the right and "+leftChanged+" on the left.");
                        }
                    }
                    api.GetMethod("Compare").Invoke(null,new object[]{true}); waitView("original","original");
                    using(var baseline=CaptureComparison(window)) {
                        CheckFixture(baseline,0,baseline.Width);
                        Check(ChangedPixels(original,baseline,full)==0,"Full original differs from the original fixture before comparison.");
                    }
                    api.GetMethod("Compare").Invoke(null,new object[]{false}); waitView("enhanced","sharp");
                    using(var enhanced=CaptureComparison(window)) {
                        CheckFixture(enhanced,0,enhanced.Width);
                        Check(ChangedPixels(original,enhanced,full)>20,"Full enhanced mode did not restore the selected picture effect.");
                    }
                }
            } else {
                setView("side-by-side"); waitView("side-by-side","original");
                api.GetMethod("Configure").Invoke(null,new object[]{1,2}); waitView("side-by-side","sharp");
                api.GetMethod("Compare").Invoke(null,new object[]{true}); waitView("original","original");
                api.GetMethod("Compare").Invoke(null,new object[]{false}); waitView("enhanced","sharp");
            }
            if(supportsCapture) {
                CheckFullSizeWipe(window,api,setView,waitView,new[]{.5f,.25f,.75f,0f,1f});
                // Resize and cap the processing raster while the source is paused. The letterboxed output is wider than the 720p processing cap.
                window.Width=1403; window.Height=811; window.UpdateLayout(); Pump();
                ClientBounds resized;
                Check(GetClientRect(host.Handle,out resized),"Cannot measure resized fixture.");
                WaitSnapshot(api,s=>Convert.ToInt32(s["outputWidth"])==resized.Right && Convert.ToInt32(s["outputHeight"])==resized.Bottom,"resized wipe output");
                api.GetMethod("ConfigureOutput").Invoke(null,new object[]{"720",null});
                var capped=WaitSnapshot(api,s=>(string)s["resolution"]=="720","capped processing raster");
                Check(Convert.ToInt32(capped["renderWidth"])<Convert.ToInt32(capped["outputWidth"]),"Fixture did not exercise processing below output resolution.");
                CheckFullSizeWipe(window,api,setView,waitView,new[]{.35f,.65f});
            } else {
                setView("wipe"); SetWipePosition(api,.25f); SetWipePosition(api,.75f);
            }
            setView("side-by-side"); var moving=waitView("side-by-side","sharp");
            int generated=Convert.ToInt32(moving["generated"]); pause.Reset();
            WaitSnapshot(api,s=>(string)s["comparisonView"]=="side-by-side" && Convert.ToInt32(s["generated"])>generated+3 &&
                Convert.ToInt32(s["targetPresentedFps"])==60,"2x interpolation in the enhanced pane");
            setView("wipe"); var wiped=SetWipePosition(api,.5f);
            generated=Convert.ToInt32(wiped["generated"]);
            WaitSnapshot(api,s=>(string)s["comparisonView"]=="wipe" && Convert.ToInt32(s["generated"])>generated+3 &&
                Convert.ToInt32(s["targetPresentedFps"])==60,"2x interpolation during a wipe");
            Check(visible.SetEquals(VisibleWindows(desktop)),"Changing comparison opened a second top-level window.");
            api.GetMethod("Stop").Invoke(null,null); clock.Restart(); while(worker.IsAlive && clock.ElapsedMilliseconds<3000) Pump();
            Check(!worker.IsAlive && result==0,"Comparison playback did not stop cleanly.");
            var final=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(args[4]));
            Check(Convert.ToInt32(final["sampledFrames"])>0 && Convert.ToInt32(final["sampleMaxLuma"])>Convert.ToInt32(final["sampleMinLuma"])+64,
                  "The GPU output did not contain the high-contrast fixture.");
            Console.WriteLine(args[5]+" passed live view changes, focused original/enhanced output, side-by-side, and full-size wipe with interpolation.");
            return 0;
        } catch(Exception e) {
            Console.Error.WriteLine(e); File.WriteAllText(args[4]+".error.txt",e.ToString());
            using(var image=CaptureComparison(window)) image.Save(Path.ChangeExtension(args[4],".png"));
            return 1;
        } finally {
            api.GetMethod("Stop").Invoke(null,null); end.Set();
            var stop=Stopwatch.StartNew(); while(worker!=null && worker.IsAlive && stop.ElapsedMilliseconds<3000) Pump();
            window.Close(); host.Dispose();
        }
    }
}
