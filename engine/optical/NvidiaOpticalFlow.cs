// NVIDIA Optical Flow SDK 5.0 D3D11 interface, see NVIDIA-OPTICAL-FLOW-LICENSE.txt.
// https://docs.nvidia.com/video-technologies/optical-flow-sdk/nvofa-programming-guide/
// ABI declarations from the NVIDIA SDK headers:
// https://github.com/jp7677/dxvk-nvapi/tree/e6642d051706479ef82e19f91f3bf7c741dd3447/inc/nvofapi
using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

// All calls belong on the immediate-context thread. Dispose before releasing the D3D11 device.
internal sealed class NvidiaOpticalFlow : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Api
    {
        public IntPtr Create, Init, FormatCount, Formats, Register, Unregister, Execute, Destroy, Error, Caps;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct InitParams
    {
        public int Width, Height, Grid, HintGrid, Mode, Performance, ExternalHints, Cost;
        public IntPtr Private;
        public int Disparity, Roi, Prediction, Global, InputFormat;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ExecuteIn
    {
        public IntPtr Input, Reference, Hints;
        public int DisableTemporal, Pad;
        public IntPtr Private;
        public int Pad2, NumRois;
        public IntPtr Rois;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ExecuteOut
    {
        public IntPtr Output, Cost, Private, Backward, BackwardCost, Global;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvofapi64.dll", ExactSpelling=true)]
    static extern int NvOFAPICreateInstanceD3D11(uint version, out Api api);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateFn(IntPtr device, IntPtr context, out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int InitFn(IntPtr session, ref InitParams parameters);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FormatCountFn(IntPtr session, int usage, int mode, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FormatsFn(IntPtr session, int usage, int mode, [Out] int[] formats);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CapsFn(IntPtr session, int cap, [Out] int[] values, ref int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int RegisterFn(IntPtr session, IntPtr texture, out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFn(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ExecuteFn(IntPtr session, ref ExecuteIn input, ref ExecuteOut output);

    const int OpticalFlowMode = 1;
    const int Abgr8 = 3;
    const int PerformanceQuality = 5;
    readonly int width, height;
    readonly Device device;
    readonly DeviceContext context;
    IntPtr session, outputHandle;
    readonly IntPtr[] inputHandles = new IntPtr[4];
    readonly Texture2D[] inputs = new Texture2D[4];
    int nextPair;
    Texture2D flowTexture;
    ReleaseFn unregister, destroy;
    ExecuteFn execute;
    bool first = true;
    public int GridSize { get; private set; }
    public ShaderResourceView BackwardFlow { get; private set; }
    internal Texture2D OutputTexture { get { return flowTexture; } }

    NvidiaOpticalFlow(Device gpu, int w, int h)
    {
        device = gpu;
        context = gpu.ImmediateContext;
        width = w;
        height = h;
    }
    static T Bind<T>(IntPtr address) where T : class
    {
        if (address == IntPtr.Zero) throw new NotSupportedException("Missing NVIDIA optical flow entry point.");
        return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
    }
    static void Check(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException("NVIDIA optical flow " + operation + " returned status " + status + ".");
    }
    static int[] QueryCaps(CapsFn caps, IntPtr instance, int cap)
    {
        int count = 0;
        Check(caps(instance, cap, null, ref count), "capability count");
        if (count <= 0 || count > 256) throw new NotSupportedException("Unsupported NVIDIA optical flow capability.");
        var values = new int[count];
        Check(caps(instance, cap, values, ref count), "capabilities");
        return values;
    }
    void RequireFormat(Api api, int usage, Format expected)
    {
        int count;
        Check(Bind<FormatCountFn>(api.FormatCount)(session, usage, OpticalFlowMode, out count), "surface count");
        if (count <= 0 || count > 256) throw new NotSupportedException("Unsupported NVIDIA optical flow surface count.");
        var formats = new int[count];
        Check(Bind<FormatsFn>(api.Formats)(session, usage, OpticalFlowMode, formats), "surface formats");
        if (Array.IndexOf(formats,(int)expected) < 0) {
            throw new NotSupportedException("Optical flow requires " + expected + " (reported DXGI formats " + String.Join(",", formats) + ").");
        }
    }
    Texture2D Surface(int w, int h, Format format, BindFlags flags)
    {
        return new Texture2D(device, new Texture2DDescription {
            Width=w, Height=h, ArraySize=1, MipLevels=1, Format=format,
            SampleDescription=new SampleDescription(1,0), BindFlags=flags, Usage=ResourceUsage.Default
        });
    }
    void Initialize()
    {
        Api api;
        Check(NvOFAPICreateInstanceD3D11(0x50, out api), "SDK 5.0 interface");
        destroy = Bind<ReleaseFn>(api.Destroy);
        unregister = Bind<ReleaseFn>(api.Unregister);
        execute = Bind<ExecuteFn>(api.Execute);
        Check(Bind<CreateFn>(api.Create)(device.NativePointer,context.NativePointer,out session),"create");
        var caps = Bind<CapsFn>(api.Caps);
        if (width < QueryCaps(caps, session, 4)[0] || width > QueryCaps(caps, session, 6)[0] ||
            height < QueryCaps(caps, session, 5)[0] || height > QueryCaps(caps, session, 7)[0])
            throw new NotSupportedException("Capture dimensions are outside the optical flow hardware range.");
        var grids = QueryCaps(caps, session, 0);
        GridSize = Array.IndexOf(grids, 2) >= 0 ? 2 : 4;
        if (Array.IndexOf(grids, GridSize) < 0) throw new NotSupportedException("Unsupported optical flow grid.");
        var init = new InitParams { Width=width,Height=height,Grid=GridSize,Mode=OpticalFlowMode,Performance=PerformanceQuality,InputFormat=Abgr8 };
        Check(Bind<InitFn>(api.Init)(session, ref init), "initialize");
        RequireFormat(api, 1, Format.B8G8R8A8_UNorm);
        RequireFormat(api, 2, Format.R16G16_SInt);
        // Bind the driver's writable output as in the D3D11 SDK sample. SRV-only resources
        // yielded stale vectors when consecutive submissions were read without a CPU wait.
        flowTexture = Surface((width+GridSize-1)/GridSize,(height+GridSize-1)/GridSize,Format.R16G16_SInt,
            BindFlags.ShaderResource | BindFlags.RenderTarget | BindFlags.UnorderedAccess);
        var reg = Bind<RegisterFn>(api.Register);
        for(int i=0;i<inputs.Length;i++) {
            inputs[i]=Surface(width,height,Format.B8G8R8A8_UNorm,BindFlags.None);
            Check(reg(session,inputs[i].NativePointer,out inputHandles[i]),"register input surface");
        }
        Check(reg(session,flowTexture.NativePointer,out outputHandle), "register output");
        BackwardFlow = new ShaderResourceView(device,flowTexture);
    }
    public static bool TryCreate(Device device, int width, int height, out NvidiaOpticalFlow flow, out string reason)
    {
        flow = null;
        if (device == null || device.IsDisposed || width <= 0 || height <= 0) {
            reason = "Optical flow needs an active D3D11 device and positive capture dimensions.";
            return false;
        }
        var candidate = new NvidiaOpticalFlow(device,width,height);
        try {
            candidate.Initialize();
            flow = candidate; reason = null; return true;
        } catch (Exception error) {
            candidate.Dispose(); reason = error.Message; return false;
        }
    }
    bool ValidInput(Texture2D texture)
    {
        if (texture == null || texture.IsDisposed) return false;
        var desc = texture.Description;
        return desc.Width==width && desc.Height==height && desc.Format==Format.B8G8R8A8_UNorm &&
               desc.MipLevels==1 && desc.ArraySize==1 && desc.SampleDescription.Count==1 &&
               texture.Device.NativePointer==device.NativePointer;
    }
    public bool TryEstimate(Texture2D current, Texture2D preceding, out string reason)
    {
        try {
            if (session==IntPtr.Zero || !ValidInput(current) || !ValidInput(preceding))
                throw new InvalidOperationException("Optical flow needs two matching BGRA8 frames on the session device.");
            context.CopyResource(current, inputs[nextPair]);
            context.CopyResource(preceding, inputs[nextPair+1]);
            var input = new ExecuteIn { Input=inputHandles[nextPair],Reference=inputHandles[nextPair+1],DisableTemporal=first ? 1 : 0 };
            var output = new ExecuteOut { Output=outputHandle };
            Check(execute(session,ref input,ref output), "execute");
            // Rotate two disjoint pairs so neighboring hardware submissions can overlap.
            nextPair = (nextPair+2)%inputs.Length;
            first = false; reason = null; return true;
        } catch (Exception error) { first = true; reason = error.Message; return false; }
    }
    public void Dispose()
    {
        if (outputHandle != IntPtr.Zero) { unregister(outputHandle); outputHandle=IntPtr.Zero; }
        for(int i=0;i<inputs.Length;i++) {
            if (inputHandles[i]!=IntPtr.Zero) { unregister(inputHandles[i]); inputHandles[i]=IntPtr.Zero; }
            if (inputs[i]!=null) { inputs[i].Dispose(); inputs[i]=null; }
        }
        if (BackwardFlow != null) { BackwardFlow.Dispose(); BackwardFlow=null; }
        if (flowTexture != null) { flowTexture.Dispose(); flowTexture=null; }
        if (session != IntPtr.Zero) { destroy(session); session=IntPtr.Zero; }
    }
}
