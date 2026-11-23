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
    GpuImage neuralCurrent, neuralPrevious, rgba, vectors, depth, strokeMask, ngxOutput;
    Size neuralSize;
    string neuralMode;
    int evaluatedFrame = -1;
    bool havePriorEnhanced, resetHistory;
    float frameMilliseconds = 16.67f;
    public int VendorEvaluations { get; private set; }
    public int HardwareMotionPairs { get; private set; }
    public string ActiveEffect { get { return IsVendor(EffectivePictureMode) ? neuralMode ?? "" : ""; } }

    public void SetFrameTiming(double delta,bool continuous)
    {
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
        if(!triedOptical)
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
        // With no enlargement the NGX native-resolution feature is the appropriate configuration.
        bool native=mode=="dlaa" || (mode=="dlss" && (outSize.Width<=srcSize.Width || outSize.Height<=srcSize.Height));
        string effective=native ? "dlaa" : mode;
        var target=native ? srcSize : outSize;
        if(neuralMode!=effective || neuralSize!=target)
        {
            ReleaseEffects();
            neuralSize=target;
            neuralMode=effective;
            var format=HdrEnabled ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm;
            neuralCurrent=new GpuImage(Device,target.Width,target.Height,format,true,true);
            neuralPrevious=new GpuImage(Device,target.Width,target.Height,format,true,true);
            rgba=new GpuImage(Device,srcSize.Width,srcSize.Height,format,true);
            if(mode=="rtxvideo") video=new RtxVideo(Device.NativePointer);
            else
            {
                strokeMask=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.R8_UNorm,true);
                ngxOutput=new GpuImage(Device,target.Width,target.Height,format,true,true);
                vectors=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.R16G16_Float,true);
                depth=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.R32_Float,true);
                // Remote Play supplies no scene geometry. Model the finished video as one plane.
                context.ClearRenderTargetView(depth.Write,new RawColor4(0.5f,0,0,1));
                ngx=new NgxVideo(Device,srcSize.Width,srcSize.Height,target.Width,target.Height,native,HdrEnabled);
            }
            RestoreDrawState();
        }
        if(Captured!=evaluatedFrame)
        {
            havePriorEnhanced=HasPrevious && evaluatedFrame==Captured-1 && !resetHistory;
            var temp=neuralPrevious; neuralPrevious=neuralCurrent; neuralCurrent=temp;
            var area=new Rectangle(Point.Empty,srcSize);
            Draw(HdrEnabled ? "HdrToLinear2020" : "Original",rgba.Write,area,curr.Read,1);
            if(ngx!=null)
            {
                if(havePriorEnhanced)
                {
                    if(HdrEnabled) PrepareHdrMotion(); else PrepareMotion();
                }
                Draw("NgxVectors",vectors.Write,area,curr.Read,havePriorEnhanced ? 1 : 0,
                     havePriorEnhanced ? prev.Read : curr.Read,havePriorEnhanced ? motion.Read : null,auxiliary:strokeMask.Write);
                // The console's camera jitter is unknown. Do not invent offsets for captured pixels.
                ngx.Evaluate(context,rgba.Texture,vectors.Texture,depth.Texture,ngxOutput.Texture,0,0,frameMilliseconds,!havePriorEnhanced);
            }
            else video.Evaluate(context.NativePointer,rgba.Texture.NativePointer,neuralCurrent.Texture.NativePointer,
                                srcSize.Width,srcSize.Height,target.Width,target.Height);
            RestoreDrawState();
            if(ngx!=null) Draw("PreserveNgxStrokes",neuralCurrent.Write,new Rectangle(Point.Empty,neuralSize),
                ngxOutput.Read,HdrEnabled ? 1 : 0,rgba.Read,strokeMask.Read);
            VendorEvaluations++;
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
    void ReleaseEffects()
    {
        if(ngx!=null) { ngx.Dispose(); ngx=null; }
        if(video!=null) { video.Dispose(); video=null; }
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
