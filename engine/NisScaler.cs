using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer=SharpDX.Direct3D11.Buffer;
using Device=SharpDX.Direct3D11.Device;
using Rectangle=System.Drawing.Rectangle;
using Size=System.Drawing.Size;

internal sealed class NisScaler : IDisposable
{
    internal const string BackendReason="NVIDIA Image Scaling requires Direct3D 11, Direct3D 12 or Vulkan.";
    internal static bool SupportsBackend(string backend) {return backend=="d3d11" || backend=="d3d12" || backend=="vulkan";}

    readonly Device device;
    readonly ComputeShader[,] shaders=new ComputeShader[2,2];
    readonly List<Pass> passes=new List<Pass>();
    readonly ShaderResourceView[] unbind=new ShaderResourceView[3];
    ShaderResourceView scalerCoefficients,usmCoefficients;
    SamplerState sampler;
    Size sourceSize,targetSize;
    bool pq;
    public int Evaluations {get;private set;}
    public string Filter {get {return sourceSize==targetSize?"NVSharpen":"NVScaler";}}
    public ShaderResourceView Output {get {return passes[passes.Count-1].Image.Read;}}

    sealed class Pass : IDisposable
    {
        public readonly Size Size;
        public readonly bool Scaling;
        public GpuImage Image;
        public UnorderedAccessView Write;
        public Buffer Constants;
        public Pass(Device device,Size input,Size output,bool pq,bool last) {
            Size=output; Scaling=input!=output;
            try {
                var format=pq || !last?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm;
                Image=new GpuImage(device,output.Width,output.Height,format,false,true);
                Write=new UnorderedAccessView(device,Image.Texture);
                Constants=Buffer.Create(device,BindFlags.ConstantBuffer,NisConfig.Constants(input.Width,input.Height,output.Width,output.Height,pq,last?.5f:0f));
            } catch {Dispose(); throw;}
        }
        public void Dispose() {
            if(Constants!=null) Constants.Dispose();
            if(Write!=null) Write.Dispose();
            if(Image!=null) Image.Dispose();
        }
    }

