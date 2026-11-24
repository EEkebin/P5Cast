using System;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device=SharpDX.Direct3D11.Device;

internal static class VulkanCaptureProbe
{
    [DllImport("P5Cast.Dxvk.dll",ExactSpelling=true)]
    static extern int D3D11CreateDevice(IntPtr adapter,int driverType,IntPtr software,DeviceCreationFlags flags,
        ref int featureLevel,int featureLevelCount,int sdkVersion,out IntPtr device,out int selected,IntPtr context);

    // Naming the Microsoft software adapter makes DXVK choose a fallback on systems
    // with hardware Vulkan. DXGI still returns the originally requested adapter.
    static void VerifyFallbackAdapter(Type importer)
    {
        using(var factory=new Factory1()) {
            int count=factory.GetAdapterCount1();
            for(int i=0;i<count;i++) using(var requested=factory.GetAdapter1(i)) {
                if((requested.Description1.Flags & AdapterFlags.Software)==0) continue;
                int feature=0xb000,selected;
                IntPtr pointer;
                int result=D3D11CreateDevice(requested.NativePointer,0,IntPtr.Zero,DeviceCreationFlags.BgraSupport,
                    ref feature,1,7,out pointer,out selected,IntPtr.Zero);
                if(result<0) continue;
                using(var vk=new Device(pointer))
                using(var bridge=(IDisposable)Activator.CreateInstance(importer,new object[]{vk})) {
                    var capture=(Device)importer.GetProperty("CaptureDevice").GetValue(bridge,null);
                    VerifySize(vk,capture,bridge,importer.GetMethod("Transfer",BindingFlags.Instance|BindingFlags.NonPublic),
                        Format.B8G8R8A8_UNorm,37,29);
                }
                Console.WriteLine("Verified capture after requesting the Windows software adapter from DXVK.");
                return;
            }
        }
        Console.WriteLine("Skipped DXVK adapter fallback probe (no usable Windows software adapter).");
    }
    static void VerifySize(Device vk,Device capture,object bridge,MethodInfo transfer,Format format,int width,int height)
    {
        var desc=new Texture2DDescription {
            Width=width, Height=height, Format=format, MipLevels=1, ArraySize=1,
            SampleDescription=new SampleDescription(1,0), BindFlags=BindFlags.RenderTarget
        };
        using(var input=new Texture2D(capture,desc))
        using(var target=new RenderTargetView(capture,input)) {
            desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read;
            using(var readback=new Texture2D(vk,desc)) {
                // Repeated reuse of both slots must return the latest source with
                // the correct channel ordering. Include values above SDR white.
                bool fp16=format==Format.R16G16B16A16_Float;
                for(int i=0;i<14;i++) {
                    bool high=(i&1)!=0;
                    capture.ImmediateContext.ClearRenderTargetView(target,new RawColor4(high?1:0,0,fp16?2:1,1));
                    var received=(Texture2D)transfer.Invoke(bridge,new object[]{input});
                    vk.ImmediateContext.CopyResource(received,readback);
                    // Queue several consecutive transfers before a CPU pixels check.
                    if(i<8) continue;
                    var data=vk.ImmediateContext.MapSubresource(readback,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
                    try {
                        int offset=(height-1)*data.RowPitch+(width-1)*(fp16?8:4);
                        long pixel=fp16?Marshal.ReadInt64(data.DataPointer,offset):(long)(uint)Marshal.ReadInt32(data.DataPointer,offset);
                        long expected=fp16?(high?0x3C00400000003C00L:0x3C00400000000000L):(high?0xFFFF00FFL:0xFF0000FFL);
                        if(pixel!=expected) throw new InvalidOperationException(format+" shared capture pixels differ on frame "+i+".");
                    } finally {vk.ImmediateContext.UnmapSubresource(readback,0);}
                }
            }
        }
    }
    static int Main(string[] args)
    {
        try {
            var assembly=Assembly.LoadFrom(args[0]);
            var effects=assembly.GetType("VulkanEffects",true);
            var importer=assembly.GetType("VulkanCapture",true);
            using(var vk=(Device)effects.GetMethod("Create").Invoke(null,new object[]{null}))
            using(var bridge=(IDisposable)Activator.CreateInstance(importer,new object[]{vk})) {
                var capture=(Device)importer.GetProperty("CaptureDevice").GetValue(bridge,null);
                var transfer=importer.GetMethod("Transfer",BindingFlags.Instance|BindingFlags.NonPublic);
                VerifySize(vk,capture,bridge,transfer,Format.B8G8R8A8_UNorm,137,79);
                VerifySize(vk,capture,bridge,transfer,Format.R16G16B16A16_Float,257,131);
                VerifySize(vk,capture,bridge,transfer,Format.B8G8R8A8_UNorm,319,205);
            }
            VerifyFallbackAdapter(importer);
            Console.WriteLine("Verified exact Vulkan capture pixels across slot reuse, SDR/scRGB changes and resize.");
            return 0;
        } catch(Exception error) {Console.Error.WriteLine(error.GetBaseException());return 1;}
    }
}
