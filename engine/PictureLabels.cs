internal static class PictureLabels
{
    // Saved dashboard profiles store the index. Append new modes to preserve existing selections.
    internal static readonly string[] Ids={"original","sharp","fsr","rtxvideo","dlss","dlaa","fsr-temporal","dlss-nr","nis","open-nr",
        "open-nr-rtxvideo","open-nr-sharp","open-nr-fsr","open-nr-dlss","open-nr-dlaa","open-nr-fsr-temporal","open-nr-nis"};
    internal static readonly string[] Modes={
        "Original","Sharpen","FSR 1 · Spatial","RTX Video SR","DLSS","DLAA","FSR Temporal","DLSS 5 NR","NIS · Spatial","OpenDLSS-NR",
        "OpenDLSS-NR + RTX Video SR","OpenDLSS-NR + Sharpen","OpenDLSS-NR + FSR 1","OpenDLSS-NR + DLSS","OpenDLSS-NR + DLAA",
        "OpenDLSS-NR + FSR Temporal","OpenDLSS-NR + NIS"
    };

    internal static bool IsNeuralRendering(string mode) { return mode=="dlss-nr" || mode=="open-nr"; }
    internal static bool HasOpenNr(string mode) { return mode=="open-nr" || IsCombined(mode); }
    internal static bool IsCombined(string mode) { return mode!=null && mode.StartsWith("open-nr-",System.StringComparison.Ordinal); }
    internal static string BaseMode(string mode) { return mode=="open-nr" ? "original" : IsCombined(mode) ? mode.Substring(8) : mode; }
    internal static string WithOpenNr(string mode) { return mode=="original" || mode=="dlss-nr" ? "open-nr" : HasOpenNr(mode) ? mode : "open-nr-"+mode; }

    internal static string TemporalFsr(string provider)
    {
        return string.IsNullOrWhiteSpace(provider) ? "FSR Temporal" : "FSR "+provider+" · Temporal";
    }

    internal static bool? FsrUsesAi(string provider)
    {
        System.Version version;
        if(System.Version.TryParse(provider,out version)) {
            if(version.Major==3) return false;
            if(version.Major==4) return true;
        }
        return null;
    }
}
