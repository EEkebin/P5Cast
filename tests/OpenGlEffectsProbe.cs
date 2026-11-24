// Exercise GL/D3D registered textures and the actual back buffer of a hidden test window.
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
    static Texture2D Surface(Device device, Size size, Format format,BindFlags bindings=BindFlags.RenderTarget|BindFlags.ShaderResource) {
        return new Texture2D(device,new Texture2DDescription {Width=size.Width,Height=size.Height,Format=format,MipLevels=1,ArraySize=1,
            BindFlags=bindings,SampleDescription=new SampleDescription(1,0)});
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
    static Color PresentAndRead(Type type,object gl,Texture2D source,int x,int y) {
        var bytes=new byte[4];var error=new byte[4096];
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var state=type.GetField("instance",flags).GetValue(gl);
        var frame=type.GetMethod("p5_gl_present",BindingFlags.NonPublic|BindingFlags.Static);
        int ok=(int)frame.Invoke(null,new object[]{state,source.NativePointer,new[]{x,y},bytes,error,error.Length});
        Require(ok==1,"OpenGL window read failed: "+System.Text.Encoding.UTF8.GetString(error).TrimEnd('\0'));
        return Color.FromArgb(255,bytes[0],bytes[1],bytes[2]);
    }
    static void Run(Device device, Control host,Type type,Action<string> checkContext) {
        var c=device.ImmediateContext;
        var bigger=new Size(125,73);
        var srcArea=new Rectangle(Point.Empty,SizeA);var biggerArea=new Rectangle(Point.Empty,bigger);
        int[] source=new int[SizeA.Width*SizeA.Height];
        for(int y=0;y<SizeA.Height;y++) for(int x=0;x<SizeA.Width;x++) {
            source[y*SizeA.Width+x]=Color.FromArgb(255,((x/5+y/7)%2)*94+71,y*5+11,x*3+15).ToArgb();
        }
        using(var gl=(IDisposable)Activator.CreateInstance(type,host.Handle,device))
        using(var input=Surface(device,SizeA,Format.B8G8R8A8_UNorm))
        using(var target=Surface(device,SizeA,Format.B8G8R8A8_UNorm,BindFlags.RenderTarget))
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
            checkContext("creating the presenter");
            c.UpdateSubresource(source,input,0,SizeA.Width*4);
            Draw(gl,"Original",SizeA,srcArea,output,src);
            checkContext("drawing a frame");
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
            Require(shared>=5,"The OpenGL passes did not use shared GPU textures.");
            var resize=type.GetMethod("ResizeWindow");
            resize.Invoke(gl,new object[]{SizeA});
            type.GetMethod("Present").Invoke(gl,new object[]{target});
            checkContext("presenting a frame");
            Draw(gl,"Original",SizeA,srcArea,output,src);
            foreach(Point p in new[]{new Point(2,3),new Point(54,29)}) {
                Color actual=PresentAndRead(type,gl,target,p.X,p.Y);
                int expected=source[p.Y*SizeA.Width+p.X];
                Require(actual.ToArgb()==expected,"OpenGL window pixel at "+p+" was "+actual+"; expected "+Color.FromArgb(expected));
            }
            resize.Invoke(gl,new object[]{new Size(63,63)});
            Require(PresentAndRead(type,gl,input,24,5).ToArgb()==Color.Black.ToArgb(),"OpenGL resize left a colored letterbox.");
            Require(PresentAndRead(type,gl,input,2,16).ToArgb()==source[3*SizeA.Width+2],"OpenGL resize shifted the letterboxed image.");
        }
    }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [StructLayout(LayoutKind.Explicit,Size=40)]
    struct PixelFormatDescriptor {
        [FieldOffset(0)] public ushort Size;
        [FieldOffset(2)] public ushort Version;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(9)] public byte ColorBits;
    }
    [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr dc,ref PixelFormatDescriptor format);
    [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr dc,int index,ref PixelFormatDescriptor format);
    [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr context);
    [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr dc,IntPtr context);
    [DllImport("opengl32.dll")] static extern IntPtr wglGetCurrentContext();
    [DllImport("opengl32.dll")] static extern IntPtr wglGetCurrentDC();
    static void RunWithCurrentContext(Device device, Control host, Type type) {
        var window=new NativeWindow();
        window.CreateHandle(new CreateParams { ClassStyle=0x20 }); // CS_OWNDC
        var dc=GetDC(window.Handle);
        IntPtr context=IntPtr.Zero;
        try {
            const uint drawToWindow=4,supportOpenGL=32;
            var format=new PixelFormatDescriptor {Size=40,Version=1,Flags=drawToWindow|supportOpenGL,ColorBits=24};
            int index=ChoosePixelFormat(dc,ref format);
            Require(index!=0 && SetPixelFormat(dc,index,ref format),"Could not set the host WGL pixel format.");
            context=wglCreateContext(dc);
            Require(context!=IntPtr.Zero && wglMakeCurrent(dc,context),"Could not bind the host WGL context.");
            Action<string> checkContext=delegate(string action) {
                Require(wglGetCurrentContext()==context && wglGetCurrentDC()==dc,"OpenGL displaced the host WGL context while "+action+".");
            };
            var create=type.GetMethod("p5_gl_create",BindingFlags.NonPublic|BindingFlags.Static);
            var error=new byte[4096];
            IntPtr failed=(IntPtr)create.Invoke(null,new object[]{host.Handle,device.NativePointer,"bad GLSL",error,error.Length});
            Require(failed==IntPtr.Zero && error[0]!=0,"Expected a shader compilation error.");
            checkContext("recovering from shader compilation failure");
            Run(device,host,type,checkContext);
            checkContext("disposing the presenter");
        } finally {
            wglMakeCurrent(IntPtr.Zero,IntPtr.Zero);
            if(context!=IntPtr.Zero) wglDeleteContext(context);
            ReleaseDC(window.Handle,dc);window.DestroyHandle();
        }
    }
    [STAThread] static int Main(string[] args) {
        try {
            var renderer=Assembly.LoadFrom(args[0]).GetType("OpenGlEffects",true);
            using(var host=new Control())
            using(var device=new Device(SharpDX.Direct3D.DriverType.Hardware,DeviceCreationFlags.BgraSupport)) {
                host.ClientSize=new Size(160,120);
                Run(device,host,renderer,action=>Require(wglGetCurrentContext()==IntPtr.Zero,"OpenGL left a context bound while "+action+"."));
                RunWithCurrentContext(device,host,renderer);
            }
            Console.WriteLine("Verified shared OpenGL effects, window RGB pixels, orientation, resized letterbox and restoration of the calling thread's WGL context.");
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
