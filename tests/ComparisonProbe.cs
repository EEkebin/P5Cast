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
            setView("side-by-side"); var moving=waitView("side-by-side","sharp");
            int generated=Convert.ToInt32(moving["generated"]); pause.Reset();
            WaitSnapshot(api,s=>(string)s["comparisonView"]=="side-by-side" && Convert.ToInt32(s["generated"])>generated+3 &&
                Convert.ToInt32(s["targetPresentedFps"])==60,"2x interpolation in the enhanced pane");
            Check(visible.SetEquals(VisibleWindows(desktop)),"Changing comparison opened a second top-level window.");
            api.GetMethod("Stop").Invoke(null,null); clock.Restart(); while(worker.IsAlive && clock.ElapsedMilliseconds<3000) Pump();
            Check(!worker.IsAlive && result==0,"Comparison playback did not stop cleanly.");
            var final=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(args[4]));
            Check(Convert.ToInt32(final["sampledFrames"])>0 && Convert.ToInt32(final["sampleMaxLuma"])>Convert.ToInt32(final["sampleMinLuma"])+64,
                  "The GPU output did not contain the high-contrast fixture.");
            Console.WriteLine(args[5]+" passed live view changes, focused original/enhanced output, and side-by-side with interpolation.");
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
