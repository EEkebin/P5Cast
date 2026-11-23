using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]

namespace Cadrivect.Engine
{
    /// <summary>Renders on a caller-owned child HWND. Run on an STA worker while the owning UI dispatcher pumps messages.</summary>
    public static class EmbeddedPlayback
    {
        static readonly object Gate=new object();
        static HostedSurface current;
        static string snapshot="{}",error="";
        public static bool Running { get { lock(Gate) return current!=null; } }
        public static string Error { get { lock(Gate) return error; } }
        public static string Snapshot() { lock(Gate) return snapshot; }
        internal static void Publish(string value) { lock(Gate) snapshot=value; }
        internal static void Failed(string message) { lock(Gate) error=message; }
        public static int Run(string[] args)
        {
            HostedSurface session;
            lock(Gate) {
                if(current!=null) return 1;
                error=""; snapshot="{}";
                try {
                    if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)
                        throw new InvalidOperationException("Start embedded playback on a dedicated STA worker.");
                    int index=Array.IndexOf(args,"--surface");
                    if(index<0 || index+1>=args.Length) throw new ArgumentException("A child window is required for --surface.");
                    if(Array.IndexOf(args,"--self-test")>=0 || Array.IndexOf(args,"--capabilities")>=0)
                        throw new ArgumentException("Use Direct video or window capture when rendering into a dashboard surface.");
                    session=new HostedSurface(new IntPtr(long.Parse(args[index+1],CultureInfo.InvariantCulture)));
                    int view=Array.IndexOf(args,"--comparison");
                    if(view>=0) session.ComparisonView=ValidateComparison(view+1<args.Length ? args[view+1] : null);
                    session.WipeHorizontal=Array.IndexOf(args,"--wipe-horizontal")>=0;
                    int split=Array.IndexOf(args,"--wipe-position");
                    if(split>=0) session.WipePosition=ParseWipePosition(split+1<args.Length ? args[split+1] : null);
                    current=session;
                } catch(Exception ex) { error=ex.Message; return 1; }
            }
            try { return global::Player.Run(args,session); }
            finally { lock(Gate) current=null; }
        }
        public static void Configure(int modeIndex,int multiplier)
        {
            if(modeIndex< -1 || modeIndex>=global::PlaybackControls.Modes.Length) throw new ArgumentOutOfRangeException("modeIndex");
            if(multiplier!= -1 && (multiplier<1 || multiplier>4)) throw new ArgumentOutOfRangeException("multiplier");
            lock(Gate) if(current!=null) current.Configure(modeIndex,multiplier);
        }
        public static void ConfigureOutput(string resolution,string hdr)
        {
            if(resolution!=null) { resolution=resolution.ToLowerInvariant(); global::DisplayTarget.ResolutionLimit(resolution); }
            if(hdr!=null) {
                hdr=hdr.ToLowerInvariant();
                if(hdr!="auto" && hdr!="off" && hdr!="hdr10") throw new ArgumentException("Choose auto, off, or hdr10.","hdr");
            }
            lock(Gate) if(current!=null) current.SetOutput(resolution,hdr);
        }
        internal static string ValidateComparison(string view)
        {
            if(view!="enhanced" && view!="original" && view!="side-by-side" && view!="wipe")
                throw new ArgumentException("Choose enhanced, original, side-by-side, or wipe.","view");
            return view;
        }
        public static void SetComparison(string view)
        {
            ValidateComparison(view);
            lock(Gate) if(current!=null) current.ComparisonView=view;
        }
        internal static float ParseWipePosition(string value)
        {
            float position;
            if(!float.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out position))
                throw new ArgumentException("Expected a divider position between 0 and 1.");
            return ValidatePosition(position);
        }
        static float ValidatePosition(float position)
        {
            if(!(position>=0 && position<=1)) throw new ArgumentOutOfRangeException("position");
            return position;
        }
        public static void SetWipeHorizontal(bool horizontal)
        {
            lock(Gate) if(current!=null) current.WipeHorizontal=horizontal;
        }
        public static void SetWipePosition(float position)
        {
            ValidatePosition(position);
            lock(Gate) if(current!=null) current.WipePosition=position;
        }
        public static void Compare(bool enabled) { SetComparison(enabled ? "original" : "enhanced"); }
        public static void Stop() { lock(Gate) if(current!=null) current.Stopped=true; }
        public static void InvalidateDisplay() { lock(Gate) if(current!=null) current.Invalidate(); }
    }

    internal sealed class HostedSurface
    {
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int L,T,R,B; }
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint id);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetStyle(IntPtr hwnd,int index);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
        readonly object gate=new object();
        int mode=-1,factor=-1,displayChanged;
        string resolution,hdr;
        public readonly IntPtr Handle;
        public volatile bool Stopped;
        public volatile string ComparisonView="enhanced";
        public volatile float WipePosition=0.5f;
        public volatile bool WipeHorizontal;
        public HostedSurface(IntPtr hwnd) {
            uint pid;
            using(var process=Process.GetCurrentProcess())
                if(hwnd==IntPtr.Zero || GetWindowThreadProcessId(hwnd,out pid)==0 || pid!=(uint)process.Id || (GetStyle(hwnd,-16)&0x40000000)==0)
                    throw new ArgumentException("--surface must be an existing child HWND in the dashboard process.");
            Handle=hwnd;
        }
        public bool Exists { get { return !Stopped && global::WindowsApi.IsWindow(Handle); } }
        public bool Hidden { get { return !IsWindowVisible(Handle) || global::WindowsApi.IsIconic(GetAncestor(Handle,2)); } }
        public Size ClientSize {
            get { Rect r; return GetClientRect(Handle,out r) ? new Size(Math.Max(1,r.R-r.L),Math.Max(1,r.B-r.T)) : new Size(1,1); }
        }
        public void Configure(int nextMode,int nextFactor) {
            lock(gate) { if(nextMode>=0) mode=nextMode; if(nextFactor>0) factor=nextFactor; }
        }
        public void TakeChanges(Action<string,int> apply) {
            lock(gate) {
                if(mode== -1 && factor== -1) return;
                apply(mode<0 ? null : global::PlaybackControls.Modes[mode],factor); mode=factor=-1;
            }
        }
        public void SetOutput(string size,string color) {
            lock(gate) { if(size!=null) resolution=size; if(color!=null) hdr=color; }
        }
        public bool TakeOutput(out string size,out string color) {
            lock(gate) { size=resolution; color=hdr; resolution=hdr=null; return size!=null || color!=null; }
        }
        public void Invalidate() { Interlocked.Exchange(ref displayChanged,1); }
        public bool ConsumeDisplayChange() { return Interlocked.Exchange(ref displayChanged,0)!=0; }
    }
}
