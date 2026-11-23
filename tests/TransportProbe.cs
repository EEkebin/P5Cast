using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

internal static partial class WindowChromeProbe
{
    static bool CheckTransportOption {get {return Environment.GetEnvironmentVariable("P5CAST_PROBE_TRANSPORT")=="1";}}

    static void PressTransport() {
        // Raise Click synchronously to inspect the pending state before the first native frame.
        Find<Button>("OpenSource").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    static bool OperationVisible {get {return Find<FrameworkElement>("OperationOverlay").IsVisible;}}
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr windowHandle,IntPtr region);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr region);
    static bool VideoClippedForModal() {
        var region=CreateRectRgn(0,0,0,0);
        Require(region!=IntPtr.Zero,"Could not allocate a test region.");
        try {return GetWindowRgn(video,region)==1;} // NULLREGION: the HWND has an empty visible area.
        finally {DeleteObject(region);}
    }
    static Task WaitForOperation(string action,int timeout=12000) {
        return Until(()=>!OperationVisible,action+" left the status modal open.",timeout);
    }
    static async Task SelectPlaybackMode(RadioButton button) {
        Require(button.IsEnabled,"Playback option is disabled: "+button.Content);
        button.IsChecked=true;
        await WaitForOperation("Selecting "+button.Content);
    }
    static void CheckCancelableOperation(string action) {
        var overlay=Find<FrameworkElement>("OperationOverlay");
        var cancel=Find<Button>("CancelOperation");
        Require(overlay.IsVisible && Find<FrameworkElement>("OperationSpinner").IsVisible &&
            !string.IsNullOrWhiteSpace(Find<TextBlock>("OperationTitle").Text) &&
            !string.IsNullOrWhiteSpace(Find<TextBlock>("OperationDetail").Text),
            action+" did not open a spinner with a title and progress message.");
        Require(overlay.IsAncestorOf(cancel) && cancel.IsEnabled && cancel.IsVisible &&
            !Find<FrameworkElement>("Workspace").IsEnabled && VideoClippedForModal(),
            action+" lost the in-window Cancel action, modal input isolation, or native surface occlusion.");
        Require(Video()==video && window.OwnedWindows.Count==0,action+" changed the playback HWND or opened a separate window.");
        if(SystemParameters.ClientAreaAnimation)
            Require(Find<RotateTransform>("OperationRotation").HasAnimatedProperties,action+" did not animate its spinner.");
    }
    static void CancelOperationNow() {
        var cancel=Find<Button>("CancelOperation");
        Require(cancel.IsVisible && cancel.IsEnabled,"Cancel must be available at this point.");
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    static void TransportState(string state,string icon,bool enabled) {
        var button=Find<Button>("OpenSource");
        Require(Peer(button).GetItemStatus()==state && !string.IsNullOrWhiteSpace(Peer(button).GetName()) &&
                button.IsEnabled==enabled,"Unexpected transport state "+Peer(button).GetItemStatus()+" ("+Peer(button).GetName()+").");
        var glyph=button.Content;
        Require(glyph!=null && glyph.GetType().Name=="TransportGlyph","The action lost its glyph.");
        var actual=glyph.GetType().GetProperty("State",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
        Require(actual!=null && actual.GetValue(glyph,null).ToString()==icon,"Transport glyph doesn't match "+state+".");
    }
    static void BeginDemoWithSpinner() {
        Require(Find<ComboBox>("SourceChoice").SelectedItem.ToString()=="Offline demo","Refusing to open a network source.");
        TransportState("Ready","Play",true);
        PressTransport();
        CheckCancelableOperation("Starting offline playback");
        TransportState("Connecting","Spinner",false);
        var button=Find<Button>("OpenSource");
        Require(Peer(button).GetName().IndexOf("cancel",StringComparison.OrdinalIgnoreCase)>=0,"Connecting action must offer cancellation.");
        if(SystemParameters.ClientAreaAnimation) {
            var glyph=button.Content;
            var moving=glyph.GetType().GetProperty("IsMorphing",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            Require(moving!=null && (bool)moving.GetValue(glyph,null),"Play snapped directly to the connecting indicator.");
            var entering=(TranslateTransform)Find<TextBlock>("Status").RenderTransform;
            var leaving=(TranslateTransform)Find<TextBlock>("PreviousStatus").RenderTransform;
            Require(entering.HasAnimatedProperties && leaving.HasAnimatedProperties &&
                    entering.Y>=0 && (double)entering.GetAnimationBaseValue(TranslateTransform.YProperty)==0 &&
                    (double)leaving.GetAnimationBaseValue(TranslateTransform.YProperty)<0,"Status lines should animate in from below and out above.");
        }
    }
    static async Task CheckCanceledStartup() {
        BeginDemoWithSpinner();
        CancelOperationNow();
        await WaitForOperation("Cancel startup");
        await Until(()=>Peer(Find<Button>("OpenSource")).GetItemStatus()=="Ready","Cancel did not release the connecting player.");
        TransportState("Ready","Play",true);
        Require(Find<ComboBox>("SourceChoice").IsEnabled,"Cancel left the source locked.");
        Note("The status modal cancelled startup before the first frame and restored Play.");
    }
    static async Task CheckIdleRendererCancel() {
        var choice=Find<ComboBox>("VideoRenderer");
        object prior=choice.SelectedItem;
        Require(choice.IsEnabled,"The idle renderer selection is disabled.");
        choice.SelectedItem=Equals(prior,"Direct3D 12")?"Vulkan":"Direct3D 12";
        CheckCancelableOperation("Switching the idle renderer");
        CancelOperationNow();
        await WaitForOperation("Cancelled renderer probe",60000);
        Require(choice.IsEnabled && Equals(choice.SelectedItem,prior),"Cancelled GPU probe left a different renderer selected.");
        Note("Cancelling an idle renderer switch restored the previous graphics API.");
    }
    static async Task CheckLiveSwitchModal(MethodInfo snapshot) {
        var panel=Find<Border>("MeterHost");
        Func<string,RadioButton> picture=id=>(RadioButton)FindVisual(panel,"RadioButton",e=>AutomationProperties.GetAutomationId(e)=="Picture_"+id);
        Func<string,bool> active=id=>((string)snapshot.Invoke(null,null)).Contains("\"effectivePictureMode\":\""+id+"\"");
        var original=picture("original"); var sharp=picture("sharp");
        Require(original.IsChecked==true && sharp.IsEnabled && active("original"),"The switch fixture needs original, unprocessed playback.");
        sharp.IsChecked=true;CheckCancelableOperation("Switching picture mode");
        CancelOperationNow();
        await WaitForOperation("Cancelled picture switch");
        Require(original.IsChecked==true && active("original"),"Cancelling the picture switch failed to restore its UI and rendered state.");
        sharp.IsChecked=true;CheckCancelableOperation("Switching to Sharpen");
        await WaitForOperation("Switch to Sharpen");
        Require(active("sharp") && IsWindowVisible(video) && !VideoClippedForModal() && Video()==video,
            "The switch completed before the live renderer presented Sharpen on the restored surface.");
        original.IsChecked=true;
        await WaitForOperation("Switch back to Original");
        Require(active("original"),"The renderer did not switch back to Original.");
        var resolution=Find<ComboBox>("RenderResolution");
        int priorIndex=resolution.SelectedIndex;
        var json=new System.Web.Script.Serialization.JavaScriptSerializer();
        Func<string> liveResolution=()=>(string)json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(
            (string)snapshot.Invoke(null,null))["resolution"];
        string priorResolution=liveResolution();
        resolution.SelectedIndex=priorIndex==1?2:1;
        CheckCancelableOperation("Changing the output resolution");
        CancelOperationNow();await WaitForOperation("Cancelled output switch");
        Require(resolution.SelectedIndex==priorIndex && liveResolution()==priorResolution,"Cancel failed to restore the selected and rendered output settings.");
        Note("A cancelled picture switch restored Original; an accepted switch reported Sharpen on the same playback surface.");
    }
    static async Task CheckCancelPendingStop(MethodInfo snapshot) {
        PressTransport();CheckCancelableOperation("Stopping offline playback");
        var cancel=Find<Button>("CancelOperation");
        Require(cancel.IsKeyboardFocused,"Pending Stop did not focus Cancel.");
        cancel.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        var focused=Keyboard.FocusedElement as UIElement;
        Require(focused!=null && Find<FrameworkElement>("OperationOverlay").IsAncestorOf(focused),"Tab escaped the pending Stop dialog.");
        var escape=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,Key.Escape)
            {RoutedEvent=Keyboard.PreviewKeyDownEvent};
        focused.RaiseEvent(escape);
        Require(escape.Handled,"Escape did not cancel pending Stop.");
        await WaitForOperation("Cancelled stop");
        TransportState("Playing","Stop",true);
        string before=(string)snapshot.Invoke(null,null);
        await Until(()=>IsWindowVisible(video) && !VideoClippedForModal() && Video()==video && ((string)snapshot.Invoke(null,null))!=before,
            "Cancelling Stop did not preserve the running stream.");
        Note("Cancelling a pending Stop kept the existing demo running.");
    }
    static async Task CheckCompletedStop() {
        var cancel=Find<Button>("CancelOperation");
        bool committed=false;
        DependencyPropertyChangedEventHandler stateChanged=delegate {
            if(OperationVisible && !cancel.IsEnabled) committed=true;
        };
        cancel.IsEnabledChanged+=stateChanged;
        try {
            PressTransport();CheckCancelableOperation("Final disconnect");
            await WaitForOperation("Completed disconnect");
            Require(committed && Find<TextBlock>("Status").Text=="Disconnected" && Find<Button>("OpenSource").IsEnabled &&
                IsWindowVisible(video) && !VideoClippedForModal(),"Completed stop failed to disable Cancel at commit and restore the idle surface.");
        } finally {cancel.IsEnabledChanged-=stateChanged;}
        Note("Stop remained cancellable until teardown, then closed and restored the idle video surface.");
    }
    static async Task CheckLiveTransport() {
        TransportState("Playing","Stop",true);
        var api=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Cadrivect.Engine.EmbeddedPlayback")).First(t=>t!=null);
        var snapshot=api.GetMethod("Snapshot");
        var history=Find<Button>("StatusHistoryButton");
        Click("StatusHistoryButton");
        var sheet=Find<Border>("StatusHistorySheet");
        var close=Find<Button>("CloseStatusHistory");
        await Until(()=>sheet.IsVisible && close.IsKeyboardFocused,"History did not open and focus its close action.");
        Require(!Find<FrameworkElement>("Workspace").IsEnabled && VideoClippedForModal(),
            "Status history failed to isolate input and occlude the playback HWND.");
        var list=Find<ListBox>("StatusHistoryList");
        Func<object,string> message=entry=>(string)entry.GetType().GetProperty("Message").GetValue(entry,null);
        var messages=list.Items.Cast<object>().Select(message).ToArray();
        Require(messages.Length>0 && messages[0]=="Offline demo" && messages.Any(m=>m.IndexOf("demo",StringComparison.OrdinalIgnoreCase)>=0 && m!="Offline demo"),
                "History lost or reordered real playback progress.");
        string before=(string)snapshot.Invoke(null,null);
        await Until(()=>Video()==video && ((string)snapshot.Invoke(null,null))!=before,"Opening history interrupted the native playback surface.");
        var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(close),Environment.TickCount,Key.Escape);
        key.RoutedEvent=Keyboard.PreviewKeyDownEvent;
        close.RaiseEvent(key);
        if(!key.Handled) {key.RoutedEvent=Keyboard.KeyDownEvent; close.RaiseEvent(key);}
        await Until(()=>!sheet.IsVisible && history.IsKeyboardFocused,"Escape did not close history and restore focus.");
        Click("StatusHistoryButton"); await Until(()=>sheet.IsVisible,"History did not reopen.");
        Click("CloseStatusHistory"); await Until(()=>!sheet.IsVisible,"Close did not dismiss history.");
        TransportState("Playing","Stop",true);
        Note("Play morphed through a cancellable spinner to Stop. History showed real startup events, retained playback, and restored keyboard focus.");
        await CheckLiveSwitchModal(snapshot);
        await CheckCancelPendingStop(snapshot);
    }
}
