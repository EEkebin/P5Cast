using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;
using Forms=System.Windows.Forms;
using Point=System.Drawing.Point;
using Size=System.Drawing.Size;

internal static class NisQualityRunner {
    [STAThread] public static int Main(string[] args) {return EmbeddedProbe.CheckNisQuality(args);}
}

internal static partial class EmbeddedProbe
{
    static double FromHalf(ushort bits) {
        int exponent=(bits>>10)&31,fraction=bits&1023;
        if(exponent==31) return double.NaN;
        double magnitude=exponent==0?fraction/16777216.0:(1+fraction/1024.0)*Math.Pow(2,exponent-15);
        return (bits&0x8000)==0?magnitude:-magnitude;
    }

    static List<Point> NisSampleLocations(Size size) {
        var locations=new List<Point>();
        for(int band=0;band<5;band++) locations.Add(new Point((2*band+1)*size.Width/10,size.Height/10));
        // The last output column is inside a flat colored patch. This catches
        // dispatches that miss a partial group on the right.
        locations.Add(new Point(size.Width-1,size.Height/10));
        locations.Add(new Point(size.Width/2,size.Height/2));
        for(int y=size.Height/4;y<size.Height;y+=Math.Max(1,size.Height/71))
            for(int x=size.Width/16;x<size.Width;x+=Math.Max(1,size.Width/137))
                locations.Add(new Point(x,y));
        for(int x=size.Width/16;x<size.Width;x+=Math.Max(1,size.Width/137))
            locations.Add(new Point(x,size.Height-1));
        return locations;
    }

