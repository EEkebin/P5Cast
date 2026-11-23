using System;
using System.Drawing;
using SharpDX.Direct3D;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using SharpDX.Direct3D11;

internal sealed partial class Renderer
{
    NvidiaOpticalFlow optical;
    bool triedOptical;
    RtxVideo video;
    NgxVideo ngx;
    FsrUpscaler temporalFsr;
    public string ActiveFsrVersion { get {return temporalFsr!=null && EffectivePictureMode=="fsr-temporal" ? temporalFsr.ActiveVersion : "";} }
    public int FsrEvaluations {get;private set;}
    GpuImage neuralCurrent, neuralPrevious, rgba, vectors, depth, strokeMask, ngxOutput;
    Size neuralSize;
    string neuralMode;
    string dlssQuality="auto",configuredQuality,configuredModel;
    string dlssModelHint="auto";
    public string DlssModelHint {get {return dlssModelHint;} }
    int evaluatedFrame = -1;
    bool havePriorEnhanced, resetHistory;
    float frameMilliseconds = 16.67f;
    public int VendorEvaluations { get; private set; }
    public int HardwareMotionPairs { get; private set; }
    public string ActiveEffect { get { return IsVendor(EffectivePictureMode) && EffectivePictureMode!="fsr-temporal" ? neuralMode ?? "" : ""; } }

    public string DlssQuality { get { return dlssQuality; } }
    public string ActiveDlssQuality { get { return ngx!=null && (ActiveEffect=="dlss" || ActiveEffect=="dlaa") ? ngx.ActiveQuality : ""; } }

    public void SetDlssModel(string key)
    {
        if(dlssModelHint==key) return;
        DlssModel.Apply(Device,key);
        dlssModelHint=key;
        if(failedMode=="dlss" || failedMode=="dlaa") {failedMode=null; EffectNotice="";}
    }
    public void SetDlssQuality(string key)
    {
        NgxVideo.QualityValue(key);
        if(dlssQuality==key) return;
        dlssQuality=key;
        if(failedMode=="dlss") { failedMode=null; EffectNotice=""; }
    }

    public void SetFrameTiming(double delta,bool continuous)
    {
        if(!continuous) amdNeedsReset=true;
        resetHistory=!continuous;
        if(!continuous && optical!=null) optical.ResetHistory();
        frameMilliseconds=continuous ? (float)Math.Max(1,Math.Min(120,delta)) : 16.67f;
    }
    void PrepareMotion(GpuImage currentGuide=null,GpuImage previousGuide=null)
    {
        if(motionReady) return;
        currentGuide=currentGuide ?? curr;
        previousGuide=previousGuide ?? prev;
        string reason;
        if(!triedOptical && Backend=="d3d11")
        {
            triedOptical=true;
            NvidiaOpticalFlow.TryCreate(Device,srcSize.Width,srcSize.Height,out optical,out reason);
        }
        bool hardware=optical!=null && optical.TryEstimate(currentGuide.Texture,previousGuide.Texture,out reason);
        if(!hardware && optical!=null) { optical.Dispose(); optical=null; }
        int grid=hardware ? optical.GridSize : 12;
        var field=new Rectangle(0,0,(srcSize.Width+grid-1)/grid,(srcSize.Height+grid-1)/grid);
        if(motion==null || motion.Texture.Description.Width!=field.Width || motion.Texture.Description.Height!=field.Height)
        {
            if(motion!=null) motion.Dispose();
            motion=new GpuImage(Device,field.Width,field.Height,Format.R16G16B16A16_Float,true);
        }
        if(hardware) context.PixelShader.SetShaderResource(3,optical.BackwardFlow);
        try { Draw(hardware ? "OpticalField" : "Motion",motion.Write,field,currentGuide.Read,1,previousGuide.Read); }
        finally { if(hardware) context.PixelShader.SetShaderResource(3,null); }
        if(hardware) HardwareMotionPairs++;
        motionReady=true;
    }

