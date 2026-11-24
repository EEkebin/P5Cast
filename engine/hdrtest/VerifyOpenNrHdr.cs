using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;

partial class VerifyHdr
{
    static void CheckOpenNrHdr(Assembly compiled,string displayName)
    {
        var screen=Array.Find(Screen.AllScreens,s=>s.DeviceName==displayName);
        if(screen==null) throw new ArgumentException("Missing HDR display "+displayName);
        var type=compiled.GetType("Renderer",true);
        foreach(string backend in new[]{"vulkan","d3d12","d3d11"}) {
            var size=new Size(640,360);
            foreach(string captions in backend=="vulkan" ? new[]{"amd","p5cast"} : new[]{"fruc"})
                CheckSubtitles(compiled,backend,captions,new Size(960,540),screen);
            using(var window=new Form {ClientSize=size,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=screen.WorkingArea.Location})
            using(var renderer=(IDisposable)Activator.CreateInstance(type,new object[]{window.Handle,size,Size.Empty,"hdr10","open-nr",false,backend})) {
                if(!(bool)type.GetProperty("PresenterHdr10").GetValue(renderer,null)) throw new InvalidDataException("Missing HDR10 swap chain.");
                var device=(Device)type.GetProperty("Device").GetValue(renderer,null);
                using(var frame=new Image(device,size.Width,size.Height,Format.R16G16B16A16_Float)) {
                    var data=new ushort[size.Width*size.Height*4];
                    var receive=type.GetMethod("Receive"); var show=type.GetMethod("Show");
                    foreach(string provider in backend=="vulkan" ? new[]{"p5cast","amd"} :
                        backend=="d3d12" ? new[]{"fruc","amd"} : new[]{"fruc"}) {
                        type.GetMethod("SetFrameGenerationProvider").Invoke(renderer,new object[]{provider});
                        int changed=0,worst=0,generatedPatches=0;
                        for(int n=0;n<48;n++) {
                            int bar=n/4;
                            for(int y=0;y<size.Height;y++) for(int x=0;x<size.Width;x++) {
                                int p=4*(y*size.Width+x);
                                float scene=(MovingCode(x,y,n)/4+4*(x%8))/32f;
                                bool patch=bar<7 && Math.Abs(x-size.Width/2)<48 && Math.Abs(y-size.Height/2)<32;
                                for(int k=0;k<3;k++) data[p+k]=HalfBits(patch ? Bars[bar,k] : scene);
                                data[p+3]=0x3c00;
                            }
                            device.ImmediateContext.UpdateSubresource(data,frame.texture,0,size.Width*8,0);
                            receive.Invoke(renderer,new object[]{frame.texture,new Rectangle(Point.Empty,size),false});
                            int before=(int)type.GetProperty("Generated").GetValue(renderer,null);
                            int mid=(int)show.Invoke(renderer,new object[]{"open-nr",.5f,true,false});
                            int real=(int)show.Invoke(renderer,new object[]{"open-nr",1f,true,false});
                            if(!Equals(type.GetProperty("EffectivePictureMode").GetValue(renderer,null),"open-nr"))
                                throw new InvalidDataException("OpenDLSS-NR fell back: "+type.GetProperty("EffectNotice").GetValue(renderer,null));
                            if(bar<7) {
                                bool stable=n%4>=2 && (int)type.GetProperty("Generated").GetValue(renderer,null)>before;
                                if(stable) generatedPatches++;
                                for(int k=0;k<3;k++) {
                                    int expected=Expected(bar,k,1);
                                    worst=Math.Max(worst,Math.Abs(((real>>(10*k))&1023)-expected));
                                    if(stable) worst=Math.Max(worst,Math.Abs(((mid>>(10*k))&1023)-expected));
                                }
                            } else {
                                int original=(int)show.Invoke(renderer,new object[]{"original",1f,true,false});
                                if(Math.Abs((real&1023)-(original&1023))>=2) changed++;
                            }
                        }
                        Console.WriteLine("{0} / {1}: maximum HDR10 patch error {2}/1023, {3} generated patch samples, {4} textured samples changed.",backend,provider,worst,generatedPatches,changed);
                        if(worst>3 || generatedPatches<7 || changed==0) throw new InvalidDataException("HDR color, generation, or neural detail check failed.");
                    }
                }
            }
        }
    }
}
