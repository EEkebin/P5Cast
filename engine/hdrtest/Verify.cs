using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;
using Buffer = SharpDX.Direct3D11.Buffer;

class VerifyHdr
{
    const int W=896, H=360;
    // Black, SDR reference white, 1,000 nits, 10,000 nits, and extended scRGB colors outside BT.709.
    static readonly float[,] Bars={{0,0,0},{1,1,1},{12.5f,12.5f,12.5f},{125,125,125},
                                  {2,-0.125f,0},{0.5f,1,0.25f},{0.125f,0,3}};
    static readonly double[,] To2020={{.627403896,.329283038,.043313066},{.069097289,.919540395,.011362316},{.016391439,.088013308,.895595253}};
    static ushort HalfBits(float x) {
        // Fixture values are exactly representable normal binary16 values.
        if(x==0) return 0;
        int bits=BitConverter.ToInt32(BitConverter.GetBytes(x),0);
        return (ushort)(((bits>>16)&32768) | ((((bits>>23)&255)-112)<<10) | ((bits>>13)&1023));
    }
    static double Pq(double value) {
        double v=Math.Pow(Math.Max(0,Math.Min(1,value*.008)),2610.0/16384);
        return Math.Pow((3424.0/4096+2413.0/128*v)/(1+2392.0/128*v),2523.0/32);
    }
    static int Expected(int bar,int channel,double intensity) {
        double v=0;
        for(int k=0;k<3;k++) v+=To2020[channel,k]*Bars[bar,k]*intensity;
        return (int)Math.Round(Pq(v)*1023);
    }
    struct Frame { public float sw,sh,ow,oh,phase,x,y,padding; }
    sealed class Image : IDisposable {
        public Texture2D texture;
        public ShaderResourceView read;
        public RenderTargetView write;
        public Image(Device d,int w,int h,Format format,bool unordered=false) {
            texture=new Texture2D(d,new Texture2DDescription{Width=w,Height=h,MipLevels=1,ArraySize=1,
                Format=format,Usage=ResourceUsage.Default,SampleDescription=new SampleDescription(1,0),
                BindFlags=BindFlags.ShaderResource|BindFlags.RenderTarget|(unordered ? BindFlags.UnorderedAccess : BindFlags.None)});
            read=new ShaderResourceView(d,texture); write=new RenderTargetView(d,texture);
        }
        public void Dispose() { write.Dispose(); read.Dispose(); texture.Dispose(); }
    }
    static int Main(string[] args) {
        try { Run(Assembly.LoadFrom(Path.GetFullPath(args[0])),Array.IndexOf(args,"--nvidia")>=0); return 0; }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static byte[] Shader(Assembly a,string name) {
        using(var input=a.GetManifestResourceStream(name+".cso"))
        using(var bytes=new MemoryStream()) {
            if(input==null) throw new Exception("Missing compiled shader "+name);
            input.CopyTo(bytes); return bytes.ToArray();
        }
    }
    static void Check(Device d, Image output, string label, double intensity, bool neural=false) {
        var desc=output.texture.Description;
        desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read;
        using(var staging=new Texture2D(d,desc)) {
            var c=d.ImmediateContext; c.CopyResource(output.texture,staging);
            var range=c.MapSubresource(staging,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
            try {
                int maxError=0;
                var whites=new List<int>();
                for(int bar=0;bar<7;bar++) {
                    int x=(2*bar+1)*desc.Width/14;
                    uint code=unchecked((uint)Marshal.ReadInt32(range.DataPointer,(desc.Height/2)*range.RowPitch+4*x));
                    if((code>>30)!=3) throw new Exception("Expected opaque HDR10 alpha.");
                    for(int i=0;i<3;i++) {
                        int channel=(int)((code>>(10*i))&1023);
                        maxError=Math.Max(maxError,Math.Abs(channel-Expected(bar,i,intensity)));
                        if(i==0 && bar<=3) whites.Add(channel);
                    }
                }
                Console.WriteLine("{0}: grayscale PQ10 codes {1}, max RGB code error {2}",label,string.Join(", ",whites),maxError);
                if(neural) {
                    // A trained filter changes the picture. Check that real highlights survived its FP16 path.
                    if(whites[2]<700 || whites[3]<940 || whites[2]<=whites[1] || whites[3]<=whites[2])
                        throw new InvalidDataException(label+" clipped the HDR luminance ramp.");
                } else if(maxError>2) throw new InvalidDataException(label+" failed its independent ST 2084 / primary conversion check.");
            } finally { c.UnmapSubresource(staging,0); }
        }
    }
    static void Run(Assembly compiled,bool nvidia) {
        using(var d=new Device(DriverType.Hardware,DeviceCreationFlags.BgraSupport))
        using(var src=new Image(d,W,H,Format.R16G16B16A16_Float))
        using(var zero=new Image(d,W,H,Format.R16G16B16A16_Float))
        using(var temp=new Image(d,W,H,Format.R16G16B16A16_Float))
        using(var enlarged=new Image(d,W*2,H*2,Format.R16G16B16A16_Float))
        using(var hdr10=new Image(d,W*2,H*2,Format.R10G10B10A2_UNorm))
        using(var vs=new VertexShader(d,Shader(compiled,"Fullscreen")))
        using(var cb=new Buffer(d,32,ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0))
        using(var sampler=new SamplerState(d,new SamplerStateDescription {
            AddressU=TextureAddressMode.Clamp,AddressV=TextureAddressMode.Clamp,AddressW=TextureAddressMode.Clamp,
            Filter=Filter.MinMagMipLinear,ComparisonFunction=Comparison.Never,MaximumAnisotropy=1,MaximumLod=16})) {
            var c=d.ImmediateContext;
            ushort[] data=new ushort[W*H*4];
            for(int y=0;y<H;y++) for(int x=0;x<W;x++) {
                int p=(y*W+x)*4;
                for(int k=0;k<3;k++) data[p+k]=HalfBits(Bars[x*7/W,k]);
                data[p+3]=HalfBits(1);
            }
            var pin=GCHandle.Alloc(data,GCHandleType.Pinned);
            try {c.UpdateSubresource(new DataBox(pin.AddrOfPinnedObject(),W*8,W*H*8),src.texture,0);}
            finally {pin.Free();}
            c.ClearRenderTargetView(zero.write,new RawColor4(0,0,0,0));
            c.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
            c.VertexShader.Set(vs); c.PixelShader.SetConstantBuffer(0,cb); c.PixelShader.SetSampler(0,sampler);
            Action<string,Image,Image> draw=(name,input,target)=>{
                using(var ps=new PixelShader(d,Shader(compiled,name))) {
                    var info=new Frame{sw=W,sh=H,ow=2*W,oh=2*H,phase=.5f,x=0,y=0,padding=0};
                    c.UpdateSubresource(ref info,cb);
                    c.OutputMerger.SetRenderTargets(target.write);
                    c.Rasterizer.SetViewport(0,0,target.texture.Description.Width,target.texture.Description.Height);
                    c.PixelShader.SetShaderResource(0,input.read);
                    if(name=="Interpolate") {c.PixelShader.SetShaderResource(1,zero.read); c.PixelShader.SetShaderResource(2,zero.read);}
                    c.PixelShader.Set(ps); c.Draw(3,0);
                    c.PixelShader.SetShaderResources(0,new ShaderResourceView[3]); c.OutputMerger.ResetTargets();
                }
            };
            draw("HdrToPq",src,hdr10); Check(d,hdr10,"Original",1);
            draw("HdrSharpToPq",src,hdr10); Check(d,hdr10,"Sharpen flat patches",1);
            draw("HdrToLinear2020",src,temp); draw("HdrLinear2020ToPq",temp,hdr10); Check(d,hdr10,"Linear BT.2020 round trip",1);
            draw("HdrToPq",src,temp); draw("Easu",temp,enlarged); draw("Rcas",enlarged,hdr10); Check(d,hdr10,"FSR flat patches",1);
            draw("Interpolate",src,temp); draw("HdrToPq",temp,hdr10); Check(d,hdr10,"Interpolated half-luminance",.5);
            if(nvidia) {
                draw("HdrToLinear2020",src,temp);
                var ngxType=compiled.GetType("NgxVideo",true);
                using(var vectors=new Image(d,W,H,Format.R16G16_Float))
                using(var depth=new Image(d,W,H,Format.R32_Float)) {
                    c.ClearRenderTargetView(vectors.write,new RawColor4(0,0,0,0));
                    c.ClearRenderTargetView(depth.write,new RawColor4(.5f,0,0,1));
                    foreach(bool native in new[]{false,true}) {
                        int width=native ? W : W*2, height=native ? H : H*2;
                        using(var modelOutput=new Image(d,width,height,Format.R16G16B16A16_Float,true))
                        using(var ngx=(IDisposable)Activator.CreateInstance(ngxType,new object[]{d,W,H,width,height,native,true})) {
                            for(int i=0;i<4;i++) ngxType.GetMethod("Evaluate").Invoke(ngx,new object[]{
                                c,temp.texture,vectors.texture,depth.texture,modelOutput.texture,0f,0f,16.67f,i==0});
                            c.ClearState();
                            c.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
                            c.VertexShader.Set(vs); c.PixelShader.SetConstantBuffer(0,cb); c.PixelShader.SetSampler(0,sampler);
                            draw("HdrLinear2020ToPq",modelOutput,hdr10);
                            Check(d,hdr10,native ? "NGX DLAA FP16" : "NGX DLSS FP16",1,true);
                        }
                    }
                }
            }
            c.ClearState();
        }
    }
}
