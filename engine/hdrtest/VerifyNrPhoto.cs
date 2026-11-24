using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;

partial class VerifyHdr
{
    static double PreviewNits(int code)
    {
        double p=Math.Pow(code/1023.0,32.0/2523);
        return 10000*Math.Pow(Math.Max(0,p-3424.0/4096)/(2413.0/128-2392.0/128*p),16384.0/2610);
    }

    static int[] PreviewReference(int pixel,double white)
    {
        double r=PreviewNits(pixel&1023),g=PreviewNits((pixel>>10)&1023),b=PreviewNits((pixel>>20)&1023);
        double[] channel={
            Math.Max(0,1.660491*r-.587641*g-.072850*b)/white,
            Math.Max(0,-.124550*r+1.132900*g-.008349*b)/white,
            Math.Max(0,-.018151*r-.100579*g+1.118730*b)/white
        };
        double peak=Math.Max(channel[0],Math.Max(channel[1],channel[2]));
        for(int k=0;k<3;k++) {
            double v=channel[k]*(peak>.75 ? (.75+.25*(1-Math.Exp(-4*(peak-.75))))/peak : 1);
            double s=Math.Round(255*(v<=.0031308 ? 12.92*v : 1.055*Math.Pow(v,1/2.4)-.055))/255;
            channel[k]=white/80*(s<=.04045 ? s/12.92 : Math.Pow((s+.055)/1.055,2.4));
        }
        r=channel[0]; g=channel[1]; b=channel[2];
        return new[]{(int)Math.Round(1023*Pq(.627403896*r+.329283038*g+.043313066*b)),
            (int)Math.Round(1023*Pq(.069097289*r+.919540395*g+.011362316*b)),
            (int)Math.Round(1023*Pq(.016391439*r+.088013308*g+.895595253*b))};
    }

