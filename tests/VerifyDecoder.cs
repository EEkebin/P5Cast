using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class VerifyDecoder
{
    [StructLayout(LayoutKind.Sequential)]
    struct Picture {
        public IntPtr Y,U,V;
        public int StrideY,StrideU,StrideV,Width,Height,Depth,FullRange,Primaries,Transfer,Matrix;
    }
    const string Bridge="Cadrivect.Decoder.dll";
    [DllImport(Bridge,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode,ExactSpelling=true)]
    static extern IntPtr CreateDecoder(string codec,string util,int id);
    [DllImport(Bridge,CallingConvention=CallingConvention.Cdecl)] static extern int SubmitPacket(IntPtr api,byte[] bytes,int length);
    [DllImport(Bridge,CallingConvention=CallingConvention.Cdecl)] static extern int NextPicture(IntPtr api,out IntPtr frame,out Picture picture);
    [DllImport(Bridge,CallingConvention=CallingConvention.Cdecl)] static extern void FreePicture(IntPtr api,IntPtr frame);
    [DllImport(Bridge,CallingConvention=CallingConvention.Cdecl)] static extern void DestroyDecoder(IntPtr api);

    // Lossless libx264/libx265 fixtures contain "PS5" plus a one-pixel horizontal and vertical stroke.
    static readonly string[] Glyphs={
        "11110100011000111110100001000010000","01111100001000001110000010000111110","11111100001000011110000010000111110"
    };
    static bool Ink(int x,int y) {
        int col=(x-16)/5,row=(y-12)/5,g=col/6;
        return (x>=16 && y>=12 && g<3 && row<7 && col%6<5 && Glyphs[g][row*5+col%6]=='1') ||
               (x==109 && y>5 && y<58) || (y==52 && x>8 && x<119);
    }
    static void Check(IntPtr api,string file,int depth,bool inverse) {
        byte[] packet=File.ReadAllBytes(file);
        if(SubmitPacket(api,packet,packet.Length)<0) throw new InvalidDataException("Decoder rejected "+file);
        IntPtr frame;
        Picture p;
        if(NextPicture(api,out frame,out p)!=1) throw new InvalidDataException("No decoded picture for "+file);
        try {
            int primaries=depth==10 ? 9 : 1, transfer=depth==10 ? 16 : 1;
            if(p.Width!=128 || p.Height!=64 || p.Depth!=depth || p.FullRange!=0 || p.Primaries!=primaries ||
               p.Matrix!=primaries || p.Transfer!=transfer) throw new InvalidDataException("Incorrect dimensions, format or color metadata.");
            IntPtr[] planes={p.Y,p.U,p.V}; int[] strides={p.StrideY,p.StrideU,p.StrideV};
            for(int plane=0;plane<3;plane++) {
                int width=plane==0 ? p.Width : p.Width/2, height=plane==0 ? p.Height : p.Height/2;
                for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
                    int offset=y*strides[plane]+x*(depth==10 ? 2 : 1);
                    int sample=depth==10 ? Marshal.ReadInt16(planes[plane],offset) : Marshal.ReadByte(planes[plane],offset);
                    int expected=plane!=0 ? 128 : Ink(x,y)!=inverse ? 235 : 16;
                    if(depth==10) expected*=4;
                    if(sample!=expected) throw new InvalidDataException(string.Format("{0} plane {1} at {2},{3}: got {4} expected {5}",file,plane,x,y,sample,expected));
                }
            }
            Console.WriteLine("{0}: 128 x 64 YUV420 {1}-bit, 12,288 plane samples exact, color metadata matched.",Path.GetFileName(file),depth);
        } finally {FreePicture(api,frame);}
        if(NextPicture(api,out frame,out p)!=0) throw new InvalidDataException("Unexpected second picture.");
    }
    static void Test(string runtime,string fixtures,int id,params string[] frames) {
        IntPtr api=CreateDecoder(Path.Combine(runtime,"avcodec-63.dll"),Path.Combine(runtime,"avutil-61.dll"),id);
        if(api==IntPtr.Zero) throw new InvalidOperationException("Could not load the matched decoder libraries for mode "+id);
        try {
            foreach(string name in frames) Check(api,Path.Combine(fixtures,name),id==3 ? 10 : 8,name.Contains("inverted"));
        } finally {DestroyDecoder(api);}
    }
    static int Main(string[] args) {
        try {
            Test(args[0],args[1],1,"text.h264","text-inverted.h264","text.h264");
            Test(args[0],args[1],2,"text.hevc");
            Test(args[0],args[1],3,"text-pq10.hevc");
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e.Message); return 1;}
    }
}
