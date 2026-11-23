using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;

partial class VerifyHdr
{
    static int MovingCode(int x,int y,int frame) {
        int u=x-8*frame;
        return 128+4*(((u>>3)*17+(y>>3)*29+((u>>5)^(y>>5))*7)&159);
    }
    static ushort LinearFromPq10(int code) {
        double p=Math.Pow(code/1023.0,32.0/2523);
        return HalfBits((float)(125*Math.Pow(Math.Max(0,p-3424.0/4096)/(2413.0/128-2392.0/128*p),16384.0/2610)));
    }
    static void CheckFrucHdr(Assembly compiled,Device device,Action<string,Image,Image> draw) {
        var provider=compiled.GetType("FrucFrames",true);
        var context=device.ImmediateContext;
        using(var fruc=(IDisposable)Activator.CreateInstance(provider,new object[]{device,W,H}))
        using(var input=new Image(device,W,H,Format.R16G16B16A16_Float))
        using(var linearA=new Image(device,W,H,Format.R16G16B16A16_Float))
        using(var linearB=new Image(device,W,H,Format.R16G16B16A16_Float))
        using(var packed=new Image(device,W,H,Format.R8G8B8A8_UNorm))
        using(var interpolated=new Image(device,W,H,Format.R8G8B8A8_UNorm))
        using(var midpoint=new Image(device,W,H,Format.R16G16B16A16_Float))
        using(var hdr10=new Image(device,W,H,Format.R10G10B10A2_UNorm)) {
            var data=new ushort[W*H*4];
            var ramp=new ushort[1024];
            for(int code=1;code<ramp.Length;code++) ramp[code]=LinearFromPq10(code);
            for(int frame=0;frame<5;frame++) {
                for(int y=0;y<H;y++) for(int x=0;x<W;x++) {
                    int p=(y*W+x)*4;
                    for(int k=0;k<3;k++)
                        data[p+k]=Math.Abs(y-H/2)<20 ? HalfBits(Bars[x*7/W,k]) :
                            Math.Abs(y-H*7/10)<20 ? ramp[320+x/2] : ramp[MovingCode(x,y,frame)];
                    data[p+3]=0x3c00;
                }
                context.UpdateSubresource(data,input.texture,0,W*8,0);
                var current=frame%2==0?linearA:linearB;
                var previous=frame%2==0?linearB:linearA;
                draw("HdrToLinear2020",input,current);
                draw("HdrLinear2020ToPq",current,packed);
                bool valid=(bool)provider.GetMethod("Evaluate").Invoke(fruc,new object[]{packed.texture,interpolated.texture});
                if(!valid) continue;
                if(frame==0) throw new InvalidDataException("FRUC produced a midpoint before a pair was submitted.");
                context.PixelShader.SetShaderResources(1,new[]{current.read,previous.read});
                draw("FrucHdrMidpoint",interpolated,midpoint);
                draw("HdrToPq",midpoint,hdr10);
                Check(device,hdr10,"NVIDIA FRUC HDR midpoint",1);
                var desc=hdr10.texture.Description;
                desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read;
                using(var staging=new Texture2D(device,desc)) {
                    context.CopyResource(hdr10.texture,staging);
                    var map=context.MapSubresource(staging,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                    try {
                        var distinct=new HashSet<int>();
                        int priorChanges=0,nextChanges=0;
                        for(int x=16;x<W-16;x++) {
                            int value=Marshal.ReadInt32(map.DataPointer,H*7/10*map.RowPitch+x*4)&1023;
                            int expected=320+x/2;
                            if(Math.Abs(value-expected)>2) throw new InvalidDataException("HDR ramp expected PQ10 "+expected+" but received "+value+".");
                            distinct.Add(value);
                            int y=H*9/10;
                            int motion=Marshal.ReadInt32(map.DataPointer,y*map.RowPitch+x*4)&1023;
                            if(Math.Abs(motion-MovingCode(x,y,frame-1))>8) priorChanges++;
                            if(Math.Abs(motion-MovingCode(x,y,frame))>8) nextChanges++;
                        }
                        if(distinct.Count<390) throw new InvalidDataException("FRUC flattened the HDR10 gradient to "+distinct.Count+" unique codes.");
                        if(priorChanges>=75 && nextChanges>=75) {
                            Console.WriteLine("NVIDIA FRUC retained {0} distinct PQ10 ramp codes; the moving midpoint differed from both source frames.",distinct.Count);
                            return;
                        }
                    } finally {context.UnmapSubresource(staging,0);}
                }
            }
            throw new InvalidDataException("NVIDIA FRUC did not return a distinct HDR midpoint.");
        }
    }
}
