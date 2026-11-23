using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Cadrivect
{
    static class NetworkPlayerFixture
    {
        static string root=AppDomain.CurrentDomain.BaseDirectory;
        static string PathFor(string name) {return Path.Combine(root,name);}
        static void Require(bool condition,string detail) {if(!condition) throw new Exception(detail);}
        static void Pump() {
            var frame=new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate {frame.Continue=false;}));
            Dispatcher.PushFrame(frame); Thread.Sleep(8);
        }
        static void Until(Func<bool> condition,string detail) {
            var clock=Stopwatch.StartNew();
            while(!condition()) {if(clock.ElapsedMilliseconds>9000) throw new Exception(detail); Pump();}
        }
        static IntPtr Recorded(string name) {
            string file=PathFor(name);
            if(!File.Exists(file)) return IntPtr.Zero;
            long value; return long.TryParse(File.ReadAllText(file),out value)?new IntPtr(value):IntPtr.Zero;
        }
        static void Record(string name, long value) {
            string file=PathFor(name),tmp=file+".tmp";
            File.WriteAllText(tmp,value.ToString()); File.Move(tmp,file);
        }
        static IntPtr Make(string cls,string title) {
            return CreateWindowEx(0,cls,title,0x00cf0000,0,0,500,300,IntPtr.Zero,IntPtr.Zero,GetModuleHandle(null),IntPtr.Zero);
        }
        static int Child() {
            Record("pid",Process.GetCurrentProcess().Id);
            Require(Environment.GetEnvironmentVariable("ML_AUDIO")=="sdl" && Environment.GetEnvironmentVariable("SDL_AUDIO_DRIVER")=="dummy","Missing child audio guard.");
            WndClass wc=new WndClass(); wc.size=(uint)Marshal.SizeOf(wc); wc.instance=GetModuleHandle(null); wc.name="Qt6QWindowIcon";
            wc.procedure=Marshal.GetFunctionPointerForDelegate(proc);
            Require(RegisterClassEx(ref wc)!=0,"Register synthetic Qt window");
            IntPtr qt=Make(wc.name,"Moonlight");
            IntPtr unrelated=Make("STATIC","Unrelated - Moonlight");
            Require(qt!=IntPtr.Zero && unrelated!=IntPtr.Zero,"Fixture windows");
            Record("qt",qt.ToInt64()); Record("other",unrelated.ToInt64());
            Until(delegate{return File.Exists(PathFor("open-sdl"));},"SDL not requested");
            ShowWindow(qt,0);
            SDL_SetMainReady();
            Require(SDL_Init(0x20)==0,"SDL video initialization");
            IntPtr sdl=SDL_CreateWindow("Fixture - Moonlight",0,0,600,360,0x00000008|0x00000020);
            Require(sdl!=IntPtr.Zero,"Create pinned SDL window");
            Until(delegate{return File.Exists(PathFor("return-qt"));},"QML handoff not requested");
            SDL_DestroyWindow(sdl); SDL_Quit();
            ShowWindow(qt,4);
            Record("returned",1);
            // Exercise the job's bounded teardown even when a client ignores WM_CLOSE.
            while(true) Pump();
        }
        static int Parent() {
            IntPtr host=Make("STATIC","Hidden video host fixture");
            Require(host!=IntPtr.Zero,"Create hidden video host");
            NetworkPlayer player=null;
            try {
                var plan=new Dictionary<string,object>{
                    {"executable",Process.GetCurrentProcess().MainModule.FileName},{"arguments",new string[]{"stream","fixture.invalid"}},
                    {"muted",true},{"message","Preparing fixture"}
                };
                int announcements=0;
                player=NetworkPlayer.Start(host,plan,delegate(string s){if(s.Contains("Network video is playing")) announcements++;},
                    delegate(int code){throw new Exception("Fixture exited early: "+code);});
                Until(delegate{return Recorded("qt")!=IntPtr.Zero && GetParent(Recorded("qt"))==host;},"Qt panel did not attach.");
                Require(GetParent(Recorded("other"))==IntPtr.Zero,"Unrelated fixture window was touched.");
                File.WriteAllText(PathFor("open-sdl"),"");
                IntPtr video=IntPtr.Zero;
                Until(delegate{
                    video=FindWindowEx(host,IntPtr.Zero,null,"Fixture - Moonlight");
                    return video!=IntPtr.Zero && announcements==1;
                },"Pinned SDL video did not attach.");
                SetWindowPos(host,IntPtr.Zero,0,0,823,487,0x0016);
                Until(delegate{
                    Rect a,b; GetClientRect(host,out a); GetClientRect(video,out b);
                    return a.right==b.right && a.bottom==b.bottom;
                },"SDL video did not resize.");
                Require(!IsWindowVisible(host) && !IsWindowVisible(video),"Fixture became visible on the desktop.");
                File.WriteAllText(PathFor("return-qt"),"");
                Until(delegate{return File.Exists(PathFor("returned")) && !IsWindow(video) && GetParent(Recorded("qt"))==host;},"Qt panel did not return after SDL.");
                var child=Process.GetProcessById((int)Recorded("pid").ToInt64());
                var stopped=player.StopAsync();
                Require(stopped==player.StopAsync(),"Repeated Stop lost the pending shutdown task.");
                Until(delegate{return stopped.IsCompleted;},"Network shutdown did not complete.");
                stopped.GetAwaiter().GetResult();
                Require(child.HasExited,"Network shutdown completed before its player exited.");
                player.Dispose(); player=null;
                child.Dispose();
                Console.WriteLine("Passed hidden Qt to pinned SDL handoff, resizing, return to Qt, muted child environment and owned-process shutdown.");
                return 0;
            } finally {if(player!=null) player.Dispose(); DestroyWindow(host);}
        }
        [STAThread] static int Main(string[] args) {
            try{return args.Length==0?Parent():Child();}
            catch(Exception e){Console.Error.WriteLine(e);return 1;}
        }
        delegate IntPtr WndProc(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
        static readonly WndProc proc=DefWindowProc;
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct WndClass {
            public uint size,style; public IntPtr procedure; public int cls,win; public IntPtr instance,icon,cursor,brush;
            public string menu,name; public IntPtr smallIcon;
        }
        [StructLayout(LayoutKind.Sequential)] struct Rect {public int left,top,right,bottom;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern ushort RegisterClassEx(ref WndClass wc);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int w,int h,IntPtr p,IntPtr menu,IntPtr instance,IntPtr data);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string cls,string name);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h,out Rect rect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int cmd);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("SDL2.dll",CallingConvention=CallingConvention.Cdecl)] static extern void SDL_SetMainReady();
        [DllImport("SDL2.dll",CallingConvention=CallingConvention.Cdecl)] static extern int SDL_Init(uint flags);
        [DllImport("SDL2.dll",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr SDL_CreateWindow(string title,int x,int y,int w,int h,uint flags);
        [DllImport("SDL2.dll",CallingConvention=CallingConvention.Cdecl)] static extern void SDL_DestroyWindow(IntPtr window);
        [DllImport("SDL2.dll",CallingConvention=CallingConvention.Cdecl)] static extern void SDL_Quit();
    }
}