    internal NisScaler(Device device) {
        this.device=device;
        try {
            for(int h=0;h<2;h++) for(int scale=0;scale<2;scale++)
                shaders[h,scale]=new ComputeShader(device,Renderer.Shader((scale==1?"NVScaler":"NVSharpen")+(h==1?"PQ":"SDR")));
            var sampling=SamplerStateDescription.Default();
            sampling.Filter=SharpDX.Direct3D11.Filter.MinMagMipLinear;
            sampling.AddressU=sampling.AddressV=sampling.AddressW=TextureAddressMode.Clamp;
            sampler=new SamplerState(device,sampling);
            scalerCoefficients=Coefficients(false);
            usmCoefficients=Coefficients(true);
        } catch {Dispose();throw;}
    }
    ShaderResourceView Coefficients(bool usm) {
        var data=NisConfig.Coefficients(usm);
        var desc=new Texture2DDescription {Width=2,Height=64,MipLevels=1,ArraySize=1,
            Format=Format.R32G32B32A32_Float,SampleDescription=new SampleDescription(1,0),
            Usage=ResourceUsage.Immutable,BindFlags=BindFlags.ShaderResource};
        var pinned=GCHandle.Alloc(data,GCHandleType.Pinned);
        try {
            using(var texture=new Texture2D(device,desc,new DataRectangle(pinned.AddrOfPinnedObject(),32)))
                return new ShaderResourceView(device,texture);
        } finally {pinned.Free();}
    }
    void Configure(Size input,Size output,bool hdr) {
        if(passes.Count>0 && sourceSize==input && targetSize==output && pq==hdr) return;
        if(input.Width<=0 || input.Height<=0 || output.Width<input.Width || output.Height<input.Height)
            throw new ArgumentException("Invalid NVIDIA Image Scaling extent.");
        ClearPasses();
        try {
            Size current=input;
            do {
                // NVScaler supports an enlargement of up to 2× per axis per dispatch.
                var next=new Size(Math.Min(output.Width,current.Width*2),Math.Min(output.Height,current.Height*2));
                passes.Add(new Pass(device,current,next,hdr,next==output));
                current=next;
            } while(current!=output);
        } catch {ClearPasses();throw;}
        sourceSize=input;targetSize=output;pq=hdr;
    }
    internal void Evaluate(ShaderResourceView source,Size input,Size output,bool hdr) {
        Configure(input,output,hdr);
        var context=device.ImmediateContext;
        var compute=context.ComputeShader;
        compute.SetSampler(0,sampler);
        try {
            foreach(var pass in passes) {
                compute.Set(shaders[hdr?1:0,pass.Scaling?1:0]);
                compute.SetConstantBuffer(0,pass.Constants);
                compute.SetShaderResource(0,source);
                if(pass.Scaling) {
                    compute.SetShaderResource(1,scalerCoefficients);
                    compute.SetShaderResource(2,usmCoefficients);
                }
                compute.SetUnorderedAccessView(0,pass.Write);
                context.Dispatch((pass.Size.Width+31)/32,(pass.Size.Height+(pass.Scaling?23:31))/(pass.Scaling?24:32),1);
                compute.SetUnorderedAccessView(0,null);
                source=pass.Image.Read;
                Evaluations++;
            }
        } finally {
            compute.SetUnorderedAccessView(0,null);
            compute.SetShaderResources(0,unbind);
            compute.SetConstantBuffer(0,null);
            compute.SetSampler(0,null);
            compute.Set(null);
        }
    }
    byte[] ReadProbe(int bytesPerPixel) {
        using(var image=Output.ResourceAs<Texture2D>()) {
            var desc=image.Description;
            desc.BindFlags=BindFlags.None;desc.Usage=ResourceUsage.Staging;desc.CpuAccessFlags=CpuAccessFlags.Read;
            using(var read=new Texture2D(device,desc)) {
                var context=device.ImmediateContext;
                context.CopyResource(image,read);
                var map=context.MapSubresource(read,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                try {
                    int stride=desc.Width*bytesPerPixel;
                    var pixels=new byte[stride*desc.Height];
                    for(int y=0;y<desc.Height;y++)
                        Marshal.Copy(IntPtr.Add(map.DataPointer,y*map.RowPitch),pixels,y*stride,stride);
                    return pixels;
                } finally {context.UnmapSubresource(read,0);}
            }
        }
    }
    internal static string Probe(Device device) {
        try {
            var size=new Size(32,32);
            using(var scaler=new NisScaler(device))
            using(var input=new GpuImage(device,32,32,Format.R8G8B8A8_UNorm,false))
            using(var pqInput=new GpuImage(device,32,32,Format.R16G16B16A16_Float,true)) {
                var colors=new uint[32*32];
                for(int y=0;y<32;y++) for(int x=0;x<32;x++)
                    colors[y*32+x]=x<10?0xff2c6492u:x>=22?0xffb9364eu:0xff000000u+0x010101u*(uint)(40+((3*x+5*y)%7)*25);
                var context=device.ImmediateContext;
                context.UpdateSubresource(colors,input.Texture,0,32*4,0);
                context.ClearRenderTargetView(pqInput.Write,new SharpDX.Mathematics.Interop.RawColor4(.25f,.5f,.75f,1));
                foreach(int width in new[]{32,48}) {
                    var output=new Size(width,width);
                    scaler.Evaluate(input.Read,size,output,false);
                    var pixels=scaler.ReadProbe(4);
                    for(int side=0;side<2;side++) {
                        int offset=4*(width*(width/2)+(side==0?3:width-4));
                        uint expected=colors[side==0?3:29];
                        for(int ch=0;ch<3;ch++) if(Math.Abs(pixels[offset+ch]-(int)((expected>>(8*ch))&255))>2)
                            return scaler.Filter+" returned incorrect SDR reference colors.";
                    }
                    if(width==32) {
                        int changed=0;
                        for(int y=4;y<28;y++) for(int x=12;x<20;x++) {
                            int index=y*32+x;
                            if(Math.Abs(pixels[index*4]-(int)(colors[index]&255))>2) changed++;
                        }
                        if(changed==0) return "NVSharpen left the textured test region unprocessed.";
                    }
                    scaler.Evaluate(pqInput.Read,size,output,true);
                    var pq=scaler.ReadProbe(8);
                    int center=8*(width*(width/2)+width/2);
                    // FP16 encodings for the three independent normalized PQ channels.
                    int[] half={0x3400,0x3800,0x3a00};
                    for(int ch=0;ch<3;ch++) if(Math.Abs(BitConverter.ToInt16(pq,center+2*ch)-half[ch])>3)
                        return scaler.Filter+" returned incorrect HDR PQ reference colors.";
                }
            }
            return "";
        } catch(Exception error) {return "NVIDIA Image Scaling GPU test failed. "+error.GetBaseException().Message;}
    }
    void ClearPasses() {foreach(var pass in passes) pass.Dispose();passes.Clear();}
    public void Dispose() {
        ClearPasses();
        foreach(var shader in shaders) if(shader!=null) shader.Dispose();
        if(sampler!=null) sampler.Dispose();
        if(usmCoefficients!=null) usmCoefficients.Dispose();
        if(scalerCoefficients!=null) scalerCoefficients.Dispose();
    }
}

internal sealed partial class Renderer
{
    NisScaler nis;
    int nisCachedFrame=-1,nisPriorEvaluations;
    float nisCachedPhase;
    IntPtr nisCachedInput;
    Size nisCachedSize;
    bool nisCachedHdr;
    public int NisEvaluations {get {return nisPriorEvaluations+(nis==null?0:nis.Evaluations);}}
    public string ActiveNisFilter {get {return EffectivePictureMode=="nis" && nis!=null ? nis.Filter : "";}}
    void ReleaseNis() {
        if(nis!=null) {nisPriorEvaluations+=nis.Evaluations;nis.Dispose();nis=null;}
        nisCachedFrame=-1;
    }
    void EvaluateNis(ShaderResourceView source,float phase,Size target) {
        if(!NisScaler.SupportsBackend(Backend)) throw new InvalidOperationException(NisScaler.BackendReason);
        if(nis==null) nis=new NisScaler(Device);
        IntPtr input=source.NativePointer;
        if(nisCachedFrame==Captured && nisCachedPhase==phase && nisCachedInput==input && nisCachedSize==target && nisCachedHdr==HdrEnabled) return;
        if(HdrEnabled) {
            // The SDK's HDR PQ shader expects display-referred BT.2020 PQ.
            HdrScratch(ref hdrFsrInput,srcSize,HdrFloatFormat);
            Draw("HdrToPq",hdrFsrInput.Write,new Rectangle(System.Drawing.Point.Empty,srcSize),source,1);
            source=hdrFsrInput.Read;
        }
        nis.Evaluate(source,srcSize,target,HdrEnabled);
        nisCachedFrame=Captured;nisCachedPhase=phase;nisCachedInput=input;nisCachedSize=target;nisCachedHdr=HdrEnabled;
    }
    void DrawNis(ShaderResourceView source,float phase,Rectangle area,Rectangle? clip=null) {
        try {
            EvaluateNis(source,phase,outSize);
        } catch(Exception error) {
            if(!IsEffectFailure(error)) throw;
            failedMode="nis";EffectivePictureMode="original";
            EffectNotice="Showing Original. "+error.Message;
            ReleaseNis(); RestoreDrawState(); SetProcessingSize(srcSize);
            Draw(HdrEnabled?"HdrToPq":"Original",display,area,source,1,clip:clip);
            return;
        }
        Draw("Original",display,area,nis.Output,1,clip:clip);
    }
}
