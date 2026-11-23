using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device=SharpDX.Direct3D11.Device;

internal static partial class VerifyFsr
{
    static int Rgb(int r,int g,int b) {return unchecked((int)0xff000000) | b<<16 | g<<8 | r;}
    static readonly int[] patches={Rgb(219,34,62),Rgb(49,179,95),Rgb(72,89,227),Rgb(125,122,128)};

    // Read the raw SDK output before the shared text-preservation shader.
    // Different RGB channels and a colored HUD catch regressions that equal-channel patches cannot.
    static void CheckHud(Device device,string backend)
    {
        CheckHud(device,backend,false);
        CheckHud(device,backend,true);
    }
    static void CheckHud(Device device,string backend,bool guided)
    {
        const int width=320,height=180,scale=2;
        const int ow=width*scale,oh=height*scale;
        var c=device.ImmediateContext;
        var pixels=new int[width*height];
        var mask=new byte[width*height];
        var gpu=new int[ow*oh];
        int maxColorError=0,gradientError=0;
        double movingEnergy=0,ghostEnergy=0,strokeRgbError=0;
        using(var color=Texture(device,width,height,Format.R8G8B8A8_UNorm))
        using(var motion=Texture(device,width,height,Format.R16G16_Float))
        using(var depth=Texture(device,width,height,Format.R32_Float))
        using(var composition=Texture(device,width,height,Format.R8_UNorm))
        using(var output=Texture(device,ow,oh,Format.R8G8B8A8_UNorm,true))
        using(var staging=Texture(device,ow,oh,Format.R8G8B8A8_UNorm,false,true))
        using(var vectors=new RenderTargetView(device,motion))
        using(var plane=new RenderTargetView(device,depth))
        using(var fsr=new FsrUpscaler(device,width,height,ow,oh,false,backend)) {
            c.ClearRenderTargetView(vectors,new RawColor4());
            c.ClearRenderTargetView(plane,new RawColor4(.5f,0,0,0));
            for(int frame=0;frame<12;++frame) {
                int line=98+frame*4;
                for(int y=0;y<height;++y) for(int x=0;x<width;++x) {
                    int baseR=42+x/12,baseG=92+y/9,baseB=108+(x+y)/15;
                    int rgb=Rgb(baseR,baseG,baseB);
                    if(y>=18 && y<49 && x>=24 && x<280) rgb=patches[(x-24)/64];
                    mask[y*width+x]=0;
                    if(y>=80 && y<113 && x==line) {rgb=Rgb(244,51,131);mask[y*width+x]=255;}
                    if (y>=130 && y<151 && (x==205 || y==130 && x>=205 && x<=216 || y==141 && x>=205 && x<=212)) {
                        rgb=Rgb(236,235,231);
                        mask[y*width+x]=255;
                    }
                    pixels[y*width+x]=rgb;
                }
                c.UpdateSubresource(pixels,color,0,width*4,0);
                c.UpdateSubresource(mask,composition,0,width,0);
                fsr.Evaluate(c,color,motion,depth,output,16.67f,frame==0,guided?composition:null);
                c.CopyResource(output,staging);
                var map=c.MapSubresource(staging,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                try {
                    for(int y=0;y<oh;y++) Marshal.Copy(IntPtr.Add(map.DataPointer,y*map.RowPitch),gpu,y*ow,ow);
                } finally {c.UnmapSubresource(staging,0);}
                for(int i=0;i<patches.Length;i++) {
                    int sampled=gpu[33*scale*ow+(52+i*64)*scale];
                    for(int ch=0;ch<3;ch++) maxColorError=Math.Max(maxColorError,
                        Math.Abs(((sampled>>(ch*8))&255)-((patches[i]>>(ch*8))&255)));
                }
                for(int x=16;x<width-16;x+=9) {
                    const int y=65;
                    int source=pixels[y*width+x],actual=gpu[y*scale*ow+x*scale];
                    for(int ch=0;ch<3;++ch) gradientError=Math.Max(gradientError,Math.Abs(
                        ((actual>>(ch*8))&255)-((source>>(ch*8))&255)));
                }
                if(frame>=3) {
                    for(int y=90*scale;y<101*scale;++y) {
                        int background=gpu[y*ow+(line-9)*scale]&255;
                        int red=((gpu[y*ow+line*scale]&255)+(gpu[y*ow+line*scale+1]&255))/2;
                        int previous=((gpu[y*ow+(line-4)*scale]&255)+(gpu[y*ow+(line-4)*scale+1]&255))/2;
                        movingEnergy+=(red-background)/(double)(244-background);
                        ghostEnergy+=Math.Abs(previous-background)/(double)(244-background);
                        int input=pixels[(y/scale)*width+line];
                        int neighbor=pixels[(y/scale)*width+line-1];
                        for(int ch=0;ch<3;++ch) {
                            int shift=ch*8;
                            double spatial=.75*((input>>shift)&255)+.25*((neighbor>>shift)&255);
                            int a=gpu[y*ow+line*scale],b=gpu[y*ow+line*scale+1];
                            strokeRgbError+=Math.Abs(.5*(((a>>shift)&255)+((b>>shift)&255))-spatial);
                        }
                    }
                }
            }
        }
        movingEnergy/=9*11*scale;
        ghostEnergy/=9*11*scale;
        strokeRgbError/=9*11*scale*3;
        Console.WriteLine("Colored HUD {0}: RGB patch/gradient error {1}/{2} codes, moving stroke {3:P1}, previous location {4:P1}, stroke RGB MAE {5:F1}/255.",
            guided?"with guidance":"without guidance",maxColorError,gradientError,movingEnergy,ghostEnergy,strokeRgbError);
        if(maxColorError>7 || gradientError>6 || guided && (movingEnergy<.64 || ghostEnergy>.08 || strokeRgbError>14))
            throw new Exception("Temporal FSR changed scene colors or lost moving HUD contrast.");
    }
}
