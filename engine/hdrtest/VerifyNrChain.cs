using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;

partial class VerifyHdr
{
    static void CheckNrChain(Assembly compiled,string backend,Screen screen,bool hdr)
    {
        var inputSize=new Size(640,360);
        var target=new Size(1280,720);
        const int sceneFrames=12;
        const int count=sceneFrames+4;
        string upscaler=backend=="vulkan" ? "nis" : "rtxvideo";
        string combined="open-nr-"+upscaler;
        var modes=new List<string>{"open-nr",upscaler,combined,"open-nr-sharp","open-nr-fsr"};
        if(backend!="vulkan") modes.AddRange(new[]{"open-nr-nis","open-nr-dlss","open-nr-dlaa"});
        if(backend!="d3d11") modes.Add("open-nr-fsr-temporal");
        modes.Add(upscaler); // Turning NR off keeps the same output surface and upscaler.
        var samples=new Dictionary<string,int[]>();
        var type=compiled.GetType("Renderer",true);
        using(var window=new Form { ClientSize=target,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=screen.WorkingArea.Location })
        using(var renderer=(IDisposable)Activator.CreateInstance(type,new object[]{window.Handle,target,target,hdr?"hdr10":"off","original",false,backend})) {
            var device=(Device)type.GetProperty("Device").GetValue(renderer,null);
            if((bool)type.GetProperty("HdrEnabled").GetValue(renderer,null)!=hdr ||
                (hdr && !(bool)type.GetProperty("PresenterHdr10").GetValue(renderer,null)))
                throw new InvalidDataException("Requested color path unavailable on "+screen.DeviceName);
            var receive=type.GetMethod("Receive");
            var show=type.GetMethod("Show");
            var select=type.GetMethod("SetPictureMode");
            Func<string,int> counter=name=>(int)type.GetProperty(name).GetValue(renderer,null);
            var frames=new List<Image>();
            try {
                for(int n=0;n<count;n++) {
                    var image=new Image(device,inputSize.Width,inputSize.Height,hdr?Format.R16G16B16A16_Float:Format.B8G8R8A8_UNorm);
                    frames.Add(image);
                    UploadNrScene(device,image,inputSize,n,sceneFrames,hdr);
                }
                foreach(string mode in modes) {
                    select.Invoke(renderer,new object[]{mode});
                    int nrBefore=counter("OpenNrEvaluations");
                    int vendorBefore=counter("VendorEvaluations");
                    int fsrBefore=counter("FsrEvaluations");
                    int nisBefore=counter("NisEvaluations");
                    int[] pixels=new int[count];
                    for(int n=0;n<count;n++) {
                        receive.Invoke(renderer,new object[]{frames[n].texture,new Rectangle(Point.Empty,inputSize),false});
                        pixels[n]=(int)show.Invoke(renderer,new object[]{mode,1f,true,false});
                        if(mode==combined && n==sceneFrames-1) {
                            var wipe=type.GetMethod("SetWipe");
                            int original=(int)show.Invoke(renderer,new object[]{"original",1f,true,false});
                            wipe.Invoke(renderer,new object[]{true,.8f,false,0,1f});
                            int left=(int)show.Invoke(renderer,new object[]{mode,1f,true,false});
                            wipe.Invoke(renderer,new object[]{true,.2f,false,0,1f});
                            int right=(int)show.Invoke(renderer,new object[]{mode,1f,true,false});
                            wipe.Invoke(renderer,new object[]{false,.5f,false,0,1f});
                            if(left!=original || right!=pixels[n]) throw new InvalidDataException(combined+" changed the wrong side of the wipe.");
                        }
                    }
                    // Re-present the same captured frame. Neural passes must be cached.
                    show.Invoke(renderer,new object[]{mode,1f,false,false});
                    bool usesNr=mode.StartsWith("open-nr",StringComparison.Ordinal);
                    int nrCount=counter("OpenNrEvaluations")-nrBefore;
                    int vendorCount=counter("VendorEvaluations")-vendorBefore;
                    bool native=mode=="open-nr" || mode=="open-nr-sharp" || mode=="open-nr-dlaa";
                    if(!Equals(type.GetProperty("EffectivePictureMode").GetValue(renderer,null),mode) ||
                        !Equals(type.GetProperty("RenderSize").GetValue(renderer,null),native?inputSize:target) ||
                        nrCount!=(usesNr ? count : 0) ||
                        ((mode=="open-nr-rtxvideo" || mode=="rtxvideo" || mode=="open-nr-dlaa" || mode=="open-nr-dlss") && vendorCount!=count) ||
                        (mode=="open-nr-fsr-temporal" && counter("FsrEvaluations")-fsrBefore!=count) ||
                        (mode=="open-nr-nis" && counter("NisEvaluations")-nisBefore<count))
                        throw new InvalidDataException(mode+" active="+type.GetProperty("EffectivePictureMode").GetValue(renderer,null)+
                            " size="+type.GetProperty("RenderSize").GetValue(renderer,null)+" NR="+nrCount+" vendor="+vendorCount+" "+
                            type.GetProperty("EffectNotice").GetValue(renderer,null));
                    samples[mode]=pixels;
                }
                int mask=hdr?1023:255;
                int baseChanges=0,nrChanges=0;
                for(int n=0;n<sceneFrames;n++) {
                    if(Math.Abs((samples[combined][n]&mask)-(samples[upscaler][n]&mask))>=2) baseChanges++;
                    if(Math.Abs((samples[combined][n]&mask)-(samples["open-nr"][n]&mask))>=2) nrChanges++;
                }
                int worst=0;
                for(int n=0;n<4;n++) {
                    int shift=hdr ? 10 : 8;
                    int pixel=samples[combined][sceneFrames+n];
                    for(int channel=0;channel<3;channel++) {
                        // SDR NR is allowed to relight midtones. VSR should retain the
                        // reconstructed level. HDR patches are protected at the source.
                        int expected=hdr ? Expected(n,channel,1) : n==0 ? 0 : n==3 ? 255 :
                            (samples["open-nr"][sceneFrames+n]>>(shift*channel))&mask;
                        worst=Math.Max(worst,Math.Abs(((pixel>>(shift*channel))&mask)-expected));
                    }
                }
                Console.WriteLine("{0} {1}: {2} modes, {3}x{4} -> {5}x{6}; combined vs {12} {7}/{9}, vs NR {8}/{9} textured samples differ; flat patch error {10}/{11}.",
                    backend,hdr?"HDR10":"SDR",samples.Count,inputSize.Width,inputSize.Height,target.Width,target.Height,baseChanges,nrChanges,sceneFrames,worst,mask,upscaler);
                foreach(var label in new[]{upscaler,"open-nr",combined})
                    Console.WriteLine("{0} four gray levels: {1}, {2}, {3}, {4}",label,
                        samples[label][sceneFrames]&mask,samples[label][sceneFrames+1]&mask,samples[label][sceneFrames+2]&mask,samples[label][sceneFrames+3]&mask);
                if(baseChanges<2 || nrChanges<2 || worst>(hdr?4:8)) throw new InvalidDataException(combined+" output comparison or luminance preservation failed.");
            } finally { foreach(var image in frames) image.Dispose(); }
        }
    }

