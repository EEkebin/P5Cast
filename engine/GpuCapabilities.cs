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
    string dlssReason,dlaaReason,rtxReason,flowReason;

    public static GpuCapabilities Probe()
    {
        var result=new GpuCapabilities();
        result.Adapter="Unavailable";
        try
        {
            using(var device=new Device(DriverType.Hardware,DeviceCreationFlags.BgraSupport))
            using(var dxgi=device.QueryInterface<SharpDX.DXGI.Device>())
            using(var adapter=dxgi.Adapter)
            {
                result.Adapter=adapter.Description.Description.TrimEnd('\0',' ');
                if(adapter.Description.VendorId!=0x10de)
                    result.AllUnavailable("The active Direct3D adapter is not an NVIDIA GPU.");
                else
                {
                    result.dlssReason=CheckVideo(device,"dlss");
                    result.dlaaReason=CheckVideo(device,"dlaa");
                    result.rtxReason=CheckVideo(device,"rtxvideo");
                    result.flowReason=CheckMotion(device);
                }
            }
        }
        catch(Exception e) { result.AllUnavailable(e.Message); }
        return result;
    }
    void AllUnavailable(string message) { dlssReason=dlaaReason=rtxReason=flowReason=message; }

    // Evaluate and read one center texel to catch runtimes that accept creation but produce no picture.
    static string CheckVideo(Device device,string mode)
    {
        var context=device.ImmediateContext;
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
                    using(var model=new NgxVideo(device,W,H,outputW,outputH,mode=="dlaa"))
                    {
                        context.ClearRenderTargetView(vectors.Write,new RawColor4(0,0,0,0));
                        context.ClearRenderTargetView(depth.Write,new RawColor4(.5f,0,0,0));
                        model.Evaluate(context,color.Texture,vectors.Texture,depth.Texture,output.Texture,0,0,16.67f,true);
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
        switch(mode)
        {
            case "dlss": return dlssReason ?? "";
            case "dlaa": return dlaaReason ?? "";
            case "rtxvideo": return rtxReason ?? "";
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
        foreach(char c in value)
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
        foreach(string id in new[]{"dlss","dlaa","rtxvideo","opticalFlow","nativeFrameGeneration"})
            json.Append(',').Append(Quote(id)).Append(Supports(id) ? ":true" : ":false")
                .Append(',').Append(Quote(id+"Reason")).Append(':').Append(Quote(Reason(id)));
        return json.Append('}').ToString();
    }
}
