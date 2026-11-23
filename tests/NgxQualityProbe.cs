using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;
using Forms=System.Windows.Forms;
using Size=System.Drawing.Size;
using Rectangle=System.Drawing.Rectangle;

internal static class NgxQualityRunner {
    [STAThread] public static int Main(string[] args) {return EmbeddedProbe.RunQuality(args);}
}
internal static partial class EmbeddedProbe {
    const int InputW=640,InputH=360,OutputW=960,OutputH=540;
    static readonly string[] HudLetters={"1000110001111111000110001","1000110001100011000101110","1111010001100011000111110"};
    // Generated input is scRGB in HDR, and neutral/primary sRGB code values in SDR.
    // The real production renderer and native NGX produce the images being measured.
    static readonly byte[] MenuCoverage=MakeMenu();
    static byte[] MakeMenu() {
        var mask=new byte[300*74];
        using(var raster=new Bitmap(300,74))
        using(var canvas=Graphics.FromImage(raster))
        using(var title=new Font("Segoe UI",21,System.Drawing.FontStyle.Regular,GraphicsUnit.Pixel))
        using(var body=new Font("Segoe UI",14,System.Drawing.FontStyle.Regular,GraphicsUnit.Pixel)) {
            canvas.Clear(Color.Black); canvas.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            canvas.DrawString("Resume activity",title,Brushes.White,2,0);
            canvas.DrawString("Trophies   83 / 100",body,Brushes.White,2,36);
            for(int y=0;y<74;y++) for(int x=0;x<300;x++) mask[y*300+x]=raster.GetPixel(x,y).R;
        }
        return mask;
    }
    static int Texel(int x,int y,int frame) {
        if(y<72) return Math.Min(4,x/128);
        if(y>=250 && y<324 && x<300) return 6+MenuCoverage[(y-250)*300+x];
        if(y>=250 && y<324 && x>=320) {
            int localX=(x-320+frame*5)%300, localY=y-250;
            if(localY>56 && localX>=42 && localX<210) return 3; // moving solid highlight/bar
            return 6+MenuCoverage[localY*300+localX]; // scrolling text changes position every frame
        }
        bool ink=false;
        if(x<320 && y>=112 && y<240) {
            int cell=x%36, row=(y-112)%24;
            ink=cell<5 && row<5 && HudLetters[(x/36)%3][row*5+cell]=='1';
            ink|=cell>=12 && cell<24 && row==cell-12;
        }
        if(ink) return 5;
        return x>=320 && (((x+frame*3)/4+y/8)%2==0) ? 1 : 0;
    }
    static ushort PositiveHalf(double value) {
        if(value<1.0/16384) return (ushort)Math.Round(value*16777216);
        int exponent=(int)Math.Floor(Math.Log(value,2));
        return (ushort)(((exponent+15)<<10)+(int)Math.Round((value/Math.Pow(2,exponent)-1)*1024));
    }
    static void UploadQualityFrame(Device device,Texture2D texture,int frame,bool hdr) {
        int width=texture.Description.Width,height=texture.Description.Height,count=width*height;
        if(hdr) {
            // Exactly representable positive half values; scRGB 1.0 means 80 cd/m².
            var neutral=new ushort[262];
            new ushort[]{0,0x3c00,0x4100,0x4a40,0x4000,0x4100}.CopyTo(neutral,0);
            for(int i=0;i<256;i++) neutral[i+6]=PositiveHalf(2.5*Math.Pow(i/255.0,2.2)); // 0, 80, 200, 1000 nits, colored patch, text.
            var pixels=new ushort[count*4];
            for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
                int i=4*(width*y+x),p=Texel(x%InputW,y%InputH,frame);
                pixels[i]=neutral[p]; pixels[i+1]=p==4?(ushort)0x3800:neutral[p];
                pixels[i+2]=p==4?(ushort)0x3400:neutral[p]; pixels[i+3]=0x3c00;
            }
            device.ImmediateContext.UpdateSubresource(pixels,texture,0,width*8,0);
        } else {
            var palette=new uint[262];
            new uint[]{0xff000000,0xff808080,0xffe0e0e0,0xffffffff,0xffe08040,0xffffffff}.CopyTo(palette,0);
            for(uint i=0;i<256;i++) palette[i+6]=0xff000000|i*0x010101;
            var pixels=new uint[count];
            for(int y=0;y<height;y++) for(int x=0;x<width;x++) pixels[y*width+x]=palette[Texel(x%InputW,y%InputH,frame)];
            device.ImmediateContext.UpdateSubresource(pixels,texture,0,width*4,0);
        }
    }
    static int[] ReadPresentedTexture(Device device,Texture2D source) {
        var desc=source.Description;
        desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read; desc.OptionFlags=ResourceOptionFlags.None;
        using(var copy=new Texture2D(device,desc)){
            var ctx=device.ImmediateContext; ctx.CopyResource(source,copy);
            var map=ctx.MapSubresource(copy,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
            try {
                var pixels=new int[desc.Width*desc.Height];
                for(int y=0;y<desc.Height;y++) Marshal.Copy(IntPtr.Add(map.DataPointer,y*map.RowPitch),pixels,y*desc.Width,desc.Width);
                return pixels;
            } finally {ctx.UnmapSubresource(copy,0);}
        }
    }
    static double Code(int pixel,int channel,bool hdr) {
        int shift=hdr ? channel*10 : (2-channel)*8;
        return hdr ? ((pixel>>shift)&1023)/1023.0 : ((pixel>>shift)&255)/255.0;
    }
    static double Nits(double pq) {
        double p=Math.Pow(pq,32.0/2523);
        return 10000*Math.Pow(Math.Max(0,p-3424.0/4096)/(2413.0/128-2392.0/128*p),16384.0/2610);
    }
    static double Quantile(List<double> values,double q) {
        values.Sort(); return values[(int)Math.Round((values.Count-1)*q)];
    }
    static int DisplayCode(double code,bool hdr) {
        double linear=hdr?Nits(code)/200:0;
        double display=hdr?(linear<=.0031308 ? 12.92*linear:1.055*Math.Pow(linear,1/2.4)-.055):code;
        return (int)Math.Round(255*Math.Max(0,Math.Min(1,display)));
    }
    static void SaveTextCrop(string directory,int[] image,bool hdr,string mode) {
        Directory.CreateDirectory(directory);
        using(var crop=new Bitmap(269,100)) {
            for(int y=0;y<crop.Height;y++) for(int x=0;x<crop.Width;x++) {
                int pixel=image[(375+y)*OutputW+x];
                crop.SetPixel(x,y,Color.FromArgb(DisplayCode(Code(pixel,0,hdr),hdr),DisplayCode(Code(pixel,1,hdr),hdr),DisplayCode(Code(pixel,2,hdr),hdr)));
            }
            crop.Save(Path.Combine(directory,(hdr?"hdr-":"sdr-")+mode+".png"),ImageFormat.Png);
        }
    }
    static bool MeasureQuality(int[] baseline,int[] image,int[] prior,bool hdr,string mode) {
        bool valid=true;
        for(int patch=0;patch<5;patch++) {
            double maximum=0; string level="";
            for(int ch=0;ch<3;ch++) {
                var samples=new List<double>(); var refs=new List<double>();
                for(int y=24;y<84;y+=4) for(int x=patch*192+24;x<patch*192+168;x+=4) {
                    samples.Add(Code(image[y*OutputW+x],ch,hdr)); refs.Add(Code(baseline[y*OutputW+x],ch,hdr));
                }
                double a=Quantile(refs,.5),b=Quantile(samples,.5);
                if(hdr) {a=Nits(a);b=Nits(b);}
                if(ch==0 && patch<4) {
                    double expected=hdr?new[]{0,80,200,1000}[patch]:new[]{0,128,224,255}[patch]/255.0;
                    Check(Math.Abs(expected-a)<=Math.Max(hdr?.01:.002,expected*.01),"Unexpected original patch level.");
                }
                double delta=Math.Abs(a-b); maximum=Math.Max(maximum,delta/Math.Max(hdr?10:0.05,a));
                if(ch==0 || patch==4) level+=string.Format(CultureInfo.InvariantCulture," {0:F3}->{1:F3}",a,b);
            }
            Console.WriteLine("{0} {1} patch {2}{3} ({4})",hdr?"HDR":"SDR",mode,patch,level,hdr?"nits":"normalized code");
            valid&=maximum<=.05;
        }
        var strokeDelta=new List<double>(); double flicker=0;
        var cores=new List<double>(); var referenceCores=new List<double>(); var darkBackground=new List<double>();
        for(int y=173;y<350;y++) for(int x=3;x<469;x++){
            int i=y*OutputW+x; double reference=Code(baseline[i],0,hdr);
            double enhanced=Code(image[i],0,hdr);
            if(reference>(hdr?.53:.75)) {
                cores.Add(hdr?Nits(enhanced):255*enhanced);
                referenceCores.Add(hdr?Nits(reference):255*reference);
            }
            if(reference<=.001) darkBackground.Add(enhanced);
            if(reference<.12) continue;
            strokeDelta.Add(Math.Abs(reference-enhanced)); flicker+=Math.Abs(enhanced-Code(prior[i],0,hdr));
        }
        Check(strokeDelta.Count>1000,"Text/reference region missing.");
        Console.WriteLine("{0} {1} stroke MAE {2:F4}, p95 {3:F4}, inter-frame MAE {4:F4} (normalized {5} code, {6} samples)",
            hdr?"HDR":"SDR",mode,strokeDelta.Average(),Quantile(strokeDelta,.95),flicker/strokeDelta.Count,hdr?"PQ":"SDR",strokeDelta.Count);
        Check(cores.Count>100 && darkBackground.Count>1000,"Missing text core or surrounding black reference.");
        Console.WriteLine("{0} {1} bright stroke median {2:F2}->{3:F2} ({4}); black-region p95 {5:F4}",
             hdr?"HDR":"SDR",mode,Quantile(referenceCores,.5),Quantile(cores,.5),hdr?"nits":"0-255",Quantile(darkBackground,.95));
        var menuDelta=new List<double>(); var menuCore=new List<double>(); var refMenuCore=new List<double>();
        for(int y=375;y<475;y++) for(int x=0;x<290;x++) {
            double reference=Code(baseline[y*OutputW+x],0,hdr),value=Code(image[y*OutputW+x],0,hdr);
            if(reference>.12) menuDelta.Add(Math.Abs(reference-value));
            if(reference>(hdr?.53:.75)) {refMenuCore.Add(hdr?Nits(reference):255*reference);menuCore.Add(hdr?Nits(value):255*value);}
        }
        Check(menuDelta.Count>1200 && menuCore.Count>150,"Antialiased test menu is missing.");
        double referenceMedian=Quantile(refMenuCore,.5),outputMedian=Quantile(menuCore,.5);
        Console.WriteLine("{0} {1} menu text MAE {2:F4}; bright median {3:F2}->{4:F2}",hdr?"HDR":"SDR",mode,
            menuDelta.Average(),referenceMedian,outputMedian);
        // Compare identical output coordinates across the whole glyph, including its antialiased edges.
        valid&=menuDelta.Average()<=(hdr?.01:.03) && strokeDelta.Average()<=(hdr?.007:.03);
        valid&=Math.Abs(outputMedian/referenceMedian-1)<.065;
        valid&=MeasureMovingWhites(baseline,image,OutputW,new Rectangle(486,375,454,109),hdr,mode);
        int changed=0,sceneSamples=0;
        for(int y=174;y<350;y+=2) for(int x=490;x<940;x+=2) {
            int i=y*OutputW+x; sceneSamples++;
            if(Math.Abs(Code(image[i],0,hdr)-Code(baseline[i],0,hdr))>.015) changed++;
        }
        Console.WriteLine("{0} {1} moving scene samples changed {2}/{3}",hdr?"HDR":"SDR",mode,changed,sceneSamples);
        Check(changed>sceneSamples/20,"NGX must still change moving, textured content.");
        return valid;
    }
    static bool MeasureMovingWhites(int[] baseline,int[] image,int width,Rectangle region,bool hdr,string mode) {
        var whiteLoss=new List<double>();
        for(int y=region.Top;y<region.Bottom;y++) for(int x=region.Left;x<region.Right;x++) {
            int i=y*width+x;
            double reference=Code(baseline[i],0,hdr),output=Code(image[i],0,hdr);
            if(hdr) {reference=Nits(reference); output=Nits(output);}
            if(reference<(hdr?120:.82)) continue;
            whiteLoss.Add(Math.Max(0,1-output/reference));
        }
        Check(whiteLoss.Count>500,"Moving white HUD fixture is missing.");
        double peakLoss=whiteLoss.Max(), p99Loss=Quantile(whiteLoss,.99);
        Console.WriteLine("{0} {1} scrolling HUD white loss vs Original: p99 {2:P2}, maximum {3:P2} ({4} pixels)",
            hdr?"HDR":"SDR",mode,p99Loss,peakLoss,whiteLoss.Count);
        return p99Loss<.016 && peakLoss<.035;
    }
    static void CheckDlssPresets(Type type,object renderer,Device device,Texture2D input,FieldInfo backBuffer,bool hdr) {
        var source=new Size(input.Description.Width,input.Description.Height);
        var fullscreen=new Size(2560,1440);
        var active=type.GetProperty("ActiveDlssQuality");
        var choose=type.GetMethod("SetDlssQuality");
        var resize=type.GetMethod("Resize");
        var evaluations=type.GetProperty("VendorEvaluations");
        int frame=0;
        Action<string,Size> checkFrame=(expected,size)=>{
            int before=(int)evaluations.GetValue(renderer,null);
            UploadQualityFrame(device,input,frame++,hdr);
            type.GetMethod("Receive").Invoke(renderer,new object[]{input,new Rectangle(System.Drawing.Point.Empty,source),false});
            type.GetMethod("Show").Invoke(renderer,new object[]{"dlss",1f,false,false});
            Check((int)evaluations.GetValue(renderer,null)>before,"No real NGX evaluation for "+expected+".");
            Check((string)active.GetValue(renderer,null)==expected,"Wrong active DLSS quality; expected "+expected+".");
            Check((Size)type.GetProperty("InputSize").GetValue(renderer,null)==source &&
                  (Size)type.GetProperty("RenderSize").GetValue(renderer,null)==size,"Choosing "+expected+" changed the stream or processing resolution.");
            var target=(Texture2D)backBuffer.GetValue(renderer);
            Check(target.Description.Width==size.Width && target.Description.Height==size.Height,"Unexpected presentation size.");
            var pixels=ReadPresentedTexture(device,target);
            int x=320*size.Width/source.Width,y=36*size.Height/source.Height;
            double code=Code(pixels[y*size.Width+x],0,hdr);
            Check(hdr ? Math.Abs(Nits(code)-200)<5 : Math.Abs(code-224.0/255)<.02,"Missing reference patch for "+expected+".");
        };
        foreach(string preset in new[]{"performance","quality","balanced","ultra-performance","auto"}) {
            choose.Invoke(renderer,new object[]{preset});
            checkFrame(preset=="auto" ? "quality" : preset,fullscreen);
        }
        var modelHint=type.GetProperty("DlssModelHint");
        foreach(string hint in new[]{"k","m","l","auto"}) {
            type.GetMethod("SetDlssModel").Invoke(renderer,new object[]{hint});
            Check((string)modelHint.GetValue(renderer,null)==hint,"Model preference was lost.");
            checkFrame("quality",fullscreen);
        }
        choose.Invoke(renderer,new object[]{"balanced"});
        resize.Invoke(renderer,new object[]{source});
        checkFrame("dlaa",source);
        resize.Invoke(renderer,new object[]{fullscreen});
        checkFrame("balanced",fullscreen);
        type.GetMethod("SetPictureMode").Invoke(renderer,new object[]{"original"});
        Check(string.IsNullOrEmpty((string)active.GetValue(renderer,null)),"Original still reports an active DLSS preset.");
        Console.WriteLine("{0} 1080p to 1440p: all four manual NGX modes, Auto, native DLAA, and returning to Balanced passed.",
                          hdr ? "HDR" : "SDR");
    }
    static bool CheckFullHdFit(Type type,object renderer,Device device,FieldInfo backBuffer,bool hdr) {
        var fullHd=new Size(1920,1080);
        bool pass=true;
        using(var input=new Texture2D(device,new Texture2DDescription{Width=fullHd.Width,Height=fullHd.Height,
            Format=hdr?Format.R16G16B16A16_Float:Format.B8G8R8A8_UNorm,ArraySize=1,MipLevels=1,
            SampleDescription=new SampleDescription(1,0),BindFlags=BindFlags.ShaderResource})) {
            foreach(var fit in new[]{new Size(1595,897),new Size(2560,1440)}) {
                type.GetMethod("Resize").Invoke(renderer,new object[]{fit});
                type.GetMethod("SetPictureMode").Invoke(renderer,new object[]{"original"});
                int[] baseline=null;
                foreach(string mode in new[]{"original","fsr-temporal","dlss"}) {
                    type.GetMethod("SetPictureMode").Invoke(renderer,new object[]{mode});
                    for(int frame=0;frame<5;frame++) {
                        UploadQualityFrame(device,input,frame,hdr);
                        type.GetMethod("Receive").Invoke(renderer,new object[]{input,new Rectangle(System.Drawing.Point.Empty,fullHd),false});
                        type.GetMethod("SetFrameTiming").Invoke(renderer,new object[]{16.67,frame>0});
                        type.GetMethod("Show").Invoke(renderer,new object[]{mode,1f,false,false});
                    }
                    var pixels=ReadPresentedTexture(device,(Texture2D)backBuffer.GetValue(renderer));
                    if(mode=="original") {baseline=pixels;continue;}
                    bool enlarged=fit.Width>fullHd.Width;
                    string effect=mode=="fsr-temporal" ? mode : enlarged?"dlss":"dlaa";
                    if(mode=="fsr-temporal") Check((int)type.GetProperty("FsrEvaluations").GetValue(renderer,null)>0 &&
                        !string.IsNullOrEmpty((string)type.GetProperty("ActiveFsrVersion").GetValue(renderer,null)),"AMD did not evaluate the input.");
                    Check((Size)type.GetProperty("RenderSize").GetValue(renderer,null)==(enlarged?fit:fullHd) &&
                          (string)type.GetProperty("EffectivePictureMode").GetValue(renderer,null)==effect,
                          "Full HD fit did not use the expected temporal reconstruction.");
                    double scale=(double)fit.Height/fullHd.Height;
                    var crop=new Rectangle((int)(324*scale),(int)(250*scale),(int)(290*scale),(int)(73*scale));
                    pass&=MeasureMovingWhites(baseline,pixels,fit.Width,crop,hdr,effect+" at "+fit.Width+"x"+fit.Height);
                }
            }
            CheckDlssPresets(type,renderer,device,input,backBuffer,hdr);
        }
        return pass;
    }
    public static int RunQuality(string[] args) {
        if(args.Length==4) return RunOnDesktop(args,"P5CastNgx"+Guid.NewGuid().ToString("N"));
        if(args.Length!=5 || args[4]!="--inactive-desktop") return 2;
        try {
            var assembly=Assembly.LoadFrom(args[0]); var type=assembly.GetType("Renderer",true);
            var displays=(Array)assembly.GetType("DisplayTarget",true).GetMethod("Enumerate").Invoke(null,null);
            var advanced=displays.Cast<object>().Where(d=>(bool)d.GetType().GetProperty("AdvancedColorEnabled").GetValue(d,null))
                .Select(d=>(string)d.GetType().GetProperty("DeviceName").GetValue(d,null)).ToArray();
            var name=args[1]=="auto" ? advanced.FirstOrDefault() ?? Forms.Screen.PrimaryScreen.DeviceName : args[1];
            var screen=Forms.Screen.AllScreens.First(s=>s.DeviceName==name);
            bool hdrAvailable=advanced.Contains(name,StringComparer.OrdinalIgnoreCase);
            if(!hdrAvailable) Console.WriteLine("SDR only. Windows HDR is off on "+name+".");
            bool pass=true;
            var capsType=assembly.GetType("GpuCapabilities",true);
            var caps=capsType.GetMethod("ProbeForBackend").Invoke(null,new object[]{"d3d12"});
            bool videoSr=(bool)capsType.GetMethod("Supports").Invoke(caps,new object[]{"rtxvideo"});
            using(var host=new VideoHost()) {
                var window=new Window{WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Left=screen.Bounds.Left+12,Top=screen.Bounds.Top+12,
                    Width=OutputW,Height=OutputH,ShowInTaskbar=false,Content=host};
                window.Show(); Pump();
                foreach(bool hdr in hdrAvailable?new[]{false,true}:new[]{false})
                using(var renderer=(IDisposable)Activator.CreateInstance(type,new object[]{host.Handle,new Size(OutputW,OutputH),Size.Empty,hdr?"hdr10":"off","original",true,"d3d12"})) {
                    Check((bool)type.GetProperty("PresenterHdr10").GetValue(renderer,null)==hdr,"Unexpected presentation format.");
                    var device=(Device)type.GetProperty("Device").GetValue(renderer,null);
                    // The portable presenter reads this final D3D11 texture without discarding it.
                    var backBuffer=type.GetField("backBuffer",BindingFlags.Instance|BindingFlags.NonPublic);
                    using(var input=new Texture2D(device,new Texture2DDescription{Width=InputW,Height=InputH,Format=hdr?Format.R16G16B16A16_Float:Format.B8G8R8A8_UNorm,
                        ArraySize=1,MipLevels=1,SampleDescription=new SampleDescription(1,0),BindFlags=BindFlags.ShaderResource})) {
                        int[] baseline=null;
                        foreach(string mode in new[]{"original","dlss","dlaa"}) {
                            var target=(Texture2D)backBuffer.GetValue(renderer);
                            int evaluations=(int)type.GetProperty("VendorEvaluations").GetValue(renderer,null);
                            type.GetMethod("SetPictureMode").Invoke(renderer,new object[]{mode});
                            int[] prior=null;
                            for(int f=0;f<9;f++) {
                                UploadQualityFrame(device,input,f,hdr);
                                type.GetMethod("Receive").Invoke(renderer,new object[]{input,new Rectangle(0,0,InputW,InputH),false});
                                type.GetMethod("SetFrameTiming").Invoke(renderer,new object[]{16.67,f>0});
                                type.GetMethod("Show").Invoke(renderer,new object[]{mode,1f,false,false});
                                if(f==7) prior=ReadPresentedTexture(device,target);
                            }
                            var pixels=ReadPresentedTexture(device,target);
                            var expectedSize=mode=="dlss" ? new Size(OutputW,OutputH) : new Size(InputW,InputH);
                            Check((Size)type.GetProperty("RenderSize").GetValue(renderer,null)==expectedSize,
                                  mode+" processed video at the wrong resolution.");
                            Check((string)type.GetProperty("EffectivePictureMode").GetValue(renderer,null)==mode,"NGX fell back from "+mode+": "+type.GetProperty("EffectNotice").GetValue(renderer,null));
                            if(mode!="original") Check((int)type.GetProperty("VendorEvaluations").GetValue(renderer,null)-evaluations>=9,"Insufficient NVIDIA evaluations.");
                            if(args[3]!="-") SaveTextCrop(args[3],pixels,hdr,mode);
                            if(mode=="original") baseline=pixels;
                            else {
                                pass&=MeasureQuality(baseline,pixels,prior,hdr,mode);
                                // Inspect the actual enhanced output in its swap-chain format (including 10-bit PQ).
                                const int cx=612,cy=248;
                                var setLoupe=type.GetMethod("SetLoupe");
                                setLoupe.Invoke(renderer,new object[]{true,(cx+.5f)/OutputW,(cy+.5f)/OutputH,5,1f});
                                type.GetMethod("Repaint").Invoke(renderer,new object[]{mode});
                                var enlarged=ReadPresentedTexture(device,target);
                                int changed=0;
                                for(int dy=-9;dy<=5;dy++) for(int dx=-9;dx<=9;dx++) {
                                    int at=(cy+dy*5)*OutputW+cx+dx*5;
                                    Check(enlarged[at]==pixels[(cy+dy)*OutputW+cx+dx],"The loupe changed an enhanced pixel's channel values.");
                                    if(enlarged[at]!=pixels[at]) changed++;
                                }
                                Check(changed>10,"The quality fixture did not exercise magnification.");
                                setLoupe.Invoke(renderer,new object[]{false,0f,0f,0,1f});
                                Console.WriteLine("{0} {1} loupe preserved exact output pixel values at 5x.",hdr?"HDR":"SDR",mode);
                                // A display resize changes the DLSS target while DLAA keeps its native input size.
                                foreach(var size in new[]{new Size(OutputW+160,OutputH+90),new Size(InputW*2,InputH*2),
                                                         new Size(InputW*3,InputH*3),new Size(InputW/2,InputH/2),new Size(OutputW,OutputH)}) {
                                    var watch=System.Diagnostics.Stopwatch.StartNew();
                                    type.GetMethod("Resize").Invoke(renderer,new object[]{size});
                                    UploadQualityFrame(device,input,9,hdr);
                                    type.GetMethod("Receive").Invoke(renderer,new object[]{input,new Rectangle(0,0,InputW,InputH),false});
                                    type.GetMethod("Show").Invoke(renderer,new object[]{mode,1f,false,false});
                                    var output=(Texture2D)backBuffer.GetValue(renderer);
                                    bool enlarging=mode=="dlss" && size.Width>InputW;
                                    Check(output.Description.Width==size.Width && output.Description.Height==size.Height &&
                                          (Size)type.GetProperty("RenderSize").GetValue(renderer,null)==(enlarging ? size : new Size(InputW,InputH)) &&
                                          (string)type.GetProperty("EffectivePictureMode").GetValue(renderer,null)==(enlarging ? "dlss" : "dlaa") &&
                                          (bool)type.GetProperty("PresenterHdr10").GetValue(renderer,null)==hdr,
                                          "Resizing lost the selected NVIDIA effect or display format.");
                                    Console.WriteLine("{0} {1} resize to {2}x{3} and frame submission: {4} ms",
                                        hdr?"HDR":"SDR",mode,size.Width,size.Height,watch.ElapsedMilliseconds);
                                    if(enlarging && size.Width>=2*InputW) {
                                        var resized=ReadPresentedTexture(device,output);
                                        for(int p=0;p<5;p++) for(int ch=0;ch<3;ch++) {
                                            int x=(2*p+1)*size.Width/10,y=size.Height/10;
                                            int reference=baseline[(OutputH/10)*OutputW+(2*p+1)*OutputW/10];
                                            Check(Math.Abs(Code(reference,ch,hdr)-Code(resized[y*size.Width+x],ch,hdr))<=.02,
                                                  "DLSS changed a flat color at "+size.Width+"x"+size.Height+".");
                                        }
                                    }
                                }
                            }
                        }
                        if(!hdr && videoSr) {
                            type.GetMethod("SetPictureMode").Invoke(renderer,new object[]{"rtxvideo"});
                            type.GetMethod("Show").Invoke(renderer,new object[]{"rtxvideo",1f,false,false});
                            Check((string)type.GetProperty("EffectivePictureMode").GetValue(renderer,null)=="rtxvideo" &&
                                  (Size)type.GetProperty("RenderSize").GetValue(renderer,null)==new Size(OutputW,OutputH),
                                  "RTX Video SR did not produce the enlarged video resolution.");
                        }
                    }
                    pass&=CheckFullHdFit(type,renderer,device,backBuffer,hdr);
                }
                window.Close();
            }
            if(!pass && args[2]!="measure") throw new Exception("NGX changed flat colors or damaged stationary text beyond the fixture tolerance.");
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e.GetBaseException()); return 1;}
    }
}
