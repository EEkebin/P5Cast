using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        int volumeLevel=100;
        bool volumeMuted;
        StreamAudio externalStreamAudio;
        int EffectiveStreamVolume {get {return volumeMuted ? 0 : volumeLevel;}}

        void AttachStreamAudio(int processId)
        {
            ReleaseStreamAudio();
            externalStreamAudio=new StreamAudio(processId,EffectiveStreamVolume);
        }

        void ReleaseStreamAudio()
        {
            if(externalStreamAudio==null) return;
            externalStreamAudio.Dispose();
            externalStreamAudio=null;
        }

        void InitVolume(Dictionary<string,object> saved)
        {
            var button=UI<Button>("OpenVolume");
            var popup=UI<Popup>("VolumePopup");
            var slider=UI<Slider>("StreamVolumeSlider");
            var mute=UI<CheckBox>("MuteStream");
            int level;
            if(int.TryParse(Field(saved,"streamVolume"),out level))
                volumeLevel=Math.Max(0,Math.Min(100,level));
            bool.TryParse(Field(saved,"streamMuted"),out volumeMuted);
            slider.Value=volumeLevel;
            mute.IsChecked=volumeMuted;
            var save=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
            save.Tick+=delegate {save.Stop(); SaveSettings();};
            Action changed=delegate {
                UpdateVolume();
                if(window.IsLoaded) {save.Stop(); save.Start();}
            };
            slider.ValueChanged+=delegate {
                volumeLevel=(int)Math.Round(slider.Value);
                if(volumeMuted) mute.IsChecked=false;
                else changed();
            };
            mute.Checked+=delegate {volumeMuted=true; changed();};
            mute.Unchecked+=delegate {volumeMuted=false; changed();};
            button.Click+=delegate {popup.IsOpen=!popup.IsOpen;};
            popup.Opened+=delegate {
                button.IsHitTestVisible=false;
                slider.Focus();
            };
            popup.Closed+=delegate {button.IsHitTestVisible=true;};
            popup.Child.PreviewKeyDown+=delegate(object sender,KeyEventArgs e) {
                if(e.Key==Key.Escape) {
                    e.Handled=true;
                    popup.IsOpen=false;
                    button.Focus();
                }
            };
            button.IsEnabledChanged+=delegate {if(!button.IsEnabled) popup.IsOpen=false;};
            button.IsVisibleChanged+=delegate {if(!button.IsVisible) popup.IsOpen=false;};
            window.Closed+=delegate {save.Stop(); popup.IsOpen=false;};
            UpdateVolume();
        }

        void UpdateVolume()
        {
            string percent=volumeLevel+"%";
            bool silent=volumeMuted || volumeLevel==0;
            string description=volumeMuted ? "Muted ("+percent+")" : percent;
            UI<TextBlock>("VolumeBadge").Text=volumeMuted ? "Muted" : percent;
            UI<TextBlock>("VolumeReadout").Text=description;
            UI<Path>("VolumeWaves").Visibility=silent ? Visibility.Collapsed : Visibility.Visible;
            UI<Path>("VolumeCross").Visibility=silent ? Visibility.Visible : Visibility.Collapsed;
            var button=UI<Button>("OpenVolume");
            button.ToolTip="Stream volume · "+description;
            AutomationProperties.SetItemStatus(button,description);
            DirectConsole.SetVolume(directCore,EffectiveStreamVolume);
            if(externalStreamAudio!=null) externalStreamAudio.SetPercent(EffectiveStreamVolume);
        }
    }
}
