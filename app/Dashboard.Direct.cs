using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        NetworkPlayer network;
        bool firstFrame;
        bool stopRequested;
        bool Active { get {return rendering!=null || network!=null;} }
        void MoveDashboard()
        {
            var target=UI<ComboBox>("PlaybackDisplay").SelectedItem as DisplayTarget;
            if(target!=null) target.MoveSourceWindow(new WindowInteropHelper(window).Handle);
        }
        void ToggleFullscreen()
        {
            surface.EndPointerGesture();
            SetFullscreenFrame(!fullscreen);
            fullscreen=!fullscreen;
            UI<Button>("Fullscreen").Content=fullscreen?"Windowed":"Fullscreen";
            UpdateFrameInsets();
            QueueInterfaceScale();
            UI<RowDefinition>("HeaderRow").Height=new GridLength(fullscreen?0:HeaderHeight);
            UI<Border>("HeaderBar").Visibility=fullscreen?Visibility.Collapsed:Visibility.Visible;
            UI<RowDefinition>("TransportRow").Height=new GridLength(fullscreen?0:52);
            UI<RowDefinition>("StatusRow").Height=fullscreen?new GridLength(0):GridLength.Auto;
            UI<Grid>("Workspace").Margin=fullscreen?new Thickness(0):new Thickness(16,14,16,12);
            ApplySidebar(false);
            ApplySettingsDrawer();
            window.Focus();
        }
        void ToggleStats()
        {
            if(fullscreen) {
                ToggleFullscreen();
                UI<ToggleButton>("ShowStats").IsChecked=true;
            } else UI<ToggleButton>("ShowStats").IsChecked=!stats;
        }
        void UpdateTransport()
        {
            var button=UI<Button>("OpenSource");
            var glyph=button.Content as TransportGlyph;
            if(glyph==null) button.Content=glyph=new TransportGlyph();
            bool ending=stopping || requestingRest;
            bool connecting=preparing || (rendering!=null && !firstFrame) || (network!=null && !network.HasVideo);
            string action=source=="Demo" ? "Play demo" : source=="Direct" ? "Connect to PS5" : "Connect to Sony Remote Play";
            if(ending) action="Stopping playback";
            else if(connecting) action="Cancel connection";
            else if(Active) action="Stop playback";
            string state=ending ? "Stopping" : connecting ? "Connecting" : Active ? "Playing" : "Ready";
            button.IsEnabled=!ending;
            button.ToolTip=connecting && !ending ? "Connecting… Click to cancel." : action;
            AutomationProperties.SetName(button,action);
            AutomationProperties.SetItemStatus(button,state);
            glyph.SetState(ending || connecting ? TransportGlyph.Shape.Spinner : Active ? TransportGlyph.Shape.Stop : TransportGlyph.Shape.Play);
            bool canSelect=!preparing && !Active;
            UI<ComboBox>("VideoRenderer").IsEnabled=canSelect;
            UI<ComboBox>("ConsolePicker").IsEnabled=canSelect;
            UI<TextBox>("ConsoleAddress").IsEnabled=canSelect;
            UI<Button>("AddConsole").IsEnabled=canSelect;
            RefreshWakeButton();
            UI<ComboBox>("SourceChoice").IsEnabled=!preparing && !Active;
        }
        void RefreshRendererOutput()
        {
            var color=UI<ComboBox>("HdrOutput");
            bool sdr=UsesSdrPresenter;
            color.SelectedIndex=sdr?1:preferredHdr;
            color.IsEnabled=!sdr;
            UI<TextBlock>("RendererHint").Text=sdr
                ? "OpenGL uses SDR on Windows. Choose Direct3D or Vulkan for HDR10."
                : "Auto color uses HDR10 when the playback display and driver support it.";
        }
        void UpdateOutput()
        {
            if(rendering!=null) engine.ConfigureOutput(resolutionIds[UI<ComboBox>("RenderResolution").SelectedIndex],hdrIds[UI<ComboBox>("HdrOutput").SelectedIndex]);
        }
        double sourceAspect;
        void UpdateWipeInput()
        {
            surface.ConfigurePicture(meter.ComparisonView,meter.WipePosition,rendering!=null ? sourceAspect : 0,meter.WipeHorizontal);
        }
        void Tick()
        {
            TickDiscord();
            if(rendering==null) return;
            if(stopping) {engine.Stop(); return;}
            if(directCore!=null && directCore.HasExited) {
                if(directCore.ExitCode!=0) failure=CoreError(directCore.ExitCode);
                stopping=true; UpdateTransport(); engine.Stop(); return;
            }
            string raw=engine.Snapshot();
            if(!string.IsNullOrEmpty(raw) && raw!="{}") {
                var snapshot=json.Deserialize<Dictionary<string,object>>(raw);
                if(source=="Direct") snapshot["requestedVideoKbps"]=sessionVideoKbps;
                // A selection made while the engine worker starts may precede its session.
                if(!firstFrame && Field(snapshot,"comparisonView")!=meter.ComparisonView)
                    engine.SetComparison(meter.ComparisonView);
                if(!firstFrame && meter.ComparisonView=="wipe") {
                    engine.SetWipeHorizontal(meter.WipeHorizontal);
                    engine.SetWipePosition((float)meter.WipePosition);
                }
                double width=PlaybackMeter.Number(snapshot,"inputWidth"),height=PlaybackMeter.Number(snapshot,"inputHeight");
                sourceAspect=width>0 && height>0 ? width/height : 0;
                UpdateWipeInput();
                meter.Update(snapshot);
                if(!firstFrame && Field(snapshot,"presented")!="0" && Field(snapshot,"presented")!="") {
                    string label=source=="Demo"?"Offline demo":source=="Direct"?"PS5 connected":"Sony Remote Play";
                    firstFrame=true; UpdateTransport(); Show(label);
                }
            }
        }
        void StopPlayback()
        {
            if(!stopRequested && (preparing || Active) && !closing && !stopping)
                Show(preparing || (rendering!=null && !firstFrame) ? "Cancelling connection…" : "Disconnecting…");
            stopRequested=true;
            if(wakeCheck!=null) wakeCheck.Cancel();
            stopping=preparing || Active;
            if(network!=null) {var owned=network; network=null; owned.Dispose(); surface.Clear(); stopping=false; Show("Disconnected");}
            if(rendering!=null) engine.Stop();
            UpdateTransport();
        }
        void Finish()
        {
            preparing=stopping=false;
            sourceAspect=0; UpdateWipeInput();
            playingMac=playingPipe=null; EndDiscordSession();
            if(UI<StackPanel>("StopPanel").Visibility==Visibility.Visible) SetPage("Play");
            meter.Reset(mode,factor); meter.SetRequestedVideoKbps(source=="Direct"?DirectKbps():0); surface.Clear(); UpdateTransport();
            if(closing) QueueWindowClose();
        }
        async Task StartPlayback()
        {
            if(preparing || Active || requestingRest) return;
            preparing=true; firstFrame=false; stopRequested=false; failure=""; UpdateTransport();
            try {
                if(engine==null) engine=new EngineSession();
                var args=new List<string> {"--surface",surface.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),
                    "--mode",modeIds[mode],"--multiplier",factor.ToString(),"--display","source","--renderer",SelectedRenderer,
                    "--resolution",resolutionIds[UI<ComboBox>("RenderResolution").SelectedIndex],
                    "--hdr",hdrIds[UI<ComboBox>("HdrOutput").SelectedIndex]};
                if(source=="Direct") {
                    var console=UI<ComboBox>("ConsolePicker").SelectedItem as DirectConsole;
                    if(console==null) {OpenPair(); Show("Add your PS5 to play."); return;}
                    string address=DirectConsole.Host(UI<TextBox>("ConsoleAddress").Text);
                    consoleAddresses[console.Mac]=address; SaveSettings();
                    address=await EnsureConsoleReady(console,address,false);
                    if(closing || stopping) {Show("Disconnected"); return;}
                    Show("Preparing your PS5 connection…");
                    string runtime=Field(await Script("sources/Prepare-Decoder.ps1"),"runtime");
                    if(closing || stopping) {Show("Disconnected"); return;}
                    if(string.IsNullOrEmpty(runtime)) throw new IOException("The video decoder runtime is unavailable.");
                    int fps=UI<ComboBox>("DirectFps").SelectedIndex==1?30:60;
                    var display=DisplayTarget.Select("source",surface.Handle); int hdr=UI<ComboBox>("HdrOutput").SelectedIndex;
                    int codec=hdr==2 || (hdr==0 && display.AdvancedColorEnabled && mode!=3)?3:2;
                    string pipe="Cadrivect-Video-"+Guid.NewGuid().ToString("N");
                    args.AddRange(new[]{"--direct-pipe",pipe,"--direct-runtime",runtime,"--target-fps",fps.ToString()});
                    sessionVideoKbps=DirectKbps();
                    directCore=console.Connect(pipe,address,codec,fps,sessionVideoKbps);
                    playingMac=console.Mac; playingPipe=pipe; titleAddress=address;
                    Show("Connecting to "+console.Name+"…");
                } else if(source=="Demo") {
                    args.AddRange(new[]{"--demo","--target-fps","60"}); Show("Starting offline demo…");
                } else {
                    using(var sony=Process.GetProcessesByName("RemotePlay").FirstOrDefault(p=>p.MainWindowHandle!=IntPtr.Zero)) {
                        if(sony==null) throw new InvalidOperationException("Connect in Sony PS Remote Play first, or select Direct to PS5 for built-in pairing and playback.");
                        args.AddRange(new[]{"--hwnd",sony.MainWindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture)});
                    }
                    Show("Capturing your Sony Remote Play session…");
                }
                if(meter.WipeHorizontal) args.Add("--wipe-horizontal");
                args.AddRange(new[]{"--comparison",meter.ComparisonView,"--wipe-position",meter.WipePosition.ToString("R",CultureInfo.InvariantCulture)});
                rendering=engine.Start(args.ToArray()); preparing=false; UpdateTransport();
                int code=await rendering;
                if(!stopping && directCore!=null && directCore.HasExited && directCore.ExitCode!=0) failure=CoreError(directCore.ExitCode);
                if(!stopRequested && code!=0 && failure.Length==0) failure=engine.Error();
                if(!stopRequested && !firstFrame && failure.Length==0) failure="The stream ended before its first picture. Check that the console is awake, then reconnect.";
                Show(failure.Length>0?failure:"Disconnected",failure.Length>0);
            } catch(OperationCanceledException) {Show("Disconnected");}
            catch(Exception e) {Show(e.GetBaseException().Message,true);}
            finally {EndPlayback();}
        }
        async void EndPlayback()
        {
            stopping=true; UpdateTransport();
            try {
                if(directCore!=null) {
                    var owned=directCore; directCore=null;
                    await Task.Run(delegate {using(owned) {if(!owned.WaitForExit(1500)) StopOwned(owned);}});
                }
            } finally {rendering=null; Finish();}
        }
        async Task StartNetwork(bool pair)
        {
            if(Active || preparing || requestingRest) {Show("Stop the current session first."); return;}
            string address=ClientAddress();
            var args=new List<string>{"-Action",pair?"PreparePair":"PreparePlay","-Address",address};
            int preset=UI<ComboBox>("Quality").SelectedIndex;
            int requestedRate=networkRates[UI<ComboBox>("NetworkBitrate").SelectedIndex];
            if(!pair && preset>0) {
                var resolutions=new[]{"","1080","1440","4K","4K"};
                var rates=new[]{"","60","120","60","120"}; var mbps=new[]{0,35,65,80,120};
                args.AddRange(new[]{"-Resolution",resolutions[preset],"-Fps",rates[preset]});
                if(requestedRate==0) requestedRate=mbps[preset];
            }
            if(!pair && requestedRate>0) args.AddRange(new[]{"-Mbps",requestedRate.ToString(CultureInfo.InvariantCulture)});
            int layout=UI<ComboBox>("NetworkAudio").SelectedIndex;
            if(!pair && layout>0) args.AddRange(new[]{"-AudioLayout",audioLayouts[layout]});
            if(!pair && UI<CheckBox>("ClientHdr").IsChecked==true) args.Add("-Hdr");
            preparing=true; stopRequested=false; UpdateTransport(); Show("Preparing the network client…");
            try {
                var plan=await Script("streaming/Relay.ps1",args.ToArray());
                if(stopping || closing) {Show("Disconnected"); return;}
                NetworkPlayer owned=null;
                owned=NetworkPlayer.Start(surface.Handle,plan,message=>{Show(message); UpdateTransport();},code=>{
                    if(network!=owned) return;
                    network=null; Finish();
                    Show(owned.Error ?? (code==0?"Network session ended.":"Network session ended (code "+code+")."),code!=0);
                });
                network=owned;
            } finally {preparing=false; if(network==null) Finish(); else UpdateTransport();}
        }
        static string CoreError(int code)
        {
            if(code==24 || code==25) return "The PS5 requires its user login PIN. Unlock the active console profile and reconnect.";
            if(code==26) return "The audio output could not start. Select an available Windows playback device and reconnect.";
            if(code==23) return "The PS5 session disconnected. Check that the console is awake and its address is reachable.";
            return "The direct streaming engine stopped (code "+code+").";
        }
    }
}
