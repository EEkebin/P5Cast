using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Cadrivect
{
    internal partial class Dashboard
    {

        void RefreshWakeButton()
        {
            UI<Button>("WakeConsole").IsEnabled=!closing && !preparing && !Active &&
                UI<ComboBox>("ConsolePicker").SelectedItem is DirectConsole;
        }

        async Task<string> EnsureConsoleReady(DirectConsole console,string address,bool wakeOnly)
        {
            var check=preparingConnection;
            TransportProgress("Checking your paired PS5…");
            return await ConsoleWake.Prepare(address,console.Mac,console.Keys,wakeOnly,check.Token,
                message=>window.Dispatcher.BeginInvoke(new Action(delegate {
                    if(preparingConnection==check && !closing && !check.IsCancellationRequested) TransportProgress(message);
                })));
        }

        async Task WakeWithoutPlaying()
        {
            var console=UI<ComboBox>("ConsolePicker").SelectedItem as DirectConsole;
            if(console==null || preparing || Active || closing) return;
            preparing=true; stopRequested=false; UpdateTransport();
            BeginConnection("Waking PS5","Checking your paired PS5…");
            try {
                string address=DirectConsole.Host(UI<TextBox>("ConsoleAddress").Text);
                consoleAddresses[console.Mac]=address; SaveSettings();
                await EnsureConsoleReady(console,address,true);
                if(!closing && !stopping) Show(console.Name+" is awake.");
            } catch(OperationCanceledException) {Show("Wake check cancelled.");}
            catch(Exception error) {Show(error.GetBaseException().Message,true);}
            finally {Finish();}
        }
    }
}
