using System;
using System.Collections.Generic;
using System.Drawing;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;

internal interface IFramePresenter : IDisposable
{
    bool Hdr10 { get; }
    void ResizeWindow(Size size);
    void Present(Texture2D texture);
}

internal static class Presentation
{
    public static string Resolve(string requested)
    {
        string key=requested.ToLowerInvariant();
        switch(key) {
            case "auto": case "d3d11": return "d3d11";
            case "d3d12": case "vulkan": case "opengl": return key;
            case "metal": throw new InvalidOperationException("metal presentation is unavailable on Windows.");
            default: throw new ArgumentException("Choose auto, d3d11, d3d12, vulkan or opengl for --renderer.");
        }
    }

    public static IEnumerable<string> BuiltDrivers()
    {
        return new[]{"d3d11","d3d12","vulkan","opengl"};
    }

    public static SwapChainDescription DescribeSwapChain(IntPtr hwnd,Size size,bool hdr10)
    {
        return new SwapChainDescription {
            BufferCount=2,Flags=SwapChainFlags.None,IsWindowed=true,OutputHandle=hwnd,
            ModeDescription=new ModeDescription(size.Width,size.Height,new Rational(0,1),DisplayHdr.OutputFormat(hdr10)),
            Usage=Usage.RenderTargetOutput,SampleDescription=new SampleDescription(1,0),SwapEffect=SwapEffect.FlipDiscard
        };
    }

    // An effects device alone does not establish that its window-system interop works.
    public static void CheckOutput(string backend,Device device)
    {
        if(backend=="d3d11") return;
        var size=new Size(32,32);
        using(var window=new System.Windows.Forms.Form {ClientSize=size,ShowInTaskbar=false}) {
            if(backend=="vulkan") {
                using(var chain=VulkanSwapChain.Create(device,DescribeSwapChain(window.Handle,size,false)))
                using(var target=Texture2D.FromSwapChain<Texture2D>(chain,0))
                using(var view=new RenderTargetView(device,target)) {
                    device.ImmediateContext.ClearRenderTargetView(view,new SharpDX.Mathematics.Interop.RawColor4(0,0,0,1));
                    chain.Present(0,PresentFlags.None);
                }
            } else {
                using(var presenter=Create(window.Handle,backend,device,size,false))
                using(var target=new GpuImage(device,size.Width,size.Height,Format.B8G8R8A8_UNorm,true)) {
                    device.ImmediateContext.ClearRenderTargetView(target.Write,new SharpDX.Mathematics.Interop.RawColor4(0,0,0,1));
                    presenter.Present(target.Texture);
                }
            }
        }
    }

    public static IFramePresenter Create(IntPtr hwnd,string backend,Device device,Size size,bool hdr10)
    {
        if(backend=="d3d12") return new NativeD3D12Presentation(hwnd,device,size,hdr10);
        if(backend=="opengl" && !hdr10) {
            var presenter=new OpenGlEffects(hwnd,device);
            try { presenter.ResizeWindow(size); return presenter; }
            catch { presenter.Dispose(); throw; }
        }
        throw new InvalidOperationException("No GPU presenter for "+backend+" in the selected color mode.");
    }
}