    // Unpaced renderer-only throughput. A readback drains the queue before and
    // after the timed frames, and all input uploads finish before the warmup.
    static void BenchmarkNrChain(Assembly compiled,string backend,Screen screen,bool hdr)
    {
        var source=new Size(1920,1080);
        var target=new Size(2560,1440);
        var type=compiled.GetType("Renderer",true);
        using(var window=new Form { ClientSize=target,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=screen.WorkingArea.Location })
        using(var renderer=(IDisposable)Activator.CreateInstance(type,new object[]{window.Handle,target,target,hdr?"hdr10":"off","original",false,backend})) {
            var device=(Device)type.GetProperty("Device").GetValue(renderer,null);
            var format=hdr ? Format.R16G16B16A16_Float:Format.B8G8R8A8_UNorm;
            using(var first=new Image(device,source.Width,source.Height,format))
            using(var second=new Image(device,source.Width,source.Height,format)) {
                UploadNrScene(device,first,source,0,2,hdr);
                UploadNrScene(device,second,source,1,2,hdr);
                var receive=type.GetMethod("Receive");
                var show=type.GetMethod("Show");
                foreach(string mode in new[]{"rtxvideo","open-nr","open-nr-rtxvideo"}) {
                    type.GetMethod("SetPictureMode").Invoke(renderer,new object[]{mode});
                    var timer=new System.Diagnostics.Stopwatch();
                    const int warmup=12,measured=90;
                    for(int n=0;n<warmup+measured;n++) {
                        if(n==warmup) timer.Start();
                        receive.Invoke(renderer,new object[]{(n%2==0 ? first : second).texture,new Rectangle(Point.Empty,source),false});
                        show.Invoke(renderer,new object[]{mode,1f,n==warmup-1 || n==warmup+measured-1,false});
                    }
                    timer.Stop();
                    if(!Equals(type.GetProperty("EffectivePictureMode").GetValue(renderer,null),mode))
                        throw new InvalidDataException("Benchmark fell back from "+mode);
                    Console.WriteLine("{0} {1} {2}: {3} synthetic 1080p frames presented to 1440p in {4:F3}s ({5:F1} fps, {6:F1} ms/frame).",
                        backend,hdr?"HDR10":"SDR",mode,measured,timer.Elapsed.TotalSeconds,measured/timer.Elapsed.TotalSeconds,timer.Elapsed.TotalMilliseconds/measured);
                }
            }
        }
    }

    static void UploadNrScene(Device device,Image frame,Size size,int n,int sceneFrames,bool hdr)
    {
        var bytes=hdr ? null : new int[size.Width*size.Height];
        var linear=hdr ? new ushort[size.Width*size.Height*4] : null;
        for(int y=0;y<size.Height;y++) for(int x=0;x<size.Width;x++) {
            int p=y*size.Width+x;
            int gray=(MovingCode(x+7*n,y+n,2*n)>>2)+((x+y+n)%3)*9;
            bool patch=n>=sceneFrames && Math.Abs(x-size.Width/2)<64 && Math.Abs(y-size.Height/2)<48;
            if(hdr) {
                float lum=patch ? Bars[n-sceneFrames,0] : 12.5f*gray*gray/(255*255);
                linear[4*p]=linear[4*p+1]=linear[4*p+2]=HalfBits(lum);
                linear[4*p+3]=0x3c00;
            } else {
                if(patch) gray=n==sceneFrames?0:n==sceneFrames+1?64:n==sceneFrames+2?160:255;
                bytes[p]=unchecked((int)0xff000000)|gray*0x10101;
            }
        }
        if(hdr) device.ImmediateContext.UpdateSubresource(linear,frame.texture,0,size.Width*8,0);
        else device.ImmediateContext.UpdateSubresource(bytes,frame.texture,0,size.Width*4,0);
    }
}
