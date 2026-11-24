using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;

partial class VerifyHdr
{
    // Sample the production renderer before Present.
    // A narrow, outlined H appears, holds, and disappears over a camera pan.
    static void CheckSubtitles(Assembly compiled,string backend,string provider,Size viewport,Screen hdrScreen=null)
    {
        const int w=640,h=360;
        var type=compiled.GetType("Renderer",true);
        var size=new Size(w,h);
        bool hdr=hdrScreen!=null;
        using(var window=new Form {ClientSize=viewport,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,
            Location=hdr ? hdrScreen.WorkingArea.Location : Point.Empty})
        using(var renderer=(IDisposable)Activator.CreateInstance(type,new object[]{window.Handle,viewport,Size.Empty,
            hdr?"hdr10":"off","open-nr",false,backend})) {
            var device=(Device)type.GetProperty("Device").GetValue(renderer,null);
            var receive=type.GetMethod("Receive"); var show=type.GetMethod("Show");
            var count=type.GetProperty("Generated");
            type.GetMethod("SetFrameGenerationProvider").Invoke(renderer,new object[]{provider});
            using(var frame=new Image(device,w,h,hdr?Format.R16G16B16A16_Float:Format.B8G8R8A8_UNorm)) {
                int[] data=new int[w*h];
                var floats=hdr ? new ushort[w*h*4] : null;
                var light=new ushort[256];
                for(int i=0;i<light.Length;i++) light[i]=HalfBits(12.5f*i*i/(255*255));
                int lastReal=0,midpoints=0,transitions=0,sceneMidpoints=0,worst=0;
                bool hadInk=false;
                const int frameCount=32;
                for(int n=0;n<frameCount;n++) {
                    bool ink=n<16 && n%8>=2 && n%8<=5;
                    for(int y=0;y<h;y++) for(int x=0;x<w;x++) {
                        int gray=48+MovingCode(x+3,y+3,n)/8+(int)(34*Math.Sin((x-8*n)*0.08+y*0.015));
                        int dx=x-w/2,dy=y-h/2;
                        if(ink && Math.Abs(dy)<=8 && (Math.Abs(dx)<=1 || Math.Abs(dx-7)<=1 || (dx>=0 && dx<=7 && Math.Abs(dy)<=1))) {
                            gray=(dx==0 || dx==7 || (dy==0 && dx>=0 && dx<=7)) ? 255 : 8;
                        }
                        int p=y*w+x;
                        if(hdr) {
                            floats[4*p]=floats[4*p+1]=floats[4*p+2]=light[gray];
                            floats[4*p+3]=0x3c00;
                        } else data[p]=unchecked((int)0xff000000)|gray*0x10101;
                    }
                    if(hdr) device.ImmediateContext.UpdateSubresource(floats,frame.texture,0,w*8,0);
                    else device.ImmediateContext.UpdateSubresource(data,frame.texture,0,w*4,0);
                    receive.Invoke(renderer,new object[]{frame.texture,new Rectangle(Point.Empty,size),false});
                    int before=(int)count.GetValue(renderer,null);
                    float phase=provider=="p5cast" ? (1+n%3)*.25f : .5f;
                    int maxCode=hdr?1023:255;
                    int midpoint=(int)show.Invoke(renderer,new object[]{"open-nr",phase,true,false})&maxCode;
                    int real=(int)show.Invoke(renderer,new object[]{"open-nr",1f,true,false})&maxCode;
                    int minimum=hdr ? (size==viewport ? 765 : 710) : (size==viewport ? 250 : 190);
                    if(ink && real<minimum) throw new InvalidDataException("The real subtitle stroke dimmed to "+real+".");
                    if((int)count.GetValue(renderer,null)>before && (hadInk || ink)) {
                        midpoints++;
                        if(ink!=hadInk) transitions++;
                        worst=Math.Max(worst,Math.Abs(midpoint-lastReal));
                    }
                    if((int)count.GetValue(renderer,null)>before && !hadInk && !ink &&
                        Math.Abs(midpoint-lastReal)>2 && Math.Abs(midpoint-real)>2) sceneMidpoints++;
                    lastReal=real;
                    hadInk=ink;
                }
                Console.WriteLine("{0} / OpenDLSS-NR + {1} at {2}: {3} caption frames, {4} transitions, worst stroke error {5}/{6}; {7} distinct scene samples.",backend,provider,viewport,midpoints,transitions,worst,hdr?1023:255,sceneMidpoints);
                if((int)type.GetProperty("OpenNrEvaluations").GetValue(renderer,null)!=frameCount || midpoints<5 || transitions<2 || sceneMidpoints<1 || worst>3)
                    throw new InvalidDataException("OpenDLSS-NR / "+provider+" subtitle regression. "+type.GetProperty("FrameGenerationNotice").GetValue(renderer,null));
            }
        }
    }
}
