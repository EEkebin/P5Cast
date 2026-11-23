using System;
using System.Linq;
using System.Reflection;
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
        PressTransport();
        await Until(()=>Peer(Find<Button>("OpenSource")).GetItemStatus()=="Ready","Cancel did not release the connecting player.");
        TransportState("Ready","Play",true);
        Require(Find<ComboBox>("SourceChoice").IsEnabled,"Cancel left the source locked.");
        Note("Cancel before the offline demo's first frame returned the transport to Play.");
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
    }
}
