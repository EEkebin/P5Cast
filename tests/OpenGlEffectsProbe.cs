// GPU readback on a hidden child window. Exercise GL/D3D registered textures
// and the staging transfer path used when driver interop is unavailable.
using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;

internal static class OpenGlEffectsProbe
{
    static readonly Size SizeA=new Size(63,37);
    static Texture2D Surface(Device device, Size size, Format format) {
        return new Texture2D(device,new Texture2DDescription {Width=size.Width,Height=size.Height,Format=format,MipLevels=1,ArraySize=1,
            BindFlags=BindFlags.RenderTarget|BindFlags.ShaderResource,SampleDescription=new SampleDescription(1,0)});
    }
    static int[] Read(Device device, Texture2D image) {
        var desc=image.Description;
        desc.Usage=ResourceUsage.Staging;desc.CpuAccessFlags=CpuAccessFlags.Read;desc.BindFlags=BindFlags.None;
        using(var staging=new Texture2D(device,desc)) {
            var context=device.ImmediateContext;context.CopyResource(image,staging);
            var map=context.MapSubresource(staging,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
            try {
                var pixels=new int[desc.Width*desc.Height];
                for(int y=0;y<desc.Height;y++) Marshal.Copy(IntPtr.Add(map.DataPointer,y*map.RowPitch),pixels,y*desc.Width,desc.Width);
                return pixels;
            } finally {context.UnmapSubresource(staging,0);}
        }
    }
    static void Require(bool valid,string error) {if(!valid) throw new Exception(error);}
    static void Draw(object gl,string pass,Size source,Rectangle area,RenderTargetView target,ShaderResourceView frame,
                     ShaderResourceView previous=null,ShaderResourceView flow=null,Rectangle? clip=null,float phase=1) {
        var taken=(bool)gl.GetType().GetMethod("Draw").Invoke(gl,new object[]{pass,target,area,frame,previous,flow,clip,phase,source});
        Require(taken,pass+" did not execute through OpenGL.");
    }
    static int[] Run(Device device, Control host,Type type,bool sharing) {
        var c=device.ImmediateContext;
        var bigger=new Size(125,73);
        var srcArea=new Rectangle(Point.Empty,SizeA);var biggerArea=new Rectangle(Point.Empty,bigger);
        int[] source=new int[SizeA.Width*SizeA.Height];
        for(int y=0;y<SizeA.Height;y++) for(int x=0;x<SizeA.Width;x++) {
            source[y*SizeA.Width+x]=Color.FromArgb(255,((x/5+y/7)%2)*94+71,y*5+11,x*3+15).ToArgb();
        }
        using(var gl=(IDisposable)Activator.CreateInstance(type,host.Handle,device,sharing))
        using(var input=Surface(device,SizeA,Format.B8G8R8A8_UNorm))
        using(var target=Surface(device,SizeA,Format.B8G8R8A8_UNorm))
        using(var scaled=Surface(device,bigger,Format.B8G8R8A8_UNorm))
        using(var enhanced=Surface(device,bigger,Format.B8G8R8A8_UNorm))
        using(var vectors=Surface(device,new Size(6,4),Format.R16G16B16A16_Float))
        using(var src=new ShaderResourceView(device,input))
        using(var output=new RenderTargetView(device,target))
        using(var up=new RenderTargetView(device,scaled))
        using(var upRead=new ShaderResourceView(device,scaled))
        using(var sharpened=new RenderTargetView(device,enhanced))
        using(var motion=new RenderTargetView(device,vectors))
        using(var motionRead=new ShaderResourceView(device,vectors)) {
            c.UpdateSubresource(source,input,0,SizeA.Width*4);
            Draw(gl,"Original",SizeA,srcArea,output,src);
            var pixels=Read(device,target);
            for(int i=0;i<source.Length;i++) Require(source[i]==pixels[i],"Original changed an RGB channel or row at "+i+".");
            Draw(gl,"Motion",SizeA,new Rectangle(0,0,6,4),motion,src,src);
            Draw(gl,"Interpolate",SizeA,srcArea,output,src,src,motionRead,phase:.5f);
            pixels=Read(device,target);
            for(int i=0;i<source.Length;i++) Require(source[i]==pixels[i],"Motion interpolation changed static pixels.");
            var clip=new Rectangle(8,5,23,14);
            Draw(gl,"Sharp",SizeA,srcArea,output,src,clip:clip);
            pixels=Read(device,target);
            int changed=0;
            for(int i=0;i<pixels.Length;i++) if(source[i]!=pixels[i]) {
                Require(clip.Contains(i%SizeA.Width,i/SizeA.Width),"Sharpen wrote outside its clip.");changed++;
            }
            Require(changed>20,"Sharpen lost the fixture's edges.");
            Draw(gl,"Easu",SizeA,biggerArea,up,src);
            Draw(gl,"Rcas",SizeA,biggerArea,sharpened,upRead);
            var result=Read(device,enhanced);
            Require(Color.FromArgb(result[0]).G<50 && Color.FromArgb(result[result.Length-1]).G>165,"FSR flipped or corrupted the source.");
            int shared=(int)type.GetProperty("SharedTextures").GetValue(gl,null);
            if(!sharing) Require(shared==0,"The forced staging test used registered textures.");
            Console.WriteLine("{0}: {1} registered textures.",sharing?"Driver-selected transfer":"Forced staging",shared);
            return result;
        }
    }
    [STAThread] static int Main(string[] args) {
        try {
            var renderer=Assembly.LoadFrom(args[0]).GetType("OpenGlEffects",true);
            using(var host=new Control())
            using(var device=new Device(SharpDX.Direct3D.DriverType.Hardware,DeviceCreationFlags.BgraSupport)) {
                int[] shared=Run(device,host,renderer,true),copied=Run(device,host,renderer,false);
                for(int i=0;i<shared.Length;i++) Require(shared[i]==copied[i],"FSR colors differ between transfer paths at pixel "+i+".");
            }
            Console.WriteLine("OpenGL Original, clipped Sharpen, Motion, Interpolate and FSR1 preserved orientation, channels and clipping. Shared and staged FSR outputs matched pixel-for-pixel.");
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
