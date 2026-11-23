using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;
using SharpDX.Mathematics.Interop;

internal static class VerifyFsr
{
    static Texture2D Texture(Device device,int w,int h,Format format,bool output=false,bool readback=false)
    {
        return new Texture2D(device,new Texture2DDescription{Width=w,Height=h,Format=format,ArraySize=1,MipLevels=1,
            SampleDescription=new SampleDescription(1,0), Usage=readback?ResourceUsage.Staging:ResourceUsage.Default,
            BindFlags=readback?BindFlags.None:BindFlags.RenderTarget|BindFlags.ShaderResource|(output?BindFlags.UnorderedAccess:0),
            CpuAccessFlags=readback?CpuAccessFlags.Read:CpuAccessFlags.None});
    }
    static void Check(Device device,bool hdr,string backend)
    {
        var ctx=device.ImmediateContext;
        const int w=480,h=270,ow=960,oh=540;
        var format=hdr?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm;
        using(var color=Texture(device,w,h,format))
        using(var motion=Texture(device,w,h,Format.R16G16_Float))
        using(var depth=Texture(device,w,h,Format.R32_Float))
        using(var output=Texture(device,ow,oh,format,true))
        using(var staging=Texture(device,ow,oh,format,false,true))
        using(var mv=new RenderTargetView(device,motion))
        using(var z=new RenderTargetView(device,depth))
        using(var target=new RenderTargetView(device,output))
        using(var fsr=new FsrUpscaler(device,w,h,ow,oh,hdr,backend)) {
            ctx.ClearRenderTargetView(mv,new RawColor4());
            ctx.ClearRenderTargetView(z,new RawColor4(.5f,0,0,0));
            Console.WriteLine("{0}: {1} on {2}x{3} -> {4}x{5}",hdr?"HDR":"SDR",fsr.ActiveVersion,w,h,ow,oh);
            float[][] measurements=new float[2][];
            for(int f=0;f<8;f++) {
                // Shift two large patches halfway through to exercise scene-change reset and reuse.
                bool swap=f>=4;
                if(hdr) {
                    var px=new ushort[w*h*4];
                    for(int y=0;y<h;y++) for(int x=0;x<w;x++){
                        int i=4*(y*w+x); ushort value=((x<w/2)^swap)?(ushort)0x3c00:(ushort)0x4a40;
                        px[i]=px[i+1]=px[i+2]=value;px[i+3]=0x3c00;
                    }
                    ctx.UpdateSubresource(px,color,0,w*8,0);
                } else {
                    var px=new int[w*h];
                    for(int y=0;y<h;y++) for(int x=0;x<w;x++) px[y*w+x]=unchecked((int)(((x<w/2)^swap)?0xff555555:0xffdddddd));
                    ctx.UpdateSubresource(px,color,0,w*4,0);
                }
                ctx.ClearRenderTargetView(target,new RawColor4());
                fsr.Evaluate(ctx,color,motion,depth,output,16.67f,f==0 || f==4);
                if(f!=3 && f!=7) continue;
                ctx.CopyResource(output,staging);
                var map=ctx.MapSubresource(staging,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                try {
                    var result=measurements[f/4]=new float[2];
                    for(int k=0;k<2;k++) {
                        int offset=oh/2*map.RowPitch+(ow/4+k*ow/2)*(hdr?8:4);
                        if(hdr) {
                            int bits=(ushort)Marshal.ReadInt16(map.DataPointer,offset);
                            result[k]=(float)(Math.Pow(2,(bits>>10)-15)*(1+(bits&1023)/1024.0));
                        } else result[k]=Marshal.ReadByte(map.DataPointer,offset)/255f;
                        float expected=((k==0)^swap)?(hdr?1:85/255f):(hdr?12.5f:221/255f);
                        if(Math.Abs(result[k]-expected)>Math.Max(.02f,.05f*expected)) throw new Exception("Unexpected FSR pixel "+result[k]+" against "+expected+".");
                    }
                } finally {ctx.UnmapSubresource(staging,0);}
            }
            Console.WriteLine("GPU readback {0:F3}, {1:F3} then {2:F3}, {3:F3}.",measurements[0][0],measurements[0][1],measurements[1][0],measurements[1][1]);
        }
    }
    static int Main(string[] args) {
        try {
            string backend=args.Length>0?args[0]:"d3d12"; string adapter;
            using(var device=backend=="vulkan"?VulkanEffects.Create(out adapter):SelectedEffectDevice.CreateD3D12()){
                Check(device,false,backend);Check(device,true,backend);
            }
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
