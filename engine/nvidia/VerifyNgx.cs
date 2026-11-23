// Standalone GPU smoke test for both NGX quality modes. Compiled by Test-Ngx.ps1.
using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;

internal static class VerifyNgx
{
    static Texture2D Texture(Device device,int width,int height,Format format,BindFlags flags)
    {
        return new Texture2D(device,new Texture2DDescription {
            Width=width,Height=height,ArraySize=1,MipLevels=1,Format=format,
            SampleDescription=new SampleDescription(1,0),BindFlags=flags,Usage=ResourceUsage.Default
        });
    }
    static void Verify(Device device,bool dlaa)
    {
        const int w=640,h=360;
        int ow=dlaa ? w : 960, oh=dlaa ? h : 540;
        var ctx=device.ImmediateContext;
        var inputFlags=BindFlags.RenderTarget | BindFlags.ShaderResource;
        using (var color=Texture(device,w,h,Format.R8G8B8A8_UNorm,inputFlags))
        using (var motion=Texture(device,w,h,Format.R16G16_Float,inputFlags))
        using (var depth=Texture(device,w,h,Format.R32_Float,inputFlags))
        using (var output=Texture(device,ow,oh,Format.R8G8B8A8_UNorm,inputFlags|BindFlags.UnorderedAccess))
        using (var mv=new RenderTargetView(device,motion))
        using (var z=new RenderTargetView(device,depth))
        using (var target=new RenderTargetView(device,output))
        using (var ngx=new NgxVideo(device,w,h,ow,oh,dlaa))
        {
            var staging=output.Description;
            staging.Usage=ResourceUsage.Staging; staging.BindFlags=BindFlags.None; staging.CpuAccessFlags=CpuAccessFlags.Read;
            byte[] picture=new byte[w*h*4];
            ctx.ClearRenderTargetView(mv,new RawColor4(0,0,0,0));
            ctx.ClearRenderTargetView(z,new RawColor4(.5f,0,0,0));
            int priorHash=0,changed=0,min=255,max=0;
            long differenceSum=0;
            int sampleCount=0,changedFromBaseline=0,peakDifference=0;
            using (var read=new Texture2D(device,staging))
            {
                for (int frame=0;frame<14;frame++)
                {
                    // The last eight frames repeat the same high-frequency image after a history reset.
                    int movement=Math.Min(frame,5);
                    for (int y=0;y<h;y++) for (int x=0;x<w;x++)
                    {
                        int i=(y*w+x)*4;
                        picture[i]=(byte)((x+movement*20)%245);
                        picture[i+1]=(byte)((x/8+y/8)%2==0 ? 210:44);
                        picture[i+2]=(byte)((x+2*y+movement)%9<2 ? 235 : 40+y*100/h);
                        picture[i+3]=255;
                    }
                    ctx.UpdateSubresource(picture,color,0,w*4,0);
                    ctx.ClearRenderTargetView(target,new RawColor4(0,0,0,0));
                    ngx.Evaluate(ctx,color,motion,depth,output,0,0,16.67f,frame==0 || frame==6);
                    ctx.CopyResource(output,read);
                    var mapped=ctx.MapSubresource(read,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                    try
                    {
                        int hash=17;
                        for(int y=7;y<oh;y+=17) for(int x=5;x<ow;x+=13)
                        {
                            int pixel=Marshal.ReadInt32(mapped.DataPointer,y*mapped.RowPitch+x*4);
                            min=Math.Min(min,pixel & 255); max=Math.Max(max,pixel & 255);
                            hash=unchecked(hash*31+pixel);
                            if(frame==13)
                            {
                                int pixelPeak=0;
                                for(int c=0;c<3;c++)
                                {
                                    int reference=Bilinear(picture,w,h,(x+.5)*w/ow-.5,(y+.5)*h/oh-.5,c);
                                    int error=Math.Abs(((pixel>>(8*c)) & 255)-reference);
                                    differenceSum+=error; peakDifference=Math.Max(peakDifference,error);
                                    pixelPeak=Math.Max(pixelPeak,error);
                                }
                                if(pixelPeak>2) changedFromBaseline++;
                                sampleCount++;
                            }
                        }
                        if(frame>0 && frame<6 && hash!=priorHash) changed++;
                        priorHash=hash;
                    }
                    finally { ctx.UnmapSubresource(read,0); }
                }
                byte[] levels={0,32,128,224,255};
                for(int y=0;y<h;y++) for(int x=0;x<w;x++) {
                    int i=(y*w+x)*4;
                    picture[i]=picture[i+1]=picture[i+2]=levels[x*levels.Length/w];
                }
                ctx.UpdateSubresource(picture,color,0,w*4,0);
                for(int frame=0;frame<8;frame++)
                    ngx.Evaluate(ctx,color,motion,depth,output,0,0,16.67f,frame==0);
                ctx.CopyResource(output,read);
                var neutral=ctx.MapSubresource(read,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                try {
                    int worst=0;
                    for(int band=0;band<levels.Length;band++) {
                        int center=ow*(2*band+1)/(2*levels.Length);
                        for(int y=oh/4;y<3*oh/4;y+=37) for(int x=center-4;x<=center+4;x+=4)
                            for(int c=0;c<3;c++)
                                worst=Math.Max(worst,Math.Abs(Marshal.ReadByte(neutral.DataPointer,y*neutral.RowPitch+x*4+c)-levels[band]));
                    }
                    Console.WriteLine("{0} SDR flat-field test: black to reference white, worst channel error {1}/255.",dlaa ? "DLAA":"DLSS",worst);
                    if(worst>4) throw new InvalidOperationException("NGX changed the neutral SDR transfer beyond four code values.");
                } finally {ctx.UnmapSubresource(read,0);}
            }
            if (max-min<32 || changed<4) throw new InvalidOperationException("NGX produced an empty or static image.");
            if (changedFromBaseline==0) throw new InvalidOperationException("The NGX output matched the baseline within the two-level tolerance.");
            Console.WriteLine("{0} {1}x{2} to {3}x{4}, 14 NGX evaluations, {5}/5 moving updates changed, sampled red {6}-{7}",
                dlaa ? "DLAA":"DLSS",w,h,ow,oh,changed,min,max);
            Console.WriteLine("Static comparison to {0}: {1}/{2} sampled RGB pixels differed by >2 levels; mean absolute channel difference {3:F2}/255, peak {4}/255.",
                dlaa ? "native input":"bilinear",changedFromBaseline,sampleCount,differenceSum/(3.0*sampleCount),peakDifference);
        }
    }
    static int Bilinear(byte[] image,int w,int h,double x,double y,int channel)
    {
        x=Math.Max(0,Math.Min(w-1,x)); y=Math.Max(0,Math.Min(h-1,y));
        int x0=(int)x, y0=(int)y, x1=Math.Min(x0+1,w-1), y1=Math.Min(y0+1,h-1);
        double dx=x-x0,dy=y-y0;
        double top=image[(y0*w+x0)*4+channel]*(1-dx)+image[(y0*w+x1)*4+channel]*dx;
        double bottom=image[(y1*w+x0)*4+channel]*(1-dx)+image[(y1*w+x1)*4+channel]*dx;
        return (int)Math.Round(top*(1-dy)+bottom*dy);
    }
    static double HalfValue(ushort bits)
    {
        int exponent=(bits>>10)&31;
        if(exponent==31) return double.NaN;
        int mantissa=bits&1023;
        double value=exponent==0 ? mantissa*Math.Pow(2,-24) : (1+mantissa/1024.0)*Math.Pow(2,exponent-15);
        return (bits&0x8000)==0 ? value:-value;
    }
    static void VerifyHdr(Device device,bool dlaa)
    {
        const int w=640,h=360;
        int ow=dlaa ? w:960,oh=dlaa ? h:540;
        BindFlags input=BindFlags.ShaderResource | BindFlags.RenderTarget;
        var ctx=device.ImmediateContext;
        using(var color=Texture(device,w,h,Format.R16G16B16A16_Float,input))
        using(var motion=Texture(device,w,h,Format.R16G16_Float,input))
        using(var depth=Texture(device,w,h,Format.R32_Float,input))
        using(var output=Texture(device,ow,oh,Format.R16G16B16A16_Float,input|BindFlags.UnorderedAccess))
        using(var mv=new RenderTargetView(device,motion))
        using(var z=new RenderTargetView(device,depth))
        using(var ngx=new NgxVideo(device,w,h,ow,oh,dlaa,true))
        {
            // Four neutral scRGB bands at 20, 80, 320 and 1000 nits (1.0 = 80 cd/m2).
            ushort[] halfBits={0x3400,0x3c00,0x4400,0x4a40};
            var image=new ushort[w*h*4];
            for(int y=0;y<h;y++) for(int x=0;x<w;x++)
            {
                int i=(y*w+x)*4;
                image[i]=image[i+1]=image[i+2]=halfBits[x*4/w];
                image[i+3]=0x3c00;
            }
            ctx.UpdateSubresource(image,color,0,w*8,0);
            ctx.ClearRenderTargetView(mv,new RawColor4(0,0,0,0));
            ctx.ClearRenderTargetView(z,new RawColor4(.5f,0,0,0));
            for(int frame=0;frame<8;frame++) ngx.Evaluate(ctx,color,motion,depth,output,0,0,16.67f,frame==0);
            var desc=output.Description;
            desc.Usage=ResourceUsage.Staging; desc.BindFlags=BindFlags.None; desc.CpuAccessFlags=CpuAccessFlags.Read;
            using(var read=new Texture2D(device,desc))
            {
                ctx.CopyResource(output,read);
                var map=ctx.MapSubresource(read,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    double[] nits=new double[4];
                    for(int band=0;band<4;band++)
                    {
                        int offset=(oh/2)*map.RowPitch+(ow*(band*2+1)/8)*8;
                        double expected=HalfValue(halfBits[band]);
                        for(int channel=0;channel<3;channel++)
                        {
                            double actual=HalfValue(unchecked((ushort)Marshal.ReadInt16(map.DataPointer,offset+channel*2)));
                            if(double.IsNaN(actual) || Math.Abs(actual-expected)>expected*.12)
                                throw new InvalidOperationException("NGX HDR output is clipped or outside the flat-field tolerance.");
                            nits[band]+=actual*(80.0/3.0);
                        }
                    }
                    Console.WriteLine("{0} HDR evaluated 8 FP16 frames. For 20/80/320/1000 nits: {1:F1}/{2:F1}/{3:F1}/{4:F1} nits on readback.",
                        dlaa ? "DLAA":"DLSS",nits[0],nits[1],nits[2],nits[3]);
                }
                finally { ctx.UnmapSubresource(read,0); }
            }
        }
    }
    static int Main()
    {
        try
        {
            using (var device=new Device(DriverType.Hardware,DeviceCreationFlags.BgraSupport))
            {
                Verify(device,false); Verify(device,true);
                VerifyHdr(device,false); VerifyHdr(device,true);
            }
            return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
