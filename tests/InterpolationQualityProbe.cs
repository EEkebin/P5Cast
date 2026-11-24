using System;
using System.Drawing;
using System.Reflection;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;

internal static partial class EmbeddedProbe {
    // Translate one textured patch on a stationary background. The texture gives a
    // midpoint a unique position and distinguishes motion compensation from a crossfade.
    static double Lattice(int x,int y) {
        uint hash=unchecked((uint)(x*374761393+y*668265263));
        hash=(hash^(hash>>13))*1274126177u;
        return ((hash^(hash>>16))&255)/255.0;
    }
    static double Noise(int u,int v,int scale) {
        int x=u/scale,y=v/scale;
        double a=(u%scale)/(double)scale,b=(v%scale)/(double)scale;
        a=a*a*(3-2*a); b=b*b*(3-2*b);
        return (1-b)*((1-a)*Lattice(x,y)+a*Lattice(x+1,y))+b*((1-a)*Lattice(x,y+1)+a*Lattice(x+1,y+1));
    }
    static uint[] MotionFixture(int width,int height,int offset) {
        var pixels=new uint[width*height];
        for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
            int u=x-48-offset,v=y-24;
            int code=18;
            if(u>=0 && u<96 && v>=0 && v<48)
                code=(int)Math.Round(36+86*Noise(u,v,12)+100*Noise(u,v,5)+30*Noise(u,v,2));
            pixels[width*y+x]=0xff000000u+(uint)code*0x010101u;
        }
        return pixels;
    }
    static void CheckInterpolation(Type type,IntPtr host) {
        const int width=192,height=96,distance=8;
        var size=new Size(width,height);
        using(var renderer=(IDisposable)Activator.CreateInstance(type,new object[]{host,size,Size.Empty,"off","original",true,"d3d12"})) {
            var device=(Device)type.GetProperty("Device").GetValue(renderer,null);
            var backBuffer=type.GetField("backBuffer",BindingFlags.Instance|BindingFlags.NonPublic);
            using(var input=new Texture2D(device,new Texture2DDescription{Width=width,Height=height,Format=Format.B8G8R8A8_UNorm,
                ArraySize=1,MipLevels=1,SampleDescription=new SampleDescription(1,0),BindFlags=BindFlags.ShaderResource})) {
                Action<int> receive=offset=>{
                    device.ImmediateContext.UpdateSubresource(MotionFixture(width,height,offset),input,0,width*4,0);
                    type.GetMethod("Receive").Invoke(renderer,new object[]{input,new Rectangle(Point.Empty,size),false});
                    type.GetMethod("SetFrameTiming").Invoke(renderer,new object[]{16.67,offset!=0});
                };
                Func<float,int[]> present=phase=>{
                    type.GetMethod("Show").Invoke(renderer,new object[]{"original",phase,false,false});
                    var texture=(Texture2D)backBuffer.GetValue(renderer);
                    Check(texture.Description.Width==width && texture.Description.Height==height,"Unexpected presentation size.");
                    return ReadPresentedTexture(device,texture);
                };
                receive(0);
                int[] first=present(1);
                receive(distance);
                foreach(float phase in new[]{.25f,.5f,.75f,1f}) {
                    int[] output=present(phase);
                    var errors=new double[distance+1];
                    double crossfadeError=0;
                    int samples=0;
                    for(int y=36;y<60;y++) for(int x=72;x<136;x++) {
                        int i=y*width+x,value=output[i]&255;
                        double crossfade=(first[i]&255)*(1-phase)+(first[i-distance]&255)*phase;
                        crossfadeError+=Math.Abs(value-crossfade);
                        for(int shift=0;shift<=distance;shift++) errors[shift]+=Math.Abs(value-(first[i-shift]&255));
                        samples++;
                    }
                    int best=0;
                    for(int shift=1;shift<=distance;shift++) if(errors[shift]<errors[best]) best=shift;
                    Console.WriteLine("P5Cast D3D12 phase {0:F2}: patch offset {1} px, texture MAE {2:F2}, crossfade MAE {3:F2}",
                        phase,best,errors[best]/samples,crossfadeError/samples);
                    Check(best==(int)(phase*distance),"Presented texture is at the wrong position.");
                    if(phase<1) Check(errors[best]<crossfadeError*.85,"Generated frame resembles a crossfade.");
                    else Check(errors[best]==0,"Real frame changed when generating the intermediates.");
                }
            }
        }
    }
}
