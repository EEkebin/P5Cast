using System;
using System.Drawing;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

internal sealed partial class Renderer
{
    FsrFrameGeneration amdFrames;
    FrucFrames frucFrames;
    GpuImage sdkFrameColor,amdFrameMotion,amdFrameDepth,sdkMidpoint,frucPqMidpoint;
    Size sdkFrameSize;
    int sdkEvaluated=-1;
    bool sdkNeedsReset,sdkHasMidpoint,frucProducedFrame;
    internal const string FrucBackendReason="NVIDIA FRUC requires Direct3D 11 or Direct3D 12.";
    internal static bool SupportsFrucBackend(string backend) { return backend=="d3d11" || backend=="d3d12"; }
    public string FrameGenerationProvider {get;private set;}
    public string FrameGenerationNotice {get;private set;}
    public string ActiveFrameGenerationVersion {get {return amdFrames==null?"":amdFrames.ActiveVersion;}}
    public string ActiveFrameGenerationProvider {get {return frucProducedFrame?"fruc":amdFrames!=null?"amd":"";}}
    public int FrucEvaluations {get;private set;}
    public int FrucMidpoints {get;private set;}

    internal static void ValidateFrameGeneration(string provider,int multiplier)
    {
        if(provider==null || provider=="p5cast") return;
        if(provider!="amd" && provider!="fruc") throw new ArgumentException("Choose p5cast, amd, or fruc for frame generation.");
        if(multiplier!=2) throw new ArgumentException((provider=="fruc"?"NVIDIA FRUC":"AMD frame generation")+" requires a 2× multiplier.");
    }

    public void SetFrameGenerationProvider(string provider)
    {
        if(FrameGenerationProvider==provider) return;
        ValidateFrameGeneration(provider,2);
        if(provider==null) throw new ArgumentNullException("provider");
        ReleaseFrameGeneration();
        FrameGenerationProvider=provider;
    }
    void ReleaseFrameGeneration()
    {
        if(amdFrames!=null) {amdFrames.Dispose();amdFrames=null;}
        if(frucFrames!=null) {frucFrames.Dispose();frucFrames=null;}
        foreach(var image in new[]{sdkFrameColor,amdFrameMotion,amdFrameDepth,sdkMidpoint,frucPqMidpoint}) if(image!=null) image.Dispose();
        sdkFrameColor=amdFrameMotion=amdFrameDepth=sdkMidpoint=frucPqMidpoint=null;
        sdkFrameSize=Size.Empty;sdkEvaluated=-1;sdkNeedsReset=true;sdkHasMidpoint=false;frucProducedFrame=false;FrameGenerationNotice="";
    }
    void PrepareSdkFrame(bool vendor)
    {
        if(!string.IsNullOrEmpty(FrameGenerationNotice) || sdkEvaluated==Captured) return;
        bool fruc=FrameGenerationProvider=="fruc";
        try {
            if(fruc && !SupportsFrucBackend(Backend)) throw new InvalidOperationException(FrucBackendReason);
            if(!fruc && Backend!="d3d12" && Backend!="vulkan") throw new InvalidOperationException("AMD frame generation requires Direct3D 12 or Vulkan.");
            Size size=vendor?neuralSize:srcSize;
            bool continuous=HasPrevious && sdkEvaluated==Captured-1 && !sdkNeedsReset;
            if(vendor && !havePriorEnhanced) continuous=false;
            if(size!=sdkFrameSize) {
                ReleaseFrameGeneration();
                continuous=false;
                var format=HdrEnabled?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm;
                sdkFrameColor=new GpuImage(Device,size.Width,size.Height,fruc?Format.R8G8B8A8_UNorm:format,true);
                sdkMidpoint=new GpuImage(Device,size.Width,size.Height,format,true,!fruc);
                if(fruc) {
                    if(HdrEnabled) frucPqMidpoint=new GpuImage(Device,size.Width,size.Height,Format.R8G8B8A8_UNorm,true);
                    frucFrames=new FrucFrames(Device,size.Width,size.Height);
                }
                else {
                    amdFrameMotion=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.R16G16_Float,true);
                    amdFrameDepth=new GpuImage(Device,srcSize.Width,srcSize.Height,Format.R32_Float,true);
                    context.ClearRenderTargetView(amdFrameDepth.Write,new RawColor4(.5f,0,0,0));
                    amdFrames=new FsrFrameGeneration(Device,srcSize.Width,srcSize.Height,size.Width,size.Height,HdrEnabled,Backend);
                }
                sdkFrameSize=size;
            }
            // Finish a vendor upscaler before interpolation; spatial modes process the
            // generated source frame with their regular presentation path.
            var area=new Rectangle(Point.Empty,size);
            var current=vendor?neuralCurrent.Read:curr.Read;
            string pack="Original";
            if(HdrEnabled && fruc) pack=vendor?"HdrLinear2020ToPq":"HdrToPq";
            else if(HdrEnabled && vendor) pack="HdrLinear2020ToScRgb";
            Draw(pack,sdkFrameColor.Write,area,current,1);
            if(fruc) {
                // Processing the first frame after a gap primes FRUC with the new
                // baseline. Its midpoint spanning the gap is deliberately suppressed.
                sdkHasMidpoint=frucFrames.Evaluate(sdkFrameColor.Texture,(frucPqMidpoint ?? sdkMidpoint).Texture) && continuous;
                FrucEvaluations++;
                if(sdkHasMidpoint) {
                    if(HdrEnabled) Draw("FrucHdrMidpoint",sdkMidpoint.Write,area,frucPqMidpoint.Read,
                        vendor?1:0,current,vendor?neuralPrevious.Read:prev.Read);
                    FrucMidpoints++;frucProducedFrame=true;
                }
            } else {
                if(continuous) {
                    if(HdrEnabled) PrepareHdrMotion(); else PrepareMotion();
                }
                // The PS5 stream has no depth/camera buffers. Use source pixel motion
                // and the same single depth plane as the captured-video upscalers.
                Draw("NgxVectors",amdFrameMotion.Write,new Rectangle(Point.Empty,srcSize),curr.Read,continuous?1:0,
                    continuous?prev.Read:curr.Read,continuous?motion.Read:null);
                amdFrames.Evaluate(context,sdkFrameColor.Texture,amdFrameMotion.Texture,amdFrameDepth.Texture,
                    sdkMidpoint.Texture,frameMilliseconds,!continuous);
                sdkHasMidpoint=continuous;
            }
            sdkEvaluated=Captured;sdkNeedsReset=false;
            RestoreDrawState();
        } catch(Exception e) {
            if(!IsEffectFailure(e)) throw;
            ReleaseFrameGeneration();RestoreDrawState();
            FrameGenerationNotice=(fruc?"NVIDIA FRUC":"AMD frame generation")+" paused. "+e.Message;
        }
    }
}
