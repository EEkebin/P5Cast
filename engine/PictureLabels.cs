internal static class PictureLabels
{
    internal static readonly string[] Modes={
        "Original","Sharpen","FSR 1 · Spatial","RTX Video SR","DLSS","DLAA","FSR Temporal","DLSS 5 NR"
    };

    internal static string TemporalFsr(string provider)
    {
        return string.IsNullOrWhiteSpace(provider) ? "FSR Temporal" : "FSR "+provider+" · Temporal";
    }
}
