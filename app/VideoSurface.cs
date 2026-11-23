using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cadrivect
{
    internal sealed class VideoSurface : HwndHost
    {
        internal event Action DoubleClicked;
        internal event Action<double> WipePositionChanged;
        internal event Action<int,float> WipeGripChanged;
        bool wipeActive, horizontalWipe, dragging;
        int gripState;
        float gripScale=1;
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
        [StructLayout(LayoutKind.Sequential)] struct MouseTracking {public int Size,Flags; public IntPtr Window; public int HoverTime;}
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref MouseTracking request);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr LoadCursor(IntPtr instance,IntPtr resource);
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
        static readonly IntPtr resizeWE=LoadCursor(IntPtr.Zero,new IntPtr(32644)); // IDC_SIZEWE
        static readonly IntPtr resizeNS=LoadCursor(IntPtr.Zero,new IntPtr(32645)); // IDC_SIZENS
        IntPtr ResizeCursor {get {return horizontalWipe ? resizeNS : resizeWE;}}
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
        internal void SetWipeScale(float scale)
        {
            if(gripScale==scale) return;
            gripScale=scale;
            if(wipeActive && WipeGripChanged!=null) WipeGripChanged(gripState,scale);
        }
        void SetGrip(int state)
        {
            if(gripState==state) return;
            gripState=state;
            if(WipeGripChanged!=null) WipeGripChanged(state,gripScale);
        }
        void UpdateHover(IntPtr hwnd,int x,int y)
        {
            bool hit=HitDivider(hwnd,x,y);
            if(hit && gripState==0) {
                var request=new MouseTracking {Size=Marshal.SizeOf(typeof(MouseTracking)),Flags=2,Window=hwnd}; // TME_LEAVE
                TrackMouseEvent(ref request);
            }
            SetGrip(hit ? 1 : 0);
        }
        internal void ConfigureWipe(bool active,double position,double sourceAspect,bool horizontal)
        {
            bool wasActive=wipeActive;
            wipeActive=active && sourceAspect>0 && !double.IsInfinity(sourceAspect);
            if(dragging && (!wipeActive || horizontalWipe!=horizontal)) EndDrag(Handle);
            if(!wipeActive || horizontalWipe!=horizontal) SetGrip(0);
            if(wipeActive && !wasActive && WipeGripChanged!=null) WipeGripChanged(gripState,gripScale);
            horizontalWipe=horizontal;
            wipePosition=Math.Max(0,Math.Min(1,position));
            pictureAspect=sourceAspect;
        }
        bool GetPicture(IntPtr hwnd,out Rectangle picture)
        {
            // Fit the source to the live client. A fullscreen change or drawer resize
            // can reach this HWND before the next engine telemetry sample.
            picture=Rectangle.Empty;
            Rect client;
            if(!GetClientRect(hwnd,out client) || !wipeActive || client.Right<1 || client.Bottom<1) return false;
            int width=Math.Max(1,(int)Math.Round(Math.Min(client.Right,client.Bottom*pictureAspect)));
            int height=Math.Max(1,(int)Math.Round(Math.Min(client.Bottom,client.Right/pictureAspect)));
            picture=new Rectangle((client.Right-width)/2,(client.Bottom-height)/2,width,height);
            return true;
        }
        bool HitDivider(IntPtr hwnd,int x,int y)
        {
            Rectangle picture;
            if(!GetPicture(hwnd,out picture) || !picture.Contains(x,y)) return false;
            var layout=new WipeLayout(picture,horizontalWipe,wipePosition,gripScale);
            return Math.Abs((horizontalWipe ? y : x)-layout.Center)<=Math.Max(6,8*gripScale);
        }
        double PointerFraction(IntPtr hwnd,int x,int y)
        {
            Rectangle picture;
            if(!GetPicture(hwnd,out picture)) return wipePosition;
            var layout=new WipeLayout(picture,horizontalWipe,wipePosition,gripScale);
            return layout.PositionAt(horizontalWipe ? y : x);
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
            SetGrip(0);
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
                if(msg==MouseMove && !dragging && wipeActive) UpdateHover(hwnd,x,y);
                if((msg==ButtonDown || msg==DoubleClick) && HitDivider(hwnd,x,y)) {
                    initialPosition=wipePosition;
                    grabOffset=wipePosition-PointerFraction(hwnd,x,y);
                    SetCapture(hwnd); dragging=GetCapture()==hwnd;
                    if(dragging) SetGrip(2);
                    SetCursor(ResizeCursor); handled=true; return IntPtr.Zero;
                }
                if(dragging && (msg==MouseMove || msg==ButtonUp)) {
                    ChangePosition(PointerFraction(hwnd,x,y)+grabOffset);
                    if(msg==ButtonUp) {
                        EndDrag(hwnd);
                        UpdateHover(hwnd,x,y);
                    }
                    handled=true; return IntPtr.Zero;
                }
            }
            if(msg==Cursor && wipeActive) {
                PixelPoint pt;
                if(dragging || (GetCursorPos(out pt) && ScreenToClient(hwnd,ref pt) && HitDivider(hwnd,pt.X,pt.Y))) {
                    SetCursor(ResizeCursor); handled=true; return new IntPtr(1);
                }
            }
            if(msg==CaptureChanged) { dragging=false; SetGrip(0); }
            if(msg==0x02A3 && !dragging) SetGrip(0); // WM_MOUSELEAVE
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