    ShaderResourceView EnhancedFrame(string mode,bool previous)
    {
        if(!HasVendorBridge(Backend,mode))
            throw new InvalidOperationException("The selected graphics API has no native bridge for this effect.");
        // With no enlargement the NGX native-resolution feature is the appropriate configuration.
        var target=ProcessingSize(mode);
        bool native=mode=="dlaa" || (mode=="dlss" && target==srcSize);
        string effective=native ? "dlaa" : mode;
        string quality=effective=="dlss" ? dlssQuality : "auto";
        string model=(effective=="dlss" || effective=="dlaa") ? dlssModelHint : "auto";
        if(neuralMode!=effective || neuralSize!=target || configuredQuality!=quality || configuredModel!=model)
        {
            // The input and GPU device are unchanged on a window resize. Keep their
            // initialized SDK runtime while rebuilding the size-dependent output.
            bool reuseRuntime=mode=="rtxvideo" ? video!=null : (mode=="dlss" || mode=="dlaa") && ngx!=null;
            ReleaseEffects(reuseRuntime);
            neuralSize=target;
            neuralMode=effective;
            configuredQuality=quality; configuredModel=model;
            var format=HdrEnabled ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm;
            neuralCurrent=new GpuImage(Device,target.Width,target.Height,format,true,true);
            neuralPrevious=new GpuImage(Device,target.Width,target.Height,format,true,true);
            rgba=new GpuImage(Device,srcSize.Width,srcSize.Height,format,true);
            if(mode=="rtxvideo") {
                if(video==null) video=new RtxVideo(Device.NativePointer);
            }
            else
            {
                strokeMask=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.R8_UNorm,true);
                ngxOutput=new GpuImage(Device,target.Width,target.Height,format,true,true);
                vectors=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.R16G16_Float,true);
                depth=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.R32_Float,true);
                // Remote Play supplies no scene geometry. Model the finished video as one plane.
                context.ClearRenderTargetView(depth.Write,new RawColor4(0.5f,0,0,1));
                if(mode=="fsr-temporal") temporalFsr=new FsrUpscaler(Device,srcSize.Width,srcSize.Height,target.Width,target.Height,HdrEnabled,Backend);
                else if(ngx==null) ngx=new NgxVideo(Device,srcSize.Width,srcSize.Height,target.Width,target.Height,native,HdrEnabled,quality);
                else ngx.Resize(context,target.Width,target.Height,native,HdrEnabled,quality);
            }
            RestoreDrawState();
        }
        if(Captured!=evaluatedFrame)
        {
            havePriorEnhanced=HasPrevious && evaluatedFrame==Captured-1 && !resetHistory;
            var temp=neuralPrevious; neuralPrevious=neuralCurrent; neuralCurrent=temp;
            var area=new Rectangle(Point.Empty,srcSize);
            Draw(HdrEnabled ? "HdrToLinear2020" : "Original",rgba.Write,area,curr.Read,1);
            if(ngx!=null || temporalFsr!=null)
            {
                if(havePriorEnhanced)
                {
                    if(HdrEnabled) PrepareHdrMotion(); else PrepareMotion();
                }
                Draw("NgxVectors",vectors.Write,area,curr.Read,havePriorEnhanced ? 1 : 0,
                     havePriorEnhanced ? prev.Read : curr.Read,havePriorEnhanced ? motion.Read : null,auxiliary:strokeMask.Write);
                // The console's camera jitter is unknown. Do not invent offsets for captured pixels.
                if(temporalFsr!=null)
                    temporalFsr.Evaluate(context,rgba.Texture,vectors.Texture,depth.Texture,ngxOutput.Texture,frameMilliseconds,!havePriorEnhanced);
                else ngx.Evaluate(context,rgba.Texture,vectors.Texture,depth.Texture,ngxOutput.Texture,0,0,frameMilliseconds,!havePriorEnhanced);
            }
            else video.Evaluate(context.NativePointer,rgba.Texture.NativePointer,neuralCurrent.Texture.NativePointer,
                                srcSize.Width,srcSize.Height,target.Width,target.Height);
            RestoreDrawState();
            if(ngx!=null || temporalFsr!=null) Draw("PreserveNgxStrokes",neuralCurrent.Write,new Rectangle(Point.Empty,neuralSize),
                ngxOutput.Read,HdrEnabled ? 1 : 0,rgba.Read,strokeMask.Read);
            if(temporalFsr==null) VendorEvaluations++; else FsrEvaluations++;
            evaluatedFrame=Captured;
            resetHistory=false;
        }
        if(previous) return havePriorEnhanced ? neuralPrevious.Read : neuralCurrent.Read;
        return neuralCurrent.Read;
    }
    void RestoreDrawState()
    {
        context.ClearState();
        context.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
        context.VertexShader.Set(vertex);
        context.PixelShader.SetSampler(0,sampler);
        context.PixelShader.SetConstantBuffer(0,constants);
    }
    void ReleaseEffects(bool keepRuntime=false)
    {
        if(temporalFsr!=null) {temporalFsr.Dispose(); temporalFsr=null;}
        if(!keepRuntime && ngx!=null) { ngx.Dispose(); ngx=null; }
        if(!keepRuntime && video!=null) { video.Dispose(); video=null; }
        foreach(var image in new[]{neuralCurrent,neuralPrevious,rgba,vectors,depth,strokeMask,ngxOutput}) if(image!=null) image.Dispose();
        neuralCurrent=neuralPrevious=rgba=vectors=depth=strokeMask=ngxOutput=null;
        havePriorEnhanced=false; evaluatedFrame=-1; neuralMode=null;
    }
    void ReleaseNvidia()
    {
        ReleaseEffects();
        if(optical!=null) { optical.Dispose(); optical=null; }
        triedOptical=false;
    }
}
