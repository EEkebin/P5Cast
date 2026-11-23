using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace Cadrivect
{
    // Owns only the Moonlight process launched for this panel. The pinned Qt
    // client uses one QML window for pairing/status and another SDL HWND for video.
    internal sealed class NetworkPlayer : IDisposable
    {
        readonly IntPtr host;
        readonly Process process;
        readonly DispatcherTimer timer;
        readonly WinEventProc events;
        readonly Action<string> status;
        readonly Action<int> ended;
        readonly bool muted;
        readonly Stopwatch age = Stopwatch.StartNew();
        IntPtr hook, panel, stream;
        SafeFileHandle job;
        bool disposed;
        long nextScan;

        public static NetworkPlayer Start(IntPtr host, IDictionary<string,object> plan, Action<string> status, Action<int> ended)
        {
            if (!IsWindow(host)) throw new InvalidOperationException("The video area is not ready.");
            var tokens = ((IEnumerable)plan["arguments"]).Cast<object>().Select(Convert.ToString).ToArray();
            if (tokens.Length < 2 || (tokens[0] != "stream" && tokens[0] != "pair") ||
                tokens.Any(s => string.IsNullOrEmpty(s) || s.Any(c => c == '"' || char.IsWhiteSpace(c) || char.IsControl(c))))
                throw new InvalidDataException("Invalid network client launch settings.");
            string file = Convert.ToString(plan["executable"]);
            var start = new ProcessStartInfo(file, string.Join(" ", tokens)) {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file),
                WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true
            };
            bool mute = Convert.ToBoolean(plan["muted"]);
            if (mute) {
                // PreparePlay first verifies the pinned executable and both SDL DLLs.
                start.EnvironmentVariables["ML_AUDIO"] = "sdl";
                start.EnvironmentVariables["SDL_AUDIODRIVER"] = "dummy";
                start.EnvironmentVariables["SDL_AUDIO_DRIVER"] = "dummy";
            }
            int dpi = GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(host));
            if (dpi >= 0 && dpi <= 2) {
                start.EnvironmentVariables["QT_QPA_PLATFORM"] = "windows:dpiawareness=" + dpi;
                start.EnvironmentVariables["SDL_WINDOWS_DPI_AWARENESS"] = new string[]{"unaware","system","permonitorv2"}[dpi];
            }
            start.EnvironmentVariables["SDL_WINDOW_NO_ACTIVATION_WHEN_SHOWN"] = "1";
            var native = LaunchSuspended(start);
            Process child = null;
            NetworkPlayer session = null;
            try {
                child = Process.GetProcessById((int)native.id);
                session = new NetworkPlayer(host, child, mute, status, ended, Convert.ToString(plan["message"]));
                if (ResumeThread(native.thread)==uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                return session;
            } catch {
                if (session!=null) session.Dispose();
                TerminateProcess(native.process,1);
                if (session==null && child!=null) child.Dispose();
                throw;
            } finally {CloseHandle(native.thread); CloseHandle(native.process);}
        }

        static ChildInfo LaunchSuspended(ProcessStartInfo start)
        {
            // .NET Framework ignores WindowStyle with UseShellExecute=false. Set STARTUPINFO
            // directly and install the process-specific window hook before the first GUI instruction.
            var sorted=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(string key in start.EnvironmentVariables.Keys) sorted[key]=start.EnvironmentVariables[key];
            string block=string.Join("\0", sorted.Select(v=>v.Key+"="+v.Value))+"\0\0";
            IntPtr environment=Marshal.StringToHGlobalUni(block);
            var startup=new StartupInfo(); startup.size=Marshal.SizeOf(typeof(StartupInfo));
            startup.flags=1; startup.show=0; // STARTF_USESHOWWINDOW, SW_HIDE
            ChildInfo child;
            try {
                var args=new StringBuilder("\""+start.FileName+"\" "+start.Arguments);
                if (!CreateProcess(start.FileName,args,IntPtr.Zero,IntPtr.Zero,false,0x08000404u,environment,start.WorkingDirectory,ref startup,out child))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return child;
            } finally {Marshal.FreeHGlobal(environment);}
        }

        NetworkPlayer(IntPtr host, Process child, bool muted, Action<string> status, Action<int> ended, string message)
        {
            this.host = host; this.process = child; this.muted = muted;
            this.status = status; this.ended = ended;
            events = WindowEvent;
            timer = new DispatcherTimer(DispatcherPriority.Normal);
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += Tick;
            try {
                job = CreateLifetimeJob(child);
                // Out-of-context callbacks run on this dispatcher; no code is loaded into the client.
                hook = SetWinEventHook(0x8000,0x8002,IntPtr.Zero,events,(uint)child.Id,0,0);
                if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                timer.Start();
                if (status != null) status(message);
            } catch {
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                timer.Stop();
                if (job != null) job.Dispose();
                throw;
            }
        }

        void WindowEvent(IntPtr handle, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            if (disposed || window == IntPtr.Zero || objectId != 0 || childId != 0) return;
            if (eventType == 0x8001) {
                if (stream == window) stream = IntPtr.Zero;
                if (panel == window) panel = IntPtr.Zero;
                return;
            }
            Inspect(window);
        }

        void Inspect(IntPtr window)
        {
            if (disposed || window == panel || window == stream) return;
            uint owner;
            GetWindowThreadProcessId(window,out owner);
            if (owner != process.Id || GetParent(window) != IntPtr.Zero) return;
            var className = new StringBuilder(160);
            if (GetClassName(window,className,className.Capacity) == 0) return;
            string cls = className.ToString();
            var title = new StringBuilder(256);
            GetWindowText(window,title,title.Capacity);
            bool sdl = cls.StartsWith("SDL",StringComparison.OrdinalIgnoreCase) &&
                title.ToString().EndsWith(" - Moonlight",StringComparison.Ordinal);
            long style = GetWindowLongPtr(window,-16).ToInt64();
            bool qt = cls.StartsWith("Qt",StringComparison.Ordinal) && cls.IndexOf("QWindow",StringComparison.Ordinal) >= 0 &&
                (style & 0x00c00000L) != 0;
            if (!sdl && !qt) return;
            if (qt && IsWindow(panel)) return;
            // Win32's cross-thread calls can pump messages, including another hook callback.
            if (sdl) stream=window; else panel=window;
            try {
                Attach(window);
                if (sdl) {
                    if (status != null) status(muted ? "Network video is playing here. Client audio is muted on this PC." : "Network video is playing here.");
                }
            } catch (Win32Exception ex) {
                if(sdl) stream=IntPtr.Zero; else panel=IntPtr.Zero;
                if (IsWindow(window)) Fail("Could not place the network client in the video area. " + ex.Message);
            }
        }

        void Attach(IntPtr window)
        {
            ShowWindowAsync(window,0);
            long style = GetWindowLongPtr(window,-16).ToInt64();
            // Clear popup, caption, sizing frame and system buttons before making the HWND a child.
            SetStyle(window,-16,(style & ~0xa1cf0000L) | 0x40000000L);
            SetStyle(window,-20,GetWindowLongPtr(window,-20).ToInt64() & ~0x00040188L);
            SetLastError(0);
            if (SetParent(window,host) == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Fit(window,true);
            ShowWindowAsync(window,4); // SW_SHOWNOACTIVATE preserves the dashboard's focus.
        }

        static void SetStyle(IntPtr window, int index, long value)
        {
            SetLastError(0);
            if (SetWindowLongPtr(window,index,new IntPtr(value)) == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        void Fit(IntPtr window, bool first)
        {
            Rect area,current;
            if (!GetClientRect(host,out area) || area.Right <= 0 || area.Bottom <= 0 || !GetWindowRect(window,out current)) return;
            MapWindowPoints(IntPtr.Zero,host,ref current,2);
            if (!first && current.Left == 0 && current.Top == 0 && current.Right == area.Right && current.Bottom == area.Bottom) return;
            // SWP_NOACTIVATE | ASYNCWINDOWPOS. The game surface is above the connection panel.
            uint flags = first ? 0x4030u : 0x4014u;
            SetWindowPos(window,IntPtr.Zero,0,0,area.Right,area.Bottom,flags);
        }

        void Tick(object sender, EventArgs args)
        {
            if (disposed) return;
            if (process.HasExited) { int code=process.ExitCode; Finish(false); if (ended != null) ended(code); return; }
            if (!IsWindow(host)) { Fail("The network video area closed."); return; }
            if (age.ElapsedMilliseconds>=nextScan) {
                EnumWindows(delegate(IntPtr window,IntPtr data) { Inspect(window); return !disposed; },IntPtr.Zero);
                nextScan=age.ElapsedMilliseconds+(stream!=IntPtr.Zero || panel!=IntPtr.Zero ? 1000 : 100);
            }
            foreach (IntPtr window in new IntPtr[]{panel,stream}) {
                if (disposed || !IsWindow(window)) continue;
                if (GetParent(window) == host && (GetWindowLongPtr(window,-16).ToInt64() & 0x40000000L)!=0) Fit(window,false);
                else {
                    try { Attach(window); }
                    catch (Win32Exception ex) { Fail("The network window could not stay in the video area. " + ex.Message); return; }
                }
            }
            if (!disposed && panel == IntPtr.Zero && stream == IntPtr.Zero && age.Elapsed.TotalSeconds > 40)
                Fail("The network client did not open its connection panel. Check the host address and try again.");
        }

        void Fail(string message)
        {
            if (disposed) return;
            Finish(true);
            if (status != null) status(message);
            if (ended != null) ended(-1);
        }

        public void Dispose() { Finish(true); }
        void Finish(bool stop)
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook=IntPtr.Zero; }
            if (stop) {
                IntPtr active = IsWindow(stream) ? stream : panel;
                if (IsWindow(active)) { ShowWindowAsync(active,0); PostMessage(active,0x0010,IntPtr.Zero,IntPtr.Zero); }
                Task.Run(delegate {
                    try { process.WaitForExit(1800); }
                    finally { job.Dispose(); process.Dispose(); }
                });
            } else { job.Dispose(); process.Dispose(); }
        }

        static SafeFileHandle CreateLifetimeJob(Process child)
        {
            var job = CreateJobObject(IntPtr.Zero,null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new JobLimits();
            limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf(typeof(JobLimits))) ||
                !AssignProcessToJobObject(job,child.Handle)) {
                int error=Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error);
            }
            return job;
        }

        [StructLayout(LayoutKind.Sequential)] struct BasicLimits {
            public long ProcessTime,JobTime; public uint Flags; public UIntPtr MinWorking,MaxWorking;
            public uint ProcessLimit; public UIntPtr Affinity; public uint Priority,Schedule;
        }
        [StructLayout(LayoutKind.Sequential)] struct JobLimits {
            public BasicLimits Basic; public ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes;
            public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;
        }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct StartupInfo {
            public int size; public IntPtr reserved,desktop,title;
            public uint x,y,width,height,cols,rows,fill,flags; public ushort show,reservedCount;
            public IntPtr reserved2,input,output,error;
        }
        [StructLayout(LayoutKind.Sequential)] struct ChildInfo { public IntPtr process,thread; public uint id,threadId; }
        delegate bool EnumWindowProc(IntPtr hwnd,IntPtr param);
        delegate void WinEventProc(IntPtr hook,uint action,IntPtr hwnd,int obj,int child,uint thread,uint time);
        [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEventProc callback,uint process,uint thread,uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowProc proc,IntPtr data);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetParent(IntPtr hwnd,IntPtr parent);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint process);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd,StringBuilder buffer,int length);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd,StringBuilder buffer,int length);
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)] static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
        [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from,IntPtr to,ref Rect rect,uint count);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd,int cmd);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
        [DllImport("kernel32.dll", EntryPoint="CreateProcessW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string file,StringBuilder command,IntPtr processAttrs,IntPtr threadAttrs,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo info,out ChildInfo child);
        [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint exitCode);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern SafeFileHandle CreateJobObject(IntPtr attrs,string name);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(SafeFileHandle job,int type,ref JobLimits value,uint length);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
    }
}
