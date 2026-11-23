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
    [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region,int x,int y);
    static bool VideoSurroundsProgressCard() {
        var card=ScreenRect(Find<FrameworkElement>("OperationCard")); var bounds=Rect(video);
        int x=(card.Left+card.Right)/2-bounds.Left,y=(card.Top+card.Bottom)/2-bounds.Top;
        var region=CreateRectRgn(0,0,0,0);
        Require(region!=IntPtr.Zero,"Could not allocate a test region.");
        try {
            return GetWindowRgn(video,region)>=2 && !PtInRegion(region,x,y) &&
                (PtInRegion(region,1,1) || PtInRegion(region,bounds.Width/2,1) || PtInRegion(region,1,bounds.Height/2));
        } finally {DeleteObject(region);}
    }
    static bool VideoClippedForModal() {
        var region=CreateRectRgn(0,0,0,0);
        Require(region!=IntPtr.Zero,"Could not allocate a test region.");
        try {return GetWindowRgn(video,region)==1;} // NULLREGION: the HWND has an empty visible area.
        finally {DeleteObject(region);}
    }
    static Task WaitForOperation(string action,int timeout=12000) {
        return Until(()=>!OperationVisible,action+" left playback progress open.",timeout);
    }
    static void CheckInlineSwitch(string action) {
        Require(!OperationVisible && Find<FrameworkElement>("Workspace").IsEnabled &&
            IsWindowVisible(video) && !VideoClippedForModal() && Video()==video,
            action+" blocked the dashboard or interrupted the video surface.");
    }
    static RadioButton PictureButton(string id) {
        return (RadioButton)FindVisual(Find<Border>("MeterHost"),"RadioButton",
            e=>AutomationProperties.GetAutomationId(e)=="Picture_"+id);
    }
    static void SelectPlaybackMode(RadioButton button) {
        Require(button.IsEnabled,"Playback option is disabled: "+button.Content);
        button.IsChecked=true;
        CheckInlineSwitch("Selecting "+button.Content);
    }
    static void CheckCancelableOperation(string action) {
        var overlay=Find<FrameworkElement>("OperationOverlay");
        var cancel=Find<Button>("CancelOperation");
        Require(overlay.IsVisible && Find<FrameworkElement>("OperationSpinner").IsVisible &&
            !string.IsNullOrWhiteSpace(Find<TextBlock>("OperationTitle").Text) &&
            !string.IsNullOrWhiteSpace(Find<TextBlock>("OperationDetail").Text),
            action+" did not open a spinner with a title and progress message.");
        window.UpdateLayout();
        var playback=ScreenRect(Find<Border>("VideoHost"));
        var panel=ScreenRect(overlay);
        Require(overlay.IsAncestorOf(cancel) && cancel.IsEnabled && cancel.IsVisible &&
            Find<FrameworkElement>("Workspace").IsEnabled && Find<FrameworkElement>("TransportBar").IsEnabled &&
            Find<FrameworkElement>("StatusFooter").IsEnabled && Find<FrameworkElement>("HeaderBar").IsEnabled &&
            panel.Width>0 && panel.Height>0 && panel.Left>=playback.Left-2 && panel.Right<=playback.Right+2 &&
            panel.Top>=playback.Top-2 && panel.Bottom<=playback.Bottom+2,
            action+" covered or disabled controls outside the playback area.");
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
        TransportState("Connecting","Spinner",true);
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
        Note("The playback progress card cancelled startup before the first frame and restored Play.");
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
    static async Task CheckLiveSwitches(MethodInfo snapshot) {
        var original=PictureButton("original"); var sharp=PictureButton("sharp");
        var json=new System.Web.Script.Serialization.JavaScriptSerializer();
        Func<System.Collections.Generic.Dictionary<string,object>> sample=()=>
            json.Deserialize<System.Collections.Generic.Dictionary<string,object>>((string)snapshot.Invoke(null,null));
        Require(original.IsChecked==true && sharp.IsEnabled && Equals(sample()["effectivePictureMode"],"original"),
            "The switch fixture needs original, unprocessed playback.");
        SelectPlaybackMode(sharp);
        await Until(()=>Equals(sample()["effectivePictureMode"],"sharp"),"Sharpen did not reach the renderer.");
        SelectPlaybackMode(original);
        await Until(()=>Equals(sample()["effectivePictureMode"],"original"),"Original did not reach the renderer.");

        var resolution=Find<ComboBox>("RenderResolution");
        int priorIndex=resolution.SelectedIndex;
        string before=(string)sample()["resolution"];
        resolution.SelectedIndex=priorIndex==1?2:1;
        CheckInlineSwitch("Changing the output resolution");
        await Until(()=>Equals(sample()["resolution"],priorIndex==1?"1080":"720"),"The selected output target did not reach playback.");
        resolution.SelectedIndex=priorIndex;
        CheckInlineSwitch("Restoring the output resolution");
        await Until(()=>Equals(sample()["resolution"],before),"The original output target did not return.");
        Note("Picture and output changes took effect on the same playback surface with the dashboard available.");
    }
    static async Task CheckCancelPendingStop(MethodInfo snapshot) {
        PressTransport();CheckCancelableOperation("Stopping offline playback");
        var cancel=Find<Button>("CancelOperation");
        await Until(VideoSurroundsProgressCard,"The Stop card must leave surrounding native video visible.",800);
        var header=Find<Button>("MaximizeWindow");
        Require(header.Focus() && header.IsKeyboardFocused,"Pending Stop trapped focus inside the playback area.");
        Require(cancel.Focus() && cancel.IsKeyboardFocused,"The playback Cancel action is unreachable.");
        var escape=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,Key.Escape)
            {RoutedEvent=Keyboard.PreviewKeyDownEvent};
        cancel.RaiseEvent(escape);
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
        await CheckLiveSwitches(snapshot);
        await CheckCancelPendingStop(snapshot);
    }
}
