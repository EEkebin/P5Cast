// Launches the real player on a new, inactive desktop. Exercises only that process's HWNDs.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class OverlayProbe
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartInfo {
        public int Size; public string Reserved,Desktop,Title; public uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags;
        public short Show,ReservedSize; public IntPtr ReservedBytes,StdIn,StdOut,StdErr;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread; public int Id,ThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    delegate bool Visit(IntPtr hwnd,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr desktop,Visit visit,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent,Visit visit,IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out int pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetProp(IntPtr hwnd,string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr a,StringBuilder text);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr a,IntPtr b);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int RegisterWindowMessage(string message);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
    [DllImport("user32.dll")] static extern bool SetThreadDesktop(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint id);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr process,IntPtr thread,bool inherit,uint flags,IntPtr env,string dir,ref StartInfo info,out ProcessInfo output);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static void Check(bool ok,string reason) { if(!ok) throw new Exception(reason); }
    static string Caption(IntPtr hwnd) {
        var text=new StringBuilder(512); SendMessage(hwnd,0x000d,new IntPtr(text.Capacity),text); return text.ToString();
    }
    static IntPtr Find(IntPtr desktop,int pid,bool player) {
        IntPtr found=IntPtr.Zero;
        EnumDesktopWindows(desktop,delegate(IntPtr hwnd,IntPtr ignored) {
            int owner; GetWindowThreadProcessId(hwnd,out owner);
            if(owner==pid && (player ? GetProp(hwnd,"Cadrivect.PlaybackWindow.v1")!=IntPtr.Zero : Caption(hwnd)=="P5Cast playback controls")) {
                found=hwnd; return false;
            }
            return true;
        },IntPtr.Zero);
        return found;
    }
    static IntPtr Child(IntPtr parent,string title,bool contains) {
        IntPtr found=IntPtr.Zero;
        EnumChildWindows(parent,delegate(IntPtr hwnd,IntPtr ignored) {
            string caption=Caption(hwnd);
            if(IsWindowVisible(hwnd) && (contains ? caption.Contains(title) : caption==title)) { found=hwnd; return false; }
            return true;
        },IntPtr.Zero);
        return found;
    }
    static void Button(IntPtr hud,string name) {
        IntPtr hwnd=Child(hud,name,false); Check(hwnd!=IntPtr.Zero,"Missing button: "+name);
        SendMessage(hwnd,0x00f5,IntPtr.Zero,IntPtr.Zero); // BM_CLICK, addressed to this test's HWND
    }
    static void SavePanel(IntPtr hud,string path) {
        Rect r; Check(GetWindowRect(hud,out r),"HUD bounds missing.");
        using(var image=new Bitmap(r.Right-r.Left,r.Bottom-r.Top)) {
            using(var g=Graphics.FromImage(image)) {
                IntPtr dc=g.GetHdc(); try { Check(PrintWindow(hud,dc,2),"HUD paint failed."); } finally { g.ReleaseHdc(dc); }
            }
            image.Save(path,ImageFormat.Png);
            int traces=0; var unique=new HashSet<int>();
            for(int y=0;y<image.Height;y+=2)
                for(int x=0;x<image.Width;x+=2) {
                    var color=image.GetPixel(x,y); unique.Add(color.ToArgb());
                    if(y>image.Height*.21 && y<image.Height*.38 && color.G>112 && (color.B-color.R>25 || color.G-color.R>25)) traces++;
                }
            Check(unique.Count>35 && traces>=8,"Paint lacks visible graph traces. Inspect "+path);
        }
    }
    static int Main(string[] args) {
        if(args.Length!=4) { Console.Error.WriteLine("Pass player EXE, Direct runtime, packet fixture and output folder."); return 2; }
        string player=Path.GetFullPath(args[0]),runtime=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[3]);
        string id="CadrivectControls"+Guid.NewGuid().ToString("N"),report=Path.Combine(output,"report.json");
        var packets=new List<byte[]>();
        IntPtr desktop=IntPtr.Zero,original=GetThreadDesktop(GetCurrentThreadId());
        try {
            using(var input=new BinaryReader(File.OpenRead(args[2]))) while(input.BaseStream.Position<input.BaseStream.Length) {
                int size=input.ReadInt32(); Check(size>0 && size<4000000,"Invalid synthetic packet fixture."); packets.Add(input.ReadBytes(size));
            }
            Check(packets.Count>1,"Expected at least two synthetic video packets.");
            Directory.CreateDirectory(output);
            desktop=CreateDesktop(id,IntPtr.Zero,IntPtr.Zero,0,0x000f01ff,IntPtr.Zero);
            if(desktop==IntPtr.Zero || !SetThreadDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup=new StartInfo { Size=Marshal.SizeOf(typeof(StartInfo)), Desktop="winsta0\\"+id };
            var command=new StringBuilder("\""+player+"\" --direct-runtime \""+runtime+"\" --direct-pipe "+id+" --target-fps 30 --hdr off --resolution 720 --mode original --multiplier 1 --windowed --controls --quiet --seconds 10 --report \""+report+"\"");
            ProcessInfo info;
            if(!CreateProcess(player,command,IntPtr.Zero,IntPtr.Zero,false,0,IntPtr.Zero,Path.GetDirectoryName(player),ref startup,out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(info.Thread); CloseHandle(info.Process);
            using(var process=Process.GetProcessById(info.Id))
            try {
                // Keep a query handle for exit status after the process has ended.
                IntPtr processHandle=process.Handle;
                Exception streamError=null; var done=new ManualResetEvent(false);
                var writer=new Thread(delegate() {
                    try {
                        using(var pipe=new NamedPipeClientStream(".",id,PipeDirection.Out)) {
                            pipe.Connect(10000);
                            using(var data=new BinaryWriter(pipe)) {
                                data.Write(0x31564443); data.Write(1);
                                data.Write(0); data.Write(12000); data.Write(0); data.Write(3300); data.Write(18000);
                                int index=0;
                                while(!done.WaitOne(33)) {
                                    var packet=packets[index++%packets.Count]; data.Write(packet.Length); data.Write(packet); data.Flush();
                                }
                            }
                        }
                    } catch(IOException) { } catch(Exception e) { streamError=e; }
                }) { IsBackground=true };
                writer.Start();
                try {
                    IntPtr playerHwnd=IntPtr.Zero,hud=IntPtr.Zero;
                    var clock=Stopwatch.StartNew();
                    while(clock.ElapsedMilliseconds<8000 && !process.HasExited) {
                        playerHwnd=Find(desktop,process.Id,true); hud=Find(desktop,process.Id,false);
                        if(playerHwnd!=IntPtr.Zero && hud!=IntPtr.Zero && Child(hud,"FSR 1 · Spatial",false)!=IntPtr.Zero) break;
                        Thread.Sleep(50);
                    }
                    Check(hud!=IntPtr.Zero && playerHwnd!=IntPtr.Zero,"Player or HUD failed to open.");
                    int msg=RegisterWindowMessage("Cadrivect.Player.Controls.0.3");
                    Check(SendMessage(playerHwnd,msg,new IntPtr(1),new IntPtr(3))==new IntPtr(1),"Dashboard settings were rejected.");
                    Thread.Sleep(350);
                    Button(hud,"FSR 1 · Spatial"); Button(hud,"2×"); Button(hud,"Pin stats");
                    Thread.Sleep(4100);
                    Check(Child(hud,"Audio queue  12.0 ms",true)!=IntPtr.Zero,"Audio-queue telemetry was not visible.");
                    Check(Child(hud,"FSR 1 · Spatial",true)!=IntPtr.Zero && Child(hud,"Unpin stats",false)!=IntPtr.Zero,"Pinned HUD selection failed.");
                    SavePanel(hud,Path.Combine(output,"playback-controls.png"));
                    var json=new JavaScriptSerializer(); string live=Path.ChangeExtension(report,".live.json");
                    var snapshot=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(live));
                    Check(Convert.ToInt32(snapshot["targetPresentedFps"])==60 && Convert.ToInt32(snapshot["audioQueueMs"])==12 &&
                          snapshot["audioBitrateKbps"]==null && snapshot["audioSampleRate"]==null && snapshot["audioChannels"]==null &&
                          Convert.ToDouble(snapshot["handshakeRttMs"])>3 && Convert.ToInt32(snapshot["generated"])>0,"Legacy CDV1 telemetry or generated frame count is incorrect.");
                    Rect before,after; GetWindowRect(playerHwnd,out before);
                    SendMessage(playerHwnd,0x0312,new IntPtr(3),IntPtr.Zero);
                    Thread.Sleep(250); GetWindowRect(playerHwnd,out after);
                    Check(after.Right-after.Left>before.Right-before.Left || after.Bottom-after.Top>before.Bottom-before.Top,
                          "Native player failed to enter fullscreen.");
                    Check(SendMessage(playerHwnd,msg,new IntPtr(-1),new IntPtr(-1))==new IntPtr(1),"Fullscreen controls did not open.");
                    Check(Child(hud,"Close",false)!=IntPtr.Zero,"Fullscreen controls were not visible.");
                    SendMessage(playerHwnd,0x0312,new IntPtr(5),IntPtr.Zero); Thread.Sleep(200);
                    Check(Child(hud,"Close",false)==IntPtr.Zero,"HUD hotkey did not collapse controls.");
                    SendMessage(playerHwnd,0x0312,new IntPtr(3),IntPtr.Zero); // leave original windowed size
                    Thread.Sleep(180); // allow the render loop to resize its backbuffer before recording the final report
                    SendMessage(playerHwnd,0x0312,new IntPtr(1),IntPtr.Zero); // Ctrl+Alt+Q closes this player
                    Check(process.WaitForExit(12000) && process.ExitCode==0,"Player failed its Direct session.");
                    var values=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(report));
                    Check((string)values["mode"]=="fsr" && Convert.ToInt32(values["multiplier"])==2 &&
                          Convert.ToInt32(values["generated"])>0 && Convert.ToInt32(values["captured"])>=20 &&
                          !File.Exists(live),"Live control changes or final telemetry cleanup failed.");
                    Console.WriteLine("Passed Direct HUD, graph paint, pinned stats and control changes. Output: "+output);
                } finally { done.Set(); writer.Join(2000); }
                if(streamError!=null) throw streamError;
            } finally { if(!process.HasExited) {process.Kill(); process.WaitForExit(3000);} }
            return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if(desktop!=IntPtr.Zero) { SetThreadDesktop(original); CloseDesktop(desktop); } }
    }
}