    // Read RGBA from the actual NIS UAV and BGRA8/PQ RGB10 from the presenter using the same sample points.
    static double[,] ReadNisColors(Device device,Texture2D texture,List<Point> locations) {
        var desc=texture.Description;
        var format=desc.Format;
        Check(format==Format.R16G16B16A16_Float || format==Format.R8G8B8A8_UNorm ||
            format==Format.R10G10B10A2_UNorm || format==Format.B8G8R8A8_UNorm,"Unsupported readback format "+format);
        desc.Usage=ResourceUsage.Staging;desc.BindFlags=BindFlags.None;desc.CpuAccessFlags=CpuAccessFlags.Read;
        desc.OptionFlags=ResourceOptionFlags.None;
        var data=new double[locations.Count,3];
        using(var copy=new Texture2D(device,desc)) {
            var ctx=device.ImmediateContext;ctx.CopyResource(texture,copy);
            var map=ctx.MapSubresource(copy,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
            try {
                for(int sample=0;sample<locations.Count;sample++) {
                    var point=locations[sample];
                    int offset=point.Y*map.RowPitch+point.X*(format==Format.R16G16B16A16_Float?8:4);
                    for(int c=0;c<3;c++) {
                        if(format==Format.R16G16B16A16_Float)
                            data[sample,c]=FromHalf((ushort)Marshal.ReadInt16(map.DataPointer,offset+c*2));
                        else if(format==Format.R10G10B10A2_UNorm)
                            data[sample,c]=((Marshal.ReadInt32(map.DataPointer,offset)>>(10*c))&1023)/1023.0;
                        else data[sample,c]=Marshal.ReadByte(map.DataPointer,offset+(format==Format.B8G8R8A8_UNorm?2-c:c))/255.0;
                        Check(!double.IsNaN(data[sample,c]),"NIS produced a non-finite pixel.");
                    }
                }
            } finally {ctx.UnmapSubresource(copy,0);}
        }
        return data;
    }

    static void CheckNisPatches(double[,] output,double[,] original,bool hdr) {
        for(int s=0;s<6;s++) for(int c=0;c<3;c++) {
            double value=output[s,c];
            Check(Math.Abs(value-original[s,c])<=(hdr?.003:.009),"NIS changed a flat patch at "+s+", channel "+c+" ("+value+" vs "+original[s,c]+").");
            if(s>=4) continue;
            if(hdr) {
                double expected=new[]{0,80,200,1000}[s],actual=Nits(value);
                Check(Math.Abs(expected-actual)<Math.Max(.06,expected*.035),"NIS HDR patch measured "+actual+" nits; expected "+expected+".");
            } else {
                double expected=new[]{0,128,224,255}[s]/255.0;
                Check(Math.Abs(value-expected)<=2.0/255,"NIS SDR patch differs from the incoming reference.");
            }
        }
    }

    static int ChangedNisSamples(double[,] a,double[,] b,double threshold) {
        int changed=0;
        for(int s=6;s<a.GetLength(0);s++)
            if(Math.Max(Math.Abs(a[s,0]-b[s,0]),Math.Max(Math.Abs(a[s,1]-b[s,1]),Math.Abs(a[s,2]-b[s,2])))>threshold) changed++;
        return changed;
    }
    static void CheckNormalizedNis(double[,] samples) {
        foreach(double value in samples) Check(value>=0 && value<=1,"NIS produced an out-of-range normalized color channel: "+value);
    }

    static void EvaluateNisSize(Type type,object renderer,Device device,Texture2D input,string backend,bool hdr,Size target,
                                Dictionary<string,double[,]> references) {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        type.GetMethod("Resize").Invoke(renderer,new object[]{target});
        var src=new Size(InputW,InputH);var size=target.Width>InputW?target:src;
        var points=NisSampleLocations(size);
        string key=size.ToString();
        foreach(string mode in new[]{"original","sharp","nis"}) {
            type.GetMethod("SetPictureMode").Invoke(renderer,new object[]{mode});
            UploadQualityFrame(device,input,4,hdr);
            type.GetMethod("Receive").Invoke(renderer,new object[]{input,new Rectangle(Point.Empty,src),false});
            int before=(int)type.GetProperty("NisEvaluations").GetValue(renderer,null);
            type.GetField("VerifyNextOutput").SetValue(renderer,true);
            int center=(int)type.GetMethod("Show").Invoke(renderer,new object[]{mode,1f,true,false});
            string effective=(string)type.GetProperty("EffectivePictureMode").GetValue(renderer,null);
            Check(effective==mode,"Effective mode "+effective+" differs from "+mode+": "+type.GetProperty("EffectNotice").GetValue(renderer,null));
            int passes=(int)type.GetProperty("NisEvaluations").GetValue(renderer,null)-before;
            var filter=(string)type.GetProperty("ActiveNisFilter").GetValue(renderer,null);
            Check((Size)type.GetProperty("InputSize").GetValue(renderer,null)==src &&
                (Size)type.GetProperty("RenderSize").GetValue(renderer,null)==(mode=="nis"?size:src),"Unexpected processing dimensions for "+mode);
            if(mode!="nis") {
                Check(passes==0 && string.IsNullOrEmpty(filter),"Inactive NIS reported an evaluation.");
                if(backend=="d3d12" && target==size)
                    references[key+mode]=ReadNisColors(device,(Texture2D)type.GetField("backBuffer",Private).GetValue(renderer),points);
                continue;
            }
            string expectedFilter=size==src?"NVSharpen":"NVScaler";
            Check(filter==expectedFilter && passes==(size.Width>2*InputW?2:1),"Unexpected SDK filter/dispatch count "+filter+" / "+passes);
            var context=type.GetField("nis",Private).GetValue(renderer);
            var view=(ShaderResourceView)context.GetType().GetProperty("Output").GetValue(context,null);
            using(var texture=view.ResourceAs<Texture2D>()) {
                Check(texture.Description.Width==size.Width && texture.Description.Height==size.Height,"NIS UAV dimensions differ from the processing size.");
                var gpu=ReadNisColors(device,texture,points);
                CheckNormalizedNis(gpu);
                if(passes>1) {
                    var chain=(System.Collections.IList)context.GetType().GetField("passes",Private).GetValue(context);
                    var intermediate=chain[0].GetType().GetField("Image").GetValue(chain[0]);
                    var image=(Texture2D)intermediate.GetType().GetField("Texture").GetValue(intermediate);
                    CheckNormalizedNis(ReadNisColors(device,image,NisSampleLocations(new Size(image.Description.Width,image.Description.Height))));
                }
                var original=references[key+"original"];var sharp=references[key+"sharp"];
                CheckNisPatches(gpu,original,hdr);
                int fromOriginal=ChangedNisSamples(gpu,original,hdr?.002:2.0/255);
                int fromSharpen=ChangedNisSamples(gpu,sharp,hdr?.002:2.0/255);
                Check(fromOriginal>points.Count*.01 && fromSharpen>points.Count*.01,"NIS did not change fixture detail independently of Original and Sharpen ("+fromOriginal+", "+fromSharpen+").");
                if(size==target) for(int c=0;c<3;c++)
                    Check(Math.Abs(Code(center,c,hdr)-gpu[6,c])<(hdr?.003:.009),
                        "Final pre-present GPU center pixel differs from NIS. "+backend+" "+size);
                // D3D11's flip-discard swap chain gives no defined back-buffer contents after Present.
                // Its pre-present diagnostic and retained compute UAV are both measured above/below.
                if(backend!="d3d11" && target==size) {
                    var final=ReadNisColors(device,(Texture2D)type.GetField("backBuffer",Private).GetValue(renderer),points);
                    CheckNisPatches(final,original,hdr);
                    double biggest=0;int mistakes=0;
                    for(int s=0;s<points.Count;s++) for(int c=0;c<3;c++) {
                        double difference=Math.Abs(gpu[s,c]-final[s,c]);
                        biggest=Math.Max(biggest,difference);
                        if(difference>(hdr?.003:2.0/255)) mistakes++;
                    }
                    Check(mistakes==0,"Presented picture differs from NIS output in "+mistakes+" channel samples (maximum error "+biggest+").");
                }
                Check((int)type.GetProperty("SampleMaxLuma").GetValue(renderer,null)>(int)type.GetProperty("SampleMinLuma").GetValue(renderer,null),
                    "Final pre-present samples contain no image detail.");
                if(target.Width==965) {
                    int evaluations=(int)type.GetProperty("NisEvaluations").GetValue(renderer,null);
                    var wipe=type.GetMethod("SetWipe");
                    wipe.Invoke(renderer,new object[]{true,.5f,false,0,1f});
                    type.GetMethod("Repaint").Invoke(renderer,new object[]{"nis"});
                    Check((int)type.GetProperty("NisEvaluations").GetValue(renderer,null)==evaluations,"Moving the comparison divider re-evaluated an unchanged NIS frame.");
                    if(backend!="d3d11") {
                        var split=ReadNisColors(device,(Texture2D)type.GetField("backBuffer",Private).GetValue(renderer),points);
                        for(int s=0;s<points.Count;s++) {
                            if(Math.Abs(points[s].X-size.Width/2)<60) continue;
                            for(int c=0;c<3;c++) {
                                double expected=points[s].X<size.Width/2?original[s,c]:gpu[s,c];
                                Check(Math.Abs(expected-split[s,c])<(hdr?.003:.009),"The NIS comparison wipe displayed the wrong source.");
                            }
                        }
                    }
                    wipe.Invoke(renderer,new object[]{false,.5f,false,0,1f});
                    UploadQualityFrame(device,input,13,hdr);
                    type.GetMethod("Receive").Invoke(renderer,new object[]{input,new Rectangle(Point.Empty,src),false});
                    type.GetMethod("Show").Invoke(renderer,new object[]{"nis",1f,false,false});
                    Check((int)type.GetProperty("NisEvaluations").GetValue(renderer,null)==evaluations+1,"A new source frame failed to dispatch NIS.");
                    Check(ChangedNisSamples(gpu,ReadNisColors(device,texture,points),.015)>points.Count*.01,"New source detail was missing from the retained NIS image.");
                }
                Console.WriteLine("{0} {1} {2}, 640x360 to {3}x{4} on {5}x{6}, {7} dispatch(es), {8}/{9} samples different from Original, {10}/{9} from Sharpen.",
                    backend,hdr?"HDR10":"SDR",filter,size.Width,size.Height,target.Width,target.Height,passes,fromOriginal,points.Count-6,fromSharpen);
            }
        }
    }

    public static int CheckNisQuality(string[] args) {
        if(args.Length==4) return RunOnDesktop(args,"P5CastNis"+Guid.NewGuid().ToString("N"),120000);
        if(args.Length!=5 || args[4]!="--inactive-desktop") return 2;
        try {
            var asm=Assembly.LoadFrom(args[0]);var type=asm.GetType("Renderer",true);
            Check(type.GetProperty("NisEvaluations")!=null && type.GetProperty("ActiveNisFilter")!=null,
                "The selected player build does not contain NVIDIA Image Scaling.");
            var monitors=(Array)asm.GetType("DisplayTarget",true).GetMethod("Enumerate").Invoke(null,null);
            var hdrScreens=monitors.Cast<object>().Where(s=>(bool)s.GetType().GetProperty("AdvancedColorEnabled").GetValue(s,null))
                .Select(s=>(string)s.GetType().GetProperty("DeviceName").GetValue(s,null)).ToArray();
            string display=args[1]=="auto" ? hdrScreens.FirstOrDefault() ?? Forms.Screen.PrimaryScreen.DeviceName : args[1];
            var screen=Forms.Screen.AllScreens.First(s=>s.DeviceName==display);
            bool hdrAvailable=hdrScreens.Contains(display,StringComparer.OrdinalIgnoreCase);
            if(!hdrAvailable) Console.WriteLine("SDR only. Windows HDR is off on "+display+".");
            using(var host=new VideoHost()) {
                var window=new Window{WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Content=host,
                    Left=screen.Bounds.Left+12,Top=screen.Bounds.Top+12,Width=OutputW,Height=OutputH};
                window.Show();Pump();
                foreach(bool hdr in hdrAvailable?new[]{false,true}:new[]{false}) {
                    var baselines=new Dictionary<string,double[,]>();
                    foreach(string backend in new[]{"d3d12","d3d11","vulkan"}) {
                        using(var renderer=(IDisposable)Activator.CreateInstance(type,new object[]{host.Handle,new Size(InputW,InputH),Size.Empty,
                            hdr?"hdr10":"off","original",true,backend})) {
                            Check((bool)type.GetProperty("PresenterHdr10").GetValue(renderer,null)==hdr,"Wrong presentation color mode.");
                            var device=(Device)type.GetProperty("Device").GetValue(renderer,null);
                            using(var input=new Texture2D(device,new Texture2DDescription{Width=InputW,Height=InputH,
                                Format=hdr?Format.R16G16B16A16_Float:Format.B8G8R8A8_UNorm,ArraySize=1,MipLevels=1,
                                SampleDescription=new SampleDescription(1,0),BindFlags=BindFlags.ShaderResource})) {
                                foreach(var target in new[]{new Size(InputW,InputH),new Size(965,543),new Size(1920,1080),new Size(320,180)})
                                    EvaluateNisSize(type,renderer,device,input,backend,hdr,target,baselines);
                            }
                        }
                    }
                }
                window.Close();
            }
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
