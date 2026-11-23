using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cadrivect
{
    internal sealed class VideoSurface : HwndHost
    {
        internal event Action DoubleClicked;
        internal event Action<double> WipePositionChanged;
        bool wipeActive, dragging;
        double wipePosition=.5, pictureAspect;
        double initialPosition, grabOffset;
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
        [StructLayout(LayoutKind.Sequential)] struct PixelPoint { public int X,Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out PixelPoint pt);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd,ref PixelPoint pt);
        [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetCapture();
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr LoadCursor(IntPtr instance,IntPtr resource);
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
        static readonly IntPtr resizeCursor=LoadCursor(IntPtr.Zero,new IntPtr(32644)); // IDC_SIZEWE
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
        protected override void DestroyWindowCore(HandleRef hwnd) { EndDrag(hwnd.Handle); DestroyWindow(hwnd.Handle); }
        internal void ConfigureWipe(bool active,double position,double sourceAspect)
        {
            wipeActive=active && sourceAspect>0 && !double.IsInfinity(sourceAspect);
            if(!wipeActive && dragging) EndDrag(Handle);
            wipePosition=Math.Max(0,Math.Min(1,position));
            pictureAspect=sourceAspect;
        }
        bool GetPicture(IntPtr hwnd,out Rect picture)
        {
            // Fit the source to the live client. A fullscreen change or drawer resize
            // can reach this HWND before the next engine telemetry sample.
            if(!GetClientRect(hwnd,out picture) || !wipeActive || picture.Right<1 || picture.Bottom<1) return false;
            int width=Math.Max(1,(int)Math.Round(Math.Min(picture.Right,picture.Bottom*pictureAspect)));
            int height=Math.Max(1,(int)Math.Round(Math.Min(picture.Bottom,picture.Right/pictureAspect)));
            picture.Left=(picture.Right-width)/2; picture.Top=(picture.Bottom-height)/2;
            picture.Right=picture.Left+width; picture.Bottom=picture.Top+height;
            return true;
        }
        bool HitDivider(IntPtr hwnd,int x,int y)
        {
            Rect picture;
            if(!GetPicture(hwnd,out picture)) return false;
            double seam=picture.Left+wipePosition*(picture.Right-picture.Left);
            double slop=8*Math.Max(96u,GetDpiForWindow(hwnd))/96.0;
            return y>=picture.Top && y<picture.Bottom && x>=picture.Left && x<picture.Right && Math.Abs(x-seam)<=slop;
        }
        double PointerFraction(IntPtr hwnd,int x)
        {
            Rect picture;
            if(!GetPicture(hwnd,out picture)) return wipePosition;
            return (x-picture.Left)/(double)(picture.Right-picture.Left);
        }
        void ChangePosition(double next)
        {
            next=Math.Max(0,Math.Min(1,next));
            if(wipePosition==next) return;
            wipePosition=next;
            if(WipePositionChanged!=null) WipePositionChanged(next);
        }
        void EndDrag(IntPtr hwnd)
        {
            if(!dragging) return;
            dragging=false;
            if(GetCapture()==hwnd) ReleaseCapture();
        }
        internal bool CancelWipeDrag()
        {
            if(!dragging) return false;
            EndDrag(Handle); ChangePosition(initialPosition); return true;
        }
        protected override IntPtr WndProc(IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled)
        {
            const int MouseMove=0x200, ButtonDown=0x201, ButtonUp=0x202, DoubleClick=0x203;
            const int Cursor=0x20, CancelMode=0x1F, CaptureChanged=0x215;
            if(msg==MouseMove || msg==ButtonDown || msg==ButtonUp || msg==DoubleClick) {
                int x=unchecked((short)(lp.ToInt64() & 0xffff));
                int y=unchecked((short)((lp.ToInt64() >> 16) & 0xffff));
                if((msg==ButtonDown || msg==DoubleClick) && HitDivider(hwnd,x,y)) {
                    initialPosition=wipePosition;
                    grabOffset=wipePosition-PointerFraction(hwnd,x);
                    SetCapture(hwnd); dragging=GetCapture()==hwnd;
                    SetCursor(resizeCursor); handled=true; return IntPtr.Zero;
                }
                if(dragging && (msg==MouseMove || msg==ButtonUp)) {
                    ChangePosition(PointerFraction(hwnd,x)+grabOffset);
                    if(msg==ButtonUp) EndDrag(hwnd);
                    handled=true; return IntPtr.Zero;
                }
            }
            if(msg==Cursor && wipeActive) {
                PixelPoint pt;
                if(dragging || (GetCursorPos(out pt) && ScreenToClient(hwnd,ref pt) && HitDivider(hwnd,pt.X,pt.Y))) {
                    SetCursor(resizeCursor); handled=true; return new IntPtr(1);
                }
            }
            if(msg==CaptureChanged) dragging=false;
            if(msg==CancelMode && CancelWipeDrag()) {handled=true; return IntPtr.Zero;}
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
