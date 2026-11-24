using System;
using System.Runtime.InteropServices;
using System.Text;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;

// Run in the standalone probe process or before playback. Vendor SDK initialization can use process-wide state.
internal sealed class GpuCapabilities
{
    const int W=640, H=360;
    const string FrameGenerationReason="NVIDIA DLSS Frame Generation is not integrated. The player offers video-based interpolation.";
    public string Adapter { get; private set; }
    public bool Dlss { get { return dlssReason==null; } }
    public bool Dlaa { get { return dlaaReason==null; } }
    public bool RtxVideo { get { return rtxReason==null; } }
    public bool OpticalFlow { get { return flowReason==null; } }
    public bool NativeFrameGeneration { get { return false; } }
    string fgReason="AMD frame generation requires Direct3D 12 or Vulkan.",fgVersion="";
    string frucReason=Renderer.FrucBackendReason;
    bool frucRuntimeInstalled,frucRuntimeImportAvailable;
    string fsrReason="Temporal FSR requires Direct3D 12 or Vulkan.",fsrVersion="";
    string nisReason=NisScaler.BackendReason;
    string dlssReason,dlaaReason,rtxReason,flowReason;
    string neuralRenderingReason=NeuralRendering.BackendReason;
    bool neuralRuntimeDownloadAvailable;
    string openNrReason=NeuralRendering.OpenBackendReason;
    bool openNrModelImportAvailable;
    string outputFailure;

    public static GpuCapabilities Probe() { return ProbeForBackend("d3d11"); }

    public static GpuCapabilities ProbeForBackend(string backend)
    {
        var result=new GpuCapabilities();
        result.Adapter="Unavailable";
        result.frucRuntimeInstalled=FrucFrames.RuntimeInstalled;
        try
        {
            using(var device=Renderer.CreateEffectDevice(backend))
            using(var dxgi=device.QueryInterface<SharpDX.DXGI.Device>())
            using(var adapter=dxgi.Adapter)
            {
                Presentation.CheckOutput(backend,device);
                result.Adapter=adapter.Description.Description.TrimEnd('\0',' ');
                if(NisScaler.SupportsBackend(backend)) result.nisReason=NisScaler.Probe(device);
                if(Renderer.HasVendorBridge(backend,"fsr-temporal"))
                    result.fsrReason=result.CheckVideo(device,"fsr-temporal",backend);
                if(backend=="d3d12" || backend=="vulkan") {
                    result.fgReason=FsrFrameGeneration.Probe(device,backend,out result.fgVersion);
                }
                if(adapter.Description.VendorId!=0x10de)
                    result.AllUnavailable("The active Direct3D adapter is not an NVIDIA GPU.");
                else
                {
                    if(Renderer.HasVendorBridge(backend,"dlss-nr")) {
                        try {
                            using(var neural=new NeuralRendering(device,1280,720)) {result.neuralRenderingReason=null;}
                        } catch(NeuralRendering.RuntimeMissingException error) {
                            result.neuralRenderingReason=error.Message;
                            result.neuralRuntimeDownloadAvailable=true;
                        } catch(Exception error) { result.neuralRenderingReason=error.Message; }
                    }
                    if(Renderer.HasVendorBridge(backend,"open-nr")) {
                        try {
                            using(var neural=new NeuralRendering(device,W,H,true)) {result.openNrReason=null;}
                        } catch(NeuralRendering.RuntimeMissingException error) {
                            result.openNrReason=error.Message;
                            result.openNrModelImportAvailable=true;
                        } catch(Exception error) { result.openNrReason=error.Message; }
                    }
                    string unavailable="This NVIDIA effect has no native bridge for "+backend+".";
                    result.dlssReason=Renderer.HasVendorBridge(backend,"dlss") ? result.CheckVideo(device,"dlss",backend) : unavailable;
                    result.dlaaReason=Renderer.HasVendorBridge(backend,"dlaa") ? result.CheckVideo(device,"dlaa",backend) : unavailable;
                    result.rtxReason=Renderer.HasVendorBridge(backend,"rtxvideo") ? result.CheckVideo(device,"rtxvideo",backend) : unavailable;
                    result.flowReason=backend=="d3d11" ? CheckMotion(device) : "Motion estimation runs as a shader on "+backend+".";
                    if(Renderer.SupportsFrucBackend(backend)) {
                        bool repairable;
                        result.frucReason=FrucFrames.Probe(device,out repairable);
                        result.frucRuntimeImportAvailable=(!result.frucRuntimeInstalled || repairable) &&
                            (backend=="d3d12" || string.IsNullOrEmpty(result.flowReason));
                    }
                }
            }
        }
        catch(Exception e) {
            if(result.Adapter=="Unavailable") result.outputFailure=e.Message;
            result.AllUnavailable(e.Message);result.nisReason=result.fsrReason=result.fgReason=e.Message;
        }
        return result;
    }
    void AllUnavailable(string message) { dlssReason=dlaaReason=rtxReason=flowReason=neuralRenderingReason=openNrReason=frucReason=message;neuralRuntimeDownloadAvailable=openNrModelImportAvailable=frucRuntimeImportAvailable=false; }

