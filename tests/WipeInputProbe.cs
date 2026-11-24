using System;
using System.Collections.Generic;
using System.Reflection;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;

internal static partial class WindowChromeProbe
{
    [DllImport("user32.dll")] static extern IntPtr GetCapture();
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr parent,NativePoint point,uint skip);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string cls,string title);
    static async Task CheckWipeInput(MethodInfo snapshot,string backend,bool hdr,IntPtr popout=default(IntPtr))
    {
        var json=new JavaScriptSerializer();
        Func<Dictionary<string,object>> sample=()=>json.Deserialize<Dictionary<string,object>>((string)snapshot.Invoke(null,null));
        var original=PictureButton("original");
        bool startedOriginal=original.IsChecked==true;
        if(startedOriginal) SelectPlaybackMode(PictureButton("sharp"));
        string comparison=hdr ? "hdr-sdr" : "wipe";
        var button=ViewButton(hdr ? "ViewHdrSdr" : "ViewWipe");
        Require(button!=null && button.IsEnabled,"Wipe view is missing.");
        SelectPlaybackMode(button);
        await Until(()=>Equals(sample()["comparisonView"],comparison),comparison+" did not start.");
        bool fullscreen=backend=="d3d12" && popout==IntPtr.Zero;
        if(fullscreen) {
            Click("Fullscreen");
            var full=Screen().Full;
            await Until(()=>Fits(Rect(video),full,2) && Convert.ToInt32(sample()["surfaceWidth"])==full.Width,
                        "Wipe did not expand to the full-size playback surface.");
        }
        var meter=Find<Border>("MeterHost");
        var slider=(Slider)FindVisual(meter,"Slider",e=>AutomationProperties.GetAutomationId(e)=="WipePosition");
        var orientation=(ToggleButton)FindVisual(meter,"ToggleButton",e=>AutomationProperties.GetAutomationId(e)=="WipeHorizontal");
        Require(slider!=null && orientation!=null,"Wipe needs an accessible position and orientation control.");
        bool initialOrientation=orientation.IsChecked==true;
        foreach(bool horizontal in new[]{false,true}) {
            orientation.IsChecked=horizontal;
            await Until(()=>Equals(sample()["wipeHorizontal"],horizontal),"Orientation did not reach the renderer.");
            var frame=sample();
            Bounds native;
            Require(GetClientRect(video,out native),"Cannot measure the playback HWND.");
            double sx=native.Width/Convert.ToDouble(frame["surfaceWidth"]),sy=native.Height/Convert.ToDouble(frame["surfaceHeight"]);
            var picture=new Rectangle((int)Math.Round(sx*Convert.ToDouble(frame["pictureX"])),(int)Math.Round(sy*Convert.ToDouble(frame["pictureY"])),
                                     (int)Math.Round(sx*Convert.ToDouble(frame["pictureWidth"])),(int)Math.Round(sy*Convert.ToDouble(frame["pictureHeight"])));
            if(!horizontal) await CheckLoupeInput(backend,picture,native,sample,popout==IntPtr.Zero ? hwnd : popout);
            await CheckWipeAxis(backend,picture,horizontal,slider,sample);
        }
        if(fullscreen) {
            SendMessage(video,0x203,IntPtr.Zero,Packed(80,80));
            await Until(()=>Find<Border>("HeaderBar").IsVisible,"Double-click away from the divider did not exit fullscreen.");
        }
        orientation.IsChecked=initialOrientation;
        SelectPlaybackMode(ViewButton("ViewEnhanced"));
        await Until(()=>Equals(sample()["comparisonView"],"enhanced"),"Wipe view did not exit.");
        if(startedOriginal) {
            SelectPlaybackMode(original);
            await Until(()=>Equals(sample()["effectivePictureMode"],"original"),"Wipe test failed to restore the initial picture.");
        }
        Note(backend+" passed native dragging, cancellation and inset handle recovery on both axes.");
    }

    static async Task CheckLoupeInput(string backend,Rectangle picture,Bounds native,Func<Dictionary<string,object>> sample,IntPtr focusedWindow) {
        int x=picture.Left+picture.Width/4,y=picture.Top+picture.Height/4;
        Action<IntPtr,int> wheel=(target,delta)=>SendMessage(target,0x20A,new IntPtr(delta<<16),IntPtr.Zero);
        SendMessage(video,0x201,new IntPtr(1),Packed(x,y));
        await Until(()=>GetCapture()==video && Equals(sample()["loupeActive"],true),"Holding the picture did not open the loupe.");
        Require(Convert.ToInt32(sample()["loupeLevel"])==2,"The loupe did not open at 2x.");
        int moved=x+12;
        SendMessage(video,0x200,new IntPtr(1),Packed(moved,y));
        await Until(()=>Math.Abs(Convert.ToDouble(sample()["loupeX"])-(moved+.5)/native.Width)<.0005 &&
                        Math.Abs(Convert.ToDouble(sample()["loupeY"])-(y+.5)/native.Height)<.0005,
                    "The loupe did not track the pointer in screen pixels.");
        if(backend=="d3d11") {
            wheel(video,60); wheel(focusedWindow,60);
            await Until(()=>Convert.ToInt32(sample()["loupeLevel"])==3,"Partial wheel deltas did not accumulate to one zoom step.");
            wheel(video,1200);
            await Until(()=>Convert.ToInt32(sample()["loupeLevel"])==5,"The loupe did not stop at 5x.");
            wheel(focusedWindow,-1200);
            await Until(()=>Convert.ToInt32(sample()["loupeLevel"])==0,"The focused window did not forward wheel input down to native scale.");
            wheel(video,120);
            await Until(()=>Convert.ToInt32(sample()["loupeLevel"])==2,"One wheel step did not leave native scale.");
        }
        SendMessage(video,0x202,IntPtr.Zero,Packed(-5,y));
        await Until(()=>GetCapture()!=video && Equals(sample()["loupeActive"],false),"Releasing outside the picture left the loupe active.");
        SendMessage(video,0x201,new IntPtr(1),Packed(x,y));
        Require(GetCapture()==video,"Cannot reopen loupe.");
        SetCapture(hwnd);
        try {Require(GetCapture()==hwnd,"Loupe stole capture back.");} finally {ReleaseCapture();}
        SendMessage(video,0x201,new IntPtr(1),Packed(x,y)); SendMessage(video,0x1F,IntPtr.Zero,IntPtr.Zero);
        Require(GetCapture()!=video,"Cancel mode left the loupe captured.");
        Note(backend+" passed hold-to-inspect input, tracking and release.");
    }

    static async Task CheckWipeAxis(string backend,Rectangle picture,bool horizontal,Slider slider,
                                    Func<Dictionary<string,object>> sample) {
        double initial=Convert.ToDouble(sample()["wipePosition"]);
        double scale=Convert.ToDouble(sample()["wipeGripScale"]);
        var layout=new WipeLayout(picture,horizontal,initial,scale);
        int crossMin=horizontal ? picture.Left : picture.Top;
        int crossLength=horizontal ? picture.Width : picture.Height;
        int crossCenter=crossMin+crossLength/2, crossQuarter=crossMin+crossLength/4;
        int minimum=horizontal ? picture.Top : picture.Left;
        int maximum=horizontal ? picture.Bottom : picture.Right;
        Func<int,int,NativePoint> point=(axis,cross)=>new NativePoint {X=horizontal ? cross : axis,Y=horizontal ? axis : cross};
        // Target only this private HWND. The user's physical mouse stays in place.
        Action<uint,int,int> mouse=delegate(uint msg,int axis,int cross) {
            var p=point(axis,cross); SendMessage(video,msg,new IntPtr(msg==0x202 ? 0 : 1),Packed(p.X,p.Y));
        };
        // D3D11 and Vulkan present directly to the video HWND.
        if(backend=="d3d12" || backend=="opengl") {
            string childClass=backend=="d3d12" ? "STATIC" : "P5Cast.SDL.Video";
            Require(FindWindowEx(video,IntPtr.Zero,childClass,null)!=IntPtr.Zero,backend+" has no native presentation child.");
        }
        Require(ChildWindowFromPointEx(video,point(layout.Center,crossQuarter),1|2|4)==video,"Renderer child intercepted client mouse input.");
        mouse(0x201,layout.Center,crossMin-2);
        Require(GetCapture()!=video,"A click outside the picture grabbed the divider.");
        mouse(0x202,layout.Center,crossMin-2);
        mouse(0x201,layout.Center,crossQuarter);
        await Until(()=>GetCapture()==video && Equals(sample()["loupeActive"],true) && Convert.ToInt32(sample()["wipeGripState"])==2,
                    backend+" did not open the loupe when grabbing the "+(horizontal?"horizontal":"vertical")+" divider rail.");
        var forward=new WipeLayout(picture,horizontal,.76,scale);
        mouse(0x200,forward.Center,crossQuarter);
        Require(Math.Abs(slider.Value-76)<1,"The drawer slider did not track the drag.");
        await Until(()=>Math.Abs(Convert.ToDouble(sample()["wipePosition"])-.76)<.01,"Dragging did not move the rendered divider.");
        Require(Convert.ToInt32(sample()["wipeGripState"])==2,"Captured divider did not use the drawer's active grip color.");
        Bounds client;
        Require(GetClientRect(video,out client),"Cannot measure the loupe surface.");
        Func<NativePoint,bool> lensAt=delegate(NativePoint pos) {
            var frame=sample();
            return Equals(frame["loupeActive"],true) &&
                   Math.Abs(Convert.ToDouble(frame["loupeX"])-(pos.X+.5)/client.Width)<.0005 &&
                   Math.Abs(Convert.ToDouble(frame["loupeY"])-(pos.Y+.5)/client.Height)<.0005;
        };
        await Until(()=>lensAt(point(forward.Center,crossQuarter)),"The loupe did not follow the divider forward.");
        var back=new WipeLayout(picture,horizontal,.31,scale);
        mouse(0x200,back.Center,crossCenter);
        SendMessage(video,0x20A,new IntPtr(120<<16),IntPtr.Zero);
        await Until(()=>lensAt(point(back.Center,crossCenter)) && Math.Abs(Convert.ToDouble(sample()["wipePosition"])-.31)<.01 &&
                        Convert.ToInt32(sample()["loupeLevel"])==3 && GetCapture()==video,
                    "The reverse drag and wheel zoom did not work together.");
        SendMessage(video,0x20A,new IntPtr(-120<<16),IntPtr.Zero);
        SendMessage(video,0x1F,IntPtr.Zero,IntPtr.Zero);
        await Until(()=>Math.Abs(Convert.ToDouble(sample()["wipePosition"])-initial)<.01 && GetCapture()!=video &&
                        Equals(sample()["loupeActive"],false),"Cancel did not dismiss the loupe and restore the divider.");
        mouse(0x201,layout.Center,crossQuarter); SetCapture(hwnd);
        try {
            mouse(0x200,new WipeLayout(picture,horizontal,.64,scale).Center,crossQuarter);
            Require(GetCapture()==hwnd && Math.Abs(slider.Value/100-initial)<.01,"The divider kept moving after capture loss.");
        } finally {ReleaseCapture();}
        foreach(int end in new[]{0,1}) {
            int outside=end==0 ? minimum-20 : maximum+20;
            mouse(0x201,layout.Center,crossQuarter); mouse(0x200,outside,crossQuarter); mouse(0x202,outside,crossQuarter);
            await Until(()=>Convert.ToDouble(sample()["wipePosition"])==end && GetCapture()!=video,"The drag did not stop at its limit.");
            var endpoint=new WipeLayout(picture,horizontal,end,scale);
            Require(picture.Contains(endpoint.Handle) && endpoint.Center>minimum && endpoint.Center<maximum-1,
                    "A handle crossed the picture boundary.");
            // The full-width invisible hit zone remains usable beyond either edge of the slim painted grip.
            int outer=endpoint.Center+(end==0 ? -1 : 1)*(int)Math.Max(4,Math.Floor(6*scale));
            mouse(0x201,outer,crossCenter);
            Require(GetCapture()==video,"The outer part of the handle cannot be grabbed at its limit.");
            int target=layout.Center+outer-endpoint.Center;
            mouse(0x202,target,crossCenter);
            await Until(()=>Math.Abs(Convert.ToDouble(sample()["wipePosition"])-initial)<.01 && GetCapture()!=video &&
                            Equals(sample()["loupeActive"],false),"Releasing the inset handle did not commit the drag and dismiss the loupe.");
        }
    }
}
