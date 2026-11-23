using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        readonly Dictionary<string,string> stopChoices=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string playingMac,playingPipe;
        bool updatingStopChoice,requestingRest,closeAfterStop;

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
            UI<Button>("CancelStop").Click+=delegate {SetPage("Play");};
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
            if(fullscreen) ToggleFullscreen();
            SetPage("Play"); UI<StackPanel>("PlayPage").Visibility=Visibility.Collapsed;
            SetStopAction(exit);
            UI<CheckBox>("RestOnStop").IsChecked=true;
            UI<CheckBox>("RememberStop").IsChecked=false;
            UI<StackPanel>("StopPanel").Visibility=Visibility.Visible;
            UI<Button>("CancelStop").Focus();
        }
        Task UserStop()
        {
            return RequestDisconnect(false);
        }
        async Task RequestDisconnect(bool exit)
        {
            if(requestingRest) {if(exit) closeAfterStop=true; return;}
            if(stopping) return;
            if(directCore==null || !firstFrame || playingMac==null) {StopPlayback(); return;}
            if(UI<StackPanel>("StopPanel").Visibility==Visibility.Visible) {
                if(fullscreen) ToggleFullscreen();
                SetSettingsDrawer(true);
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
                    requestingRest=true; UI<StackPanel>("StopPanel").IsEnabled=false; UpdateTransport();
                    Show("Requesting rest mode…");
                    await DirectConsole.RequestRestAsync(playingPipe,core);
                }
                if(remember) {stopChoices[mac]=rest?"rest":"keep"; RefreshStopChoice(); SaveSettings();}
                if(closeAfterStop && (directCore==core || directCore==null)) {
                    SaveSettings(); closing=true; closeAfterStop=false; StopPlayback();
                    if(rendering==null && !preparing) QueueWindowClose();
                } else if(!closing && directCore==core) {SetPage("Play"); StopPlayback();}
            } catch(Exception ex) {
                if(!closing && core==directCore) {
                    OpenStopPrompt(closeAfterStop);
                    Show("Rest mode could not be confirmed. "+ex.GetBaseException().Message+" Clear the rest mode option to disconnect.",true);
                } else if(closeAfterStop && directCore==null) {
                    closeAfterStop=false;
                    QueueWindowClose();
                }
            } finally {
                requestingRest=false; UI<StackPanel>("StopPanel").IsEnabled=true; UpdateTransport();
            }
        }
    }
}
