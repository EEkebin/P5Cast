using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal sealed class RtxVideo : IDisposable
{
    const string Bridge = "Cadrivect.RtxVideo.dll";
    const string RuntimeHash = "C3D88EEA5FF7A548EDEFA66414CF6E77464D0947277C904F324DD23ABF58A1ED";
    IntPtr session;

    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint VideoCreate(IntPtr device, string cache, string featurePath, out IntPtr session);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl)]
    static extern uint VideoEvaluate(IntPtr session, IntPtr ctx, IntPtr input, IntPtr output,
        int srcWidth, int srcHeight, int dstWidth, int dstHeight, int quality);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport(Bridge, CallingConvention = CallingConvention.Cdecl)]
    static extern void VideoDestroy(IntPtr session);

    internal RtxVideo(IntPtr device)
    {
        string featureDirectory = Path.GetDirectoryName(typeof(RtxVideo).Assembly.Location);
        string runtime = Path.Combine(featureDirectory, "nvngx_vsr.dll");
        using (var hasher = SHA256.Create())
        using (var stream = File.OpenRead(runtime))
            if (BitConverter.ToString(hasher.ComputeHash(stream)).Replace("-", "") != RuntimeHash)
                throw new InvalidDataException("The packaged NVIDIA RTX Video runtime failed its SHA-256 check.");
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cadrivect", "RTXVideo");
        Directory.CreateDirectory(cache);
        Check(VideoCreate(device, cache, featureDirectory, out session), "initialization");
    }

    internal void Evaluate(IntPtr context, IntPtr input, IntPtr output, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        EvaluateQuality(context, input, output, srcWidth, srcHeight, dstWidth, dstHeight, 3);
    }

    // Quality 3 is the production model. The GPU smoke test compares it against the SDK's quality 0 (bicubic).
    internal void EvaluateQuality(IntPtr context, IntPtr input, IntPtr output,
        int srcWidth, int srcHeight, int dstWidth, int dstHeight, int quality)
    {
        if (session == IntPtr.Zero) throw new ObjectDisposedException("RtxVideo");
        Check(VideoEvaluate(session, context, input, output, srcWidth, srcHeight, dstWidth, dstHeight, quality), "evaluation");
    }
    static void Check(uint value, string operation)
    {
        if (value != 1)
            throw new InvalidOperationException("NVIDIA RTX Video " + operation + " returned NGX 0x" + value.ToString("X8") + ".");
    }
    public void Dispose()
    {
        if (session != IntPtr.Zero) { VideoDestroy(session); session = IntPtr.Zero; }
    }
}
