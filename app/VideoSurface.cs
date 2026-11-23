using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cadrivect
{
    internal sealed class VideoSurface : HwndHost
    {
        internal event Action DoubleClicked;
        [DllImport("user32.dll", CharSet=CharSet.Unicode,SetLastError=true)]
        static extern IntPtr CreateWindowEx(int exStyle,string className,string title,int style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd,IntPtr rect,IntPtr region,uint flags);
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct Paint {
            public IntPtr Dc; public int Erase; public Rect Area; public int Restore,Increment;
            [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] public byte[] Reserved;
        }
        [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd,out Paint paint);
        [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd,ref Paint paint);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
        [DllImport("user32.dll")] static extern int FillRect(IntPtr dc,ref Rect rect,IntPtr brush);
        [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int index);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);

        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            // STATIC SS_BLACKRECT remains the swap-chain destination for the lifetime of the dashboard.
            IntPtr child=CreateWindowEx(0,"STATIC","P5Cast video",0x56000104,0,0,1,1,parent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(child==IntPtr.Zero) throw new Win32Exception();
            return new HandleRef(this,child);
        }
        protected override void DestroyWindowCore(HandleRef hwnd) { DestroyWindow(hwnd.Handle); }
        protected override IntPtr WndProc(IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled)
        {
            if(msg==0x203 && DoubleClicked!=null) {DoubleClicked(); handled=true; return IntPtr.Zero;}
            if(msg==15 || msg==20) {
                Paint paint=new Paint(); IntPtr dc=msg==15?BeginPaint(hwnd,out paint):wp;
                Rect area; GetClientRect(hwnd,out area); FillRect(dc,ref area,GetStockObject(4));
                if(msg==15) EndPaint(hwnd,ref paint);
                handled=true; return new IntPtr(1);
            }
            return base.WndProc(hwnd,msg,wp,lp,ref handled);
        }
        internal void Clear() { if(Handle!=IntPtr.Zero) RedrawWindow(Handle,IntPtr.Zero,IntPtr.Zero,0x105); }
    }
}
