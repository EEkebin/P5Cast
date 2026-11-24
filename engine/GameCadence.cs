using System;
using System.Runtime.InteropServices;
using System.Threading;

// Estimates visible picture changes in decoded video. A stationary view contains
// no reliable information about the game's frame rate.
internal sealed class GameCadence
{
    const int Columns=32,Rows=18;
    const double WindowMs=2000,StaleMs=300;
    readonly byte[] previous=new byte[Columns*Rows];
    readonly int[] sectors=new int[12];
    readonly double[] times=new double[256];
    readonly bool[] changed=new bool[256];
    int width,height,depth,first,count;
    double lastChange;
    // Decode owns the sample history. The render thread only reads these values.
    readonly object readings=new object();
    double latest=double.NegativeInfinity,game=-1;

    // Retiring the sampler makes an in-flight decode harmless. Every enable
    // starts a separate history, including off/on between two decoder frames.
    internal static void SetEnabled(ref GameCadence active,bool enabled)
    {
        if(!enabled) {Interlocked.Exchange(ref active,null);return;}
        if(Volatile.Read(ref active)==null)
            Interlocked.CompareExchange(ref active,new GameCadence(),null);
    }

    public double GameFps(double nowMs) {lock(readings) return nowMs-latest<=StaleMs ? game : -1;}

    public void Reset()
    {
        count=first=0;
        lastChange=double.NegativeInfinity;
        lock(readings) {latest=double.NegativeInfinity;game=-1;}
    }

    public void Observe(IntPtr y,int stride,int w,int h,int bits,double nowMs)
    {
        int bytes=bits==10 ? 2 : 1;
        if(y==IntPtr.Zero || (bits!=8 && bits!=10) || w<2 || h<2 || w>4096 || h>2160 ||
            Math.Abs((long)stride)<w*bytes || Math.Abs((long)stride)*h>int.MaxValue) {Reset();return;}
        if(count!=0) {
            double last=times[(first+count-1)%times.Length];
            if(nowMs-last>StaleMs || nowMs<last || width!=w || height!=h || depth!=bits) Reset();
        }
        width=w;height=h;depth=bits;
        // Average a 2x2 patch at each point. Ignore small code-value changes
        // from prediction/quantization on repeated frames and isolated UI motion.
        Array.Clear(sectors,0,sectors.Length);
        int different=0,activeSectors=0;
        for(int row=0;row<Rows;row++) {
            int line=Math.Min(h-2,(2*row+1)*h/(2*Rows))*stride;
            for(int col=0;col<Columns;col++) {
                int offset=line+Math.Min(w-2,(2*col+1)*w/(2*Columns))*bytes;
                int sample;
                if(bits==8) {
                    ushort a=(ushort)Marshal.ReadInt16(y,offset),b=(ushort)Marshal.ReadInt16(y,offset+stride);
                    sample=((a&255)+(a>>8)+(b&255)+(b>>8)+2)>>2;
                } else {
                    uint a=(uint)Marshal.ReadInt32(y,offset),b=(uint)Marshal.ReadInt32(y,offset+stride);
                    // FFmpeg AV_PIX_FMT_YUV420P10LE stores 10 bits in each 16-bit word.
                    sample=((int)((a&1023)+((a>>16)&1023)+(b&1023)+((b>>16)&1023)))>>4;
                }
                int index=row*Columns+col;
                if(count!=0 && Math.Abs(sample-previous[index])>=5) {
                    different++;
                    if(++sectors[(row/6)*4+col/8]==3) activeSectors++;
                }
                previous[index]=(byte)sample;
            }
        }
        bool visible=different>=18 && activeSectors>=3;
        if(visible) lastChange=nowMs;
        while(count!=0 && (count==times.Length || nowMs-times[first]>WindowMs)) {
            first=(first+1)%times.Length;count--;
        }
        int end=(first+count)%times.Length;
        times[end]=nowMs;changed[end]=visible;count++;
        double span=nowMs-times[first],estimated=-1;
        if(span>=900 && count>=8) {
            int changes=0,quiet=0,longestQuiet=0;
            for(int i=1;i<count;i++) {
                if(changed[(first+i)%times.Length]) {changes++;quiet=0;}
                else {quiet++;longestQuiet=Math.Max(quiet,longestQuiet);}
            }
            // Require motion throughout the window. For example, a few camera
            // cuts or a short pan followed by a menu should produce no estimate.
            if(changes>=8 && nowMs-lastChange<=200 && longestQuiet<=Math.Max(4,3*(count-1)/(double)changes))
                estimated=changes*1000.0/span;
        }
        lock(readings) {latest=nowMs;game=estimated;}
    }
}
