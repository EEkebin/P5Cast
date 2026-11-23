using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        readonly Dictionary<string,string> stopChoices=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string playingMac,playingPipe;
        bool updatingStopChoice,requestingRest,closeAfterStop;
        UIElement stopReturnFocus;
        bool StopPromptOpen {get {return UI<Grid>("StopPanel").Visibility==Visibility.Visible;}}

        void InitStopChoices(Dictionary<string,object> saved)
        {
            object raw;
            if(saved.TryGetValue("stopChoices",out raw) && raw is Dictionary<string,object>) {
                foreach(var row in (Dictionary<string,object>)raw) {
                    string choice=row.Value as string;
                    if(choice=="keep" || choice=="rest") stopChoices[row.Key]=choice;
                }
            }
            var picker=UI<ComboBox>("StopBehavior");
            picker.ItemsSource=new[]{"Ask every time","Leave console on","Put PS5 into rest mode"};
            picker.SelectionChanged+=delegate {
                var c=UI<ComboBox>("ConsolePicker").SelectedItem as DirectConsole;
                if(updatingStopChoice || c==null) return;
                if(picker.SelectedIndex<=0) stopChoices.Remove(c.Mac);
                else stopChoices[c.Mac]=picker.SelectedIndex==2?"rest":"keep";
                SaveSettings();
            };
            UI<Button>("CancelStop").Click+=delegate {CancelStopPrompt();};
            UI<Button>("ConfirmStop").Click+=async delegate {
                await StopDirect(UI<CheckBox>("RestOnStop").IsChecked==true,UI<CheckBox>("RememberStop").IsChecked==true);
            };
        }
        void RefreshStopChoice()
        {
            var c=UI<ComboBox>("ConsolePicker").SelectedItem as DirectConsole;
            var picker=UI<ComboBox>("StopBehavior");
            updatingStopChoice=true;
            try {
                string rule="";
                if(c!=null) stopChoices.TryGetValue(c.Mac,out rule);
                picker.SelectedIndex=rule=="rest"?2:rule=="keep"?1:0;
                picker.IsEnabled=c!=null;
            } finally {updatingStopChoice=false;}
        }
        void SetStopAction(bool exit)
        {
            closeAfterStop=exit;
            UI<TextBlock>("StopTitle").Text=exit?"Disconnect and close P5Cast?":"Stop playing?";
            UI<Button>("ConfirmStop").Content=exit?"Disconnect and close":"Stop session";
        }
        void OpenStopPrompt(bool exit)
        {
            if(StopPromptOpen) {if(exit) SetStopAction(true);return;}
            if(SetupOpen) CloseSetup(false);
            stopReturnFocus=Keyboard.FocusedElement as UIElement;
            SetStopAction(exit);
            SetStopMessage("",false);
            UI<CheckBox>("RestOnStop").IsChecked=true;
            UI<CheckBox>("RememberStop").IsChecked=false;
            SetModalBackgroundEnabled(false);
            UI<Grid>("StopPanel").Visibility=Visibility.Visible;
            FocusStopPrompt();
        }
        void FocusStopPrompt()
        {
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(delegate {
                if(StopPromptOpen && !requestingRest) UI<Button>("CancelStop").Focus();
            }));
        }
        void SetStopMessage(string text,bool error)
        {
            var message=UI<TextBlock>("StopMessage");
            message.Text=text;
            message.SetResourceReference(TextBlock.ForegroundProperty,error?"Theme.Error":"Theme.Muted");
            message.Visibility=text.Length==0?Visibility.Collapsed:Visibility.Visible;
        }
        void EnableStopActions(bool enabled)
        {
            foreach(string name in new[]{"RestOnStop","RememberStop","ConfirmStop","CancelStop"}) UI<Control>(name).IsEnabled=enabled;
        }
        void CancelStopPrompt()
        {
            if(requestingRest || !StopPromptOpen) return;
            closeAfterStop=false; DismissStopPrompt();
        }
        void DismissStopPrompt()
        {
            if(!StopPromptOpen) return;
            UI<Grid>("StopPanel").Visibility=Visibility.Collapsed;
            SetModalBackgroundEnabled(true);
            var previous=stopReturnFocus; stopReturnFocus=null;
            if(previous!=null && previous.IsEnabled && previous.IsVisible && previous.Focus()) return;
            if(!UI<Button>("OpenSource").Focus()) window.Focus();
        }
        Task UserStop()
        {
            return RequestDisconnect(false);
        }
        async Task RequestDisconnect(bool exit)
        {
            if(requestingRest) {if(exit) SetStopAction(true); return;}
            if(stopping) return;
            if(setupPairing) return;
            if(directCore==null || !firstFrame || playingMac==null) {StopPlayback(); return;}
            if(StopPromptOpen) {
                if(exit) SetStopAction(true);
                return;
            }
            closeAfterStop=exit;
            string rule;
            if(stopChoices.TryGetValue(playingMac,out rule)) await StopDirect(rule=="rest",false);
            else OpenStopPrompt(exit);
        }
        async void OnDashboardClosing(object sender,System.ComponentModel.CancelEventArgs e)
        {
            if(SetupOpen) {
                if(setupPairing) {e.Cancel=true; return;}
                CloseSetup(false);
            }
            if(!closing && (requestingRest || (!stopping && directCore!=null && firstFrame && playingMac!=null))) {
                e.Cancel=true;
                await RequestDisconnect(true);
                return;
            }
            if(!closing) {SaveSettings(); closing=true; StopPlayback();}
            if(rendering!=null || preparing) e.Cancel=true;
        }
        void QueueWindowClose()
        {
            window.Dispatcher.BeginInvoke(new Action(window.Close));
        }
        async Task StopDirect(bool rest,bool remember)
        {
            var core=directCore; string mac=playingMac;
            if(requestingRest || stopping || core==null || mac==null) return;
            try {
                if(rest) {
                    requestingRest=true; EnableStopActions(false); UpdateTransport();
                    if(StopPromptOpen) {SetStopMessage("Sending the rest request…",false);UI<TextBlock>("StopTitle").Focus();}
                    Show("Requesting rest mode…");
                    await DirectConsole.RequestRestAsync(playingPipe,core);
                }
                if(remember) {stopChoices[mac]=rest?"rest":"keep"; RefreshStopChoice(); SaveSettings();}
                if(closeAfterStop && (directCore==core || directCore==null)) {
                    SaveSettings(); closing=true; closeAfterStop=false; DismissStopPrompt(); StopPlayback();
                    if(rendering==null && !preparing) QueueWindowClose();
                } else if(!closing && directCore==core) {DismissStopPrompt(); StopPlayback();}
            } catch(Exception ex) {
                if(!closing && core==directCore) {
                    OpenStopPrompt(closeAfterStop);
                    string message="Could not send the rest request. "+ex.GetBaseException().Message+" Try again or clear the rest mode option to disconnect.";
                    SetStopMessage(message,true);Show(message,true);
                    FocusStopPrompt();
                } else if(closeAfterStop && directCore==null) {
                    closeAfterStop=false;
                    QueueWindowClose();
                }
            } finally {
                requestingRest=false; EnableStopActions(true); UpdateTransport();
            }
        }
    }
}
