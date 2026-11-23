using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

// Offscreen GPU test. Uses one deterministic frame, unchanged between the two NVIDIA evaluations.
static class VerifyRtxVideo
{
    const int W = 960, H = 540, OX = W * 2, OY = H * 2;
    static Texture2DDescription Description(int width, int height, BindFlags flags, Format format = Format.R8G8B8A8_UNorm)
    {
        return new Texture2DDescription {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1,
            Format = format, SampleDescription = new SampleDescription(1, 0),
            BindFlags = flags, Usage = ResourceUsage.Default
        };
    }
    static byte[] Readback(Device device, Texture2D output)
    {
        var d = Description(OX, OY, BindFlags.None);
        d.Usage = ResourceUsage.Staging;
        d.CpuAccessFlags = CpuAccessFlags.Read;
        var pixels = new byte[OX * OY * 4];
        using (var staging = new Texture2D(device, d)) {
            var ctx = device.ImmediateContext;
            ctx.CopyResource(output, staging);
            var map = ctx.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try {
                for (int y = 0; y < OY; ++y)
                    Marshal.Copy(map.DataPointer + y * map.RowPitch, pixels, y * OX * 4, OX * 4);
            } finally { ctx.UnmapSubresource(staging, 0); }
        }
        return pixels;
    }
    static int Main(string[] args)
    {
        try {
            var source = new int[W * H];
            for (int y = 0; y < H; ++y)
                for (int x = 0; x < W; ++x) {
                    int sample = ((x + y * 3) / 4 % 8 + ((x / 23 + y / 23) & 1) * 8) * 16;
                    source[y * W + x] = sample | (((x / 8) & 255) << 8) | (((y * 3) & 255) << 16) | unchecked((int)0xff000000);
                }
            var pin = GCHandle.Alloc(source, GCHandleType.Pinned);
            try {
                using (var device = args.Length>0 && args[0]=="d3d12" ? SelectedEffectDevice.CreateD3D12() : new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport))
                using (var input = new Texture2D(device, Description(W, H, BindFlags.ShaderResource, Format.B8G8R8A8_UNorm),
                    new DataRectangle(pin.AddrOfPinnedObject(), W * 4)))
                using (var output = new Texture2D(device, Description(OX, OY, BindFlags.ShaderResource | BindFlags.UnorderedAccess | BindFlags.RenderTarget)))
                using (var video = new RtxVideo(device.NativePointer)) {
                    video.EvaluateQuality(device.ImmediateContext.NativePointer, input.NativePointer, output.NativePointer, W, H, OX, OY, 0);
                    var baseline = Readback(device, output);
                    video.Evaluate(device.ImmediateContext.NativePointer, input.NativePointer, output.NativePointer, W, H, OX, OY);
                    var model = Readback(device, output);
                    int changed = 0, min = 255, max = 0;
                    long magnitude = 0;
                    for (int i = 0; i < model.Length; i += 4) {
                        int difference = Math.Abs(model[i] - baseline[i]) + Math.Abs(model[i+1] - baseline[i+1]) + Math.Abs(model[i+2] - baseline[i+2]);
                        if (difference > 0) ++changed;
                        magnitude += difference;
                        min = Math.Min(min, model[i]);
                        max = Math.Max(max, model[i]);
                    }
                    Console.WriteLine("RTX Video High vs NVIDIA bicubic: {0}/{1} differing pixels, RGB MAE {2:F3}, channel range {3}..{4}.",
                        changed, OX*OY, magnitude / (double)(OX*OY*3), min, max);
                    return changed > OX * OY / 20 && max - min > 80 ? 0 : 1;
                }
            } finally { pin.Free(); }
        } catch (Exception e) {
            Console.Error.WriteLine(e.ToString());
            return 1;
        }
    }
}
