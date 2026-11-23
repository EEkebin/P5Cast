using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cadrivect
{
    internal static class Program
    {
        internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;

        [StructLayout(LayoutKind.Sequential)]
        struct Reading {
            internal double Left700, Right1100, OffTone;
            internal uint DetectedWindows;
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Launch();
        [DllImport("ShareAudioMeter.dll")] static extern int VerifyOwnChildAudio(Launch launch, out Reading output);

        [StructLayout(LayoutKind.Sequential)]
        struct AudioSpec {
            internal int Frequency;
            internal ushort Format;
            internal byte Channels, Silence;
            internal ushort Samples, Padding;
            internal uint Size;
            internal IntPtr Callback, UserData;
        }
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] static extern void SDL_SetMainReady();
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] static extern int SDL_Init(uint flags);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] static extern uint SDL_OpenAudioDevice(IntPtr name, int capture, ref AudioSpec spec, IntPtr obtained, int changes);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] static extern int SDL_QueueAudio(uint device, short[] samples, uint bytes);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] static extern void SDL_PauseAudioDevice(uint device, int pause);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] static extern uint SDL_GetQueuedAudioSize(uint device);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] static extern void SDL_CloseAudioDevice(uint device);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] static extern void SDL_Quit();
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SDL_GetError();

        static Exception SdlError() { return new Exception(Marshal.PtrToStringAnsi(SDL_GetError())); }
        static void PlayQuietTone()
        {
            SDL_SetMainReady();
            if (SDL_Init(0x10) != 0) throw SdlError();
            uint device = 0;
            try {
                // Match sources/native/Audio.c's default render device and PCM format.
                var spec = new AudioSpec {Frequency=48000, Format=0x8010, Channels=2, Samples=256};
                device = SDL_OpenAudioDevice(IntPtr.Zero, 0, ref spec, IntPtr.Zero, 0);
                if (device == 0) throw SdlError();
                var samples = new short[48000 * 2];
                for (int i = 0; i < samples.Length / 2; ++i) {
                    double t = 2 * Math.PI * i / 48000;
                    double amplitude = 32 * Math.Min(1, Math.Min(i, 47999-i) / 480.0);
                    samples[2*i] = (short)Math.Round(amplitude * Math.Sin(700 * t));
                    samples[2*i+1] = (short)Math.Round(amplitude * Math.Sin(1100 * t));
                }
                if (SDL_QueueAudio(device, samples, (uint)samples.Length * 2) != 0) throw SdlError();
                SDL_PauseAudioDevice(device, 0);
                var watch = Stopwatch.StartNew();
                while (SDL_GetQueuedAudioSize(device) != 0) {
                    if (watch.ElapsedMilliseconds > 3500) throw new Exception("SDL output did not drain.");
                    Thread.Sleep(10);
                }
            } finally {
                if (device != 0) SDL_CloseAudioDevice(device);
                SDL_Quit();
            }
        }

        [MTAThread]
        static int Main(string[] args)
        {
            try {
                if (args.Length == 1 && args[0] == "share-audio-fixture") {
                    PlayQuietTone();
                    return 0;
                }
                if (args.Length != 0) return 2;
                // Exercise the real dashboard launcher with an offline child substituted
                // for the network core. Only this child opens an audio render device.
                var begin = typeof(DirectConsole).GetMethod("Begin", BindingFlags.Static | BindingFlags.NonPublic);
                if (begin == null) throw new Exception("Direct launcher was renamed.");
                Process child = null;
                Exception launchError = null;
                try {
                    Reading reading;
                    int hr = VerifyOwnChildAudio(delegate {
                        try { child = (Process)begin.Invoke(null, new object[] {"share-audio-fixture", false}); }
                        catch (Exception error) { launchError = error; }
                    }, out reading);
                    Marshal.ThrowExceptionForHR(hr);
                    if (launchError != null) throw launchError;
                    if (child == null || !child.WaitForExit(4000) || child.ExitCode != 0)
                        throw new Exception("The SDL child did not finish normally.");
                    Console.WriteLine("Parent-scoped WASAPI: 700 Hz left {0:F2}, 1100 Hz right {1:F2}, 1700 Hz control {2:F2}, matched 100 ms windows {3}.",
                        reading.Left700, reading.Right1100, reading.OffTone, reading.DetectedWindows);
                    if (reading.DetectedWindows < 4) throw new Exception("The shared process tree did not receive the child tones.");
                } finally {
                    if (child != null) {
                        if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); }
                        child.Dispose();
                    }
                }
                return 0;
            } catch (Exception e) {
                Console.Error.WriteLine(e);
                return 1;
            }
        }
    }
}
