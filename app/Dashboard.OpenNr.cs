using System;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        string openNrModelDirectory;
        bool checkingOpenNrModel;

        void RestoreOpenNrModelDirectory(string directory)
        {
            if(string.IsNullOrWhiteSpace(directory)) return;
            try {
                if(!Path.IsPathRooted(directory)) return;
                string path=Path.GetFullPath(directory);
                Environment.SetEnvironmentVariable("P5CAST_OPEN_NR_MODEL",path);
                openNrModelDirectory=path;
            } catch(ArgumentException) {} catch(NotSupportedException) {} catch(PathTooLongException) {}
        }

        async void SetUpOpenNrModel(bool download)
        {
            if(checkingOpenNrModel) return;
            checkingOpenNrModel=true; meter.SetOpenNrChecking(true);
            try {
                string candidate;
                if(download) {
                    Show("Downloading the OpenDLSS-NR model from the NVIDIA package…");
                    var installed=await Script("sources/Install-OpenNrModel.ps1");
                    if(closing) return;
                    candidate=Field(installed,"modelDirectory");
                    if(string.IsNullOrEmpty(candidate)) throw new InvalidDataException("The installer did not return a model folder.");
                } else {
                    var picker=new OpenFileDialog {
                        Title="Choose the OpenDLSS-NR model manifest",
                        Filter="OpenDLSS-NR model manifest (manifest.json)|manifest.json",
                        CheckFileExists=true
                    };
                    if(Directory.Exists(openNrModelDirectory)) picker.InitialDirectory=openNrModelDirectory;
                    if(picker.ShowDialog(window)!=true) return;
                    if(!string.Equals(Path.GetFileName(picker.FileName),"manifest.json",StringComparison.OrdinalIgnoreCase)) {
                        Show("Choose manifest.json from the OpenDLSS-NR 310.8.0 model folder.",true); return;
                    }
                    candidate=Path.GetDirectoryName(picker.FileName);
                }
                string renderer=SelectedRenderer;
                var gpu=await ProbeGpuReport(renderer=="opengl" ? "vulkan" : renderer,candidate,CancellationToken.None);
                if(closing) return;
                if(gpu==null || !Field(gpu,"open-nr").Equals("true",StringComparison.OrdinalIgnoreCase)) {
                    string reason=gpu==null ? "" : Field(gpu,"open-nrReason");
                    Show(string.IsNullOrEmpty(reason) ? "The GPU could not verify the selected OpenDLSS-NR model." : reason,true);
                    return;
                }
                Environment.SetEnvironmentVariable("P5CAST_OPEN_NR_MODEL",candidate);
                openNrModelDirectory=candidate;
                SaveSettings();
                if(renderer==SelectedRenderer && renderer!="opengl") {
                    ++gpuProbeGeneration; meter.SetCapabilities(gpu);
                } else await ReadGpu(mode);
                if(closing) return;
                if(rendering!=null && PictureLabels.HasOpenNr(modeIds[mode])) {
                    engine.Configure(mode,factor,meter.FrameGenerationProvider);
                    TrackSwitch(SwitchControl.Picture);
                }
                Show("OpenDLSS-NR model verified.");
            } catch(Exception error) {
                if(!closing) Show("OpenDLSS-NR model check failed. "+error.GetBaseException().Message,true);
            } finally {
                checkingOpenNrModel=false; meter.SetOpenNrChecking(false);
            }
        }
    }
}
