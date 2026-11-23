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
        public float SourceX,SourceY,OutputX,OutputY,Phase,OriginX,OriginY,WipeHorizontal,TargetX,TargetY,WipeGripState,Pad;
    }
    public Device Device { get; private set; }
    readonly DeviceContext context;
    readonly SwapChain swap;
    Presentation presentation;
    public string Backend { get; private set; }
    readonly Buffer constants;
    readonly ComputeShader changed;
    readonly Buffer changeFlag,changeReadback;
    readonly UnorderedAccessView changeView;
    readonly SamplerState sampler;
    readonly RasterizerState scissor;
    readonly BlendState handleBlend;
    readonly VertexShader vertex;
    readonly Dictionary<string,PixelShader> shaders=new Dictionary<string,PixelShader>();
    readonly ShaderResourceView[] unbind=new ShaderResourceView[3];
    Texture2D backBuffer;
    RenderTargetView display;
    GpuImage curr,prev,incoming,motion,mixed,scaled;
    Size srcSize,outSize,screenSize;
    Size pendingSize;
    long resizeStartedAt,sizeChangedAt;
    Rectangle viewport,originalViewport;
    bool sideBySide,wipe,wipeHorizontal;
    float wipePosition=.5f;
    int wipeGripState;
    float wipeGripScale=1;
    public Rectangle PictureViewport { get; private set; }
    readonly IntPtr hwnd;
    readonly bool clearOnDispose;
    Size renderLimit;
    string hdrMode;
    string pictureMode,failedMode;
    DisplayHdr outputInfo;
    IntPtr monitor;
    GpuImage raster;
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    public bool HdrEnabled { get; private set; }
    public Size RenderSize { get { return outSize; } }
    public Size InputSize { get { return srcSize; } }
    public string EffectivePictureMode { get; private set; }
    public string EffectNotice { get; private set; }
    public Size OutputSize { get { return screenSize; } }
    public string OutputStatus { get { return outputInfo.Summary+" · "+(HdrEnabled ? "HDR10 output" : "SDR output"); } }
    public bool PresenterHdr10 { get { return presentation!=null ? presentation.Hdr10 : outputInfo.Enabled; } }
    public string OutputNotice { get; private set; }
    public string DisplayName { get { return outputInfo.DeviceName; } }
    public double DisplayRefreshHz { get { return outputInfo.RefreshHz; } }
    public int DisplayBitsPerColor { get { return outputInfo.BitsPerColor; } }
    public void InvalidateDisplay() { monitor=IntPtr.Zero; }
    bool motionReady,suppressNextPair;
    public int Captured { get; private set; }
    public int Duplicates { get; private set; }
    public int Presented { get; private set; }
    public int Repaints { get; private set; }
    public int Generated { get; private set; }
    public bool HasPrevious { get; private set; }
    public int SampleMinLuma { get; private set; }
    public int SampleMaxLuma { get; private set; }
    public int SampledFrames { get; private set; }
    public bool VerifyNextOutput;
    public Renderer(IntPtr hwnd, Size window,Size limit,string hdr,string picture,bool clearOnDispose=false,string backend="auto")
    {
        this.hwnd=hwnd; this.clearOnDispose=clearOnDispose; renderLimit=limit; hdrMode=hdr; pictureMode=picture;
        EffectivePictureMode="original"; EffectNotice="";
        Backend=Presentation.Resolve(backend);
        HdrEnabled=ChooseHdr(hdrMode);
        try {
            if(Backend=="d3d11") {
                var desc=new SwapChainDescription {
                    BufferCount=2, Flags=SwapChainFlags.None, IsWindowed=true, OutputHandle=hwnd,
                    ModeDescription=new ModeDescription(window.Width,window.Height,new Rational(0,1),DisplayHdr.OutputFormat(HdrEnabled)),
                    Usage=Usage.RenderTargetOutput,SampleDescription=new SampleDescription(1,0),SwapEffect=SwapEffect.FlipDiscard
                };
                Device device; SwapChain chain;
                Device.CreateWithSwapChain(DriverType.Hardware,DeviceCreationFlags.BgraSupport,desc,out device,out chain);
                Device=device; swap=chain;
                using(var factory=swap.GetParent<Factory>()) factory.MakeWindowAssociation(hwnd,WindowAssociationFlags.IgnoreAltEnter);
            } else {
                Device=new Device(DriverType.Hardware,DeviceCreationFlags.BgraSupport);
            }
            context=Device.ImmediateContext;
            SampleMinLuma=255;
            using(var dxgi=Device.QueryInterface<SharpDX.DXGI.Device1>()) dxgi.MaximumFrameLatency=1;
            constants=new Buffer(Device,48,ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);
            changed=new ComputeShader(Device,Shader("Changed"));
            changeFlag=new Buffer(Device,4,ResourceUsage.Default,BindFlags.UnorderedAccess,CpuAccessFlags.None,ResourceOptionFlags.BufferStructured,4);
            changeView=new UnorderedAccessView(Device,changeFlag);
            changeReadback=new Buffer(Device,4,ResourceUsage.Staging,BindFlags.None,CpuAccessFlags.Read,ResourceOptionFlags.None,0);
            var sampling=SamplerStateDescription.Default();
            sampling.Filter=Filter.MinMagMipLinear; sampling.AddressU=sampling.AddressV=sampling.AddressW=TextureAddressMode.Clamp;
            sampler=new SamplerState(Device,sampling);
            var clipped=RasterizerStateDescription.Default();
            clipped.IsScissorEnabled=true;
            scissor=new RasterizerState(Device,clipped);
            var blend=BlendStateDescription.Default();
            var targetBlend=blend.RenderTarget[0];
            targetBlend.IsBlendEnabled=true;
            targetBlend.SourceBlend=BlendOption.SourceAlpha;
            targetBlend.DestinationBlend=BlendOption.InverseSourceAlpha;
            targetBlend.SourceAlphaBlend=BlendOption.One;
            targetBlend.DestinationAlphaBlend=BlendOption.InverseSourceAlpha;
            blend.RenderTarget[0]=targetBlend;
            handleBlend=new BlendState(Device,blend);
            vertex=new VertexShader(Device,Shader("Fullscreen"));
            foreach(string name in new [] {"Original","Sharp","Easu","Rcas","Motion","Interpolate","OpticalField","NgxVectors","PreserveNgxStrokes","WipeDivider","WipeHandle","InspectionLoupe"})
                shaders.Add(name,new PixelShader(Device,Shader(name)));
            InitHdrShaders();
            context.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
            context.VertexShader.Set(vertex);
            context.PixelShader.SetSampler(0,sampler);
            context.PixelShader.SetConstantBuffer(0,constants);
            Resize(window);
        } catch { Dispose(); throw; }
    }
    static byte[] Shader(string name)
    {
        using(var data=Assembly.GetExecutingAssembly().GetManifestResourceStream(name+".cso"))
        using(var stream=new MemoryStream()) { data.CopyTo(stream); return stream.ToArray(); }
    }
    public void Resize(Size window) { UpdateWindowSize(window,false); }
    public bool UpdateWindowSize(Size window,bool coalesce)
    {
        if(window.Width<=0 || window.Height<=0) return false;
        var target=MonitorFromWindow(hwnd,2);
        if(window==screenSize && target==monitor) { pendingSize=Size.Empty; return false; }
        bool displayChanged=target!=monitor;
        if(coalesce && !displayChanged && display!=null) {
            // WPF can produce several child sizes during one chrome/layout change.
            // Briefly coalesce intermediate sizes, but keep continuous dragging responsive.
            long now=System.Diagnostics.Stopwatch.GetTimestamp();
            if(pendingSize.IsEmpty) resizeStartedAt=now;
            if(window!=pendingSize) { pendingSize=window; sizeChangedAt=now; }
            long frequency=System.Diagnostics.Stopwatch.Frequency;
            if(now-sizeChangedAt<frequency/60 && now-resizeStartedAt<frequency/15) return false;
        }
        pendingSize=Size.Empty;
        // Moving an active session onto an SDR-only display keeps the stream running. Switch back
        // to PQ on return to an HDR display when HDR10 was explicitly selected.
        string request=displayChanged && display!=null && hdrMode=="hdr10" ? "auto" : hdrMode;
        bool next=displayChanged && display!=null ? ChooseHdr(request) : HdrEnabled;
        ConfigureDisplay(window,next,display!=null,request=="auto",displayChanged);
        monitor=target;
        screenSize=window; SetViewport();
        return true;
    }
    void ConfigureDisplay(Size window,bool hdr,bool resize,bool allowSdr,bool displayChanged)
    {
        if(swap!=null) {
            if(display!=null) { context.OutputMerger.ResetTargets(); display.Dispose(); display=null; }
            if(backBuffer!=null) { backBuffer.Dispose(); backBuffer=null; }
            if(resize) swap.ResizeBuffers(2,window.Width,window.Height,DisplayHdr.OutputFormat(hdr),SwapChainFlags.None);
            // Reapply the swap-chain color space without walking the display topology
            // again on a size-only change. Display notifications invalidate the cache.
            outputInfo=DisplayHdr.Configure(swap,hdr,displayChanged || HdrEnabled!=hdr ? null : outputInfo);
            backBuffer=Texture2D.FromSwapChain<Texture2D>(swap,0);
            display=new RenderTargetView(Device,backBuffer);
        } else hdr=ConfigurePortableDisplay(window,hdr,allowSdr,displayChanged);
        if(HdrEnabled!=hdr)
        {
            // WGC and DirectVideo will deliver the corresponding input format on the next frame.
            ReleaseSource(); srcSize=Size.Empty; ResetFrameHistory(); HdrEnabled=hdr;
        }
    }
    bool ConfigurePortableDisplay(Size window,bool hdr,bool allowSdr,bool displayChanged)
    {
        // Keep the currently displayed image and working swap chain until the new
        // API surface, color space, processing texture and render view all exist.
        var info=displayChanged || outputInfo==null ? DisplayHdr.Probe(hwnd) : outputInfo;
        Presentation candidate=null;
        Texture2D target=null;
        RenderTargetView view=null;
        string notice=OutputNotice;
        try {
            if(presentation==null || presentation.Hdr10!=hdr || displayChanged) {
                notice=null;
                try { candidate=new Presentation(hwnd,Backend,window,hdr); }
                catch(InvalidOperationException error) {
                    if(!allowSdr || !hdr) throw;
                    hdr=false;
                    notice="Using SDR on this display. "+error.Message;
                    if(presentation==null || presentation.Hdr10 || displayChanged)
                        candidate=new Presentation(hwnd,Backend,window,false);
                }
            }
            var desc=new Texture2DDescription {
                Width=window.Width,Height=window.Height,Format=DisplayHdr.OutputFormat(hdr),
                MipLevels=1,ArraySize=1,SampleDescription=new SampleDescription(1,0),BindFlags=BindFlags.RenderTarget
            };
            target=new Texture2D(Device,desc);
            view=new RenderTargetView(Device,target);
        } catch {
            if(view!=null) view.Dispose();
            if(target!=null) target.Dispose();
            if(candidate!=null) candidate.Dispose();
            throw;
        }
        context.OutputMerger.ResetTargets();
        if(display!=null) display.Dispose();
        if(backBuffer!=null) backBuffer.Dispose();
        if(candidate!=null) {
            if(presentation!=null) presentation.Dispose();
            presentation=candidate;
        }
        backBuffer=target; display=view; outputInfo=info; OutputNotice=notice;
        return hdr;
    }
    public void SetOutput(Size limit,string hdr)
    {
        if(!limit.IsEmpty && (limit.Width<1 || limit.Height<1)) throw new InvalidOperationException("Choose a positive resolution or Native.");
        if(hdr!="auto" && hdr!="off" && hdr!="hdr10") throw new InvalidOperationException("Choose Auto, SDR or HDR10.");
        bool next=ChooseHdr(hdr);
        if(next!=HdrEnabled)
        {
            bool old=HdrEnabled;
            try { ConfigureDisplay(screenSize,next,true,hdr=="auto",false); }
            catch(Exception error)
            {
                if(swap!=null) ConfigureDisplay(screenSize,old,true,false,false);
                throw new InvalidOperationException("Could not switch the display color mode. "+error.Message,error);
            }
        }
        if(hdr=="off") OutputNotice=null;
        renderLimit=limit; hdrMode=hdr;
        Size before=outSize; SetViewport();
        if(outSize!=before) ResetFrameHistory();
    }
    bool ChooseHdr(string hdr)
    {
        if(hdr=="off") return false;
        if(Backend!="d3d11" && Backend!="d3d12" && Backend!="vulkan") {
            if(hdr=="hdr10") throw new InvalidOperationException(Backend+" presents SDR on Windows. Choose Direct3D 11, Direct3D 12 or Vulkan for HDR10.");
            return false;
        }
        if(pictureMode=="rtxvideo")
        {
            if(hdr=="hdr10") throw new InvalidOperationException("RTX Video SR processes SDR. Choose Auto or SDR, or select another picture mode for HDR10.");
            return false;
        }
        var output=DisplayHdr.Probe(hwnd);
        if(hdr=="hdr10" && !output.WindowsHdrActive)
            throw new InvalidOperationException("HDR10 needs an active Windows HDR display. Select SDR or enable HDR on "+(output.FriendlyName ?? output.DeviceName)+".");
        return output.WindowsHdrActive;
    }
    public void SetSideBySide(bool enabled)
    {
        if(sideBySide==enabled) return;
        sideBySide=enabled;
        SetViewport();
    }
    public void SetWipe(bool enabled,float position,bool horizontal,int gripState,float gripScale)
    {
        wipeGripState=gripState;
        wipeGripScale=gripScale;
        wipeHorizontal=horizontal;
        if(float.IsNaN(position) || float.IsInfinity(position)) throw new ArgumentOutOfRangeException("position");
        wipePosition=Math.Max(0,Math.Min(1,position));
        if(wipe==enabled) return;
        wipe=enabled;
        SetViewport();
    }
    void SetViewport()
    {
        if(srcSize.IsEmpty || screenSize.IsEmpty) return;
        double ratio=Math.Min((double)screenSize.Width/srcSize.Width,(double)screenSize.Height/srcSize.Height);
        var next=new Size(Math.Max(1,(int)Math.Round(srcSize.Width*ratio)),Math.Max(1,(int)Math.Round(srcSize.Height*ratio)));
        PictureViewport=new Rectangle((screenSize.Width-next.Width)/2,(screenSize.Height-next.Height)/2,next.Width,next.Height);
        viewport=PictureViewport;
        originalViewport=Rectangle.Empty;
        if(sideBySide && !wipe && screenSize.Width>=2)
        {
            // Keep the single-view processing resolution. Shrinking the effect target here
            // could change an upscaler's operating mode in the middle of the comparison.
            int half=screenSize.Width/2;
            double fit=Math.Min((double)half/srcSize.Width,(double)screenSize.Height/srcSize.Height);
            var pane=new Size(Math.Max(1,(int)Math.Round(srcSize.Width*fit)),Math.Max(1,(int)Math.Round(srcSize.Height*fit)));
            originalViewport=new Rectangle((half-pane.Width)/2,(screenSize.Height-pane.Height)/2,pane.Width,pane.Height);
            viewport=originalViewport;
            viewport.Offset(screenSize.Width-half,0);
        }
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
        bool newSize=srcSize!=crop.Size || curr==null || curr.Texture.Description.Format!=frame.Description.Format;
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
        HasPrevious=!newSize && !suppressNextPair;
        suppressNextPair=false;
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
              ShaderResourceView previous=null, ShaderResourceView flow=null,Rectangle? clip=null,RenderTargetView auxiliary=null)
    {
        var info=new FrameInfo { SourceX=srcSize.Width,SourceY=srcSize.Height,OutputX=outSize.Width,OutputY=outSize.Height,
                                 Phase=phase,WipeHorizontal=wipeHorizontal ? 1 : 0,OriginX=area.X,OriginY=area.Y,TargetX=area.Width,TargetY=area.Height,WipeGripState=wipeGripState };
        context.UpdateSubresource(ref info,constants);
        if(auxiliary==null) context.OutputMerger.SetRenderTargets(target);
        else context.OutputMerger.SetTargets(target,auxiliary);
        context.Rasterizer.SetViewport(area.X,area.Y,area.Width,area.Height);
        context.PixelShader.SetShaderResource(0,input);
        context.PixelShader.SetShaderResource(1,previous);
        context.PixelShader.SetShaderResource(2,flow);
        context.PixelShader.Set(shaders[name]);
        if(clip.HasValue) {
            var rect=clip.Value;
            context.Rasterizer.SetScissorRectangle(rect.Left,rect.Top,rect.Right,rect.Bottom);
            context.Rasterizer.State=scissor;
        }
        context.Draw(3,0);
        if(clip.HasValue) context.Rasterizer.State=null;
        context.PixelShader.SetShaderResources(0,unbind);
        context.OutputMerger.ResetTargets();
    }
    static bool IsVendor(string mode) { return mode=="dlss" || mode=="dlaa" || mode=="rtxvideo"; }
    static bool IsEffectFailure(Exception error)
    {
        var dx=error as SharpDXException;
        // Device removal, reset, and hung-device errors cannot be repaired by switching shaders.
        if(dx!=null) return dx.HResult!=unchecked((int)0x887A0005) && dx.HResult!=unchecked((int)0x887A0006) && dx.HResult!=unchecked((int)0x887A0007);
        return error is InvalidOperationException || error is IOException || error is UnauthorizedAccessException ||
               error is DllNotFoundException || error is EntryPointNotFoundException || error is BadImageFormatException ||
               error is ArgumentException || error is OutOfMemoryException;
    }
    public void ResetFrameHistory()
    {
        HasPrevious=false; suppressNextPair=true; motionReady=false; hdrGuideFrame=-1;
        if(optical!=null) optical.ResetHistory();
        resetHistory=true; evaluatedFrame=-1; havePriorEnhanced=false;
    }
    public void SetPictureMode(string mode)
    {
        if(!IsVendor(mode) && mode!="original" && mode!="sharp" && mode!="fsr") throw new ArgumentException("Unknown picture mode.");
        if(mode=="rtxvideo" && HdrEnabled) throw new InvalidOperationException("RTX Video SR requires SDR output. Select SDR before switching picture mode.");
        if(mode==pictureMode && failedMode==null) return;
        // NGX initialization and shutdown are scoped to the device. Close the old feature before opening a different one.
        // Keep the captured textures and front buffer intact throughout the switch.
        ReleaseEffects();
        ResetFrameHistory();
        try
        {
            if(IsVendor(mode) && curr!=null) EnhancedFrame(mode,false);
        }
        catch(Exception error)
        {
            if(!IsEffectFailure(error)) throw;
            ReleaseEffects(); RestoreDrawState();
            throw new InvalidOperationException("Could not switch picture mode. "+error.Message,error);
        }
        pictureMode=mode; failedMode=null; EffectNotice="";
        EffectivePictureMode=neuralMode ?? mode;
    }
    public int Show(string mode,float phase,bool samplePixel,bool finishPrevious=false)
    {
        if(curr==null) return 0;
        int pixel=DrawFrame(mode,phase,samplePixel,finishPrevious);
        Presented++;
        return pixel;
    }
    public void Repaint(string mode)
    {
        if(curr==null) return;
        DrawFrame(mode,1,false,false);
        Repaints++;
    }
    int DrawFrame(string mode,float phase,bool samplePixel,bool finishPrevious)
    {
        finishPrevious=finishPrevious && HasPrevious;
        var source=finishPrevious ? prev.Read:curr.Read;
        if(mode==failedMode) mode="original";
        bool vendor=IsVendor(mode);
        if(vendor)
        {
            try { source=EnhancedFrame(mode,finishPrevious); }
            catch(Exception error)
            {
                if(!IsEffectFailure(error)) throw;
                failedMode=mode; mode="original"; vendor=false;
                EffectNotice="Showing Original. "+error.Message;
                ReleaseEffects(); RestoreDrawState();
            }
        }
        EffectivePictureMode=vendor ? neuralMode : mode;
        context.ClearRenderTargetView(display,new RawColor4(0,0,0,1));
        // The reference holds its last real frame between enhanced presentations.
        bool holdOriginal=(finishPrevious || (phase<1 && HasPrevious)) && (!vendor || havePriorEnhanced);
        if(!originalViewport.IsEmpty) DrawOriginal(originalViewport,holdOriginal);
        if(HdrEnabled && !vendor)
        {
            DrawHdr(mode,phase,finishPrevious,viewport);
            return Present(samplePixel,holdOriginal);
        }
        if(phase<1 && HasPrevious)
        {
            if(HdrEnabled) PrepareHdrMotion(); else PrepareMotion();
            if(vendor && havePriorEnhanced)
            {
                DrawFinal("Interpolate",neuralCurrent.Read,phase,viewport,neuralPrevious.Read,motion.Read,HdrEnabled);
                Generated++;
                return Present(samplePixel,holdOriginal);
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
            if(srcSize!=outSize)
            {
                if(scaled==null) scaled=new GpuImage(Device,outSize.Width,outSize.Height,Format.B8G8R8A8_UNorm,true);
                Draw(outSize.Width>srcSize.Width ? "Easu" : "Original",scaled.Write,new Rectangle(System.Drawing.Point.Empty,outSize),source,phase);
                source=scaled.Read;
            }
            DrawFinal("Rcas",source,phase,viewport);
        }
        else DrawFinal(mode=="sharp" ? "Sharp":"Original",source,phase,viewport,null,null,HdrEnabled && vendor);
        return Present(samplePixel,holdOriginal);
    }
    void DrawOriginal(Rectangle area,bool holdPrevious,Rectangle? clip=null)
    {
        if(HdrEnabled) DrawHdr("original",1,holdPrevious,area,clip);
        else DrawFinal("Original",holdPrevious ? prev.Read : curr.Read,1,area,clip:clip);
    }
    void DrawFinal(string shader,ShaderResourceView source,float phase,Rectangle area,ShaderResourceView previous=null,ShaderResourceView flow=null,bool linear2020=false,Rectangle? clip=null)
    {
        if(!linear2020 && outSize==area.Size) { Draw(shader,display,area,source,phase,previous,flow,clip); return; }
        var format=linear2020 ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
        if(raster==null || raster.Texture.Description.Format!=format)
        {
            if(raster!=null) raster.Dispose();
            raster=new GpuImage(Device,outSize.Width,outSize.Height,format,true);
        }
        Draw(shader,raster.Write,new Rectangle(System.Drawing.Point.Empty,outSize),source,phase,previous,flow);
        Draw(linear2020 ? "HdrLinear2020ToPq" : "Original",display,area,raster.Read,1,clip:clip);
    }
    int Present(bool samplePixel,bool holdOriginal)
    {
        bool inspectingDivider=wipe && wipeGripState==2 && loupeActive;
        Rectangle lensArea=Rectangle.Empty;
        if(wipe)
        {
            var layout=new WipeLayout(viewport,wipeHorizontal,wipePosition,wipeGripScale);
            // Keep both image layers at full scale and clip only the original layer.
            if(layout.Original.Width>0 && layout.Original.Height>0) DrawOriginal(viewport,holdOriginal,layout.Original);
            // Grab the two picture layers before drawing the handle under the pointer.
            if(inspectingDivider) lensArea=CaptureLoupe();
            float hdrUi=HdrEnabled ? 1 : 0;
            Draw("WipeDivider",display,layout.Rail,null,hdrUi);
            context.OutputMerger.SetBlendState(handleBlend);
            Draw("WipeHandle",display,layout.Handle,null,hdrUi);
            context.OutputMerger.SetBlendState(null);
        }
        if(loupeActive && !inspectingDivider) lensArea=CaptureLoupe();
        if(!lensArea.IsEmpty) DrawLoupe(lensArea);
        if(VerifyNextOutput)
        {
            SampleOutput();
            VerifyNextOutput=false;
        }
        int pixel=samplePixel ? ReadCenterPixel() : 0;
        if(presentation==null) swap.Present(0,PresentFlags.None);
        else presentation.Present(Device,context,backBuffer,screenSize);
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
        int x=viewport.Left+viewport.Width/2, y=viewport.Top+viewport.Height/2;
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
        if(clearOnDispose && swap!=null && display!=null && WindowsApi.IsWindow(hwnd)) {
            // The dashboard retains this HWND. DWM can keep its last flip-chain image
            // even after a GDI repaint, so retire both flip buffers with opaque black.
            try {
                for(int i=0;i<2;i++) {
                    context.ClearRenderTargetView(display,new RawColor4(0,0,0,1));
                    swap.Present(1,PresentFlags.None);
                }
                context.Flush(); DwmFlush();
            } catch(SharpDXException) { /* A removed device can no longer present. */ }
        }
        if(context!=null) context.ClearState();
        if(loupePixels!=null) loupePixels.Dispose();
        if(loupeLabels!=null) loupeLabels.Dispose();
        if(loupeConstants!=null) loupeConstants.Dispose();
        ReleaseSource();
        if(scaled!=null) scaled.Dispose();
        if(display!=null) display.Dispose();
        if(backBuffer!=null) backBuffer.Dispose();
        if(raster!=null) raster.Dispose();
        foreach(var shader in shaders.Values) shader.Dispose();
        foreach(IDisposable item in new IDisposable[]{changeView,changeFlag,changeReadback,changed,vertex,sampler,scissor,handleBlend,
                constants,swap,presentation,context,Device})
            if(item!=null) item.Dispose();
    }
}
