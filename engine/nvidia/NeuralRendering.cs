using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device=SharpDX.Direct3D11.Device;

// DLSS Neural Rendering uses display-referred SDR in FP16 at the source dimensions.
internal sealed class NeuralRendering : IDisposable
{
    const string Bridge="P5Cast.NeuralVk.dll";
    const int MissingRuntime=2;
    const DllImportSearchPath Search=DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32;
    readonly StringBuilder reason=new StringBuilder(768);
    IntPtr state;
    GpuImage control;
    public const string BackendReason="DLSS Neural Rendering requires Vulkan.";
    public const string HdrReason="DLSS Neural Rendering processes SDR. Select SDR to use it.";

    [DefaultDllImportSearchPaths(Search),DllImport(Bridge,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
    static extern int P5NrOpen(IntPtr device,string runtimeDirectory,string appData,int width,int height,out IntPtr state,StringBuilder reason,int reasonChars);
    [DefaultDllImportSearchPaths(Search),DllImport(Bridge,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
    static extern int P5NrEvaluate(IntPtr state,IntPtr color,IntPtr motion,IntPtr depth,IntPtr mask,IntPtr output,int reset,StringBuilder reason,int reasonChars);
    [DefaultDllImportSearchPaths(Search),DllImport(Bridge,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern void P5NrClose(IntPtr state);

    public sealed class RuntimeMissingException : InvalidOperationException
    {
        internal RuntimeMissingException(string message) : base(message) {}
    }

    public NeuralRendering(Device device,int width,int height)
    {
        string app=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"P5Cast");
        string provider=Path.Combine(app,"providers","dlss-nr","310.9.16");
        int result=P5NrOpen(device.NativePointer,provider,Path.Combine(app,"NGX"),width,height,out state,reason,reason.Capacity);
        try {
            if(result==MissingRuntime) throw new RuntimeMissingException(reason.ToString());
            Check(result);
            control=new GpuImage(device,width,height,Format.R8G8B8A8_UNorm,true);
            device.ImmediateContext.ClearRenderTargetView(control.Write,new RawColor4(1,1,1,1));
            VerifyOutput(device,width,height);
        } catch {Dispose();throw;}
    }

    public void Evaluate(Texture2D color,Texture2D motion,Texture2D depth,Texture2D output,bool reset)
    {
        if(state==IntPtr.Zero) throw new ObjectDisposedException("NeuralRendering");
        reason.Clear();
        Check(P5NrEvaluate(state,color.NativePointer,motion.NativePointer,depth.NativePointer,control.Texture.NativePointer,
            output.NativePointer,reset?1:0,reason,reason.Capacity));
    }

    void Check(int result)
    {
        if(result!=0) throw new InvalidOperationException(reason.Length==0 ? "The DLSS Neural Rendering provider failed." : reason.ToString());
    }

    // Probe the actual native session. This also protects direct CLI launches and newly
    // selected source sizes from drivers which return success with black or stale pixels.
    void VerifyOutput(Device device,int w,int h)
    {
        if(w<32 || h<32) throw new InvalidOperationException("The source is too small for Neural Rendering verification.");
        var context=device.ImmediateContext;
        using(var color=new GpuImage(device,w,h,Format.R16G16B16A16_Float,true))
        using(var vectors=new GpuImage(device,w,h,Format.R16G16_Float,true))
        using(var depth=new GpuImage(device,w,h,Format.R32_Float,true))
        using(var output=new GpuImage(device,w,h,Format.R16G16B16A16_Float,true,true)) {
            var desc=output.Texture.Description;
            desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read;
            context.ClearRenderTargetView(vectors.Write,new RawColor4());
            context.ClearRenderTargetView(depth.Write,new RawColor4(.5f,0,0,0));
            using(var sample=new Texture2D(device,desc)) {
                var source=new ushort[checked(w*h*4)];
                // Leave neutral black/white patches above a textured field. Swap the patches
                // halfway through, so a coherent readback must track a second input.
                for(int scene=0;scene<2;scene++) {
                    FillCalibration(source,w,h,scene);
                    context.UpdateSubresource(source,color.Texture,0,w*8,0);
                    for(int f=0;f<3;f++) {
                        context.ClearRenderTargetView(output.Write,new RawColor4(1,0,1,1));
                        Evaluate(color.Texture,vectors.Texture,depth.Texture,output.Texture,f==0);
                    }
                    context.CopyResource(output.Texture,sample);
                    var map=context.MapSubresource(sample,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                    try { CheckCalibration(map,source,w,h,scene); }
                    finally {context.UnmapSubresource(sample,0);}
                }
            }
        }
    }

    static void FillCalibration(ushort[] image,int w,int h,int scene)
    {
        ushort[] levels={0x3000,0x3400,0x3600,0x3800,0x3900,0x3a00,0x3b00};
        for(int y=0;y<h;y++) for(int x=0;x<w;x++) {
            int index=(y*w+x)*4;
            if(y<h/3) {
                ushort level=((x<w/2)^(scene!=0))?(ushort)0:(ushort)0x3c00;
                image[index]=image[index+1]=image[index+2]=level;
            } else {
                int stripe=(x+2*y+scene*19)/9;
                image[index]=levels[(stripe+y/33)%7];
                image[index+1]=levels[(stripe/2+x/29)%7];
                image[index+2]=levels[(stripe+y/27+x/15)%7];
            }
            image[index+3]=0x3c00;
        }
    }

    static double Half(ushort bits)
    {
        int exponent=(bits>>10)&31,mantissa=bits&1023;
        if(exponent==31) return double.NaN;
        double value=exponent==0 ? mantissa/16777216.0 : Math.Pow(2,exponent-15)*(1+mantissa/1024.0);
        return (bits&0x8000)==0 ? value : -value;
    }

    static void CheckCalibration(SharpDX.DataBox map,ushort[] source,int w,int h,int scene)
    {
        for(int side=0;side<2;side++) for(int channel=0;channel<3;channel++) {
            int offset=(h/6)*map.RowPitch+((side*2+1)*w/4)*8+2*channel;
            double actual=Half((ushort)Marshal.ReadInt16(map.DataPointer,offset));
            double expected=side==scene?0:1;
            if(double.IsNaN(actual) || Math.Abs(expected-actual)>.045)
                throw new InvalidOperationException("The Vulkan Neural Rendering output failed the SDR black and white check.");
        }
        int changed=0,samples=0;
        double totalError=0;
        for(int y=h/2;y<h;y+=Math.Max(1,h/24)) for(int x=w/16;x<w;x+=Math.Max(1,w/32)) {
            for(int c=0;c<3;c++) {
                double actual=Half((ushort)Marshal.ReadInt16(map.DataPointer,y*map.RowPitch+x*8+2*c));
                if(double.IsNaN(actual) || actual<-.1 || actual>1.15)
                    throw new InvalidOperationException("Neural Rendering produced invalid SDR pixel values.");
                double error=Math.Abs(actual-Half(source[(y*w+x)*4+c]));
                if(error>.008) changed++;
                totalError+=error;
                samples++;
            }
        }
        if(changed<3) throw new InvalidOperationException("The Vulkan provider returned the source unchanged during the Neural Rendering test.");
        if(totalError/samples>.23) throw new InvalidOperationException("The Vulkan Neural Rendering picture failed its spatial coherence check.");
    }

    public void Dispose()
    {
        if(state!=IntPtr.Zero) {P5NrClose(state);state=IntPtr.Zero;}
        if(control!=null) {control.Dispose();control=null;}
    }
}
