internal static class PictureLabels
{
    // Saved dashboard profiles store the index. Append new modes to preserve existing selections.
    internal static readonly string[] Ids={"original","sharp","fsr","rtxvideo","dlss","dlaa","fsr-temporal","dlss-nr","nis","open-nr"};
    internal static readonly string[] Modes={
        "Original","Sharpen","FSR 1 · Spatial","RTX Video SR","DLSS","DLAA","FSR Temporal","DLSS 5 NR","NIS · Spatial","OpenDLSS-NR"
    };

    internal static bool IsNeuralRendering(string mode) { return mode=="dlss-nr" || mode=="open-nr"; }

    internal static string TemporalFsr(string provider)
    {
        return string.IsNullOrWhiteSpace(provider) ? "FSR Temporal" : "FSR "+provider+" · Temporal";
    }
}
