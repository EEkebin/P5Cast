using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer=SharpDX.Direct3D11.Buffer;

// A local 540p source for exercising the same scaling and interpolation used for console video.
internal sealed class DemoVideo : IDisposable
{
    const int Width=960,Height=540;
    readonly IntPtr dashboard;
    readonly double frameMs;
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly PixelShader shader;
    readonly Buffer settings;
    GpuImage frame;
    double due,lastTime;
    float steering,distance;
    [StructLayout(LayoutKind.Sequential)] struct Scene { public float Time,Steer,Distance,White; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd,uint flag);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    static bool Down(int key) { return (GetAsyncKeyState(key)&0x8000)!=0; }
    public DemoVideo(Renderer renderer,IntPtr surface,int sourceFps) {
        dashboard=GetAncestor(surface,2); frameMs=1000.0/sourceFps;
        shader=new PixelShader(renderer.Device,Renderer.DemoShader());
        settings=new Buffer(renderer.Device,16,ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);
    }
    public bool Read(Renderer renderer,out double timestamp) {
        double now=clock.Elapsed.TotalMilliseconds; timestamp=0;
        if(now<due) return false;
        due+=frameMs*Math.Max(1,Math.Floor((now-due)/frameMs)+1);
        float dt=(float)Math.Min(.1,(now-lastTime)/1000); lastTime=now;
        bool active=GetForegroundWindow()==dashboard;
        int direction=active ? (Down(0x27)||Down(0x44) ? 1:0)-(Down(0x25)||Down(0x41) ? 1:0) : 0;
        steering=Math.Max(-1,Math.Min(1,steering+direction*dt*1.4f));
        distance+=dt*(active && Down(0x20) ? 2.8f:1);
        var format=renderer.HdrEnabled ? Format.R16G16B16A16_Float:Format.B8G8R8A8_UNorm;
        if(frame==null || frame.Texture.Description.Format!=format) {
            if(frame!=null) frame.Dispose();
            frame=new GpuImage(renderer.Device,Width,Height,format,true);
        }
        var scene=new Scene {Time=(float)(now/1000),Steer=steering,Distance=distance,White=renderer.HdrEnabled ? renderer.DirectWhiteNits/80:0};
        var context=renderer.Device.ImmediateContext;
        timestamp=Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency;
        context.UpdateSubresource(ref scene,settings); context.PixelShader.SetConstantBuffer(2,settings);
        context.Rasterizer.SetViewport(0,0,Width,Height); context.OutputMerger.SetRenderTargets(frame.Write);
        context.PixelShader.Set(shader); context.Draw(3,0); context.OutputMerger.ResetTargets();
        return renderer.Receive(frame.Texture,new Rectangle(0,0,Width,Height),false);
    }
    public void Dispose() { if(frame!=null) frame.Dispose(); settings.Dispose(); shader.Dispose(); }
}
internal sealed partial class Renderer
{
    internal static byte[] DemoShader() { return Shader("DemoScene"); }
}
