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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;

[assembly: AssemblyTitle("Cadrivect")]
[assembly: AssemblyDescription("PS5 player and private network streaming host")]
[assembly: AssemblyVersion("0.2.0.0")]

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
                var app = new Application();
                Window window;
                using (var xaml = File.OpenRead(Path.Combine(Root, "app", "Main.xaml")))
                    window = (Window)XamlReader.Load(xaml);
                new Dashboard(window, args.Contains("--client"));
                return app.Run(window);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.GetBaseException().Message, "Cadrivect", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    internal class Dashboard
    {
        readonly Window window;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly string preferences = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaRelay", "dashboard.json");
        readonly string[] names = { "Sony", "Direct", "Demo" };
        readonly string[] titles = { "PS Remote Play", "Direct to PS5", "Arcade demo" };
        readonly string[] subtitles = { "Play through your existing Sony connection.", "Pair once and play through chiaki-ng.", "Try video, audio, and controls on this PC." };
        readonly string[] actions = { "Open Remote Play", "Open direct client", "Play demo" };
        readonly string[] modeIds = { "original", "sharp", "fsr", "rtxvideo", "dlss", "dlaa" };
        readonly string[] hdrIds = { "auto","off","hdr10" };
        readonly string[] resolutionIds = { "native","720","1080","1440","4k" };
        bool refreshingDisplays;
        readonly Brush mint = new SolidColorBrush(Color.FromRgb(143,230,208));
        readonly Brush dark = new SolidColorBrush(Color.FromRgb(16,39,37));
        readonly Brush grey = new SolidColorBrush(Color.FromRgb(38,53,60));
        readonly Brush light = new SolidColorBrush(Color.FromRgb(230,240,239));
        string source = "Sony";
        string page = "Play";
        bool busy;
        Process player;

        T UI<T>(string name) where T : class { return (T)window.FindName(name); }

        internal Dashboard(Window w, bool client)
        {
            window = w;
            window.Icon = new BitmapImage(new Uri(Path.Combine(Program.Root,"app","assets","mark.ico")));
            UI<ImageBrush>("HeroBrush").ImageSource = new BitmapImage(new Uri(Path.Combine(Program.Root,"app","assets","aurora.jpg")));
            UI<ComboBox>("Mode").ItemsSource = new[] { "Original", "Sharpen", "AMD FSR 1", "NVIDIA RTX Video SR", "NVIDIA DLSS · experimental", "NVIDIA DLAA · experimental" };
            UI<ComboBox>("Mode").SelectionChanged += delegate {
                int i=UI<ComboBox>("Mode").SelectedIndex;
                UI<TextBlock>("PictureHint").Text=i>=4
                    ? "Experimental video reconstruction uses estimated motion and flat depth. Ctrl+Alt+B compares the picture. Ctrl+Alt+Q exits."
                    : "Open the view after your game is visible. Ctrl+Alt+B compares the picture. Ctrl+Alt+Q exits.";
            };
            UI<ComboBox>("Multiplier").ItemsSource = new[] { "Off", "2×", "3×", "4×" };
            var saved = ReadSettings();
            var output=UI<ComboBox>("PlaybackDisplay");
            RefreshDisplays(Field(saved,"display"));
            var hostScreen=UI<ComboBox>("HostDisplay");
            var displays=DisplayTarget.Enumerate();
            hostScreen.ItemsSource=displays;
            hostScreen.SelectedItem=displays.FirstOrDefault(d=>d.DeviceName==Field(saved,"hostDisplay")) ?? displays.FirstOrDefault(d=>d.Primary) ?? displays.FirstOrDefault();
            UI<CheckBox>("ClientHdr").IsChecked=string.Equals(Field(saved,"clientHdr"),"True",StringComparison.OrdinalIgnoreCase);
            output.DropDownOpened += delegate { RefreshDisplays(SelectedDisplayName()); };
            output.SelectionChanged += delegate {
                if(refreshingDisplays || player==null || player.HasExited) return;
                try {
                    if(DisplayTarget.MovePlayer(player.Id,SelectedDisplayName()))
                        Show("Moved playback to "+SelectedDisplayName()+".");
                } catch(Exception ex) { Show(ex.Message,true); }
            };
            UI<ComboBox>("HdrOutput").ItemsSource=new[]{"Auto","SDR","HDR10"};
            UI<ComboBox>("HdrOutput").SelectedIndex=ParseIndex(saved,"hdr",0,hdrIds.Length);
            UI<ComboBox>("RenderResolution").ItemsSource=new[]{"Native","720p","1080p","1440p","4K"};
            UI<ComboBox>("RenderResolution").SelectedIndex=ParseIndex(saved,"resolution",0,resolutionIds.Length);
            UI<CheckBox>("Fullscreen").IsChecked=!string.Equals(Field(saved,"fullscreen"),"False",StringComparison.OrdinalIgnoreCase);
            UI<ComboBox>("Quality").ItemsSource = new[] { "Use Moonlight settings", "1080p · 60 FPS · 35 Mbps", "1440p · 120 FPS · 65 Mbps", "4K · 60 FPS · 80 Mbps", "4K · 120 FPS · 120 Mbps" };
            UI<ComboBox>("Quality").SelectedIndex = ParseIndex(saved,"quality",0,5);
            UI<ComboBox>("Mode").SelectedIndex = ParseIndex(saved, "mode", 2, modeIds.Length);
            UI<ComboBox>("Multiplier").SelectedIndex = ParseIndex(saved, "multiplier", 0, 4);
            UI<TextBox>("HostAddress").Text = Field(saved, "address");
            var addresses = BindAddresses().ToList();
            UI<ComboBox>("BindIP").ItemsSource = addresses;
            var previous = Field(saved,"bind");
            UI<ComboBox>("BindIP").SelectedItem = addresses.Contains(previous) ? previous : addresses[0];
            var selected = Field(saved,"source");
            SetSource(names.Contains(selected) ? selected : "Sony");
            var previousPage = Field(saved, "page");
            SetPage(client ? "Client" : new[]{"Play","Host","Client"}.Contains(previousPage) ? previousPage : "Play");
            foreach (string key in names)
                UI<Button>(key).Click += delegate { SetSource(key); };
            foreach (var key in new[]{"Play","Host","Client"})
                UI<Button>(key+"Nav").Click += delegate { SetPage(key); };

            Bind("OpenSource", () => Open(false));
            Bind("Enhance", () => Open(true));
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
                if (MessageBox.Show(window,"Revoke host access for " + peer.Name + "? The client will need a new pairing PIN.","Revoke access",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
                await Relay("RevokeClient","-ClientId",peer.Id);
                await LoadPeers();
                Show("Access revoked for " + peer.Name + ".");
            });
            UI<Button>("Controllers").Click += delegate {
                try { Process.Start("https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2getting__started.html#windows"); }
                catch (Exception e) { Show(e.Message,true); }
            };
            UI<Button>("DirectClient").Click += delegate { SetSource("Direct"); SetPage("Play"); };
            Bind("OpenClient", () => Relay("OpenClient"));
            Bind("ClientPair", () => Relay("PairClient","-Address", ClientAddress()));
            Bind("ClientPlay", Connect);
            Bind("ApprovePin", async () => {
                var pin = UI<TextBox>("Pin").Text.Trim();
                if (pin.Length != 4 || pin.Any(c => c < '0' || c > '9'))
                    throw new InvalidOperationException("Enter the four digits shown on the client.");
                await Relay("ApprovePin","-Pin",pin);
                UI<TextBox>("Pin").Clear();
            });
            window.Closed += delegate { SaveSettings(); };
        }

        string SelectedIP() { return Convert.ToString(UI<ComboBox>("BindIP").SelectedItem); }
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
        Task Connect()
        {
            string address = ClientAddress();
            switch (UI<ComboBox>("Quality").SelectedIndex) {
                case 1: return Relay("Play","-Address",address,"-Resolution","1080","-Fps","60","-Mbps","35");
                case 2: return Relay("Play","-Address",address,"-Resolution","1440","-Fps","120","-Mbps","65");
                case 3: return Relay("Play","-Address",address,"-Resolution","4K","-Fps","60","-Mbps","80");
                case 4: return Relay("Play","-Address",address,"-Resolution","4K","-Fps","120","-Mbps","120");
                default: return Relay("Play","-Address",address);
            }
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
            b.Background = on ? mint : grey;
            b.Foreground = on ? dark : light;
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        void SetPage(string value)
        {
            page = value;
            foreach(var key in new[]{"Play","Host","Client"})
            {
                bool on = key == value;
                UI<StackPanel>(key+"Page").Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                Highlight(UI<Button>(key+"Nav"), on);
            }
        }

        void SetSource(string value)
        {
            source = value;
            int i = Array.IndexOf(names,value);
            UI<TextBlock>("HeroTitle").Text = titles[i];
            UI<TextBlock>("HeroDetail").Text = subtitles[i];
            UI<Button>("OpenSource").Content = actions[i];
            foreach(string key in names) Highlight(UI<Button>(key), key == value);
        }

        void Show(string message, bool error = false)
        {
            UI<TextBlock>("Status").Text = message;
            UI<TextBlock>("Status").ToolTip = message;
            UI<Ellipse>("Indicator").Fill = error ? Brushes.LightSalmon : mint;
        }

        async Task Open(bool enhance)
        {
            if (enhance && player != null && !player.HasExited)
                throw new InvalidOperationException("Close the running enhanced view before opening another.");
            Show(source == "Direct" ? "Opening chiaki-ng. The first launch downloads and verifies the direct client…" : "Opening " + titles[Array.IndexOf(names,source)] + "…");
            var r = await Script("sources/Start-Source.ps1", "-Source",source);
            long handle;
            if (!long.TryParse(Field(r,"windowHandle"),out handle) || handle==0)
                throw new InvalidOperationException("Open the source and connect to a game, then try the enhanced view again.");
            var target=UI<ComboBox>("PlaybackDisplay").SelectedItem as DisplayTarget;
            if(target!=null) target.MoveSourceWindow(new IntPtr(handle));
            if (!enhance)
            {
                Show("Opened " + titles[Array.IndexOf(names,source)] + ". The enhanced view is available when your game is visible.");
                return;
            }
            string mode = modeIds[UI<ComboBox>("Mode").SelectedIndex];
            string mult = (UI<ComboBox>("Multiplier").SelectedIndex+1).ToString(CultureInfo.InvariantCulture);
            string exe = Path.Combine(Program.Root,"engine","bin","CadrivectPlayer.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("CadrivectPlayer is missing. Use the complete Windows release ZIP.");
            string reportDir = Path.Combine(Path.GetDirectoryName(preferences),"sessions");
            Directory.CreateDirectory(reportDir);
            string report = Path.Combine(reportDir,Guid.NewGuid().ToString("N")+".json");
            var p = new Process();
            string options=" --display "+Quote(SelectedDisplayName())+" --resolution "+resolutionIds[UI<ComboBox>("RenderResolution").SelectedIndex]
                +" --hdr "+hdrIds[UI<ComboBox>("HdrOutput").SelectedIndex]+(UI<CheckBox>("Fullscreen").IsChecked==true ? "" : " --windowed");
            p.StartInfo = new ProcessStartInfo(exe, "--hwnd "+handle.ToString(CultureInfo.InvariantCulture)+" --mode "+mode+" --multiplier "+mult+options+" --report "+Quote(report)) { UseShellExecute=false, WorkingDirectory=Path.GetDirectoryName(exe) };
            p.EnableRaisingEvents = true;
            p.Exited += delegate {
                if (!window.Dispatcher.HasShutdownStarted)
                    window.Dispatcher.BeginInvoke(new Action(() => {
                        if (p != player) return;
                        if (p.ExitCode==0) Show(SessionSummary(report));
                        else Show("Enhanced view stopped. Check that the source window is visible and try again.", true);
                    }));
            };
            p.Start();
            player = p;
            Show("Enhanced view opened. Audio and input stay with the source.");
        }

        string SessionSummary(string file)
        {
            try {
                var result = json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
                string effect=Field(result,"activeNvidiaEffect");
                string label=effect=="rtxvideo" ? "RTX Video SR" : effect.ToUpperInvariant();
                return string.Format(CultureInfo.CurrentCulture, "Session ended. {0}{1} source FPS · {2} submitted FPS · {3} interpolated frames.",
                    label.Length==0 ? "" : label+" · ",Field(result,"sourceFps"),Field(result,"submittedFps"),Field(result,"generated"));
            } catch (IOException) {} catch (UnauthorizedAccessException) {} catch (ArgumentException) {} catch (InvalidOperationException) {}
            return "Enhanced view closed.";
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
                var settings = new { mode=UI<ComboBox>("Mode").SelectedIndex, multiplier=UI<ComboBox>("Multiplier").SelectedIndex,
                    display=SelectedDisplayName(), resolution=UI<ComboBox>("RenderResolution").SelectedIndex,hdr=UI<ComboBox>("HdrOutput").SelectedIndex, fullscreen=UI<CheckBox>("Fullscreen").IsChecked==true,
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
