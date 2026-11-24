using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;

internal static partial class WindowChromeProbe
{
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr child,uint flags);
    static bool CheckPopoutOption {get {return Environment.GetEnvironmentVariable("P5CAST_PROBE_POPOUT")=="1";}}
    static Window DetachedPlayer() {
        return Application.Current.Windows.OfType<Window>().SingleOrDefault(w=>w.IsVisible && w.Title=="P5Cast Player");
    }
    static async Task<Window> OpenDetachedPlayer() {
        Click("PopoutVideo");
        await Until(()=>DetachedPlayer()!=null &&
            GetAncestor(video,2)==new WindowInteropHelper(DetachedPlayer()).Handle,
            "The existing playback HWND did not move into the detached player.");
        return DetachedPlayer();
    }
    static void ClickPlayer(Window player,string id) {
        var button=FindVisual(player,"Button",e=>e.Name==id) as Button;
        Require(button!=null && button.IsVisible && button.IsEnabled,"Missing player action "+id);
        ((IInvokeProvider)Peer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    static Task WaitForDock() {
        return Until(()=>DetachedPlayer()==null && GetAncestor(video,2)==hwnd && Video()==video,
            "Docking did not restore the same playback HWND to the dashboard.");
    }
    static async Task CheckIdlePopout() {
        var player=await OpenDetachedPlayer();
        Require(Peer(Find<Button>("OpenSource")).GetItemStatus()=="Ready","Popping out the idle screen started playback.");
        ClickPlayer(player,"DockVideo");
        await WaitForDock();
        await OpenDetachedPlayer();
        Click("PopoutVideo");
        await WaitForDock();
        Note("Idle video detached and docked from either window without starting a session.");
    }
    static async Task CheckLivePopout() {
        await WaitForOperation("Starting the pop-out demo");
        var api=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Cadrivect.Engine.EmbeddedPlayback")).First(t=>t!=null);
        var read=api.GetMethod("Snapshot");
        var json=new JavaScriptSerializer();
        Func<Dictionary<string,object>> sample=()=>json.Deserialize<Dictionary<string,object>>((string)read.Invoke(null,null));
        int before=Convert.ToInt32(sample()["presented"]);
        var dashboardBounds=Rect(hwnd);
        var player=await OpenDetachedPlayer();
        var playerHandle=new WindowInteropHelper(player).Handle;
        var initialSize=Rect(video);
        player.Width+=96;
        player.Height+=48;
        await Until(delegate {
            var bounds=Rect(video);
            var frame=sample();
            return bounds.Width!=initialSize.Width && bounds.Width==Convert.ToInt32(frame["surfaceWidth"]) &&
                bounds.Height==Convert.ToInt32(frame["surfaceHeight"]) && Convert.ToInt32(frame["presented"])>before;
        },"Detached video did not continue presenting at the resized player dimensions.");
        Require(Fits(Rect(hwnd),dashboardBounds,1),"Resizing the detached player moved the dashboard.");
        before=Convert.ToInt32(sample()["presented"]);
        Click("MinimizeWindow");
        await Until(()=>IsIconic(hwnd) && IsWindowVisible(playerHandle) && !IsIconic(playerHandle) &&
            GetAncestor(video,2)==playerHandle && Convert.ToInt32(sample()["presented"])>before+2,
            "Minimizing the dashboard interrupted or hid the detached player.");
        ShowWindow(hwnd,9);
        await Until(()=>!IsIconic(hwnd) && Fits(Rect(hwnd),dashboardBounds,1),"Dashboard geometry changed on restore.");
        Note("The detached player stayed visible and continued submitting frames while the dashboard was minimized.");
        var displays=Find<ComboBox>("PlaybackDisplay");
        var originalDisplay=displays.SelectedItem;
        var otherDisplay=displays.Items.Cast<object>().FirstOrDefault(d=>d!=originalDisplay);
        if(otherDisplay!=null && originalDisplay!=null) {
            foreach(var display in new[]{otherDisplay,originalDisplay}) {
                string device=(string)display.GetType().GetProperty("DeviceName").GetValue(display,null);
                displays.SelectedItem=display;
                await Until(()=>Equals(sample()["display"],device),"The detached player did not present on "+device+".");
                Require(Fits(Rect(hwnd),dashboardBounds,1),"Selecting a playback monitor moved the dashboard.");
            }
            Note("Only the detached player moved between playback monitors, with the rendered display confirmed on each.");
        }
        await CheckWipeInput(read,Convert.ToString(sample()["renderer"]),false,playerHandle);
        var hdrDisplay=displays.Items.Cast<object>().FirstOrDefault(d=>(bool)d.GetType().GetProperty("AdvancedColorEnabled").GetValue(d,null));
        if(hdrDisplay!=null && !Equals(sample()["renderer"],"opengl")) {
            var color=Find<ComboBox>("HdrOutput");
            var originalColor=color.SelectedItem;
            displays.SelectedItem=hdrDisplay;
            color.SelectedItem="HDR10";
            await Until(()=>Equals(sample()["presenterHdr10"],true),"The detached player did not negotiate HDR10.");
            await WaitForOperation("Switching the detached player to HDR10");
            await CheckWipeInput(read,Convert.ToString(sample()["renderer"]),true,playerHandle);
            color.SelectedItem=originalColor;
            displays.SelectedItem=originalDisplay;
            await WaitForOperation("Restoring the detached player's color mode");
            string restored=(string)originalDisplay.GetType().GetProperty("DeviceName").GetValue(originalDisplay,null);
            await Until(()=>Equals(sample()["display"],restored),"Player did not return to the initial monitor after HDR.");
            Note("The detached HDR10/SDR split passed native dragging and zoom on both axes.");
        }

        var monitor=new Monitor {Size=Marshal.SizeOf(typeof(Monitor))};
        Require(GetMonitorInfo(MonitorFromWindow(playerHandle,2),ref monitor),"Missing pop-out monitor.");
        var windowed=Rect(video);
        ClickPlayer(player,"PlayerFullscreen");
        await Until(()=>Fits(Rect(video),monitor.Full,2),"Fullscreen did not fill the detached player's monitor.");
        Require(Fits(Rect(hwnd),dashboardBounds,1) && Find<FrameworkElement>("HeaderBar").IsVisible,
            "Fullscreen on the player changed the dashboard.");
        SendMessage(video,0x203,IntPtr.Zero,Packed(48,48));
        try {await Until(()=>Fits(Rect(video),windowed,2),"Double-click did not restore the detached player's size.");}
        catch {Note("Player video before fullscreen "+Geometry(windowed)+", after double-click "+Geometry(Rect(video))+"."); throw;}
        before=Convert.ToInt32(sample()["presented"]);
        player.Close();
        await WaitForDock();
        await Until(()=>Peer(Find<Button>("OpenSource")).GetItemStatus()=="Playing" &&
            Convert.ToInt32(sample()["presented"])>before,"Closing the detached window interrupted the stream.");
        Note("Pop-out resized, presented fullscreen, zoomed and dragged both splits, and docked on close during continuous offline playback.");
    }
}
