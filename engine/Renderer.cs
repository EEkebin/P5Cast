using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using Rectangle = System.Drawing.Rectangle;

internal sealed class GpuImage : IDisposable
{
    public readonly Texture2D Texture;
    public readonly ShaderResourceView Read;
    public readonly RenderTargetView Write;
    public GpuImage(Device device, int width, int height, Format format, bool target,bool unordered=false)
    {
        var description=new Texture2DDescription {
            Width=width,Height=height,MipLevels=1,ArraySize=1,Format=format,
            SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,
            BindFlags=BindFlags.ShaderResource | (target ? BindFlags.RenderTarget : BindFlags.None)
                | (unordered ? BindFlags.UnorderedAccess : BindFlags.None)
        };
        Texture=new Texture2D(device,description);
        Read=new ShaderResourceView(device,Texture);
        if(target) Write=new RenderTargetView(device,Texture);
    }
    public void Dispose() { if(Write!=null) Write.Dispose(); Read.Dispose(); Texture.Dispose(); }
}

internal sealed partial class Renderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct FrameInfo
    {
        public float SourceX,SourceY,OutputX,OutputY,Phase,OriginX,OriginY,Pad;
    }
    public Device Device { get; private set; }
    readonly DeviceContext context;
    readonly SwapChain swap;
    readonly Buffer constants;
    readonly ComputeShader changed;
    readonly Buffer changeFlag,changeReadback;
    readonly UnorderedAccessView changeView;
    readonly SamplerState sampler;
    readonly VertexShader vertex;
    readonly Dictionary<string,PixelShader> shaders=new Dictionary<string,PixelShader>();
    readonly ShaderResourceView[] unbind=new ShaderResourceView[3];
    Texture2D backBuffer;
    RenderTargetView display;
    GpuImage curr,prev,incoming,motion,mixed,scaled;
    Size srcSize,outSize,screenSize;
    Rectangle viewport;
    readonly IntPtr hwnd;
    readonly Size renderLimit;
    readonly string hdrMode,pictureMode;
    DisplayHdr outputInfo;
    IntPtr monitor;
    GpuImage raster;
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    public bool HdrEnabled { get; private set; }
    public Size RenderSize { get { return outSize; } }
    public Size OutputSize { get { return screenSize; } }
    public string OutputStatus { get { return outputInfo.Summary+" · "+(HdrEnabled ? "HDR10 output" : "SDR output"); } }
    public string DisplayName { get { return outputInfo.DeviceName; } }
    public void InvalidateDisplay() { monitor=IntPtr.Zero; }
    bool motionReady;
    public int Captured { get; private set; }
    public int Duplicates { get; private set; }
    public int Presented { get; private set; }
    public int Generated { get; private set; }
    public bool HasPrevious { get; private set; }
    public int SampleMinLuma { get; private set; }
    public int SampleMaxLuma { get; private set; }
    public int SampledFrames { get; private set; }
    public bool VerifyNextOutput;
    public Renderer(IntPtr hwnd, Size window,Size limit,string hdr,string picture)
    {
        this.hwnd=hwnd; renderLimit=limit; hdrMode=hdr; pictureMode=picture;
        HdrEnabled=ChooseHdr();
        Device device;
        SwapChain chain;
        var desc=new SwapChainDescription {
            BufferCount=2, Flags=SwapChainFlags.None, IsWindowed=true, OutputHandle=hwnd,
            ModeDescription=new ModeDescription(window.Width,window.Height,new Rational(0,1),DisplayHdr.OutputFormat(HdrEnabled)),
            Usage=Usage.RenderTargetOutput,SampleDescription=new SampleDescription(1,0),SwapEffect=SwapEffect.FlipDiscard
        };
        Device.CreateWithSwapChain(DriverType.Hardware, DeviceCreationFlags.BgraSupport,desc,out device,out chain);
        Device=device; swap=chain; context=device.ImmediateContext;
        SampleMinLuma=255;
        using(var dxgi=device.QueryInterface<SharpDX.DXGI.Device1>()) dxgi.MaximumFrameLatency=1;
        using(var factory=swap.GetParent<Factory>()) factory.MakeWindowAssociation(hwnd,WindowAssociationFlags.IgnoreAltEnter);
        constants=new Buffer(device,32,ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);
        changed=new ComputeShader(device,Shader("Changed"));
        changeFlag=new Buffer(device,4,ResourceUsage.Default,BindFlags.UnorderedAccess,CpuAccessFlags.None,ResourceOptionFlags.BufferStructured,4);
        changeView=new UnorderedAccessView(device,changeFlag);
        changeReadback=new Buffer(device,4,ResourceUsage.Staging,BindFlags.None,CpuAccessFlags.Read,ResourceOptionFlags.None,0);
        var sampling=SamplerStateDescription.Default();
        sampling.Filter=Filter.MinMagMipLinear; sampling.AddressU=sampling.AddressV=sampling.AddressW=TextureAddressMode.Clamp;
        sampler=new SamplerState(device,sampling);
        vertex=new VertexShader(device,Shader("Fullscreen"));
        foreach(string name in new [] {"Original","Sharp","Easu","Rcas","Motion","Interpolate","OpticalField","NgxVectors"})
            shaders.Add(name,new PixelShader(device,Shader(name)));
        InitHdrShaders();
        context.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
        context.VertexShader.Set(vertex);
        context.PixelShader.SetSampler(0,sampler);
        context.PixelShader.SetConstantBuffer(0,constants);
        Resize(window);
    }
    static byte[] Shader(string name)
    {
        using(var data=Assembly.GetExecutingAssembly().GetManifestResourceStream(name+".cso"))
        using(var stream=new MemoryStream()) { data.CopyTo(stream); return stream.ToArray(); }
    }
    public void Resize(Size window)
    {
        if(window.Width<=0 || window.Height<=0) return;
        var target=MonitorFromWindow(hwnd,2);
        if(window==screenSize && target==monitor) return;
        if(target!=monitor && display!=null)
        {
            bool next=ChooseHdr();
            if(next!=HdrEnabled) { ReleaseSource(); srcSize=Size.Empty; HasPrevious=false; HdrEnabled=next; }
        }
        monitor=target;
        if(display!=null)
        {
            context.OutputMerger.ResetTargets(); display.Dispose(); backBuffer.Dispose();
            swap.ResizeBuffers(2,window.Width,window.Height,DisplayHdr.OutputFormat(HdrEnabled),SwapChainFlags.None);
        }
        outputInfo=DisplayHdr.Configure(swap,HdrEnabled);
        backBuffer=Texture2D.FromSwapChain<Texture2D>(swap,0);
        display=new RenderTargetView(Device,backBuffer);
        screenSize=window; SetViewport();
    }
    bool ChooseHdr()
    {
        if(hdrMode=="off") return false;
        if(pictureMode=="rtxvideo")
        {
            if(hdrMode=="hdr10") throw new InvalidOperationException("RTX Video SR processes SDR. Choose Auto or SDR, or select another picture mode for HDR10.");
            return false;
        }
        return hdrMode=="hdr10" || DisplayHdr.Probe(hwnd).WindowsHdrActive;
    }
    void SetViewport()
    {
        if(srcSize.IsEmpty || screenSize.IsEmpty) return;
        double ratio=Math.Min((double)screenSize.Width/srcSize.Width,(double)screenSize.Height/srcSize.Height);
        var next=new Size(Math.Max(1,(int)Math.Round(srcSize.Width*ratio)),Math.Max(1,(int)Math.Round(srcSize.Height*ratio)));
        viewport=new Rectangle((screenSize.Width-next.Width)/2,(screenSize.Height-next.Height)/2,next.Width,next.Height);
        if(!renderLimit.IsEmpty)
        {
            double limit=Math.Min(ratio,Math.Min((double)renderLimit.Width/srcSize.Width,(double)renderLimit.Height/srcSize.Height));
            next=new Size(Math.Max(1,(int)Math.Round(srcSize.Width*limit)),Math.Max(1,(int)Math.Round(srcSize.Height*limit)));
        }
        if(next==outSize) return;
        if(scaled!=null) { scaled.Dispose(); scaled=null; }
        if(raster!=null) { raster.Dispose(); raster=null; }
        outSize=next;
    }
    bool PixelsChanged()
    {
        var info=new FrameInfo { SourceX=srcSize.Width,SourceY=srcSize.Height };
        context.UpdateSubresource(ref info,constants);
        context.ClearUnorderedAccessView(changeView,new RawInt4(0,0,0,0));
        var compute=context.ComputeShader;
        compute.Set(changed); compute.SetConstantBuffer(0,constants);
        compute.SetShaderResource(0,incoming.Read); compute.SetShaderResource(1,curr.Read);
        compute.SetUnorderedAccessView(0,changeView);
        context.Dispatch((srcSize.Width+15)/16,(srcSize.Height+15)/16,1);
        compute.SetShaderResource(0,null); compute.SetShaderResource(1,null); compute.SetUnorderedAccessView(0,null);
        context.CopyResource(changeFlag,changeReadback);
        var data=context.MapSubresource(changeReadback,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
        try { return Marshal.ReadInt32(data.DataPointer)!=0; }
        finally { context.UnmapSubresource(changeReadback,0); }
    }
    public bool Receive(Texture2D frame,Rectangle crop,bool deduplicate)
    {
        bool newSize=srcSize!=crop.Size;
        if(newSize)
        {
            ReleaseSource(); srcSize=crop.Size;
            var format=frame.Description.Format;
            curr=new GpuImage(Device,crop.Width,crop.Height,format,false);
            prev=new GpuImage(Device,crop.Width,crop.Height,format,false);
            incoming=new GpuImage(Device,crop.Width,crop.Height,format,false);
            SetViewport(); HasPrevious=false;
        }
        var region=new ResourceRegion(crop.Left,crop.Top,0,crop.Right,crop.Bottom,1);
        context.CopySubresourceRegion(frame,0,region,incoming.Texture,0);
        if(!newSize && deduplicate && !PixelsChanged()) { Duplicates++; return false; }
        var temp=prev; prev=curr; curr=incoming; incoming=temp;
        HasPrevious=!newSize;
        Captured++; motionReady=false;
        return true;
    }
    void ReleaseSource()
    {
        ReleaseNvidia();
        ReleaseHdr();
        foreach(var image in new [] {curr,prev,incoming,motion,mixed}) if(image!=null) image.Dispose();
        curr=prev=incoming=motion=mixed=null;
    }
    void Draw(string name, RenderTargetView target, Rectangle area, ShaderResourceView input, float phase,
              ShaderResourceView previous=null, ShaderResourceView flow=null)
    {
        var info=new FrameInfo { SourceX=srcSize.Width,SourceY=srcSize.Height,OutputX=outSize.Width,OutputY=outSize.Height,
                                 Phase=phase,OriginX=area.X,OriginY=area.Y };
        context.UpdateSubresource(ref info,constants);
        context.OutputMerger.SetRenderTargets(target);
        context.Rasterizer.SetViewport(area.X,area.Y,area.Width,area.Height);
        context.PixelShader.SetShaderResource(0,input);
        context.PixelShader.SetShaderResource(1,previous);
        context.PixelShader.SetShaderResource(2,flow);
        context.PixelShader.Set(shaders[name]);
        context.Draw(3,0);
        context.PixelShader.SetShaderResources(0,unbind);
        context.OutputMerger.ResetTargets();
    }
    public int Show(string mode,float phase,bool samplePixel,bool finishPrevious=false)
    {
        if(curr==null) return 0;
        var source=finishPrevious ? prev.Read:curr.Read;
        bool vendor=mode=="dlss" || mode=="dlaa" || mode=="rtxvideo";
        context.ClearRenderTargetView(display,new RawColor4(0,0,0,1));
        if(HdrEnabled && !vendor) return ShowHdr(mode,phase,samplePixel,finishPrevious);
        if(vendor) source=EnhancedFrame(mode,finishPrevious);
        if(phase<1 && HasPrevious)
        {
            if(HdrEnabled) PrepareHdrMotion(); else PrepareMotion();
            if(vendor && havePriorEnhanced)
            {
                DrawFinal("Interpolate",neuralCurrent.Read,phase,neuralPrevious.Read,motion.Read,HdrEnabled);
                Generated++;
                return Present(samplePixel);
            }
            if(!vendor)
            {
                if(mixed==null) mixed=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.B8G8R8A8_UNorm,true);
                Draw("Interpolate",mixed.Write,new Rectangle(System.Drawing.Point.Empty,srcSize),curr.Read,phase,prev.Read,motion.Read);
                source=mixed.Read; Generated++;
            }
        }
        if(mode=="fsr")
        {
            if(scaled==null) scaled=new GpuImage(Device,outSize.Width,outSize.Height,Format.B8G8R8A8_UNorm,true);
            Draw("Easu",scaled.Write,new Rectangle(System.Drawing.Point.Empty,outSize),source,phase);
            DrawFinal("Rcas",scaled.Read,phase);
        }
        else DrawFinal(mode=="sharp" ? "Sharp":"Original",source,phase,null,null,HdrEnabled && vendor);
        return Present(samplePixel);
    }
    void DrawFinal(string shader,ShaderResourceView source,float phase,ShaderResourceView previous=null,ShaderResourceView flow=null,bool linear2020=false)
    {
        if(!linear2020 && outSize==viewport.Size) { Draw(shader,display,viewport,source,phase,previous,flow); return; }
        var format=linear2020 ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
        if(raster==null || raster.Texture.Description.Format!=format)
        {
            if(raster!=null) raster.Dispose();
            raster=new GpuImage(Device,outSize.Width,outSize.Height,format,true);
        }
        Draw(shader,raster.Write,new Rectangle(System.Drawing.Point.Empty,outSize),source,phase,previous,flow);
        Draw(linear2020 ? "HdrLinear2020ToPq" : "Original",display,viewport,raster.Read,1);
    }
    int Present(bool samplePixel)
    {
        if(VerifyNextOutput)
        {
            SampleOutput();
            VerifyNextOutput=false;
        }
        int pixel=samplePixel ? ReadCenterPixel() : 0;
        swap.Present(0,PresentFlags.None); Presented++;
        return pixel;
    }
    void SampleOutput()
    {
        // Diagnostic only. Sample the inner picture to exclude title bars and letterboxing.
        var desc=backBuffer.Description;
        desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read;
        desc.OptionFlags=ResourceOptionFlags.None;
        using(var readback=new Texture2D(Device,desc))
        {
            context.CopyResource(backBuffer,readback);
            var data=context.MapSubresource(readback,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
            try
            {
                for(int row=1;row<=18;row++)
                {
                    int y=viewport.Top+(int)(viewport.Height*(.1+.8*row/19.0));
                    for(int column=1;column<=32;column++)
                    {
                        int x=viewport.Left+(int)(viewport.Width*(.1+.8*column/33.0));
                        int bgra=Marshal.ReadInt32(data.DataPointer,y*data.RowPitch+x*4);
                        int luma=((bgra & 255)*19 + ((bgra>>8) & 255)*183 + ((bgra>>16) & 255)*54)/256;
                        if(HdrEnabled) luma=(((bgra&1023)*67+((bgra>>10)&1023)*174+((bgra>>20)&1023)*15)/256)*255/1023;
                        SampleMinLuma=Math.Min(luma,SampleMinLuma);
                        SampleMaxLuma=Math.Max(luma,SampleMaxLuma);
                    }
                }
                SampledFrames++;
            }
            finally { context.UnmapSubresource(readback,0); }
        }
    }
    int ReadCenterPixel()
    {
        var desc=backBuffer.Description;
        desc.Width=desc.Height=1; desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging;
        desc.CpuAccessFlags=CpuAccessFlags.Read; desc.OptionFlags=ResourceOptionFlags.None;
        int x=screenSize.Width/2, y=screenSize.Height/2;
        using(var readback=new Texture2D(Device,desc))
        {
            context.CopySubresourceRegion(backBuffer,0,new ResourceRegion(x,y,0,x+1,y+1,1),readback,0);
            var data=context.MapSubresource(readback,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
            try { return Marshal.ReadInt32(data.DataPointer); }
            finally { context.UnmapSubresource(readback,0); }
        }
    }
    public void Dispose()
    {
        context.ClearState(); ReleaseSource();
        if(scaled!=null) scaled.Dispose(); display.Dispose(); backBuffer.Dispose();
        if(raster!=null) raster.Dispose();
        foreach(var shader in shaders.Values) shader.Dispose();
        changeView.Dispose(); changeFlag.Dispose(); changeReadback.Dispose(); changed.Dispose();
        vertex.Dispose(); sampler.Dispose(); constants.Dispose(); swap.Dispose(); context.Dispose(); Device.Dispose();
    }
}
