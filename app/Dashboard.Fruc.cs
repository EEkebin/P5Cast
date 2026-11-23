using System;
using Microsoft.Win32;

namespace Cadrivect
{
    internal partial class Dashboard
    {
        bool importingFruc;

        async void ImportFrucRuntime()
        {
            if(importingFruc) return;
            var picker=new OpenFileDialog {
                Title="Import NVIDIA FRUC from the Optical Flow SDK",
                Filter="NVIDIA SDK ZIP or FRUC runtime (*.zip;*.dll)|*.zip;*.dll",
                CheckFileExists=true
            };
            if(picker.ShowDialog(window)!=true) return;
            importingFruc=true;
            try {
                Show("Checking the FRUC runtime and installing its CUDA dependency…");
                await Script("sources/Install-Fruc.ps1","-RuntimePath",picker.FileName);
                if(closing) return;
                await ReadGpu(mode);
                if(!closing) Show("FRUC runtime imported. Choose SDR with Direct3D 11 or 12 to use NVIDIA FRUC 2× on a supported GPU.");
            } catch(Exception error) {
                if(!closing) Show("FRUC setup failed. "+error.Message,true);
            } finally {importingFruc=false;}
        }
    }
}
