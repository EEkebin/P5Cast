using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        int setupVersion, setupStep;
        bool setupPairing, syncingSetupColor;
        int setupColorPreference;
        string pendingSetupConsole;
        bool SetupOpen {get {return UI<Grid>("SetupOverlay").Visibility==Visibility.Visible;}}

        void InitSetup(Dictionary<string,object> saved,bool client)
        {
            // Existing installations keep their startup experience. An interrupted first run resumes.
            setupVersion=saved.ContainsKey("setupVersion") ? ParseIndex(saved,"setupVersion",0,2) : saved.Count==0 ? 0 : 1;
            window.Loaded+=delegate {if(!client && setupVersion==0) OpenSetup();};
            UI<Button>("OpenSetup").Click+=delegate {OpenSetup();};
            UI<Button>("SetupSkip").Click+=delegate {CloseSetup(true);};
            UI<Button>("SetupBack").Click+=delegate {if(!setupPairing && setupStep>0) SetSetupStep(setupStep-1);};
            UI<Button>("SetupNext").Click+=delegate {
                if(setupPairing) return;
                if(setupStep==0 && !SetupDisplayValid()) return;
                if(setupStep<2) SetSetupStep(setupStep+1); else FinishSetup();
            };
            UI<Button>("SetupAdd").Click+=delegate {SetSetupPairForm(UI<StackPanel>("SetupPairForm").Visibility!=Visibility.Visible);};
            UI<Button>("SetupPair").Click+=async delegate {await PairFromSetup();};
            UI<Button>("SetupAccountHelp").Click+=delegate {
                try {Process.Start("https://chiaki-ng.github.io/chiaki-ng/setup/configuration/#registering-your-playstation");}
                catch(Exception e) {SetupMessage(e.GetBaseException().Message);}
            };
            UI<ComboBox>("SetupDisplay").SelectionChanged+=delegate {SetupDisplayInfo();};
            UI<ComboBox>("SetupColor").SelectionChanged+=delegate {
                if(!syncingSetupColor && !SetupUsesSdr) setupColorPreference=UI<ComboBox>("SetupColor").SelectedIndex;
                SetupMessage("");
            };
            UI<ComboBox>("SetupRenderer").SelectionChanged+=delegate {if(!syncingSetupColor) RefreshSetupRenderer();};
        }

        void OpenSetup()
        {
            if(SetupOpen || StopPromptOpen || closing) return;
            if(busy || requestingRest) {Show("Finish the current action, then reopen Setup."); return;}
            if(fullscreen) ToggleFullscreen();
            var display=UI<ComboBox>("SetupDisplay");
            string selected=SelectedDisplayName();
            var monitors=DisplayTarget.Enumerate();
            display.ItemsSource=monitors;
            display.SelectedItem=monitors.FirstOrDefault(x=>x.DeviceName==selected) ?? monitors.FirstOrDefault(x=>x.Primary) ?? monitors.FirstOrDefault();
            syncingSetupColor=true;
            foreach(var pair in new[]{new[]{"SetupColor","HdrOutput"},new[]{"SetupStream","DirectStreamSize"},new[]{"SetupRenderer","VideoRenderer"}}) {
                var draft=UI<ComboBox>(pair[0]); var setting=UI<ComboBox>(pair[1]);
                draft.ItemsSource=setting.ItemsSource; draft.SelectedItem=setting.SelectedItem;
            }
            syncingSetupColor=false;
            setupColorPreference=preferredHdr;
            RefreshSetupRenderer();
            var console=UI<ComboBox>("ConsolePicker").SelectedItem as DirectConsole;
            RefreshSetupConsoles(pendingSetupConsole ?? (console==null ? null : console.Mac));
            SetSetupPairForm(false);
            SetupDisplayInfo();
            SetModalBackgroundEnabled(false);
            UI<Grid>("SetupOverlay").Visibility=Visibility.Visible;
            UI<Button>("SetupSkip").Content=setupVersion==0 ? "Skip setup" : "Cancel";
            SetSetupStep(0);
        }

        void SetModalBackgroundEnabled(bool enabled)
        {
            if(!enabled) surface.EndPointerGesture();
            // WPF cannot layer a visual over the embedded native playback child.
            // Hide its HWND while the session and its layout remain active.
            UI<Border>("VideoHost").Visibility=enabled?Visibility.Visible:Visibility.Hidden;
            foreach(string name in new[]{"Workspace","StatusFooter","PlayNav","HostNav","ClientNav","ShowStats"})
                UI<FrameworkElement>(name).IsEnabled=enabled;
        }

        bool SetupDropDownOpen()
        {
            return new[]{"SetupDisplay","SetupColor","SetupRenderer","SetupStream","SetupConsole"}.Any(n=>UI<ComboBox>(n).IsDropDownOpen);
        }

        void SetupMessage(string text) {UI<TextBlock>("SetupMessage").Text=text;}

        bool SetupUsesSdr {get {return UI<ComboBox>("SetupRenderer").SelectedIndex==Array.IndexOf(rendererIds,"opengl");}}

        void RefreshSetupRenderer()
        {
            var api=UI<ComboBox>("SetupRenderer");
            api.IsEnabled=!Active && !preparing;
            string hint;
            switch(Convert.ToString(api.SelectedItem)) {
                case "Direct3D 12": hint="Supports HDR10, DLSS and AMD temporal FSR on compatible hardware."; break;
                case "Vulkan": hint="Supports HDR10 and AMD temporal FSR. Experimental DLSS 5 uses SDR on eligible NVIDIA GPUs."; break;
                case "OpenGL": hint="Supports SDR playback, sharpening and spatial FSR."; break;
                default: hint="Auto uses Direct3D 11 on Windows. DLSS and DLAA are available on compatible NVIDIA GPUs."; break;
            }
            UI<TextBlock>("SetupRendererHint").Text=api.IsEnabled ? hint : "Stop playback to change the graphics API.";
            var color=UI<ComboBox>("SetupColor");
            syncingSetupColor=true;
            color.IsEnabled=!SetupUsesSdr;
            color.SelectedIndex=SetupUsesSdr ? 1 : setupColorPreference;
            syncingSetupColor=false;
            SetupDisplayInfo();
        }

        void SetupDisplayInfo()
        {
            var monitor=UI<ComboBox>("SetupDisplay").SelectedItem as DisplayTarget;
            UI<TextBlock>("SetupDisplayHint").Text=SetupUsesSdr ? "OpenGL uses SDR. Choose Direct3D or Vulkan to use HDR." :
                monitor==null ? "Select the display where you want to play." :
                monitor.AdvancedColorEnabled ? "Windows HDR is on for this display. Auto can use HDR10." :
                "Windows HDR is off for this display. Auto uses SDR.";
            SetupMessage("");
        }

        bool SetupDisplayValid()
        {
            var monitor=UI<ComboBox>("SetupDisplay").SelectedItem as DisplayTarget;
            if(monitor==null) {SetupMessage("Select a playback monitor.");return false;}
            if(UI<ComboBox>("SetupColor").SelectedIndex==2 && !monitor.AdvancedColorEnabled) {
                SetupMessage("Choose Auto or SDR, or enable Windows HDR for this display and reopen setup.");
                return false;
            }
            return true;
        }

        void RefreshSetupConsoles(string selected)
        {
            var consoles=DirectConsole.List();
            var picker=UI<ComboBox>("SetupConsole");
            picker.ItemsSource=consoles;
            picker.SelectedItem=consoles.FirstOrDefault(c=>string.Equals(c.Mac,selected,StringComparison.OrdinalIgnoreCase)) ?? consoles.FirstOrDefault();
            picker.IsEnabled=consoles.Count>0;
            UI<TextBlock>("SetupConsoleHint").Text=consoles.Count==0 ? "Add your PS5 below, or continue and pair later." :
                Active || preparing ? "Your session stays connected. This selection applies after you disconnect." :
                "This console is already paired. Continue to use it, or add another PS5.";
        }

        void SetSetupPairForm(bool open)
        {
            UI<StackPanel>("SetupPairForm").Visibility=open ? Visibility.Visible : Visibility.Collapsed;
            UI<Button>("SetupAdd").Content=open ? "Cancel adding console" : "Add a console";
            if(setupStep==1) UI<Button>("SetupNext").Content=open ? "Pair later" : "Next";
            UI<TextBox>("SetupCode").Clear();
            UI<TextBox>("SetupAccount").Text=open ? DirectConsole.KnownAccountId() : "";
            UI<TextBox>("SetupAddress").Clear();
            if(open) UI<TextBox>("SetupAddress").Focus();
            SetupMessage("");
        }

        async Task PairFromSetup()
        {
            if(!SetupOpen || setupStep!=1 || setupPairing) return;
            setupPairing=true;
            foreach(string n in new[]{"SetupPairForm","SetupAdd","SetupBack","SetupNext","SetupSkip","SetupConsole"})
                UI<FrameworkElement>(n).IsEnabled=false;
            SetupMessage("Pairing with your PS5…");
            try {
                var registered=await DirectConsole.Pair(UI<TextBox>("SetupAddress").Text,UI<TextBox>("SetupAccount").Text,UI<TextBox>("SetupCode").Text);
                consoleAddresses[registered.Mac]=registered.Address;
                var previous=UI<ComboBox>("ConsolePicker").SelectedItem as DirectConsole;
                if(!Active && !preparing) ReloadConsoles(previous==null ? registered.Mac : previous.Mac);
                else if(pendingSetupConsole==null) pendingSetupConsole=previous==null ? registered.Mac : previous.Mac;
                RefreshSetupConsoles(registered.Mac);
                SaveSettings();
                SetSetupPairForm(false);
                SetupMessage("Paired with "+registered.Name+". Choose Next to review.");
            } catch(Exception e) {
                SetupMessage(e.GetBaseException().Message);
            } finally {
                setupPairing=false;
                foreach(string n in new[]{"SetupPairForm","SetupAdd","SetupBack","SetupNext","SetupSkip"})
                    UI<FrameworkElement>(n).IsEnabled=true;
                var list=UI<ComboBox>("SetupConsole"); list.IsEnabled=list.Items.Count>0;
                if(UI<StackPanel>("SetupPairForm").Visibility==Visibility.Visible) UI<TextBox>("SetupCode").Focus();
                else UI<Button>("SetupNext").Focus();
            }
        }

        void SetSetupStep(int step)
        {
            setupStep=step;
            var pages=new[]{"SetupDisplayPage","SetupConsolePage","SetupFinishPage"};
            for(int n=0;n<pages.Length;n++) UI<StackPanel>(pages[n]).Visibility=n==step ? Visibility.Visible : Visibility.Collapsed;
            UI<TextBlock>("SetupStep").Text="Step "+(step+1)+" of 3";
            UI<TextBlock>("SetupTitle").Text=new[]{"Choose your display","Choose your console","Review your setup"}[step];
            UI<Button>("SetupBack").IsEnabled=step>0;
            UI<Button>("SetupNext").Content=step==2 ? "Finish" :
                step==1 && UI<StackPanel>("SetupPairForm").Visibility==Visibility.Visible ? "Pair later" : "Next";
            SetupMessage("");
            if(step==2) {
                var console=UI<ComboBox>("SetupConsole").SelectedItem as DirectConsole;
                UI<TextBlock>("SetupSummary").Text=Convert.ToString(UI<ComboBox>("SetupDisplay").SelectedItem)+Environment.NewLine+
                    "Color output  ·  "+UI<ComboBox>("SetupColor").SelectedItem+Environment.NewLine+
                    "Graphics API  ·  "+UI<ComboBox>("SetupRenderer").SelectedItem+Environment.NewLine+
                    "PS5 stream  ·  "+UI<ComboBox>("SetupStream").SelectedItem+Environment.NewLine+
                    "Console  ·  "+(console==null ? "Pair later" : console.Name);
                UI<TextBlock>("SetupFinishHint").Text=Active || preparing ?
                    "Finish to save your settings. Stream resolution and console selection apply on your next connection." :
                    console==null ? "Finish to save. You can try Offline demo from the source menu or return here to pair a PS5." :
                    "Finish to save. Choose Direct to PS5 and press Play when you’re ready to connect.";
            }
            var title=UI<TextBlock>("SetupTitle");
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(delegate {if(SetupOpen && setupStep==step) title.Focus();}));
        }

        void FinishSetup()
        {
            var monitor=UI<ComboBox>("SetupDisplay").SelectedItem as DisplayTarget;
            string device=monitor==null ? null : monitor.DeviceName;
            var available=DisplayTarget.Enumerate().FirstOrDefault(x=>x.DeviceName==device);
            if(available==null || (UI<ComboBox>("SetupColor").SelectedIndex==2 && !available.AdvancedColorEnabled)) {
                SetSetupStep(0);
                var display=UI<ComboBox>("SetupDisplay"); display.ItemsSource=DisplayTarget.Enumerate();
                display.SelectedItem=display.Items.Cast<DisplayTarget>().FirstOrDefault(x=>x.DeviceName==device) ?? display.Items.Cast<DisplayTarget>().FirstOrDefault();
                SetupMessage("Your display configuration changed. Review your monitor and color selections.");
                return;
            }
            RefreshDisplays(device);
            MoveDashboard();
            preferredHdr=setupColorPreference;
            if(!Active && !preparing) UI<ComboBox>("VideoRenderer").SelectedItem=UI<ComboBox>("SetupRenderer").SelectedItem;
            UI<ComboBox>("HdrOutput").SelectedItem=UI<ComboBox>("SetupColor").SelectedItem;
            RefreshFrucOutputIntent();
            UI<ComboBox>("DirectStreamSize").SelectedItem=UI<ComboBox>("SetupStream").SelectedItem;
            var console=UI<ComboBox>("SetupConsole").SelectedItem as DirectConsole;
            if(console!=null) {
                var current=UI<ComboBox>("ConsolePicker").SelectedItem as DirectConsole;
                if(current==null || !string.Equals(current.Mac,console.Mac,StringComparison.OrdinalIgnoreCase) || pendingSetupConsole!=null) {
                    if(Active || preparing) pendingSetupConsole=console.Mac;
                    else ReloadConsoles(console.Mac);
                }
            }
            CloseSetup(true);
            Show("Setup saved.");
        }

        void CloseSetup(bool remember)
        {
            if(!SetupOpen || setupPairing) return;
            UI<Grid>("SetupOverlay").Visibility=Visibility.Collapsed;
            SetSetupPairForm(false);
            SetModalBackgroundEnabled(true);
            if(remember) {setupVersion=1; SaveSettings();}
            UI<Button>("OpenSetup").Focus();
        }
    }
}
