using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Controls;

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
        var frame=sample();
        Bounds native;
        Require(GetClientRect(video,out native),"Cannot measure the playback HWND.");
        double scaleX=native.Width/Convert.ToDouble(frame["outputWidth"]);
        double scaleY=native.Height/Convert.ToDouble(frame["outputHeight"]);
        double left=scaleX*Convert.ToDouble(frame["pictureX"]),width=scaleX*Convert.ToDouble(frame["pictureWidth"]);
        double top=scaleY*Convert.ToDouble(frame["pictureY"]),height=scaleY*Convert.ToDouble(frame["pictureHeight"]);
        double initial=Convert.ToDouble(frame["wipePosition"]);
        Func<double,int> pixel=f=>(int)Math.Round(left+width*f);
        int y=(int)(top+height*.5);
        // Send input only to this test's private HWND, without moving the user's mouse.
        Action<uint,int,int> mouse=(message,x,row)=>SendMessage(video,message,new IntPtr(message==0x202 ? 0 : 1),Packed(x,row));
        var hit=new NativePoint {X=pixel(initial),Y=y};
        if(backend!="d3d11") Require(FindWindowEx(video,IntPtr.Zero,"P5Cast.SDL.Video",null)!=IntPtr.Zero,
                                      "Portable renderer has no native child.");
        Require(ChildWindowFromPointEx(video,hit,1|2|4)==video,"Renderer child intercepted client mouse input.");
        if(top>=2) {
            mouse(0x201,pixel(initial),(int)top-2);
            Require(GetCapture()!=video,"Letterbox bar grabbed the divider.");
            mouse(0x202,pixel(initial),(int)top-2);
        }
        mouse(0x201,pixel(.2),y);
        Require(GetCapture()!=video,"A click away from the divider started a drag.");
        mouse(0x202,pixel(.2),y);
        // Allow the dashboard's telemetry tick to receive the new picture geometry.
        await Until(delegate {mouse(0x201,pixel(initial),y); return GetCapture()==video;},"Cannot grab the wipe divider.");
        mouse(0x200,pixel(.76),y);
        var slider=(Slider)FindVisual(Find<Border>("MeterHost"),"Slider",e=>System.Windows.Automation.AutomationProperties.GetAutomationId(e)=="WipePosition");
        Require(slider!=null && Math.Abs(slider.Value-76)<1,"The accessible slider did not follow the drag.");
        await Until(()=>Math.Abs(Convert.ToDouble(sample()["wipePosition"])-.76)<.01,"Dragging did not move the rendered divider.");
        SendMessage(video,0x1F,IntPtr.Zero,IntPtr.Zero);
        await Until(()=>Math.Abs(Convert.ToDouble(sample()["wipePosition"])-initial)<.01 && GetCapture()!=video,
                    "Cancel did not restore the original divider position or release capture.");
        mouse(0x201,pixel(initial),y);
        SetCapture(hwnd);
        try {
            mouse(0x200,pixel(.64),y);
            Require(GetCapture()==hwnd && Math.Abs(slider.Value/100-initial)<.01,"The divider kept dragging after capture loss.");
        } finally {ReleaseCapture();}
        mouse(0x201,pixel(initial),y); mouse(0x200,pixel(1.2),y); mouse(0x202,pixel(1.2),y);
        await Until(()=>Convert.ToDouble(sample()["wipePosition"])==1 && GetCapture()!=video,
                    "Dragging past the edge did not clamp and release the divider.");
        if(pixel(1)+3<native.Width) {
            mouse(0x201,pixel(1)+3,y);
            Require(GetCapture()!=video,"The divider intercepted a click in the pillarbox bar.");
            mouse(0x202,pixel(1)+3,y);
        }
        mouse(0x201,pixel(1)-1,y); mouse(0x202,pixel(.5)-1,y);
        await Until(()=>Math.Abs(Convert.ToDouble(sample()["wipePosition"])-.5)<.01,"Cannot grab the divider at the picture edge.");
        if(fullscreen) {
            mouse(0x203,pixel(.1),y);
            await Until(()=>Find<Border>("HeaderBar").IsVisible,"Double-click away from the divider did not exit fullscreen.");
        }
        ViewButton("ViewEnhanced").IsChecked=true;
        await Until(()=>Equals(sample()["comparisonView"],"enhanced"),"Wipe view did not exit.");
        Note(backend+" native video input passed divider dragging, cancellation, endpoint recovery and hit testing.");
    }
}
