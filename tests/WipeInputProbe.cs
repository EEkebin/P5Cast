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
    static async Task CheckWipeInput(MethodInfo snapshot,string backend)
    {
        var json=new JavaScriptSerializer();
        Func<Dictionary<string,object>> sample=()=>json.Deserialize<Dictionary<string,object>>((string)snapshot.Invoke(null,null));
        var button=ViewButton("ViewWipe");
        Require(button!=null && button.IsEnabled,"Wipe view is missing.");
        button.IsChecked=true;
        await Until(()=>Equals(sample()["comparisonView"],"wipe"),"Wipe view did not start.");
        bool fullscreen=backend=="d3d12";
        if(fullscreen) {
            Click("Fullscreen");
            var full=Screen().Full;
            await Until(()=>Fits(Rect(video),full,2) && Convert.ToInt32(sample()["outputWidth"])==full.Width,
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
            double sx=native.Width/Convert.ToDouble(frame["outputWidth"]),sy=native.Height/Convert.ToDouble(frame["outputHeight"]);
            var picture=new Rectangle((int)Math.Round(sx*Convert.ToDouble(frame["pictureX"])),(int)Math.Round(sy*Convert.ToDouble(frame["pictureY"])),
                                     (int)Math.Round(sx*Convert.ToDouble(frame["pictureWidth"])),(int)Math.Round(sy*Convert.ToDouble(frame["pictureHeight"])));
            await CheckWipeAxis(backend,picture,horizontal,slider,sample);
        }
        if(fullscreen) {
            SendMessage(video,0x203,IntPtr.Zero,Packed(80,80));
            await Until(()=>Find<Border>("HeaderBar").IsVisible,"Double-click away from the divider did not exit fullscreen.");
        }
        orientation.IsChecked=initialOrientation;
        ViewButton("ViewEnhanced").IsChecked=true;
        await Until(()=>Equals(sample()["comparisonView"],"enhanced"),"Wipe view did not exit.");
        Note(backend+" passed native dragging, cancellation and inset handle recovery on both axes.");
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
        if(backend!="d3d11") Require(FindWindowEx(video,IntPtr.Zero,"P5Cast.SDL.Video",null)!=IntPtr.Zero,
                                      "Portable renderer has no native child.");
        Require(ChildWindowFromPointEx(video,point(layout.Center,crossQuarter),1|2|4)==video,"Renderer child intercepted client mouse input.");
        mouse(0x201,layout.Center,crossMin-2);
        Require(GetCapture()!=video,"A click outside the picture grabbed the divider.");
        mouse(0x202,layout.Center,crossMin-2);
        mouse(0x201,minimum+1,crossQuarter);
        Require(GetCapture()!=video,"A click away from the divider started a drag.");
        mouse(0x202,minimum+1,crossQuarter);
        await Until(delegate {mouse(0x201,layout.Center,crossQuarter); return GetCapture()==video;},"Cannot grab the divider rail.");
        mouse(0x200,new WipeLayout(picture,horizontal,.76,scale).Center,crossQuarter);
        Require(Math.Abs(slider.Value-76)<1,"The drawer slider did not track the drag.");
        await Until(()=>Math.Abs(Convert.ToDouble(sample()["wipePosition"])-.76)<.01,"Dragging did not move the rendered divider.");
        Require(Convert.ToInt32(sample()["wipeGripState"])==2,"Captured divider did not use the drawer's active grip color.");
        SendMessage(video,0x1F,IntPtr.Zero,IntPtr.Zero);
        await Until(()=>Math.Abs(Convert.ToDouble(sample()["wipePosition"])-initial)<.01 && GetCapture()!=video,"Cancel did not restore the drag.");
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
            await Until(()=>Math.Abs(Convert.ToDouble(sample()["wipePosition"])-initial)<.01,"Dragging the inset handle failed to return to its original position.");
        }
    }
}
