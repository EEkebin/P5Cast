using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

internal static class OpticalFlowProbe
{
    static Texture2D MakeImage(Device device, int width, int height, int offsetX, int offsetY)
    {
        var rand = new Random(71);
        var source = new int[width*height];
        for (int i=0; i<source.Length; i++) {
            int gray=rand.Next(256);
            source[i]=unchecked((int)0xff000000) | gray*0x010101;
        }
        var pixels=new int[width*height];
        for(int y=0;y<height;y++) for(int x=0;x<width;x++)
            pixels[y*width+x]=source[((y+height-offsetY)%height)*width+(x+width-offsetX)%width];
        var description = new Texture2DDescription {
            Width=width,Height=height,MipLevels=1,ArraySize=1,Format=Format.B8G8R8A8_UNorm,
            SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource
        };
        using(var bytes=DataStream.Create(pixels,true,false))
            return new Texture2D(device,description,new DataRectangle(bytes.DataPointer,width*4));
    }

    static void Measure(Device device, NvidiaOpticalFlow flow, Texture2D current, Texture2D reference, int dx, int dy)
    {
        string reason;
        if (!flow.TryEstimate(current,reference,out reason)) throw new InvalidOperationException(reason);
        var desc=flow.OutputTexture.Description;
        desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read;
        using (var download = new Texture2D(device,desc)) {
            device.ImmediateContext.CopyResource(flow.OutputTexture,download);
            var map=device.ImmediateContext.MapSubresource(download,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
            try {
                var xs=new List<double>(); var ys=new List<double>(); int correct=0;
                for(int y=desc.Height/4;y<desc.Height*3/4;y+=3) for(int x=desc.Width/4;x<desc.Width*3/4;x+=3) {
                    int offset=y*map.RowPitch+x*4;
                    double vx=Marshal.ReadInt16(map.DataPointer,offset)/32.0;
                    double vy=Marshal.ReadInt16(map.DataPointer,offset+2)/32.0;
                    xs.Add(vx); ys.Add(vy);
                    if(Math.Abs(vx-dx)<=1 && Math.Abs(vy-dy)<=1) correct++;
                }
                xs.Sort(); ys.Sort();
                Console.WriteLine("Expected ({0},{1}), measured median ({2},{3}), {4}/{5} interior samples within 1 px.",
                    dx,dy,xs[xs.Count/2],ys[ys.Count/2],correct,xs.Count);
                if(correct<xs.Count*0.85) throw new InvalidOperationException("Vector direction or scale regression.");
            } finally { device.ImmediateContext.UnmapSubresource(download,0); }
        }
    }
    static void CheckSize(Device device, int width, int height)
    {
        using(var a=MakeImage(device,width,height,0,0))
        using(var b=MakeImage(device,width,height,8,4)) {
            string reason; NvidiaOpticalFlow flow;
            if(!NvidiaOpticalFlow.TryCreate(device,width,height,out flow,out reason)) throw new InvalidOperationException(reason);
            using(flow) {
                Console.WriteLine("D3D11 NVIDIA Optical Flow, {0}x{1}, grid {2}.",width,height,flow.GridSize);
                Measure(device,flow,b,a,-8,-4);
                Measure(device,flow,a,b,8,4);
                Measure(device,flow,a,a,0,0);
            }
        }
    }
    static int Main()
    {
        try {
            using(var device=new Device(DriverType.Hardware,DeviceCreationFlags.BgraSupport)) {
                CheckSize(device,640,360);
                CheckSize(device,1919,1079);
                CheckSize(device,1920,1080);
            }
            return 0;
        } catch(Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
