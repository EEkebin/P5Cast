using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

internal static partial class WindowChromeProbe
{
    static int RunStopProbe(Assembly production,string root,string reconnectStage=null)
    {
        IsolateSetupProfile();
        var app=new Application();
        using(var file=File.OpenRead(Path.Combine(root,"app","Main.xaml"))) window=(Window)XamlReader.Load(file);
        var type=production.GetType("Cadrivect.Dashboard",true);
        var dashboard=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{window,false},null);
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(async delegate {
            try {
                if(reconnectStage!=null) await CheckDirectReconnect(type,dashboard,reconnectStage);
                else await CheckStopModal(type,dashboard);
                completed=true;window.Close();
            }
            catch(Exception error) {failure=error;app.Shutdown(1);}
        }));
        int result=app.Run(window);
        if(failure!=null) throw failure;
        Require(completed && result==0,"Disconnect confirmation test did not finish.");
        return result;
    }

    static Process AttachDemoHelper(Type type,object dashboard,string mac)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var helper=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--stop-holder") {UseShellExecute=false,CreateNoWindow=true});
        type.GetField("directCore",flags).SetValue(dashboard,helper);
        type.GetField("playingMac",flags).SetValue(dashboard,mac);
        type.GetField("playingPipe",flags).SetValue(dashboard,"P5Cast-Stop-Offline");
        return helper;
    }
    static void ReleaseDemoHelper(Process helper)
    {
        try {if(!helper.HasExited) helper.Kill();} catch(InvalidOperationException) {}
        helper.Dispose();
    }
    static async Task CheckStopModal(Type type,object dashboard)
    {
        var field=BindingFlags.Instance|BindingFlags.NonPublic;
        video=Video();
        Click("OpenSource");
        await Until(()=>Find<TextBlock>("Status").Text=="Offline demo","Failed to start offline playback.",12000);
        var api=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Cadrivect.Engine.EmbeddedPlayback")).First(t=>t!=null);
        Func<int> frames=()=>Convert.ToInt32(new JavaScriptSerializer().Deserialize<Dictionary<string,object>>((string)api.GetMethod("Snapshot").Invoke(null,null))["presented"]);
        // A disposable local process marks ownership of a Direct session. It cannot
        // contact a console or queue rest, so the production recovery path is exercised.
        var choices=(Dictionary<string,string>)type.GetField("stopChoices",field).GetValue(dashboard);
        const string restartConsole="0200000000AE";
        var oldRender=(Task)type.GetField("rendering",field).GetValue(dashboard);
        var restarting=AttachDemoHelper(type,dashboard,restartConsole);
        try {
            choices[restartConsole]="rest";
            Click("RestartStream");
            await Until(delegate {
                Require(!Shown("StopPanel") && !Shown("StopMessage"),"Restart tried to put the console to sleep or opened its disconnect prompt.");
                var render=(Task)type.GetField("rendering",field).GetValue(dashboard);
                return oldRender.IsCompleted && render!=null && render!=oldRender && Find<Button>("RestartStream").IsEnabled && frames()>0;
            },"Restart failed to bypass the remembered rest choice and reconnect the demo.",15000);
            Require(choices[restartConsole]=="rest" && Video()==video,"Restart changed the saved Stop policy or replaced the picture window.");
            Note("Restart bypassed the remembered PS5 rest rule and preserved it for the next explicit Stop.");
        } finally {choices.Remove(restartConsole);ReleaseDemoHelper(restarting);}
        var holder=AttachDemoHelper(type,dashboard,"0200000000AD");
        try {
            Click("HostNav");await Until(()=>Shown("HostPage"),"Host settings did not open.");
            Find<ToggleButton>("ShowSettings").IsChecked=false;
            await Until(()=>!Shown("SettingsDrawer"),"Settings drawer did not close.");
            var open=Find<Button>("OpenSource");open.Focus();Click("OpenSource");
            await Until(()=>Shown("StopPanel") && Keyboard.FocusedElement==Find<Button>("CancelStop") && !IsWindowVisible(video),
                "Stop confirmation failed to take focus and hide the HWND.");
            Require(!Shown("SettingsDrawer") && (string)type.GetField("page",field).GetValue(dashboard)=="Host","Stop changed the drawer or settings page.");
            int before=frames();await Until(()=>frames()>before+3,"Video stopped advancing while the dialog was open.");
            Require(Find<CheckBox>("RestOnStop").IsChecked==true && Find<CheckBox>("RememberStop").IsChecked==false,"Stop defaults changed.");
            var overlay=Find<Grid>("StopPanel");
            for(int i=0;i<8;i++) {
                var current=(UIElement)Keyboard.FocusedElement;
                Require(current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) && overlay.IsAncestorOf((DependencyObject)Keyboard.FocusedElement),
                    "Tab escaped the stop dialog.");
            }
            Find<CheckBox>("RestOnStop").IsChecked=false;
            window.Close();
            await Until(()=>Equals(Find<Button>("ConfirmStop").Content,"Disconnect and close"),"Closing did not upgrade the active prompt.");
            Require(Find<CheckBox>("RestOnStop").IsChecked==false,"Closing discarded the current rest selection.");
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            ((UIElement)Keyboard.FocusedElement).RaiseEvent(escape);
            await Until(()=>!Shown("StopPanel") && IsWindowVisible(video),"Escape failed to restore the video.");
            Require(Keyboard.FocusedElement==open && (string)type.GetField("page",field).GetValue(dashboard)=="Host","Cancel lost focus or the underlying page.");
            Click("OpenSetup");await Until(()=>Shown("SetupOverlay"),"Could not open Setup while playing.");
            window.Close();await Until(()=>Shown("StopPanel") && !Shown("SetupOverlay"),"Closing Setup overlapped the two modals.");
            Click("CancelStop");await Until(()=>!Shown("StopPanel"),"Cancel after closing setup failed.");
            type.GetMethod("ToggleFullscreen",field).Invoke(dashboard,null);
            await Until(()=>!Shown("HeaderBar"),"Fullscreen failed.");
            await (Task)type.GetMethod("UserStop",field).Invoke(dashboard,null);
            await Until(()=>Shown("StopPanel") && !Shown("HeaderBar"),"Stop left fullscreen.");
            Find<CheckBox>("RememberStop").IsChecked=true;
            Click("ConfirmStop");
            await Until(()=>Shown("StopMessage") && Find<TextBlock>("StopMessage").Text.Contains("Could not send the rest request"),"A rejected rest request did not produce an inline recovery.");
            Require(Find<CheckBox>("RememberStop").IsChecked==true && Find<Button>("CancelStop").IsEnabled && !Shown("HeaderBar"),
                "Failed rest request lost its choices, fullscreen, or ability to cancel.");
            Find<CheckBox>("RestOnStop").IsChecked=false;Click("ConfirmStop");
            await Until(()=>!Shown("StopPanel") && !OperationVisible && Find<TextBlock>("Status").Text=="Disconnected" &&
                Peer(Find<Button>("OpenSource")).GetItemStatus()=="Ready","Could not recover by disconnecting without rest.",10000);
            Require(!Shown("HeaderBar") && Video()==video && (string)type.GetField("page",field).GetValue(dashboard)=="Host",
                "Stopping changed fullscreen, the settings page or the video HWND.");
            var persisted=(Dictionary<string,object>)ReadPrefs()["stopChoices"];
            Require(Equals(persisted["0200000000AD"],"keep"),"Successful recovery did not persist the remembered leave-on choice.");
            Note("The same-window stop dialog preserved video, fullscreen, page, drawer and keyboard focus; close escalation, setup collision, rejected rest and remembered recovery passed.");
        } finally {
            ReleaseDemoHelper(holder);
        }
    }
}
