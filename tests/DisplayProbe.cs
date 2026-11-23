// Exercises the live player's hotkey/message handlers against its own synthetic source window.
using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using System.Web.Script.Serialization;

internal static class DisplayProbe
{
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    delegate bool Visit(IntPtr hwnd,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(Visit visit,IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetProp(IntPtr hwnd,string key);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Rect r);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr dpi);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint msg,IntPtr w,IntPtr l,uint flags,uint ms,out IntPtr result);

    static IntPtr FindPlayer(int id)
    {
        IntPtr match=IntPtr.Zero;
        EnumWindows(delegate(IntPtr hwnd,IntPtr ignored) {
            uint pid;
            GetWindowThreadProcessId(hwnd,out pid);
            if(pid!=(uint)id || GetProp(hwnd,"Cadrivect.PlaybackWindow.v1")!=new IntPtr(1)) return true;
            match=hwnd; return false;
        },IntPtr.Zero);
        return match;
    }
    static Rectangle Bounds(IntPtr hwnd)
    {
        Rect r;
        if(!GetWindowRect(hwnd,out r)) throw new Exception("Player HWND was destroyed.");
        return Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
    }
    static void Hotkey(IntPtr hwnd,int id)
    {
        IntPtr result;
        if(SendMessageTimeout(hwnd,0x0312,new IntPtr(id),IntPtr.Zero,2,2000,out result)==IntPtr.Zero)
            throw new Exception("Player did not process its hotkey.");
    }
    static void Assert(bool value,string message) { if(!value) throw new Exception(message); }
    static int Main(string[] args)
    {
        try {
            if(args.Length!=4) throw new Exception("Pass the player EXE, report path, HDR mode and destination (alternate or primary).");
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            var displays=DisplayTarget.Enumerate();
            var primary=displays[0];
            var alternate=displays.Length>1 ? displays[1] : primary;
            bool returnToPrimary=args[3]=="primary";
            if(returnToPrimary && displays.Length==1) { Console.WriteLine("Only one monitor, skipping return trip."); return 0; }
            var final=returnToPrimary ? primary : alternate;
            var timer=Stopwatch.StartNew();
            var command=new ProcessStartInfo(args[0],"--self-test --seconds 8 --verify-output --hdr "+args[2]+" --resolution 720 --windowed --report \""+args[1]+"\"") {
                UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(args[0])
            };
            using(var p=Process.Start(command)) {
                try {
                    IntPtr hwnd;
                    while((hwnd=FindPlayer(p.Id))==IntPtr.Zero && !p.HasExited && timer.ElapsedMilliseconds<12000) Thread.Sleep(25);
                    Assert(hwnd!=IntPtr.Zero,"Test player did not create a playback window.");
                    Thread.Sleep(800);
                    Assert(DisplayTarget.DeviceForWindow(hwnd)==primary.DeviceName,"Windowed player was not on the primary display.");
                    if(!returnToPrimary) {
                        Hotkey(hwnd,3);
                        Assert(Bounds(hwnd)==primary.Bounds,"Fullscreen bounds differ from physical monitor bounds.");
                        Console.WriteLine("Fullscreen: "+primary.Label+" "+Bounds(hwnd));
                        if(displays.Length>1) {
                            Hotkey(hwnd,4);
                            Assert(Bounds(hwnd)==alternate.Bounds,"Cycle display failed to move the fullscreen player.");
                            Assert(DisplayTarget.MovePlayer(p.Id,primary.DeviceName),"Dashboard move request failed.");
                            Assert(Bounds(hwnd)==primary.Bounds,"Dashboard selected the wrong monitor.");
                        }
                        Hotkey(hwnd,3);
                        Assert(Bounds(hwnd)!=primary.Bounds && IsWindow(hwnd),"Returning to windowed mode lost the player HWND or size.");
                    }
                    Assert(DisplayTarget.MovePlayer(p.Id,alternate.DeviceName),"Moving a windowed player failed.");
                    Assert(DisplayTarget.DeviceForWindow(hwnd)==alternate.DeviceName,"Windowed player was not on the requested output.");
                    if(returnToPrimary) {
                        Thread.Sleep(1500); // allow DXGI and the WGC pool to enter the alternate monitor's format
                        Assert(DisplayTarget.MovePlayer(p.Id,primary.DeviceName),"Windowed return to primary failed.");
                    }
                    Assert(DisplayTarget.DeviceForWindow(hwnd)==final.DeviceName,"Final monitor differs from requested output.");
                    Console.WriteLine("Windowed: "+final.Label+" "+Bounds(hwnd));
                    // The synthetic source changes at most 30 times per second. Bound all pre-move frames
                    // conservatively, including startup, WGC pool restarts and the initial paint.
                    int preMoveUpperBound=(int)Math.Ceiling(timer.Elapsed.TotalSeconds*30)+12;
                    Assert(p.WaitForExit(20000) && p.ExitCode==0,"GPU capture/present test failed.");
                    var report=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(args[1]));
                    bool expectHdr=args[2]=="auto" && final.AdvancedColorEnabled;
                    Assert((bool)report["hdrEnabled"]==expectHdr && (string)report["display"]==final.DeviceName,
                           "Wrong DXGI output format or monitor. See "+args[1]);
                    int newFrames=Convert.ToInt32(report["captured"])-preMoveUpperBound;
                    Assert(newFrames>=10,"Capture failed to resume after the move. See "+args[1]);
                    Assert(Convert.ToDouble(report["sourceFps"])<=35,
                           "The 30 fps test pattern produced unexpected changing pixels. See "+args[1]);
                    Assert(Convert.ToInt32(report["sampledFrames"])>=3 && Convert.ToInt32(report["sampleMaxLuma"])>Convert.ToInt32(report["sampleMinLuma"]),
                           "Output verification did not sample distinct luminance. See "+args[1]);
                    Console.WriteLine("Verified "+(expectHdr ? "HDR10":"SDR")+" on "+final.FriendlyName+"; at least "+newFrames+" new captured frames after the last move.");
                } finally {
                    if(!p.HasExited) { p.Kill(); p.WaitForExit(3000); }
                }
            }
            return 0;
        } catch(Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
