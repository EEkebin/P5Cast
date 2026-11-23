using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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
        long transportOperation;
        CancellationTokenSource preparingConnection;

        void BeginConnection(string title,string detail)
        {
            preparingConnection=new CancellationTokenSource();
            transportOperation=BeginOperation(title,detail,StopPlayback);
            Show(detail);
        }
        void TransportProgress(string detail)
        {
            UpdateOperation(transportOperation,detail);
            Show(detail);
        }
        void EndTransportOperation()
        {
            long id=transportOperation;
            transportOperation=0;
            if(id!=0) EndOperation(id);
        }
        Task<Dictionary<string,object>> PrepareConnection(string script,params string[] args)
        {
            return PrepareComponent(preparingConnection.Token,script,args);
        }
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
            if(!Active && !preparing && pendingSetupConsole!=null) {
                string selected=pendingSetupConsole;
                pendingSetupConsole=null;
                ReloadConsoles(selected);
            }
            var button=UI<Button>("OpenSource");
            var glyph=button.Content as TransportGlyph;
            if(glyph==null) button.Content=glyph=new TransportGlyph();
            bool ending=stopping || requestingRest || pendingStop!=null;
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
            if(rendering!=null && !restoringSwitch) {
                engine.ConfigureOutput(resolutionIds[UI<ComboBox>("RenderResolution").SelectedIndex],hdrIds[UI<ComboBox>("HdrOutput").SelectedIndex]);
                TrackSwitch(SwitchControl.Output,"Applying display settings");
            }
        }
        void RefreshFrucOutputIntent()
        {
            int color=UI<ComboBox>("HdrOutput").SelectedIndex;
            if(color<0) return;
            var display=UI<ComboBox>("PlaybackDisplay").SelectedItem as DisplayTarget;
            bool wantsHdr=color==2 || (color==0 && mode!=3 && display!=null && display.AdvancedColorEnabled);
            meter.SetFrucOutputIntent(hdrIds[color],wantsHdr);
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
                if(!firstFrame && Field(snapshot,"frameGenerationProvider")!=meter.FrameGenerationProvider)
                    engine.Configure(mode,factor,meter.FrameGenerationProvider);
                if(!firstFrame && Field(snapshot,"dlssModelHint")!=meter.DlssModelHint)
                    engine.SetDlssModel(meter.DlssModelHint);
                if(!firstFrame && Field(snapshot,"dlssQuality")!=meter.DlssQuality)
                    engine.SetDlssQuality(meter.DlssQuality);
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
                TrackPresentedSwitch(snapshot);
                if(!firstFrame && Field(snapshot,"presented")!="0" && Field(snapshot,"presented")!="") {
                    string label=source=="Demo"?"Offline demo":source=="Direct"?"PS5 connected":"Sony Remote Play";
                    firstFrame=true; surface.HasPresentedVideo=true; EndTransportOperation(); UpdateTransport(); Show(label);
                }
            }
        }
        void StopPlayback()
        {
            if(stopRequested && stopping) return;
            if(transportOperation!=0) CommitOperation(transportOperation,firstFrame && !preparing ? "Disconnecting…" : "Cancelling connection…");
            if(!stopRequested && (preparing || Active) && !closing && !stopping)
                Show(preparing || (rendering!=null && !firstFrame) ? "Cancelling connection…" : "Disconnecting…");
            stopRequested=true;
            ReleaseStreamAudio();
            if(preparingConnection!=null) preparingConnection.Cancel();
            stopping=preparing || Active;
            if(network!=null) {EndNetwork(network); return;}
            if(rendering!=null) engine.Stop();
            UpdateTransport();
        }
        async void EndNetwork(NetworkPlayer owned)
        {
            UpdateTransport();
            try {await owned.StopAsync(); Show("Disconnected");}
            catch(Exception error) {Show(error.GetBaseException().Message,true);}
            finally {if(network==owned) {network=null; Finish();}}
        }
        void Finish()
        {
            EndSwitchTracking();
            if(pendingStop!=null) {
                if(closeAfterStop) {SaveSettings(); closing=true;}
                pendingStop.Cancel();
            }
            if(!requestingRest) EndTransportOperation();
            if(preparingConnection!=null) {preparingConnection.Dispose(); preparingConnection=null;}
            ReleaseStreamAudio();
            preparing=stopping=false;
            sourceAspect=0; UpdateWipeInput();
            playingMac=playingPipe=null; EndDiscordSession();
            if(!requestingRest) closeAfterStop=false;
            DismissStopPrompt();
            meter.Reset(mode,factor); RefreshFrucOutputIntent();
            meter.SetRequestedVideoKbps(source=="Direct"?DirectKbps():0); surface.Clear(); UpdateTransport();
            if(closing) QueueWindowClose();
        }
        async Task StartPlayback()
        {
            if(preparing || Active || requestingRest) return;
            if(source=="Direct" && !(UI<ComboBox>("ConsolePicker").SelectedItem is DirectConsole)) {
                OpenPair(); Show("Add your PS5 to play."); return;
            }
            preparing=true; firstFrame=false; stopRequested=false; failure=""; UpdateTransport();
            BeginConnection(source=="Direct"?"Connecting to PS5":source=="Demo"?"Starting offline demo":"Connecting to Sony Remote Play",
                source=="Direct"?"Checking your paired PS5…":source=="Demo"?"Starting offline demo…":"Looking for the Remote Play window…");
            try {
                if(engine==null) engine=new EngineSession();
                var args=new List<string> {"--surface",surface.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),
                    "--mode",modeIds[mode],"--multiplier",factor.ToString(),"--display","source","--renderer",SelectedRenderer,
                    "--resolution",resolutionIds[UI<ComboBox>("RenderResolution").SelectedIndex],
                    "--hdr",hdrIds[UI<ComboBox>("HdrOutput").SelectedIndex]};
                if(source=="Direct") {
                    var console=UI<ComboBox>("ConsolePicker").SelectedItem as DirectConsole;
                    string address=DirectConsole.Host(UI<TextBox>("ConsoleAddress").Text);
                    consoleAddresses[console.Mac]=address; SaveSettings();
                    address=await EnsureConsoleReady(console,address,false);
                    if(closing || stopping) throw new OperationCanceledException();
                    TransportProgress("Preparing your PS5 connection…");
                    string runtime=Field(await PrepareConnection("sources/Prepare-Decoder.ps1"),"runtime");
                    if(closing || stopping) throw new OperationCanceledException();
                    if(string.IsNullOrEmpty(runtime)) throw new IOException("The video decoder runtime is unavailable.");
                    int fps=UI<ComboBox>("DirectFps").SelectedIndex==1?30:60;
                    var display=DisplayTarget.Select("source",surface.Handle); int hdr=UI<ComboBox>("HdrOutput").SelectedIndex;
                    int codec=hdr==2 || (hdr==0 && display.AdvancedColorEnabled && mode!=3)?3:2;
                    string pipe="Cadrivect-Video-"+Guid.NewGuid().ToString("N");
                    args.AddRange(new[]{"--direct-pipe",pipe,"--direct-runtime",runtime,"--target-fps",fps.ToString()});
                    sessionVideoKbps=DirectKbps();
                    directCore=console.Connect(pipe,address,codec,fps,sessionVideoKbps,DirectStreamHeight(),EffectiveStreamVolume);
                    playingMac=console.Mac; playingPipe=pipe; titleAddress=address;
                    TransportProgress("Connecting to "+console.Name+"…");
                } else if(source=="Demo") {
                    args.AddRange(new[]{"--demo","--target-fps","60"}); Show("Starting offline demo…");
                } else {
                    using(var sony=Process.GetProcessesByName("RemotePlay").FirstOrDefault(p=>p.MainWindowHandle!=IntPtr.Zero)) {
                        if(sony==null) throw new InvalidOperationException("Connect in Sony PS Remote Play first, or select Direct to PS5 for built-in pairing and playback.");
                        AttachStreamAudio(sony.Id);
                        args.AddRange(new[]{"--hwnd",sony.MainWindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture)});
                    }
                    TransportProgress("Waiting for video from Sony Remote Play…");
                }
                args.AddRange(new[]{"--frame-generation",meter.FrameGenerationProvider,"--dlss-quality",meter.DlssQuality,"--dlss-model",meter.DlssModelHint});
                if(meter.WipeHorizontal) args.Add("--wipe-horizontal");
                args.AddRange(new[]{"--comparison",meter.ComparisonView,"--wipe-position",meter.WipePosition.ToString("R",CultureInfo.InvariantCulture)});
                rendering=engine.Start(args.ToArray()); preparing=false; UpdateTransport();
                int code=await rendering;
                if(!stopping && directCore!=null && directCore.HasExited && directCore.ExitCode!=0) failure=CoreError(directCore.ExitCode);
                if(!stopRequested && code!=0 && failure.Length==0) failure=engine.Error();
                if(!stopRequested && !firstFrame && failure.Length==0) failure="The stream ended before its first picture. Check that the console is awake, then reconnect.";
            } catch(OperationCanceledException) { }
            catch(Exception e) {failure=e.GetBaseException().Message;}
            await EndPlayback();
        }
        async Task EndPlayback()
        {
            stopping=true; UpdateTransport();
            try {
                if(directCore!=null) {
                    var owned=directCore; directCore=null;
                    await Task.Run(delegate {using(owned) {if(!owned.WaitForExit(1500)) StopOwned(owned);}});
                }
            } catch(Exception e) {failure=e.GetBaseException().Message;}
            finally {
                rendering=null; Finish();
                Show(failure.Length>0?failure:"Disconnected",failure.Length>0);
            }
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
            preparing=true; stopRequested=false; firstFrame=false; UpdateTransport();
            BeginConnection(pair?"Pairing with a host":"Connecting to host","Preparing the network client…");
            try {
                var plan=await PrepareConnection("streaming/Relay.ps1",args.ToArray());
                if(stopping || closing) {Show("Disconnected"); return;}
                NetworkPlayer owned=null;
                owned=NetworkPlayer.Start(surface.Handle,plan,message=>{
                    if(owned!=null && owned.HasVideo) {firstFrame=true; EndTransportOperation();}
                    TransportProgress(message); UpdateTransport();
                },code=>{
                    if(network!=owned) return;
                    network=null; Finish();
                    Show(owned.Error ?? (code==0?"Network session ended.":"Network session ended (code "+code+")."),code!=0);
                });
                network=owned;
                if(pair) EndTransportOperation(); // The connection panel now displays the pairing PIN.
                if(!pair && !Convert.ToBoolean(plan["muted"])) AttachStreamAudio(owned.ProcessId);
            } catch(OperationCanceledException) {Show("Disconnected");}
            catch {
                if(network!=null) StopPlayback();
                throw;
            } finally {preparing=false; if(network==null) Finish(); else UpdateTransport();}
        }
        static string CoreError(int code)
        {
            if(code==24 || code==25) return "The PS5 requires its user login PIN. Unlock the active console profile and reconnect.";
            if(code==28) return "The Direct audio volume control could not open. Reconnect to start the session.";
            if(code==26) return "The audio output could not start. Select an available Windows playback device and reconnect.";
            if(code==23) return "The PS5 session disconnected. Check that the console is awake and its address is reachable.";
            return "The direct streaming engine stopped (code "+code+").";
        }
    }
}
