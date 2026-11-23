using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;

internal sealed class FrucFrames : IDisposable
{
    const string Library="P5Cast.Fruc.dll";
    const DllImportSearchPath Search=DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32;
    IntPtr state;

    [DefaultDllImportSearchPaths(Search),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
    static extern int P5FrucOpen(IntPtr device,int width,int height,string runtime,out IntPtr session,out IntPtr step);
    [DefaultDllImportSearchPaths(Search),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern int P5FrucProcess(IntPtr session,IntPtr source,IntPtr midpoint,out int valid);
    [DefaultDllImportSearchPaths(Search),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern void P5FrucClose(IntPtr session);

    static string RuntimePath {
        get {
            string appData=Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if(string.IsNullOrEmpty(appData)) appData=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string user=Path.Combine(appData,"P5Cast","runtimes","fruc","NvOFFRUC.dll");
            if(File.Exists(user)) return user;
            return Path.Combine(Path.GetDirectoryName(typeof(FrucFrames).Assembly.Location),"NvOFFRUC.dll");
        }
    }
    public static bool RuntimeInstalled {get {return File.Exists(RuntimePath);}}

    sealed class RuntimeError : InvalidOperationException {internal RuntimeError(string message):base(message) {}}
    public FrucFrames(Device device,int width,int height) {
        string path=RuntimePath;
        if(!File.Exists(path)) throw new RuntimeError("Install the NVIDIA Optical Flow SDK FRUC runtime to use NVIDIA FRUC.");
        IntPtr step;
        int result=P5FrucOpen(device.NativePointer,width,height,path,out state,out step);
        if(result!=0) {
            string operation=Marshal.PtrToStringUni(step) ?? "opening the provider";
            string message="NVIDIA FRUC failed while "+operation+" (0x"+result.ToString("X8")+").";
            if(operation.StartsWith("loading the NVIDIA runtime",StringComparison.Ordinal)) throw new RuntimeError(message);
            throw new InvalidOperationException(message);
        }
    }
    public bool Evaluate(Texture2D input,Texture2D midpoint) {
        if(state==IntPtr.Zero) throw new ObjectDisposedException("NVIDIA FRUC");
        int valid;
        int result=P5FrucProcess(state,input.NativePointer,midpoint.NativePointer,out valid);
        if(result!=0) throw new InvalidOperationException("NVIDIA FRUC interpolation failed (0x"+result.ToString("X8")+").");
        return valid!=0;
    }
    public static string Probe(Device device) {
        bool repairable;return Probe(device,out repairable);
    }
    public static string Probe(Device device,out bool repairable) {
        repairable=false;
        const int w=640,h=360;
        try {
            using(var frames=new FrucFrames(device,w,h)) {
                var desc=new Texture2DDescription {Width=w,Height=h,ArraySize=1,MipLevels=1,Format=Format.R8G8B8A8_UNorm,
                    SampleDescription=new SampleDescription(1,0),BindFlags=BindFlags.RenderTarget|BindFlags.ShaderResource};
                using(var input=new Texture2D(device,desc))
                using(var midpoint=new Texture2D(device,desc)) {
                    desc.Usage=ResourceUsage.Staging;desc.CpuAccessFlags=CpuAccessFlags.Read;desc.BindFlags=BindFlags.None;
                    using(var read=new Texture2D(device,desc)) {
                        int[] data=new int[w*h];
                        for(int frame=0;frame<4;frame++) {
                            // Translate a textured scene between independent, fixed color patches.
                            for(int y=0;y<h;y++) for(int x=0;x<w;x++) {
                                int u=x-frame*8;
                                int gray=32+(((u>>3)*17+(y>>3)*29+((u>>5)^(y>>5))*7)&159);
                                data[y*w+x]=unchecked((int)0xff000000)|(x<w/8?0x3175ae:x>7*w/8?0xbe5630:gray*0x10101);
                            }
                            var ctx=device.ImmediateContext;
                            ctx.UpdateSubresource(data,input,0,w*4,0);
                            bool generated=frames.Evaluate(input,midpoint);
                            if(frame==0 && generated) return "NVIDIA FRUC counted the initial frame as a generated frame.";
                            if(!generated) continue;
                            ctx.CopyResource(midpoint,read);
                            var map=ctx.MapSubresource(read,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                            try {
                                for(int side=0;side<2;side++) {
                                    int x=side==0?w/16:15*w/16;
                                    int color=Marshal.ReadInt32(map.DataPointer,map.RowPitch*(h/2)+x*4);
                                    for(int shift=0;shift<=16;shift+=8)
                                        if(Math.Abs(((color>>shift)&255)-((data[x]>>shift)&255))>8)
                                            return "NVIDIA FRUC returned an unexpected color in its GPU test.";
                                }
                                // A genuine midpoint of the translated area must differ from both real inputs.
                                int changedPrior=0,changedNext=0;
                                for(int y=64;y<h-64;y+=7) for(int x=144;x<w-144;x+=7) {
                                    int pixel=Marshal.ReadByte(map.DataPointer,map.RowPitch*y+4*x);
                                    if(Math.Abs(pixel-(data[y*w+x+8]&255))>4) changedPrior++;
                                    if(Math.Abs(pixel-(data[y*w+x]&255))>4) changedNext++;
                                }
                                if(changedPrior>50 && changedNext>50) return null;
                            } finally {ctx.UnmapSubresource(read,0);}
                        }
                    }
                }
            }
            return "NVIDIA FRUC did not produce a distinct intermediate picture during its GPU test.";
        } catch(RuntimeError ex) {repairable=true;return ex.Message;}
          catch(Exception ex) {return ex.Message;}
    }
    public void Dispose() {
        if(state!=IntPtr.Zero) {P5FrucClose(state);state=IntPtr.Zero;}
    }
}