    // Evaluate and read one center texel to catch runtimes that accept creation but produce no picture.
    string CheckVideo(Device device,string mode,string backend)
    {
        var context=device.ImmediateContext;
        string evaluatedFsrVersion=null;
        int outputW=mode=="dlaa" ? W : W*2, outputH=mode=="dlaa" ? H : H*2;
        try
        {
            using(var color=new GpuImage(device,W,H,Format.R8G8B8A8_UNorm,true))
            using(var output=new GpuImage(device,outputW,outputH,Format.R8G8B8A8_UNorm,true,true))
            {
                context.ClearRenderTargetView(color.Write,new RawColor4(.25f,.5f,.75f,1));
                context.ClearRenderTargetView(output.Write,new RawColor4(0,0,0,0));
                if(mode=="rtxvideo")
                {
                    using(var model=new RtxVideo(device.NativePointer))
                        model.Evaluate(context.NativePointer,color.Texture.NativePointer,output.Texture.NativePointer,W,H,outputW,outputH);
                }
                else
                {
                    using(var vectors=new GpuImage(device,W,H,Format.R16G16_Float,true))
                    using(var depth=new GpuImage(device,W,H,Format.R32_Float,true))
                    {
                        context.ClearRenderTargetView(vectors.Write,new RawColor4(0,0,0,0));
                        context.ClearRenderTargetView(depth.Write,new RawColor4(.5f,0,0,0));
                        if(mode=="fsr-temporal") {
                            using(var model=new FsrUpscaler(device,W,H,outputW,outputH,false,backend)) {
                                model.Evaluate(context,color.Texture,vectors.Texture,depth.Texture,output.Texture,16.67f,true);
                                evaluatedFsrVersion=model.ActiveVersion;
                            }
                        } else {
                            using(var model=new NgxVideo(device,W,H,outputW,outputH,mode=="dlaa"))
                                model.Evaluate(context,color.Texture,vectors.Texture,depth.Texture,output.Texture,0,0,16.67f,true);
                        }
                    }
                }
                var desc=output.Texture.Description;
                desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read;
                desc.Width=1; desc.Height=1;
                using(var pixel=new Texture2D(device,desc))
                {
                    int x=outputW/2,y=outputH/2;
                    context.CopySubresourceRegion(output.Texture,0,new ResourceRegion(x,y,0,x+1,y+1,1),pixel,0);
                    var map=context.MapSubresource(pixel,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                    try
                    {
                        int green=Marshal.ReadByte(map.DataPointer,1);
                        if(green<80 || green>175) return "The GPU evaluation produced an unexpected test pixel.";
                    }
                    finally { context.UnmapSubresource(pixel,0); }
                }
            }
            if(evaluatedFsrVersion!=null) fsrVersion=evaluatedFsrVersion;
            return null;
        }
        catch(Exception e) { return e.Message; }
        finally { context.ClearState(); }
    }
    static string CheckMotion(Device device)
    {
        NvidiaOpticalFlow of;
        string reason;
        if(!NvidiaOpticalFlow.TryCreate(device,W,H,out of,out reason)) return reason;
        try
        {
            using(of)
            using(var a=new GpuImage(device,W,H,Format.B8G8R8A8_UNorm,true))
            using(var b=new GpuImage(device,W,H,Format.B8G8R8A8_UNorm,true))
            {
                device.ImmediateContext.ClearRenderTargetView(a.Write,new RawColor4(.2f,.4f,.6f,1));
                device.ImmediateContext.ClearRenderTargetView(b.Write,new RawColor4(.2f,.4f,.6f,1));
                return of.TryEstimate(a.Texture,b.Texture,out reason) ? null : reason;
            }
        }
        catch(Exception e) { return e.Message; }
    }
    public string Reason(string mode)
    {
        if(PictureLabels.IsCombined(mode)) {
            string pictureReason=Reason(PictureLabels.BaseMode(mode));
            return pictureReason.Length==0 ? openNrReason ?? "" : pictureReason;
        }
        switch(mode)
        {
            case "nis": return nisReason ?? "";
            case "dlss": return dlssReason ?? "";
            case "dlaa": return dlaaReason ?? "";
            case "dlss-nr": return neuralRenderingReason ?? "";
            case "open-nr": return openNrReason ?? "";
            case "rtxvideo": return rtxReason ?? "";
            case "amdFrameGeneration": return fgReason ?? "";
            case "frucFrameGeneration": return frucReason ?? "";
            case "fsr-temporal": return fsrReason ?? "";
            case "opticalFlow": return flowReason ?? "";
            case "nativeFrameGeneration": return FrameGenerationReason;
            case "original": case "fsr": case "sharp": return "";
            default: return "Unknown picture mode.";
        }
    }
    public bool Supports(string mode) { return Reason(mode).Length==0; }
    static string Quote(string value)
    {
        var text=new StringBuilder("\"");
        foreach(char c in value ?? string.Empty)
        {
            if(c=='\\' || c=='"') text.Append('\\').Append(c);
            else if(c<32) text.Append("\\u").Append(((int)c).ToString("x4"));
            else text.Append(c);
        }
        return text.Append('"').ToString();
    }
    public string ToJson()
    {
        var json=new StringBuilder("{\"adapter\":").Append(Quote(Adapter));
        json.Append(",\"presentationError\":").Append(Quote(outputFailure ?? ""));
        json.Append(",\"fsrVersion\":").Append(Quote(fsrReason==null ? fsrVersion : ""));
        json.Append(",\"amdFrameGenerationVersion\":").Append(Quote(fgReason==null ? fgVersion : ""));
        json.Append(",\"frucRuntimeInstalled\":").Append(frucRuntimeInstalled ? "true":"false");
        json.Append(",\"frucRuntimeImportAvailable\":").Append(frucRuntimeImportAvailable ? "true":"false");
        json.Append(",\"dlssNeuralRuntimeDownloadAvailable\":").Append(neuralRuntimeDownloadAvailable ? "true":"false");
        json.Append(",\"openNrModelImportAvailable\":").Append(openNrModelImportAvailable ? "true":"false");
        foreach(string id in PictureLabels.Ids)
            json.Append(',').Append(Quote(id)).Append(Supports(id) ? ":true" : ":false")
                .Append(',').Append(Quote(id+"Reason")).Append(':').Append(Quote(Reason(id)));
        foreach(string id in new[]{"amdFrameGeneration","frucFrameGeneration","opticalFlow","nativeFrameGeneration"})
            json.Append(',').Append(Quote(id)).Append(Supports(id) ? ":true" : ":false")
                .Append(',').Append(Quote(id+"Reason")).Append(':').Append(Quote(Reason(id)));
        json.Append(",\"presentationDrivers\":[");
        bool first=true;
        foreach(string id in Presentation.BuiltDrivers()) {
            if(!first) json.Append(',');
            json.Append(Quote(id)); first=false;
        }
        return json.Append("]}").ToString();
    }
}