    static void CheckSdrPreview(Type type,object renderer,Size size,int[] hdr,Func<string,int[]> read)
    {
        var internalInstance=BindingFlags.Instance|BindingFlags.NonPublic;
        type.GetField("SdrPreview",internalInstance).SetValue(renderer,true);
        double white=Convert.ToDouble(type.GetProperty("DirectWhiteNits",internalInstance).GetValue(renderer,null));
        int worst=0;
        foreach(bool horizontal in new[]{false,true}) foreach(float position in new[]{.25f,.75f}) {
            type.GetMethod("SetWipe").Invoke(renderer,new object[]{true,position,horizontal,0,1f});
            var actual=read("open-nr");
            int changed=0,checkedSdr=0;
            for(int y=0;y<size.Height;y+=7) for(int x=0;x<size.Width;x+=9) {
                int i=y*size.Width+x;
                double distance=horizontal ? y-position*size.Height : x-position*size.Width;
                if(Math.Abs(distance)<20) continue;
                if(distance>0) {
                    if(hdr[i]!=actual[i]) throw new InvalidDataException("HDR half changed during the SDR preview.");
                    continue;
                }
                var expected=PreviewReference(hdr[i],white);
                checkedSdr++;
                if(actual[i]!=hdr[i]) changed++;
                for(int k=0;k<3;k++) worst=Math.Max(worst,Math.Abs(((actual[i]>>(k*10))&1023)-expected[k]));
            }
            if(changed<checkedSdr/10) throw new InvalidDataException("Moving the SDR divider did not tone-map the picture.");
        }
        Console.WriteLine("HDR/SDR wipe: both axes, two positions; HDR matched exactly, SDR reference error {0}/1023 at {1:F0} nits.",worst,white);
        if(worst>2) throw new InvalidDataException("SDR preview diverged from its reference.");
    }
    // D3D12 retains the composed frame texture after Present. Compare actual
    // HDR10 pixels from the three display modes using the same model evaluation.
    static void CheckNrPhoto(Assembly compiled,string displayName,string photo)
    {
        var screen=Array.Find(Screen.AllScreens,s=>s.DeviceName==displayName);
        if(screen==null) throw new ArgumentException("Missing HDR display "+displayName);
        var size=new Size(640,360);
        var type=compiled.GetType("Renderer",true);
        using(var window=new Form {ClientSize=size,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=screen.WorkingArea.Location})
        using(var renderer=(IDisposable)Activator.CreateInstance(type,new object[]{window.Handle,size,Size.Empty,"hdr10","open-nr",false,"d3d12"}))
        using(var photoFile=new Bitmap(photo))
        using(var picture=new Bitmap(photoFile,size)) {
            var device=(Device)type.GetProperty("Device").GetValue(renderer,null);
            var ctx=device.ImmediateContext;
            var lut=new ushort[256];
            for(int i=0;i<lut.Length;i++) {
                double s=i/255.0;
                lut[i]=HalfBits((float)(12.5*(s<=0.04045 ? s/12.92 : Math.Pow((s+.055)/1.055,2.4))));
            }
            var data=new ushort[size.Width*size.Height*4];
            for(int y=0;y<size.Height;y++) for(int x=0;x<size.Width;x++) {
                var c=picture.GetPixel(x,y);
                int i=4*(y*size.Width+x);
                data[i]=lut[c.R]; data[i+1]=lut[c.G]; data[i+2]=lut[c.B]; data[i+3]=0x3c00;
            }
            using(var input=new Image(device,size.Width,size.Height,Format.R16G16B16A16_Float)) {
                ctx.UpdateSubresource(data,input.texture,0,size.Width*8,0);
                var receive=type.GetMethod("Receive"); var show=type.GetMethod("Show");
                for(int n=0;n<4;n++) {
                    receive.Invoke(renderer,new object[]{input.texture,new Rectangle(Point.Empty,size),false});
                    show.Invoke(renderer,new object[]{"open-nr",1f,false,false});
                }
                if((int)type.GetProperty("OpenNrEvaluations").GetValue(renderer,null)!=4) throw new InvalidDataException("Missing neural evaluations.");
                var target=(Texture2D)type.GetField("backBuffer",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(renderer);
                var desc=target.Description;
                desc.BindFlags=BindFlags.None;desc.Usage=ResourceUsage.Staging;desc.CpuAccessFlags=CpuAccessFlags.Read;
                using(var staging=new Texture2D(device,desc)) {
                    Func<string,int[]> read=mode=>{
                        show.Invoke(renderer,new object[]{mode,1f,false,false});
                        ctx.CopyResource(target,staging);
                        var map=ctx.MapSubresource(staging,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                        var pixels=new int[size.Width*size.Height];
                        try {
                            for(int y=0;y<size.Height;y++) Marshal.Copy(IntPtr.Add(map.DataPointer,y*map.RowPitch),pixels,y*size.Width,size.Width);
                        } finally {ctx.UnmapSubresource(staging,0);}
                        return pixels;
                    };
                    var original=read("original"); var enhanced=read("open-nr");
                    type.GetMethod("SetWipe").Invoke(renderer,new object[]{true,.5f,false,0,1f});
                    var split=read("open-nr");
                    int changed=0; var histogram=new int[1024];
                    for(int i=0;i<original.Length;i++) {
                        int x=i%size.Width,error=0;
                        if(Math.Abs(x-size.Width/2)>40 && split[i]!=(x<size.Width/2 ? original[i]:enhanced[i]))
                            throw new InvalidDataException("HDR split does not match its full-frame reference.");
                        for(int k=0;k<3;k++) error=Math.Max(error,Math.Abs(((original[i]>>(10*k))&1023)-((enhanced[i]>>(10*k))&1023)));
                        histogram[error]++;
                        if(error>=4) changed++;
                    }
                    int p95=0,sum=histogram[0];
                    while(sum<original.Length*.95) sum+=histogram[++p95];
                    Console.WriteLine("HDR photo: {0:F1}% of pixels changed by at least 4 PQ codes, 95th percentile {1}/1023; split matched both views.",
                        changed*100.0/original.Length,p95);
                    if(p95<4) throw new InvalidDataException("Neural enhancement was suppressed across the HDR photo.");
                    CheckSdrPreview(type,renderer,size,enhanced,read);
                }
            }
        }
    }
}
