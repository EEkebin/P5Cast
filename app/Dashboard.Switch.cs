using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        [Flags] enum SwitchControl {Picture=1,Output=2,Model=4,Quality=8,View=16}

        Dictionary<string,object> lastPlaybackSettings;
        SwitchControl switchedControls;
        long switchOperation;
        int switchRevision;
        bool restoringSwitch,switchCancelled;
        readonly Stopwatch switchElapsed=new Stopwatch();

        void TrackSwitch(SwitchControl control,string title)
        {
            if(!firstFrame || lastPlaybackSettings==null || stopping || closing || restoringSwitch || switchCancelled) return;
            int revision=engine.MarkSettings();
            if(revision==0) return;
            switchedControls|=control;
            switchRevision=revision;
            switchElapsed.Restart();
            if(switchOperation==0)
                switchOperation=BeginOperation(title,"Waiting for the player to show the change…",CancelPlaybackSwitch);
        }

        void TrackPresentedSwitch(Dictionary<string,object> snapshot)
        {
            if(switchOperation!=0) {
                if(PlaybackMeter.Number(snapshot,"settingsRevision")<switchRevision) {
                    if(switchElapsed.Elapsed.TotalSeconds<20) return;
                    EndSwitchTracking();
                    Show("The player has not confirmed the change. Check the active settings when video resumes.",true);
                    return;
                }
                string error=Field(snapshot,"settingsError");
                if(error.Length>0) {
                    // These are the settings the renderer actually accepted.
                    RestoreSwitchSettings(snapshot,false);
                    Show(error,true);
                } else {
                    string notice=Field(snapshot,"effectNotice");
                    if(notice.Length>0 && notice!=Field(lastPlaybackSettings,"effectNotice")) Show(notice,true);
                    else if(switchCancelled) Show("Previous playback settings restored.");
                }
                EndSwitchTracking();
            }
            lastPlaybackSettings=snapshot;
        }

        void CancelPlaybackSwitch()
        {
            if(switchOperation==0 || switchCancelled) return;
            switchCancelled=true;
            CommitOperation(switchOperation,"Restoring the previous playback settings…");
            RestoreSwitchSettings(lastPlaybackSettings,true);
            switchRevision=engine.MarkSettings();
            switchElapsed.Restart();
            if(switchRevision==0) EndSwitchTracking();
        }

        void RestoreSwitchSettings(Dictionary<string,object> snapshot,bool send)
        {
            if(snapshot==null) return;
            restoringSwitch=true;
            try {
                if((switchedControls&SwitchControl.Output)!=0) {
                    string color=Field(snapshot,"hdrMode"),size=Field(snapshot,"resolution");
                    preferredHdr=Array.IndexOf(hdrIds,color);
                    UI<ComboBox>("HdrOutput").SelectedIndex=preferredHdr;
                    UI<ComboBox>("RenderResolution").SelectedIndex=Array.IndexOf(resolutionIds,size);
                    if(send) engine.ConfigureOutput(size,color);
                }
                if((switchedControls&SwitchControl.Model)!=0) {
                    meter.DlssModelHint=Field(snapshot,"dlssModelHint");
                    if(send) engine.SetDlssModel(meter.DlssModelHint);
                }
                if((switchedControls&SwitchControl.Quality)!=0) {
                    meter.DlssQuality=Field(snapshot,"dlssQuality");
                    if(send) engine.SetDlssQuality(meter.DlssQuality);
                }
                if((switchedControls&(SwitchControl.Picture|SwitchControl.View|SwitchControl.Output))!=0) {
                    mode=Array.IndexOf(modeIds,Field(snapshot,"mode"));
                    factor=(int)PlaybackMeter.Number(snapshot,"multiplier");
                    string provider=Field(snapshot,"frameGenerationProvider"),view=Field(snapshot,"comparisonView");
                    meter.RestorePlaybackControls(mode,factor,provider,view);
                    if(send && (switchedControls&(SwitchControl.Output|SwitchControl.Picture))!=0) engine.Configure(mode,factor,provider);
                    if(send && (switchedControls&SwitchControl.View)!=0) engine.SetComparison(view);
                    UpdateWipeInput();
                }
                if(window.IsLoaded) SaveSettings();
            } finally {restoringSwitch=false;}
        }

        void EndSwitchTracking()
        {
            if(switchOperation!=0) EndOperation(switchOperation);
            switchOperation=0; switchRevision=0; switchedControls=0;
            switchCancelled=false; switchElapsed.Reset(); lastPlaybackSettings=null;
        }

        async Task ChangeRenderer(ComboBox picker,SelectionChangedEventArgs change)
        {
            var old=change.RemovedItems.Count==0 ? null : change.RemovedItems[0];
            int previousMode=mode,previousFactor=factor,previousHdr=preferredHdr;
            string previousProvider=meter.FrameGenerationProvider;
            var previousGpu=meter.Capabilities;
            RefreshRendererOutput();
            if(!window.IsLoaded || Active || preparing || closing) return;
            if(mode>=3) {mode=0; meter.Reset(mode,factor);}
            meter.SetCapabilities(null);
            if(old==null || SetupOpen) {await ReadGpu(previousMode);return;}

            using(var cancel=new CancellationTokenSource()) {
                string target=Convert.ToString(picker.SelectedItem);
                long id=BeginOperation("Switching graphics API","Checking GPU support for "+target+"…",cancel.Cancel);
                try {
                    bool complete=await ReadGpu(previousMode,cancel.Token);
                    if(closing || !Equals(picker.SelectedItem,target)) return;
                    if(complete && !cancel.IsCancellationRequested) {SaveSettings();return;}
                    restoringSwitch=true;
                    try {
                        picker.SelectedItem=old;
                        preferredHdr=previousHdr; RefreshRendererOutput();
                        mode=previousMode; factor=previousFactor; meter.FrameGenerationProvider=previousProvider;
                        meter.SetCapabilities(previousGpu); meter.Reset(mode,factor);
                    } finally {restoringSwitch=false;}
                    if(previousGpu==null) {
                        CommitOperation(id,"Checking GPU support for "+old+"…");
                        await ReadGpu(previousMode);
                    }
                    SaveSettings();
                    Show(cancel.IsCancellationRequested ? "Graphics API switch cancelled." : "GPU check failed. Restored "+old+".",!cancel.IsCancellationRequested);
                } finally {EndOperation(id);}
            }
        }
    }
}
