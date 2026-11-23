using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

internal static partial class WindowChromeProbe
{
    static async Task CheckVolumeControls()
    {
        var popup=Find<Popup>("VolumePopup");
        var slider=Find<Slider>("StreamVolumeSlider");
        var mute=Find<CheckBox>("MuteStream");
        var badge=Find<TextBlock>("VolumeBadge");
        Require(slider.Value==100 && mute.IsChecked==false,"A legacy profile did not default to full volume.");
        Click("OpenVolume");
        await Until(()=>popup.IsOpen && slider.IsVisible && slider.ActualWidth>0,"Volume slider did not open.");
        slider.Value=47;
        Require(badge.Text=="47%","Volume percent did not follow the slider.");
        mute.IsChecked=true;
        Require(badge.Text=="Muted" && slider.Value==47,"Mute lost the remembered level.");
        slider.Value=46;
        Require(badge.Text=="46%" && mute.IsChecked==false,"Changing the slider did not restore sound.");
        slider.Value=0;
        Require(Find<System.Windows.Shapes.Path>("VolumeCross").IsVisible,"Silent volume did not update the speaker.");
        slider.Value=37;
        mute.IsChecked=true;
        slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(slider),Environment.TickCount,Key.Escape) {
            RoutedEvent=Keyboard.PreviewKeyDownEvent
        });
        await Until(()=>!popup.IsOpen,"Escape did not dismiss the volume flyout.");
        Note("Stream volume supports the 0–100% range, mute, unmute by adjustment, and Escape.");
    }

    static void CheckSavedVolume()
    {
        var slider=Find<Slider>("StreamVolumeSlider");
        var mute=Find<CheckBox>("MuteStream");
        Require(slider.Value==37 && mute.IsChecked==true && Find<TextBlock>("VolumeBadge").Text=="Muted",
                "The saved stream level and mute did not survive a process restart.");
        mute.IsChecked=false;
        Require(Find<TextBlock>("VolumeBadge").Text=="37%","Unmuting did not restore the saved level.");
        Note("After a clean launch the stream was still muted and unmuting restored 37%.");
    }
}
