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
        bool updatingStopChoice,requestingRest;

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
        void OpenStopPrompt()
        {
            if(fullscreen) ToggleFullscreen();
            SetPage("Play"); UI<StackPanel>("PlayPage").Visibility=Visibility.Collapsed;
            UI<CheckBox>("RestOnStop").IsChecked=false;
            UI<CheckBox>("RememberStop").IsChecked=false;
            UI<StackPanel>("StopPanel").Visibility=Visibility.Visible;
            UI<Button>("CancelStop").Focus();
        }
        async Task UserStop()
        {
            if(stopping || requestingRest) return;
            if(directCore==null || !firstFrame || playingMac==null) {StopPlayback(); return;}
            string rule;
            if(stopChoices.TryGetValue(playingMac,out rule)) await StopDirect(rule=="rest",false);
            else if(!settingsOpen || UI<StackPanel>("StopPanel").Visibility!=Visibility.Visible) OpenStopPrompt();
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
                if(!closing && directCore==core) {SetPage("Play"); StopPlayback();}
            } catch(Exception ex) {
                if(!closing && core==directCore) {
                    OpenStopPrompt();
                    Show("Rest mode could not be confirmed. "+ex.GetBaseException().Message,true);
                }
            } finally {
                requestingRest=false; UI<StackPanel>("StopPanel").IsEnabled=true; UpdateTransport();
            }
        }
    }
}
