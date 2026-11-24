using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SharpDX.Direct3D11;

internal sealed class OpenGlEffects : IFramePresenter
{
    const string Library="P5Cast.Presentation.dll";
    const DllImportSearchPath Search=DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32|DllImportSearchPath.UseDllDirectoryForDependencies;
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi,ExactSpelling=true)]
    static extern IntPtr p5_gl_create(IntPtr parent,IntPtr device,string shader,[Out] byte[] error,int length);
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern int p5_gl_draw(IntPtr gl,int mode,IntPtr output,IntPtr input,IntPtr prior,IntPtr vectors,
        int sourceWidth,int sourceHeight,int x,int y,int width,int height,int[] clip,float phase,[Out] byte[] error,int length);
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern int p5_gl_shared_textures(IntPtr gl);
    public int SharedTextures {get {return p5_gl_shared_textures(instance);}}
    public bool Hdr10 {get {return false;}}
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern int p5_gl_resize(IntPtr gl,int width,int height,[Out] byte[] error,int length);
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern int p5_gl_present(IntPtr gl,IntPtr resource,int[] sampleXY,[Out] byte[] sample,[Out] byte[] error,int length);
    [DefaultDllImportSearchPaths(Search)]
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    static extern void p5_gl_destroy(IntPtr gl);

    IntPtr instance;
    readonly byte[] error=new byte[4096];
    readonly int[] scissor=new int[4];
    static string Shader(string resource) {
        using(var reader=new StreamReader(typeof(OpenGlEffects).Assembly.GetManifestResourceStream(resource)))
            return reader.ReadToEnd();
    }
    public OpenGlEffects(IntPtr parent,Device device) {
        string source=Shader("Effects.glsl").Replace("#include \"ffx_a.h\"",Shader("ffx_a.h"))
            .Replace("#include \"ffx_fsr1.h\"",Shader("ffx_fsr1.h"));
        instance=p5_gl_create(parent,device.NativePointer,source,error,error.Length);
        if(instance==IntPtr.Zero) throw Failure();
    }
    InvalidOperationException Failure() {
        int end=Array.IndexOf(error,(byte)0);
        return new InvalidOperationException("OpenGL playback: "+Encoding.UTF8.GetString(error,0,end<0?error.Length:end));
    }
    static readonly string[] Passes={"Original","Sharp","Easu","Rcas","Motion","Interpolate"};
    public bool Draw(string name,RenderTargetView target,Rectangle area,ShaderResourceView input,ShaderResourceView prior,
                     ShaderResourceView vectors,Rectangle? clip,float phase,Size source) {
        int pass=Array.IndexOf(Passes,name);
        if(pass<0) return false;
        if(clip.HasValue) {
            Rectangle r=clip.Value;
            scissor[0]=r.X;scissor[1]=r.Y;scissor[2]=r.Width;scissor[3]=r.Height;
        }
        using(var output=target.Resource)
        using(var color=input.Resource)
        using(var previous=prior==null?null:prior.Resource)
        using(var flow=vectors==null?null:vectors.Resource) {
            if(p5_gl_draw(instance,pass,output.NativePointer,color.NativePointer,
                previous==null?IntPtr.Zero:previous.NativePointer,flow==null?IntPtr.Zero:flow.NativePointer,
                source.Width,source.Height,area.X,area.Y,area.Width,area.Height,clip.HasValue?scissor:null,phase,error,error.Length)==0) throw Failure();
        }
        return true;
    }
    public void Dispose() {if(instance!=IntPtr.Zero) {p5_gl_destroy(instance);instance=IntPtr.Zero;}}
    public void ResizeWindow(Size size) {
        if(p5_gl_resize(instance,size.Width,size.Height,error,error.Length)==0) throw Failure();
    }
    public void Present(Texture2D target) {
        if(p5_gl_present(instance,target.NativePointer,null,null,error,error.Length)==0) throw Failure();
    }
}
