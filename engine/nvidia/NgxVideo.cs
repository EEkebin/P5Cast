using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

internal sealed class NgxVideo : IDisposable
{
    const string Bridge = "Cadrivect.Ngx.dll";
    IntPtr state;

    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(Bridge, CallingConvention=CallingConvention.Cdecl, ExactSpelling=true, CharSet=CharSet.Unicode)]
    static extern uint NgxOpen2(IntPtr device, IntPtr context, string runtimeDirectory, string dataDirectory,
        int inputWidth, int inputHeight, int outputWidth, int outputHeight, int dlaa, int hdr, out IntPtr state, out int stage);

    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(Bridge, CallingConvention=CallingConvention.Cdecl, ExactSpelling=true)]
    static extern uint NgxEvaluate(IntPtr state, IntPtr context, IntPtr color, IntPtr motion, IntPtr depth,
        IntPtr output, float jitterX, float jitterY, float frameMs, int reset);

    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(Bridge, CallingConvention=CallingConvention.Cdecl, ExactSpelling=true)]
    static extern void NgxClose(IntPtr state);

    public NgxVideo(Device device,int inputWidth,int inputHeight,int outputWidth,int outputHeight,bool dlaa,bool hdr=false)
    {
        if (inputWidth<1 || inputHeight<1 || outputWidth<1 || outputHeight<1)
            throw new ArgumentOutOfRangeException("inputWidth","NVIDIA frame dimensions must be positive.");
        if (dlaa && (inputWidth!=outputWidth || inputHeight!=outputHeight))
            throw new ArgumentException("DLAA evaluates at the incoming video resolution.");
        if (!dlaa && (outputWidth<inputWidth || outputHeight<inputHeight))
            throw new ArgumentException("Select an output at least as large as the input for DLSS upscaling.");
        string runtime=AppDomain.CurrentDomain.BaseDirectory;
        if (!File.Exists(Path.Combine(runtime,"nvngx_dlss.dll")) || !File.Exists(Path.Combine(runtime,Bridge)))
            throw new FileNotFoundException("Install the complete Windows package including the NVIDIA runtime.");
        string data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Cadrivect","NGX");
        Directory.CreateDirectory(data);
        int stage;
        uint result=NgxOpen2(device.NativePointer,device.ImmediateContext.NativePointer,runtime,data,
            inputWidth,inputHeight,outputWidth,outputHeight,dlaa ? 1 : 0,hdr ? 1 : 0,out state,out stage);
        string[] stages={"allocation","driver initialization","capability query","DLSS availability","feature creation"};
        Check(result,stages[Math.Max(0,Math.Min(stage,stages.Length-1))]);
    }

    public void Evaluate(DeviceContext context,Texture2D color,Texture2D motion,Texture2D depth,Texture2D output,
        float jitterX,float jitterY,float frameMs,bool reset)
    {
        if (state==IntPtr.Zero) throw new ObjectDisposedException("NgxVideo");
        uint result=NgxEvaluate(state,context.NativePointer,color.NativePointer,motion.NativePointer,depth.NativePointer,
            output.NativePointer,jitterX,jitterY,frameMs,reset ? 1 : 0);
        Check(result,"frame evaluation");
    }

    static void Check(uint result,string operation)
    {
        if (result==1) return;
        throw new InvalidOperationException(string.Format("NVIDIA NGX {0} failed (0x{1:X8}). Verify your RTX GPU, NVIDIA driver and selected output size.",operation,result));
    }

    public void Dispose()
    {
        if (state==IntPtr.Zero) return;
        NgxClose(state); state=IntPtr.Zero;
    }
}
