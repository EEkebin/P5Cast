using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;

[assembly: AssemblyTitle("P5Cast")]
[assembly: AssemblyProduct("P5Cast")]
[assembly: AssemblyDescription("PS5 player and private network streaming host")]
[assembly: AssemblyVersion("0.13.0.0")]

namespace Cadrivect
{
    internal static class Program
    {
        internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges",false);
                AppContext.SetSwitch("Switch.System.Windows.DoNotUsePresentationDpiCapabilityTier2OrGreater",false);
                var app = new Application();
                Window window;
                using (var xaml = File.OpenRead(Path.Combine(Root, "app", "Main.xaml")))
                    window = (Window)XamlReader.Load(xaml);
                new Dashboard(window, args.Contains("--client"));
                return app.Run(window);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.GetBaseException().Message, "P5Cast", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    internal partial class Dashboard
    {
        readonly Window window;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly string preferences = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaRelay", "dashboard.json");
        readonly string[] names = { "Direct", "Sony", "Demo" };
        readonly string[] titles = { "Direct to PS5", "Sony Remote Play", "Offline demo" };
        readonly string[] modeIds = { "original", "sharp", "fsr", "rtxvideo", "dlss", "dlaa" };
        readonly string[] hdrIds = { "auto","off","hdr10" };
        readonly string[] rendererIds = { "auto","d3d11","d3d12","vulkan","opengl" };
        internal static string RendererName(string id)
        {
            switch(id) {
                case "auto": return "Auto";
                case "d3d11": return "Direct3D 11";
                case "d3d12": return "Direct3D 12";
                case "vulkan": return "Vulkan";
                case "opengl": return "OpenGL";
                default: return "—";
            }
        }
        int preferredHdr;
        string SelectedRenderer { get { return rendererIds[UI<ComboBox>("VideoRenderer").SelectedIndex]; } }
        bool UsesSdrPresenter { get { return SelectedRenderer=="opengl"; } }
        readonly string[] resolutionIds = { "native","720","1080","1440","4k" };
        readonly int[] directRates={2,5,15,30,50,75,100};
        readonly int[] networkRates={0,35,65,100,150,250,500};
        readonly string[] audioLayouts={"","stereo","5.1-surround","7.1-surround"};
        readonly string[][] settingsGroups={
            new[]{"Console","Stream","Display","Interface","Discord"},new[]{"Sharing","Devices"},new[]{"Network","Quality"}
        };
        int sessionVideoKbps;
        bool refreshingDisplays;
        readonly Brush mint = new SolidColorBrush(Color.FromRgb(143,230,208));
        readonly Brush dark = new SolidColorBrush(Color.FromRgb(16,39,37));
        readonly Brush light = new SolidColorBrush(Color.FromRgb(230,240,239));
        string source = "Sony";
        string page = "Play";
        bool busy;
        readonly VideoSurface surface=new VideoSurface();
        readonly PlaybackMeter meter;
        readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
        EngineSession engine;
        Process directCore;
        Task<int> rendering;
        bool closing, preparing, stopping, fullscreen, stats=true;
        int mode, factor=1;
        WindowState previousWindowState;
        string failure="";
        string pendingRevocation;
        DateTime revokeUntil;
        readonly Dictionary<string,string> consoleAddresses=new Dictionary<string,string>();

        T UI<T>(string name) where T : class { return (T)window.FindName(name); }

        internal Dashboard(Window w, bool client)
        {
            window = w;
            InitStatusHistory();
            window.Icon = new BitmapImage(new Uri(Path.Combine(Program.Root,"app","assets","mark.ico")));
            UI<Border>("VideoHost").Child=surface;
            surface.DoubleClicked+=ToggleFullscreen;
            meter=new PlaybackMeter(delegate(int selectedMode,int multiplier) {
                mode=selectedMode; factor=multiplier;
                if(rendering!=null) engine.Configure(mode,factor);
            },delegate(string view){
                if(rendering!=null) engine.SetComparison(view);
                UpdateWipeInput();
            });
            surface.WipePositionChanged+=delegate(double position){meter.WipePosition=position;};
            surface.WipeGripChanged+=delegate(int state,float scale){if(rendering!=null) engine.SetWipeGrip(state,scale);};
            meter.WipePositionChanged+=delegate(double position){
                if(rendering!=null) engine.SetWipePosition((float)position);
                UpdateWipeInput();
            };
            UI<Border>("MeterHost").Child=meter;
            var saved = ReadSettings();
            meter.WipeHorizontal=Field(saved,"wipeHorizontal").Equals("True",StringComparison.OrdinalIgnoreCase);
            meter.WipeHorizontalChanged+=delegate(bool horizontal){
                if(rendering!=null) engine.SetWipeHorizontal(horizontal);
                UpdateWipeInput();
                if(window.IsLoaded) SaveSettings();
            };
            InitDrawers(saved);
            InitTitleBar(!Field(saved,"sidebarOpen").Equals("False",StringComparison.OrdinalIgnoreCase));
            InitInterfaceScale(saved);
            InitDiscord(saved);
            var output=UI<ComboBox>("PlaybackDisplay");
            RefreshDisplays(Field(saved,"display"));
            var hostScreen=UI<ComboBox>("HostDisplay");
            var displays=DisplayTarget.Enumerate();
            hostScreen.ItemsSource=displays;
            hostScreen.SelectedItem=displays.FirstOrDefault(d=>d.DeviceName==Field(saved,"hostDisplay")) ?? displays.FirstOrDefault(d=>d.Primary) ?? displays.FirstOrDefault();
            UI<CheckBox>("ClientHdr").IsChecked=string.Equals(Field(saved,"clientHdr"),"True",StringComparison.OrdinalIgnoreCase);
            output.DropDownOpened += delegate { RefreshDisplays(SelectedDisplayName()); };
            output.SelectionChanged += delegate {
                if(!refreshingDisplays && window.IsLoaded) MoveDashboard();
            };
            UI<ComboBox>("HdrOutput").ItemsSource=new[]{"Auto","SDR","HDR10"};
            preferredHdr=ParseIndex(saved,"hdr",0,hdrIds.Length);
            var rendererChoice=UI<ComboBox>("VideoRenderer");
            rendererChoice.ItemsSource=rendererIds.Select(RendererName).ToArray();
            rendererChoice.SelectedIndex=Math.Max(0,Array.IndexOf(rendererIds,Field(saved,"renderer")));
            rendererChoice.SelectionChanged+=delegate {RefreshRendererOutput();};
            RefreshRendererOutput();
            UI<ComboBox>("RenderResolution").ItemsSource=new[]{"Native","720p","1080p","1440p","4K"};
            UI<ComboBox>("RenderResolution").SelectedIndex=ParseIndex(saved,"resolution",0,resolutionIds.Length);
            UI<ComboBox>("HdrOutput").SelectionChanged+=delegate {
                if(!UsesSdrPresenter) preferredHdr=UI<ComboBox>("HdrOutput").SelectedIndex;
                UpdateOutput();
            };
            UI<ComboBox>("RenderResolution").SelectionChanged+=delegate {UpdateOutput();};
            UI<ComboBox>("Quality").ItemsSource = new[] { "Use Moonlight settings", "1080p · 60 FPS · 35 Mbps", "1440p · 120 FPS · 65 Mbps", "4K · 60 FPS · 80 Mbps", "4K · 120 FPS · 120 Mbps" };
            UI<ComboBox>("Quality").SelectedIndex = ParseIndex(saved,"quality",0,5);
            bool savedPicture=Field(saved,"pictureDefaults")=="1";
            int requested=savedPicture?ParseIndex(saved,"mode",0,6):0;
            mode=requested<3?requested:0;
            factor=savedPicture?ParseIndex(saved,"multiplier",0,4)+1:1;
            meter.Reset(mode,factor);
            window.Loaded+=async delegate {
                MoveDashboard();
                var display=output.SelectedItem as DisplayTarget;
                string state=Field(saved,"maximized");
                if(state.Equals("True",StringComparison.OrdinalIgnoreCase) || (state.Length==0 && display!=null && display.WorkingArea.Width>=1600))
                    window.WindowState=WindowState.Maximized;
                HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(DisplayChanged);
                timer.Start(); await ReadGpu(requested);
            };
            timer.Tick+=delegate {Tick();};
            UI<ComboBox>("DirectFps").ItemsSource=new[]{"60 FPS","30 FPS"};
            UI<ComboBox>("DirectFps").SelectedIndex=ParseIndex(saved,"directFps",0,2);
            var directRate=UI<ComboBox>("DirectBitrate");
            directRate.ItemsSource=directRates.Select(rate=>rate+" Mbps").ToArray();
            directRate.SelectedIndex=RateIndex(saved,"directMbps",directRates,100);
            directRate.SelectionChanged+=delegate {if(!Active && !preparing) meter.SetRequestedVideoKbps(source=="Direct"?DirectKbps():0);};
            var relayRate=UI<ComboBox>("NetworkBitrate");
            relayRate.ItemsSource=networkRates.Select(rate=>rate==0?"Use video preset":rate+" Mbps").ToArray();
            relayRate.SelectedIndex=RateIndex(saved,"relayMbps",networkRates,500);
            UI<ComboBox>("NetworkAudio").ItemsSource=new[]{"Use client settings","Stereo","5.1 surround","7.1 surround"};
            UI<ComboBox>("NetworkAudio").SelectedIndex=ParseIndex(saved,"audioLayout",0,audioLayouts.Length);
            foreach(var group in settingsGroups)
                foreach(var key in group) UI<Button>(key+"Group").Click+=delegate {SelectGroup(group,key);};
            SelectGroup(settingsGroups[1],"Sharing"); SelectGroup(settingsGroups[2],"Network");
            InitStopChoices(saved);
            object rawAddresses;
            if(saved.TryGetValue("consoleAddresses",out rawAddresses) && rawAddresses is Dictionary<string,object>)
                foreach(var row in (Dictionary<string,object>)rawAddresses) consoleAddresses[row.Key]=Convert.ToString(row.Value);
            UI<ComboBox>("ConsolePicker").SelectionChanged+=delegate {
                var c=UI<ComboBox>("ConsolePicker").SelectedItem as DirectConsole;
                RefreshStopChoice();
                RefreshWakeButton();
                string remembered;
                if(c!=null) UI<TextBox>("ConsoleAddress").Text=consoleAddresses.TryGetValue(c.Mac,out remembered)?remembered:c.Address??"";
            };
            ReloadConsoles(Field(saved,"consoleMac")); RefreshStopChoice(); RefreshWakeButton();
            UI<Button>("WakeConsole").Click+=async delegate {await WakeWithoutPlaying();};
            UI<TextBox>("HostAddress").Text = Field(saved, "address");
            var addresses = BindAddresses().ToList();
            UI<ComboBox>("BindIP").ItemsSource = addresses;
            var previous = Field(saved,"bind");
            UI<ComboBox>("BindIP").SelectedItem = addresses.Contains(previous) ? previous : addresses[0];
            var selected = Field(saved,"source");
            var sourcePicker=UI<ComboBox>("SourceChoice"); sourcePicker.ItemsSource=titles;
            sourcePicker.SelectedIndex=Math.Max(0,Array.IndexOf(names,selected));
            SetSource(names[sourcePicker.SelectedIndex]);
            sourcePicker.SelectionChanged+=delegate {if(sourcePicker.SelectedIndex>=0) SetSource(names[sourcePicker.SelectedIndex]);};
            SetPage(client?"Client":"Play",client);
            foreach(var key in new[]{"Play","Host","Client"}) UI<Button>(key+"Nav").Click+=delegate {SetPage(key);};
            UI<Button>("AddConsole").Click+=delegate {OpenPair();};
            UI<Button>("CancelPair").Click+=delegate {SetPage("Play");};
            UI<Button>("OpenSource").Click+=async delegate {
                if(Active || preparing) await UserStop(); else await StartPlayback();
            };
            UI<Button>("Fullscreen").Click+=delegate {ToggleFullscreen();};
            window.PreviewKeyDown+=async delegate(object sender,KeyEventArgs e) {
                bool chord=(Keyboard.Modifiers & (ModifierKeys.Control|ModifierKeys.Alt))==(ModifierKeys.Control|ModifierKeys.Alt);
                var key=e.Key==Key.System?e.SystemKey:e.Key;
                if(key==Key.Escape && !fullscreen && UI<Border>("StatusHistorySheet").IsVisible) SetStatusHistory(false);
                else if(key==Key.F11 || (chord && key==Key.Enter)) ToggleFullscreen();
                else if(chord && key==Key.O) ToggleStats();
                else if(chord && key==Key.Q) {e.Handled=true; await UserStop(); return;}
                else if(chord && key==Key.B && rendering!=null) meter.ToggleOriginal();
                else if(key==Key.Escape && surface.CancelWipeDrag()) {e.Handled=true; return;}
                else if(key==Key.Escape && UI<StackPanel>("StopPanel").IsVisible && !requestingRest) SetPage("Play");
                else if(key==Key.Escape && UI<StackPanel>("PairPanel").IsVisible) SetPage("Play");
                else if(key==Key.Escape && fullscreen) ToggleFullscreen();
                else return;
                e.Handled=true;
            };
            Bind("PairConsole",async delegate {
                Show("Pairing with your PS5...");
                var c=await DirectConsole.Pair(UI<TextBox>("PairAddress").Text,UI<TextBox>("PsnAccount").Text,UI<TextBox>("PairCode").Text);
                consoleAddresses[c.Mac]=c.Address; ReloadConsoles(c.Mac); SetPage("Play"); SaveSettings();
                Show("Paired with "+c.Name+". Press the play button to connect.");
            });
            Bind("StartHost", () => {
                var hostTarget=hostScreen.SelectedItem as DisplayTarget;
                return hostTarget==null ? Relay("StartHost","-Address",SelectedIP()) : Relay("StartHost","-Address",SelectedIP(),"-DisplayDevice",hostTarget.DeviceName);
            });
            Bind("StopHost", () => Relay("StopHost"));
            Bind("HostStatus", () => Relay("Status"));
            Bind("LoadPeers", LoadPeers);
            Bind("RemovePeer", async () => {
                var peer = UI<ComboBox>("Peers").SelectedItem as Peer;
                if (peer==null) throw new InvalidOperationException("Load devices and select a client first.");
                if(pendingRevocation!=peer.Id || DateTime.UtcNow>revokeUntil) {
                    pendingRevocation=peer.Id; revokeUntil=DateTime.UtcNow.AddSeconds(15);
                    Show("Select Revoke access again within 15 seconds to remove "+peer.Name+". A new pairing PIN will be required.");
                    return;
                }
                pendingRevocation=null;
                await Relay("RevokeClient","-ClientId",peer.Id);
                await LoadPeers();
                Show("Access revoked for " + peer.Name + ".");
            });
            UI<Button>("Controllers").Click += delegate {
                try { Process.Start("https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2getting__started.html#windows"); }
                catch (Exception e) { Show(e.Message,true); }
            };
            UI<Button>("DirectClient").Click += delegate { UI<ComboBox>("SourceChoice").SelectedIndex=0; SetPage("Play"); };
            Bind("ClientPair", () => StartNetwork(true));
            Bind("ClientPlay", () => StartNetwork(false));
            Bind("ApprovePin", async () => {
                var pin = UI<TextBox>("Pin").Text.Trim();
                if (pin.Length != 4 || pin.Any(c => c < '0' || c > '9'))
                    throw new InvalidOperationException("Enter the four digits shown on the client.");
                await Relay("ApprovePin","-Pin",pin);
                UI<TextBox>("Pin").Clear();
            });
            window.Closing+=OnDashboardClosing;
            window.Closed+=delegate {timer.Stop(); if(discord!=null) discord.Dispose();};
        }
        static void StopOwned(Process p)
        {
            if(p==null) return;
            try {if(!p.HasExited) p.Kill();} catch(InvalidOperationException) {} catch(System.ComponentModel.Win32Exception) {}
        }
        void ReloadConsoles(string mac)
        {
            var rows=DirectConsole.List();
            var picker=UI<ComboBox>("ConsolePicker"); picker.ItemsSource=rows;
            picker.SelectedItem=rows.FirstOrDefault(c=>c.Mac==mac)??rows.FirstOrDefault();
        }
        async Task ReadGpu(int requested)
        {
            string file=Path.Combine(Path.GetTempPath(),"cadrivect-gpu-"+Guid.NewGuid().ToString("N")+".json");
            try {
                var info=new ProcessStartInfo(Path.Combine(Program.Root,"engine","bin","CadrivectPlayer.exe"),"--capabilities --report "+Quote(file))
                    {UseShellExecute=false,CreateNoWindow=true};
                using(var proc=Process.Start(info)) {
                    if(!await Task.Run(()=>proc.WaitForExit(20000))) {StopOwned(proc); return;}
                    if(proc.ExitCode!=0 || !File.Exists(file)) return;
                }
                var gpu=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
                if(closing) return;
                meter.SetCapabilities(gpu);
                if(mode==0 && !Active && !preparing && requested>=3 && Field(gpu,modeIds[requested]).Equals("true",StringComparison.OrdinalIgnoreCase))
                { mode=requested; meter.Reset(mode,factor); }
            } catch(IOException) {} catch(System.ComponentModel.Win32Exception) {} catch(ArgumentException) {} catch(InvalidOperationException) {}
            finally {try {File.Delete(file);} catch(IOException) {}}
        }

        string SelectedIP() { return Convert.ToString(UI<ComboBox>("BindIP").SelectedItem); }
        IntPtr DisplayChanged(IntPtr hwnd,int message,IntPtr wp,IntPtr lp,ref bool handled)
        {
            if(message==0x02E0 || message==0x007E) QueueInterfaceScale(); // WM_DPICHANGED / WM_DISPLAYCHANGE
            if(message==0x232 || message==0x7E) {
                RefreshDisplays(DisplayTarget.DeviceForWindow(hwnd));
                if(engine!=null) engine.InvalidateDisplay();
            }
            return IntPtr.Zero;
        }
        string SelectedDisplayName()
        {
            var d=UI<ComboBox>("PlaybackDisplay").SelectedItem as DisplayTarget;
            return d==null ? "source" : d.DeviceName;
        }
        void RefreshDisplays(string requested)
        {
            refreshingDisplays=true;
            try {
                var targets=DisplayTarget.Enumerate();
                var picker=UI<ComboBox>("PlaybackDisplay");
                picker.ItemsSource=targets;
                picker.SelectedItem=targets.FirstOrDefault(d=>d.DeviceName==requested) ?? targets.FirstOrDefault(d=>d.Primary) ?? targets.FirstOrDefault();
            } finally { refreshingDisplays=false; }
        }
        public sealed class Peer
        {
            public string Name { get; set; }
            public string Id { get; set; }
        }
        async Task LoadPeers()
        {
            var response = await Script("streaming/Relay.ps1","-Action","ListClients");
            var peers = new List<Peer>();
            object items;
            if (response.TryGetValue("clients",out items) && items is System.Collections.IEnumerable) {
                foreach (var entry in (System.Collections.IEnumerable)items) {
                    var row = entry as Dictionary<string,object>;
                    if (row==null || String.IsNullOrEmpty(Field(row,"uuid"))) continue;
                    peers.Add(new Peer { Name=Field(row,"name"), Id=Field(row,"uuid") });
                }
            }
            UI<ComboBox>("Peers").ItemsSource = peers;
            UI<ComboBox>("Peers").SelectedIndex = peers.Count>0 ? 0 : -1;
            Show(peers.Count==0 ? "This host has no paired clients." : "Select the device whose access you want to manage.");
        }
        string ClientAddress()
        {
            string host = UI<TextBox>("HostAddress").Text.Trim();
            if (host.Length == 0 || host.StartsWith("-") || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new InvalidOperationException("Enter the host PC’s LAN or private VPN address.");
            return host;
        }

        static IEnumerable<string> BindAddresses()
        {
            var networks = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderBy(n => n.Name.IndexOf("vEthernet",StringComparison.OrdinalIgnoreCase) >= 0);
            foreach(var net in networks)
            foreach(var uni in net.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                byte[] a = uni.Address.GetAddressBytes();
                if (a[0]==10 || (a[0]==192 && a[1]==168) || (a[0]==172 && a[1]>=16 && a[1]<=31) || (a[0]==100 && a[1]>=64 && a[1]<=127))
                    yield return uni.Address.ToString();
            }
            yield return IPAddress.Loopback.ToString();
        }

        void Bind(string name, Func<Task> action)
        {
            UI<Button>(name).Click += async delegate {
                if (busy) return;
                busy = true;
                UI<Grid>("Pages").IsEnabled = false;
                try { SaveSettings(); await action(); }
                catch (Exception e) { Show(e.GetBaseException().Message, true); }
                finally { UI<Grid>("Pages").IsEnabled = true; busy = false; }
            };
        }

        void Highlight(Button b, bool on)
        {
            b.Background = on ? dark : Brushes.Transparent;
            b.Foreground = on ? mint : light;
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        void SetPage(string value,bool reveal=true)
        {
            if(!requestingRest) closeAfterStop=false;
            if(reveal) SetSettingsDrawer(true);
            page = value;
            UI<StackPanel>("StopPanel").Visibility=Visibility.Collapsed;
            pendingRevocation=null;
            UI<StackPanel>("PairPanel").Visibility=Visibility.Collapsed;
            UI<TextBox>("PairCode").Clear(); UI<TextBox>("PsnAccount").Clear();
            foreach(var key in new[]{"Play","Host","Client"})
            {
                bool on = key == value;
                UI<StackPanel>(key+"Page").Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                Highlight(UI<Button>(key+"Nav"), on);
            }
        }

        static int RateIndex(Dictionary<string,object> saved,string key,int[] rates,int fallback)
        {
            int rate;
            int index=int.TryParse(Field(saved,key),out rate)?Array.IndexOf(rates,rate):-1;
            return index>=0?index:Array.IndexOf(rates,fallback);
        }
        int DirectKbps() {return directRates[UI<ComboBox>("DirectBitrate").SelectedIndex]*1000;}
        void SelectGroup(string[] group,string selected)
        {
            foreach(var key in group) {
                bool on=key==selected;
                UI<FrameworkElement>(key+"Settings").Visibility=on?Visibility.Visible:Visibility.Collapsed;
                var button=UI<Button>(key+"Group");
                System.Windows.Automation.AutomationProperties.SetItemStatus(button,on?"Selected":"");
            }
        }
        void SetSource(string value)
        {
            source = value;
            meter.SetSource(value);
            bool direct=value=="Direct";
            UI<Button>("ConsoleGroup").Visibility=UI<Button>("StreamGroup").Visibility=direct?Visibility.Visible:Visibility.Collapsed;
            SelectGroup(settingsGroups[0],direct?"Console":"Display");
            meter.SetRequestedVideoKbps(direct?DirectKbps():0);
            UpdateTransport();
        }
        void OpenPair()
        {
            SetPage("Play"); UI<StackPanel>("PlayPage").Visibility=Visibility.Collapsed;
            UI<StackPanel>("PairPanel").Visibility=Visibility.Visible;
            UI<TextBox>("PsnAccount").Text=DirectConsole.KnownAccountId();
            UI<TextBox>("PairAddress").Text=UI<TextBox>("ConsoleAddress").Text; UI<TextBox>("PairAddress").Focus();
        }
        async Task Relay(string action, params string[] args)
        {
            Show(action == "StartHost" || action == "OpenClient" || action == "PairClient" || action == "Play"
                ? "Opening streaming tools. First use downloads and verifies the required component…" : "Checking the streaming host…");
            var arguments = new List<string> { "-Action", action };
            arguments.AddRange(args);
            if(action=="Play" && UI<CheckBox>("ClientHdr").IsChecked==true) arguments.Add("-Hdr");
            var r = await Script("streaming/Relay.ps1", arguments.ToArray());
            string message = Field(r,"message");
            if (action=="Status" && string.Equals(Field(r,"controllerDriver"),"False",StringComparison.OrdinalIgnoreCase))
                message += " ViGEmBus was not detected. Remote gamepads require a compatible host driver. See Controller driver setup.";
            if (action=="Status" || action=="StartHost" || action=="StopHost") UI<TextBlock>("HostState").Text = message;
            Show(message.Length == 0 ? "Ready." : message);
        }

        static string Quote(string s)
        {
            if (s.IndexOf('"') >= 0 || s.IndexOf('\r') >= 0 || s.IndexOf('\n') >= 0)
                throw new ArgumentException("Invalid character in argument.");
            return "\"" + s + "\"";
        }

        async Task<Dictionary<string,object>> Script(string relative, params string[] args)
        {
            string script = Path.Combine(Program.Root, relative.Replace('/','\\'));
            var command = new List<string> { "-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script };
            command.AddRange(args);
            string ps = Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe");
            using (var child = new Process()) {
                child.StartInfo = new ProcessStartInfo(ps, string.Join(" ",command.Select(Quote))) {
                    UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true, WorkingDirectory=Program.Root
                };
                // Windows PowerShell needs its own inbox modules even when our parent is PowerShell 7.
                string inbox=Path.Combine(Path.GetDirectoryName(ps),"Modules");
                child.StartInfo.EnvironmentVariables["PSModulePath"]=inbox+";"+child.StartInfo.EnvironmentVariables["PSModulePath"];
                child.Start();
                Task<string> output = child.StandardOutput.ReadToEndAsync();
                Task<string> errors = child.StandardError.ReadToEndAsync();
                await Task.WhenAll(output,errors,Task.Run((Action)child.WaitForExit));
                string line = output.Result.Split(new[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries).LastOrDefault(s=>s.TrimStart().StartsWith("{"));
                var parsed = line==null ? new Dictionary<string,object>() : json.Deserialize<Dictionary<string,object>>(line);
                if (child.ExitCode!=0 || string.Equals(Field(parsed,"ok"),"False",StringComparison.OrdinalIgnoreCase)) {
                    string detail = Field(parsed,"message");
                    if (detail.Length==0) detail = Field(parsed,"error");
                    if (detail.Length==0) detail = errors.Result.Trim();
                    throw new InvalidOperationException(detail.Length>0 ? detail : "The operation failed. Check the source or host and try again.");
                }
                if (line==null) throw new InvalidOperationException("The component returned no session information.");
                return parsed;
            }
        }

        static string Field(Dictionary<string,object> data, string key)
        {
            object value;
            return data.TryGetValue(key,out value) ? Convert.ToString(value,CultureInfo.InvariantCulture) : "";
        }
        static int ParseIndex(Dictionary<string,object> data, string key, int fallback, int count)
        {
            int i;
            return int.TryParse(Field(data,key),out i) && i>=0 && i<count ? i : fallback;
        }
        Dictionary<string,object> ReadSettings()
        {
            try { return json.Deserialize<Dictionary<string,object>>(File.ReadAllText(preferences)) ?? new Dictionary<string,object>(); }
            catch (IOException) {} catch (ArgumentException) {} catch (InvalidOperationException) {} catch (UnauthorizedAccessException) {}
            return new Dictionary<string,object>();
        }
        void SaveSettings()
        {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(preferences));
                var hostTarget=UI<ComboBox>("HostDisplay").SelectedItem as DisplayTarget;
                var console=UI<ComboBox>("ConsolePicker").SelectedItem as DirectConsole;
                var settings = new { wipeHorizontal=meter.WipeHorizontal,sidebarOpen=stats,sidebarWidth=sidebarWidth,settingsOpen=settingsOpen,settingsHeight=settingsHeight,
                    discordPresence=UI<CheckBox>("SharePresence").IsChecked==true,
                    discordGame=UI<CheckBox>("ShareGame").IsChecked==true,discordProject=UI<CheckBox>("ShareProject").IsChecked==true,stopChoices=stopChoices,directMbps=DirectKbps()/1000, relayMbps=networkRates[UI<ComboBox>("NetworkBitrate").SelectedIndex],audioLayout=UI<ComboBox>("NetworkAudio").SelectedIndex,pictureDefaults=1, mode=mode, multiplier=factor-1,
                    consoleMac=console==null?"":console.Mac,consoleAddresses=consoleAddresses,directFps=UI<ComboBox>("DirectFps").SelectedIndex,
                    display=SelectedDisplayName(), uiScale=PreferredScalePercent, resolution=UI<ComboBox>("RenderResolution").SelectedIndex,hdr=preferredHdr,renderer=SelectedRenderer,
                    maximized=(fullscreen?previousWindowState:window.WindowState)==WindowState.Maximized,
                    clientHdr=UI<CheckBox>("ClientHdr").IsChecked==true,hostDisplay=hostTarget==null ? "" : hostTarget.DeviceName,
                    address=UI<TextBox>("HostAddress").Text.Trim(), bind=SelectedIP(), source=source, page=page, quality=UI<ComboBox>("Quality").SelectedIndex };
                string temp = preferences+"."+Guid.NewGuid().ToString("N")+".tmp";
                File.WriteAllText(temp,json.Serialize(settings));
                if (File.Exists(preferences)) File.Replace(temp,preferences,null);
                else File.Move(temp,preferences);
            } catch (IOException) {} catch (UnauthorizedAccessException) {}
        }
    }
}
