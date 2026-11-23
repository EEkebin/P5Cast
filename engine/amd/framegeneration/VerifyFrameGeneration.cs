using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;

internal static class VerifyFrameGeneration
{
    const int W=640,H=360,Step=12;
    static readonly int[] SdrBands={32,128,200,255};
    static readonly double[] HdrBands={.25,1,2.5,12.5};
    static readonly ushort[] HalfBands={0x3400,0x3c00,0x4100,0x4a40};
    static Texture2D Image(Device d,Format format,bool uav=false) {
        return new Texture2D(d,new Texture2DDescription {Width=W,Height=H,MipLevels=1,ArraySize=1,Format=format,
            BindFlags=BindFlags.ShaderResource | (uav ? BindFlags.UnorderedAccess:0),SampleDescription=new SampleDescription(1,0)});
    }
    static int Readback(Device d, Texture2D output, bool hdr,out double center,out double maxColorError) {
        var desc=output.Description;desc.Usage=ResourceUsage.Staging;desc.BindFlags=BindFlags.None;desc.CpuAccessFlags=CpuAccessFlags.Read;
        var ctx=d.ImmediateContext;
        using(var buffer=new Texture2D(d,desc)) {
            ctx.CopyResource(output,buffer);
            var map=ctx.MapSubresource(buffer,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
            try {
                int sum=0,count=0;
                maxColorError=0;
                for(int y=H/2-2;y<=H/2+2;y++) for(int x=0;x<W;x++){
                    double r=Channel(map,x,y,0,hdr),g=Channel(map,x,y,1,hdr),b=Channel(map,x,y,2,hdr);
                    if(r>.7 && r>g*1.7 && r>b*1.7){sum+=x;count++;}
                }
                for(int band=0;band<4;band++) {
                    double expected=hdr ? HdrBands[band] : SdrBands[band]/255.0;
                    for(int c=0;c<3;c++) maxColorError=Math.Max(maxColorError,Math.Abs(Channel(map,band*W/4+30,20,c,hdr)-expected));
                }
                center=count>0?(double)sum/count:0;
                return count;
            } finally {ctx.UnmapSubresource(buffer,0);}
        }
    }
    static double Channel(SharpDX.DataBox map,int x,int y,int channel,bool hdr) {
        if(!hdr) return Marshal.ReadByte(map.DataPointer,y*map.RowPitch+4*x+channel)/255.0;
        ushort v=unchecked((ushort)Marshal.ReadInt16(map.DataPointer,y*map.RowPitch+8*x+2*channel));
        int exponent=(v>>10)&31, fraction=v&1023;
        double sign=(v&32768)==0?1:-1;
        if(exponent==31) throw new InvalidOperationException("Nonfinite HDR sample in AMD generated output.");
        if(exponent==0) return sign*Math.Pow(2,-24)*fraction;
        return sign*Math.Pow(2,exponent-15)*(1+fraction/1024.0);
    }
    static void Run(Device d,bool hdr,string backend) {
        var fmt=hdr?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm;
        using(var fg=new FsrFrameGeneration(d,W,H,W,H,hdr,backend))
        using(var color=Image(d,fmt)) using(var mv=Image(d,Format.R16G16_Float)) using(var depth=Image(d,Format.R32_Float))
        using(var output=Image(d,fmt,true)) {
            var ctx=d.ImmediateContext;
            var pixels=new int[W*H];var fp=new ushort[W*H*4]; var vectors=new int[W*H]; var z=new float[W*H];
            for(int i=0;i<z.Length;i++)z[i]=.5f;
            ctx.UpdateSubresource(z,depth,0,W*4);
            int verified=0;
            for(int n=0;n<13;n++) {
                int left=80+n*Step;
                for(int y=0;y<H;y++) for(int x=0;x<W;x++) {
                    bool rect=y>95 && y<280 && x>=left && x<left+100;
                    int k=y*W+x;vectors[k]=rect ? 0xca00:0; // -12 pixels in R16_FLOAT
                    int value=y<50?SdrBands[x/(W/4)]:32+((x/8+y/8)&1)*32;
                    pixels[k]=rect?unchecked((int)0xff2020ef):unchecked((int)0xff000000)|(value*0x010101);
                    if(hdr) {
                        ushort gray=y<50?HalfBands[x/(W/4)]:(ushort)(value==32?0x2e00:0x3400);
                        fp[4*k]=rect?(ushort)0x4000:gray;
                        fp[4*k+1]=fp[4*k+2]=rect?(ushort)0x3400:gray;fp[4*k+3]=0x3c00;
                    }
                }
                if(hdr)ctx.UpdateSubresource(fp,color,0,W*8);else ctx.UpdateSubresource(pixels,color,0,W*4);
                ctx.UpdateSubresource(vectors,mv,0,W*4);
                bool reset=n==0 || n==8;
                fg.Evaluate(ctx,color,mv,depth,output,16.6667f,reset);
                double center,error;
                int count=Readback(d,output,hdr,out center,out error);
                Console.WriteLine("{0} {1} frame {2} center {3:F1}, wanted {4:F1}, static max error {5:F4}",fg.ActiveVersion,hdr?"HDR":"SDR",n,center,left+49.5-Step*.5,error);
                if(reset && Math.Abs(center-(left+49.5))>1) throw new InvalidOperationException("AMD reset retained stale frame history.");
                if(n<3||n==8||n==9)continue;
                if(count<450 || Math.Abs(center-(left+49.5-Step*.5))>3 || error>(hdr?.035:2.0/255))
                    throw new InvalidOperationException("AMD FG midpoint geometry or static color exceeded tolerance.");
                verified++;
            }
            Console.WriteLine("{0} {1} {2}: {3} real generated midpoint frames verified.",backend,fg.ActiveVersion,hdr?"HDR":"SDR",verified);
        }
    }
    static int Main(string[] args) {
        try {
            string backend=args.Length>0?args[0]:"d3d12", adapter;
            using(var d=backend=="vulkan"?VulkanEffects.Create(out adapter):SelectedEffectDevice.CreateD3D12()){
                Run(d,false,backend);Run(d,true,backend);
            }
            return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
