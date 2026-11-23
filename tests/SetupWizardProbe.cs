using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

internal static partial class WindowChromeProbe
{
    const string ScenarioKey="P5CAST_WIZARD_CASE";
    const string PairName="Offline fixture PS5",PairMac="02000000C005";
    const string OriginalName="A previously paired PS5",OriginalMac="02000000C006";
    const string FakeAccount="RklYVFVSRTE=",InvalidPin="13x2468";
    const string ValidPin="41759260",RegisteredMac="02000000C007",RegisteredName="New synthetic PS5";
    static readonly byte[] PairingBytes=Enumerable.Range(100,32).Select(i=>(byte)i).ToArray();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("advapi32.dll")] static extern int RegOverridePredefKey(IntPtr key,IntPtr replacement);
    static RegistryKey testRegistryRoot;

    static void IsolateSetupProfile()
    {
        // This process sees a read-only, unrelated key in place of HKCU. In particular,
        // the import of existing Chiaki registrations cannot see the tester's consoles.
        testRegistryRoot=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0",false);
        Require(testRegistryRoot!=null,"Cannot isolate registry imports for the setup test.");
        int error=RegOverridePredefKey(new IntPtr(unchecked((int)0x80000001)),testRegistryRoot.Handle.DangerousGetHandle());
        if(error!=0) throw new Win32Exception(error);
        // Keep first-run tests from changing any real Discord status without seeding
        // a dashboard.json (the absence of that file is the behavior under test).
        EventManager.RegisterClassHandler(typeof(Window),FrameworkElement.LoadedEvent,new RoutedEventHandler(delegate(object sender,RoutedEventArgs e) {
            var option=((Window)sender).FindName("SharePresence") as CheckBox;
            if(option!=null) option.IsChecked=false;
        }));
    }

    static string Preferences {get {return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"),"LumaRelay","dashboard.json");}}
    static Dictionary<string,object> ReadPrefs()
    {
        return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Preferences));
    }
    static bool Shown(string name) {return Find<FrameworkElement>(name).IsVisible;}
    static bool HasSetupFocus()
    {
        var focus=Keyboard.FocusedElement as DependencyObject;
        var modal=Find<Grid>("SetupOverlay");
        return focus==modal || (focus!=null && modal.IsAncestorOf(focus));
    }
    static async Task CheckModalPage(string page)
    {
        await Until(()=>Shown(page),"Setup did not show "+page+".");
        Require(new[]{"SetupDisplayPage","SetupConsolePage","SetupFinishPage"}.Count(Shown)==1,"Setup pages overlap.");
        string stage=Find<TextBlock>("SetupStep").Text;
        Require(!string.IsNullOrWhiteSpace(stage) && !string.IsNullOrWhiteSpace(Find<TextBlock>("SetupTitle").Text),"Missing step or title.");
        var host=Find<Border>("VideoHost").Child as HwndHost;
        await Until(()=>host==null || host.Handle==IntPtr.Zero || !IsWindowVisible(host.Handle),"Native video covers "+page+".",600);
    }
    static void CheckFocusCycle()
    {
        Require(HasSetupFocus(),"Opening setup left focus on the dashboard.");
        var touched=new HashSet<DependencyObject>();
        for(int i=0;i<22;i++) {
            var focused=Keyboard.FocusedElement as UIElement;
            Require(focused!=null && focused.MoveFocus(new TraversalRequest(i<11 ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous)),
                "Tab traversal stalled inside setup.");
            Require(HasSetupFocus(),"Tab traversal moved behind the wizard.");
            touched.Add(Keyboard.FocusedElement as DependencyObject);
        }
        Require(touched.Count>=3,"Setup keyboard traversal did not reach its choices and actions.");
        var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,Key.F11) {RoutedEvent=Keyboard.PreviewKeyDownEvent};
        ((UIElement)Keyboard.FocusedElement).RaiseEvent(key);
        Require(key.Handled && Shown("SetupOverlay"),"A dashboard fullscreen shortcut escaped the wizard.");
    }
    static async Task PairValidation()
    {
        Click("SetupAdd");
        await Until(()=>Shown("SetupPairForm"),"Add a console did not open inside setup.");
        Find<TextBox>("SetupAddress").Text="127.0.0.1";
        Find<TextBox>("SetupAccount").Text=FakeAccount;
        Find<TextBox>("SetupCode").Text=InvalidPin;
        Click("SetupPair");
        await Until(()=>Shown("SetupPairForm") && !string.IsNullOrWhiteSpace(Find<TextBlock>("SetupMessage").Text),
            "Invalid pairing input has no inline validation.");
        Require(!Find<TextBlock>("SetupMessage").Text.Contains(FakeAccount) && !Find<TextBlock>("SetupMessage").Text.Contains(InvalidPin),
            "A credential was echoed in the pairing error.");
    }

    static int RegisterSetupFixture(string address)
    {
        if(Environment.GetEnvironmentVariable(ScenarioKey)!="pair" || address!="127.0.0.1") return 2;
        using(var input=new MemoryStream()) {
            Console.OpenStandardInput().CopyTo(input);
            if(!input.ToArray().SequenceEqual(Convert.FromBase64String(FakeAccount).Concat(Encoding.ASCII.GetBytes(ValidPin)))) return 3;
        }
        var answer=new byte[74];
        Encoding.ASCII.GetBytes("CDR1").CopyTo(answer,0);new byte[]{2,0,0,0,192,7}.CopyTo(answer,4);
        Encoding.UTF8.GetBytes(RegisteredName).CopyTo(answer,10);PairingBytes.CopyTo(answer,42);
        Console.OpenStandardOutput().Write(answer,0,answer.Length);
        return 0;
    }

    static async Task PairWithFixture()
    {
        string coreRoot=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("LOCALAPPDATA")),"registration-fixture");
        string helperDir=Path.Combine(coreRoot,"engine","bin","direct");
        Directory.CreateDirectory(helperDir);
        File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(helperDir,"CadrivectDirect.exe"),true);
        var program=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetType("Cadrivect.Program")!=null);
        var root=program.GetType("Cadrivect.Program").GetField("Root",BindingFlags.Static|BindingFlags.NonPublic);
        string actualRoot=(string)root.GetValue(null);
        // Only the registration helper is replaced for this invocation. Its protocol
        // travels over redirected pipes and the fixture does not open a socket.
        root.SetValue(null,coreRoot);
        try {
            await PairValidation();
            Find<TextBox>("SetupCode").Text=ValidPin;
            Click("SetupPair");
            await Until(()=>!Shown("SetupPairForm") && Find<TextBlock>("SetupMessage").Text.Contains(RegisteredName),
                "Fixture registration failed.",9000);
        } finally {root.SetValue(null,actualRoot);}
        Require(Find<TextBox>("SetupAccount").Text=="" && Find<TextBox>("SetupCode").Text=="","Completed pairing retained credentials in the form.");
        string profile=File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"),"Cadrivect","consoles",RegisteredMac+".json"));
        var registration=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(profile);
        Require((string)registration["Mac"]==RegisteredMac && (string)registration["AccountId"]==FakeAccount &&
            !profile.Contains(ValidPin) && !profile.Contains(Convert.ToBase64String(PairingBytes)) &&
            !string.IsNullOrEmpty((string)registration["ProtectedKeys"]),"The registered profile did not protect its keys.");
    }

    static async void CheckSetupWindow()
    {
        try {
            string scenario=Environment.GetEnvironmentVariable(ScenarioKey);
            window=Application.Current.MainWindow;
            hwnd=new WindowInteropHelper(window).Handle;
            if(scenario=="fresh" || scenario=="pending") {
                await CheckModalPage("SetupDisplayPage");
                await Until(HasSetupFocus,"First-run wizard did not take keyboard focus.");
                CheckFocusCycle();
                int before=Find<ComboBox>("DirectStreamSize").SelectedIndex;
                var stream=Find<ComboBox>("SetupStream");
                stream.SelectedIndex=(before+1)%stream.Items.Count;
                Click("SetupNext");await CheckModalPage("SetupConsolePage");
                Require(Find<ComboBox>("SetupConsole").Items.Cast<object>().All(x=>!x.ToString().Contains(PairName)),"Foreign profile leaked into clean setup.");
                if(scenario=="fresh") {
                    await PairValidation();
                    Require(Find<ComboBox>("DirectStreamSize").SelectedIndex==before,"Closing setup early committed the stream draft.");
                    Note("Closed a fresh setup during inline pairing after checking focus and native-video occlusion.");
                } else {
                    Click("SetupNext");await CheckModalPage("SetupFinishPage");
                    Click("SetupSkip");await Until(()=>!Shown("SetupOverlay"),"Skip did not dismiss setup.");
                    Require(Find<ComboBox>("DirectStreamSize").SelectedIndex==before,"Skip committed the stream draft.");
                    Require(Convert.ToInt32(ReadPrefs()["setupVersion"])==1,"Skip did not persist completion.");
                    Note("An interrupted setup reopened, allowed continuing without a console, and Skip discarded the draft.");
                }
            } else if(scenario=="pair") {
                Click("OpenSetup");await CheckModalPage("SetupDisplayPage");
                var picker=Find<ComboBox>("SetupRenderer");var color=Find<ComboBox>("SetupColor");
                Require(picker.IsEnabled,"Stopped setup cannot choose the graphics API.");
                var original=Find<ComboBox>("VideoRenderer").SelectedItem;
                color.SelectedItem="HDR10";picker.SelectedItem="OpenGL";
                Require(!color.IsEnabled && Equals(color.SelectedItem,"SDR") && Equals(Find<ComboBox>("VideoRenderer").SelectedItem,original),
                    "OpenGL did not restrict draft color to SDR or prematurely committed the renderer.");
                picker.SelectedItem="Vulkan";
                Require(color.IsEnabled && Equals(color.SelectedItem,"HDR10"),"Switching away from OpenGL lost the HDR draft.");
                color.SelectedItem="SDR";
                Click("SetupNext");await CheckModalPage("SetupConsolePage");
                await PairWithFixture();
                Click("SetupNext");await CheckModalPage("SetupFinishPage");
                Require(Find<TextBlock>("SetupSummary").Text.Contains(RegisteredName),"New console missing from setup review.");
                Click("SetupNext");await Until(()=>!Shown("SetupOverlay"),"Finish failed after registering a console.");
                var saved=ReadPrefs();
                Require((string)saved["consoleMac"]==RegisteredMac && (string)saved["renderer"]=="vulkan","Registration or renderer choice was not committed.");
                Note("Validated a mock registration through the real helper protocol, confidential credential storage and the stopped graphics API/HDR draft.");
            } else if(scenario=="legacy") {
                Require(!Shown("SetupOverlay") && Shown("OpenSetup"),"An existing profile opened setup or hid the transport shortcut.");
                Require(Find<ComboBox>("SourceChoice").SelectedIndex==2,"Offline test profile did not select Demo.");
                Click("OpenSource");await Until(()=>Find<TextBlock>("Status").Text=="Offline demo","Offline demo failed to start.",12000);
                var api=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Cadrivect.Engine.EmbeddedPlayback")).First(t=>t!=null);
                var snapshot=api.GetMethod("Snapshot");
                Func<int> presented=delegate {
                    var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>((string)snapshot.Invoke(null,null));
                    return data.ContainsKey("presented")?Convert.ToInt32(data["presented"]):0;
                };
                await Until(()=>presented()>3,"Demo never presented frames.");
                var existing=Video();
                Click("OpenSetup");await CheckModalPage("SetupDisplayPage");
                Require(!Find<ComboBox>("SetupRenderer").IsEnabled,"Wizard offered to switch graphics APIs during playback.");
                int start=presented();await Until(()=>presented()>start+2,"Playback stopped behind the open wizard.");
                var color=Find<ComboBox>("SetupColor");var stream=Find<ComboBox>("SetupStream");
                var monitor=Find<ComboBox>("SetupDisplay");
                var previousMonitor=Find<ComboBox>("PlaybackDisplay").SelectedItem.ToString();
                if(monitor.Items.Count>1) monitor.SelectedIndex=(monitor.SelectedIndex+1)%monitor.Items.Count;
                string chosenMonitor=(string)monitor.SelectedItem.GetType().GetProperty("DeviceName").GetValue(monitor.SelectedItem,null);
                color.SelectedIndex=1;stream.SelectedIndex=1;
                Require(Find<ComboBox>("PlaybackDisplay").SelectedItem.ToString()==previousMonitor && Find<ComboBox>("HdrOutput").SelectedIndex!=1 && Find<ComboBox>("DirectStreamSize").SelectedIndex!=1,
                    "Editing the setup draft changed live settings.");
                Click("SetupNext");await CheckModalPage("SetupConsolePage");
                var picker=Find<ComboBox>("SetupConsole");
                picker.SelectedItem=picker.Items.Cast<object>().Single(item=>item.ToString().Contains(PairName));
                Click("SetupNext");await CheckModalPage("SetupFinishPage");
                string summary=Find<TextBlock>("SetupSummary").Text;
                Require(summary.Contains(PairName) && summary.Contains("720") && summary.Contains("SDR"),"Review omitted the console, stream or color choice.");
                Click("SetupBack");await CheckModalPage("SetupConsolePage");
                Click("SetupBack");await CheckModalPage("SetupDisplayPage");
                Require(color.SelectedIndex==1 && stream.SelectedIndex==1,"Back lost the draft.");
                Click("SetupNext");await CheckModalPage("SetupConsolePage");
                Click("SetupNext");await CheckModalPage("SetupFinishPage");
                Click("SetupNext");await Until(()=>!Shown("SetupOverlay"),"Finish did not dismiss setup.");
                await Until(()=>IsWindowVisible(existing) && Video()==existing && presented()>start+3,"Video failed to resume on the original in-window surface.");
                var prefs=ReadPrefs();
                Require(Convert.ToInt32(prefs["setupVersion"])==1 && Convert.ToInt32(prefs["directStreamHeight"])==720 &&
                    Convert.ToInt32(prefs["hdr"])==1 && (string)prefs["display"]==chosenMonitor && (string)prefs["consoleMac"]==PairMac,
                    "Finish did not persist the chosen console, monitor, SDR and 720p.");
                Require(Find<ComboBox>("ConsolePicker").SelectedItem.ToString().Contains(OriginalName),"Finish changed the current console before disconnect.");
                Click("OpenSource");await Until(()=>Find<TextBlock>("Status").Text=="Disconnected","Could not stop the offline demo.");
                Require(Find<ComboBox>("ConsolePicker").SelectedItem.ToString().Contains(PairName),"Disconnect did not apply the deferred console choice.");
                Note("Legacy profile opened normally. A live offline session kept presenting behind setup and returned to the same video HWND after Finish.");
            } else {
                Require(!Shown("SetupOverlay") && Shown("OpenSetup"),"Completed setup reappeared on launch or hid its shortcut.");
                if(scenario=="finished")
                    Require(Find<ComboBox>("DirectStreamSize").SelectedIndex==1 && Find<ComboBox>("HdrOutput").SelectedIndex==1 &&
                        Find<ComboBox>("ConsolePicker").SelectedItem.ToString().Contains(PairName),"Committed setup choices were lost on relaunch.");
                Click("OpenSetup");await CheckModalPage("SetupDisplayPage");
                var escape=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,Key.Escape) {RoutedEvent=Keyboard.PreviewKeyDownEvent};
                Find<TextBlock>("SetupTitle").RaiseEvent(escape);
                await Until(()=>!Shown("SetupOverlay") && Keyboard.FocusedElement==Find<Button>("OpenSetup"),"Escape failed to return focus to the setup shortcut.");
                Note("Reopened completed setup from the transport on a clean process launch.");
            }
            completed=true; window.Close();
        } catch(Exception error) {failure=error;Application.Current.Shutdown(1);}
    }

    static int LaunchSetupCases(string[] args)
    {
        string local=Path.Combine(args[1],"local"),prefs=Path.Combine(local,"LumaRelay","dashboard.json");
        string registered=Path.Combine(local,"Cadrivect","consoles",PairMac+".json");
        string fixture=new JavaScriptSerializer().Serialize(new{Name=PairName,Mac=PairMac,Address="127.0.0.1",ProtectedKeys="fixture-only-do-not-decrypt"});
        string original=Path.Combine(Path.GetDirectoryName(registered),OriginalMac+".json");
        string originalFixture=new JavaScriptSerializer().Serialize(new{Name=OriginalName,Mac=OriginalMac,Address="127.0.0.1",ProtectedKeys="previous-fixture"});
        foreach(var scenario in new[]{"fresh","pending","skipped","pair","legacy","finished"}) {
            if(scenario=="legacy") {
                File.WriteAllText(prefs,new JavaScriptSerializer().Serialize(new{discordPresence=false,source="Demo",maximized=false,settingsOpen=false,hdr=0,directStreamHeight=1080,consoleMac=OriginalMac}));
                Directory.CreateDirectory(Path.GetDirectoryName(registered));File.WriteAllText(registered,fixture);File.WriteAllText(original,originalFixture);
            }
            Environment.SetEnvironmentVariable(ScenarioKey,scenario);
            int result=Launch(args[0],args[1],args[2],false);
            if(result!=0) return result;
            if(!File.Exists(prefs)) {Require(scenario=="fresh","Missing preferences.");continue;}
            string saved=File.ReadAllText(prefs);
            Require(!saved.Contains(FakeAccount) && !saved.Contains(InvalidPin) && !saved.Contains(ValidPin) &&
                !saved.Contains(Convert.ToBase64String(PairingBytes)) && !saved.Contains("fixture-only-do-not-decrypt"),"Pairing secrets leaked into preferences.");
            if(scenario=="legacy" || scenario=="finished") Require(File.Exists(registered) && File.ReadAllText(registered)==fixture &&
                File.Exists(original) && File.ReadAllText(original)==originalFixture,"Wizard changed an existing console registration.");
        }
        return 0;
    }
}
