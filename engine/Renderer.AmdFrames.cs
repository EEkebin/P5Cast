using System;
using System.Drawing;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

internal sealed partial class Renderer
{
    FsrFrameGeneration amdFrames;
    GpuImage amdFrameColor,amdFrameMotion,amdFrameDepth,amdMidpoint;
    Size amdFrameSize;
    int amdEvaluated=-1;
    bool amdNeedsReset,amdHasMidpoint;
    public string FrameGenerationProvider {get;private set;}
    public bool AmdFrameGenerationEnabled {get {return FrameGenerationProvider=="amd";}}
    public string FrameGenerationNotice {get;private set;}
    public string ActiveFrameGenerationVersion {get {return amdFrames==null?"":amdFrames.ActiveVersion;}}

    public void SetFrameGenerationProvider(string provider)
    {
        if(provider!="p5cast" && provider!="amd") throw new ArgumentException("Unknown frame generation provider.");
        if(provider=="amd" && Backend!="d3d12" && Backend!="vulkan") throw new InvalidOperationException("AMD frame generation requires Direct3D 12 or Vulkan.");
        if(FrameGenerationProvider==provider) return;
        ReleaseAmdFrames();
        FrameGenerationProvider=provider;
    }
    void ReleaseAmdFrames()
    {
        if(amdFrames!=null) {amdFrames.Dispose();amdFrames=null;}
        foreach(var image in new[]{amdFrameColor,amdFrameMotion,amdFrameDepth,amdMidpoint}) if(image!=null) image.Dispose();
        amdFrameColor=amdFrameMotion=amdFrameDepth=amdMidpoint=null;
        amdFrameSize=Size.Empty;amdEvaluated=-1;amdNeedsReset=true;amdHasMidpoint=false;FrameGenerationNotice="";
    }
    void PrepareAmdFrame(bool vendor)
    {
        if(!string.IsNullOrEmpty(FrameGenerationNotice) || amdEvaluated==Captured) return;
        try {
            Size size=vendor?neuralSize:srcSize;
            if(size!=amdFrameSize) {
                ReleaseAmdFrames();
                var format=HdrEnabled?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm;
                amdFrameColor=new GpuImage(Device,size.Width,size.Height,format,true);
                amdMidpoint=new GpuImage(Device,size.Width,size.Height,format,true,true);
                amdFrameMotion=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.R16G16_Float,true);
                amdFrameDepth=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.R32_Float,true);
                context.ClearRenderTargetView(amdFrameDepth.Write,new RawColor4(.5f,0,0,0));
                amdFrames=new FsrFrameGeneration(Device,srcSize.Width,srcSize.Height,size.Width,size.Height,HdrEnabled,Backend);
                amdFrameSize=size;
            }
            bool continuous=HasPrevious && amdEvaluated==Captured-1 && !amdNeedsReset;
            if(continuous) {
                if(HdrEnabled) PrepareHdrMotion(); else PrepareMotion();
            }
            // The PS5 stream has no depth/camera buffers. Use source pixel motion
            // and the same single depth plane as the captured-video upscalers.
            Draw("NgxVectors",amdFrameMotion.Write,new Rectangle(Point.Empty,srcSize),curr.Read,continuous?1:0,
                continuous?prev.Read:curr.Read,continuous?motion.Read:null);
            // Finish a vendor upscaler before interpolation; spatial modes process the
            // generated source frame with their regular presentation path.
            Draw(vendor && HdrEnabled?"HdrLinear2020ToScRgb":"Original",amdFrameColor.Write,
                new Rectangle(Point.Empty,size),vendor?neuralCurrent.Read:curr.Read,1);
            amdFrames.Evaluate(context,amdFrameColor.Texture,amdFrameMotion.Texture,amdFrameDepth.Texture,
                amdMidpoint.Texture,frameMilliseconds,!continuous);
            amdEvaluated=Captured;amdNeedsReset=false;amdHasMidpoint=continuous;
            RestoreDrawState();
        } catch(Exception e) {
            if(!IsEffectFailure(e)) throw;
            ReleaseAmdFrames();RestoreDrawState();
            FrameGenerationNotice="AMD frame generation paused. "+e.Message;
        }
    }
}
