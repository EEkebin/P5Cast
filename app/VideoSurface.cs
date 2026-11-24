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
        internal event Action ClientResized;
        internal bool HasPresentedVideo {get;set;}
        internal event Action<double> WipePositionChanged;
        internal event Action<int,float> WipeGripChanged;
        internal event Action<bool,float,float,int,float> LoupeChanged;
        bool wipeActive, horizontalWipe, dragging;
        bool sideBySide, inspecting, loupeVisible;
        int loupeLevel=2, wheelRemainder;
        float loupeX,loupeY;
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
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int left,int top,int right,int bottom,int ellipseWidth,int ellipseHeight);
        [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr destination,IntPtr first,IntPtr second,int operation);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd,IntPtr region,bool redraw);
        bool clippedForModal;
        Rectangle progressHole;
        Size clipClientSize;
        int clipDiameter;

        internal void ClipForProgress(System.Windows.Controls.Border card)
        {
            if(Handle==IntPtr.Zero) return;
            var bounds=Rectangle.Empty;
            var size=Size.Empty;
            int diameter=0;
            Rect client;
            if(card!=null && card.IsVisible && card.ActualWidth>0 && card.ActualHeight>0 && GetClientRect(Handle,out client)) {
                var top=card.PointToScreen(new System.Windows.Point());
                var bottom=card.PointToScreen(new System.Windows.Point(card.ActualWidth,card.ActualHeight));
                var point=new PixelPoint {X=(int)Math.Floor(top.X),Y=(int)Math.Floor(top.Y)};
                ScreenToClient(Handle,ref point);
                bounds=new Rectangle(point.X,point.Y,(int)Math.Ceiling(bottom.X)-(int)Math.Floor(top.X),(int)Math.Ceiling(bottom.Y)-(int)Math.Floor(top.Y));
                size=new Size(client.Right,client.Bottom);
                diameter=(int)Math.Round(2*card.CornerRadius.TopLeft*bounds.Width/card.ActualWidth);
            }
            if(bounds==progressHole && size==clipClientSize && diameter==clipDiameter) return;
            progressHole=bounds; clipClientSize=size; clipDiameter=diameter;
            if(!clippedForModal) ApplyModalClip(Handle,false);
        }

        internal void ClipForModal(bool clip)
        {
            if(clippedForModal==clip) return;
            clippedForModal=clip;
            if(Handle!=IntPtr.Zero) ApplyModalClip(Handle,clip);
        }
        void ApplyModalClip(IntPtr hwnd,bool clip)
        {
            // Keep the native child and its client size alive until the renderer acknowledges the change.
            IntPtr region=clip ? CreateRectRgn(0,0,0,0) : IntPtr.Zero;
            if(clip && region==IntPtr.Zero) throw new Win32Exception();
            if(!clip && !progressHole.IsEmpty) {
                region=CreateRectRgn(0,0,clipClientSize.Width,clipClientSize.Height);
                // Subtract just the WPF card from the native child. Video stays visible around it.
                IntPtr hole=CreateRoundRectRgn(progressHole.Left,progressHole.Top,progressHole.Right+1,progressHole.Bottom+1,clipDiameter,clipDiameter);
                try {
                    if(region==IntPtr.Zero || hole==IntPtr.Zero || CombineRgn(region,region,hole,4)==0) {
                        if(region!=IntPtr.Zero) DeleteObject(region);
                        throw new Win32Exception();
                    }
                } finally {if(hole!=IntPtr.Zero) DeleteObject(hole);}
            }
            if(SetWindowRgn(hwnd,region,true)==0) {
                if(region!=IntPtr.Zero) DeleteObject(region);
                throw new Win32Exception();
            }
            // On success Windows owns the region.
        }
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);

        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            // STATIC SS_BLACKRECT remains the swap-chain destination for the lifetime of the dashboard.
            IntPtr child=CreateWindowEx(0,"STATIC","P5Cast video",0x56000104,0,0,1,1,parent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(child==IntPtr.Zero) throw new Win32Exception();
            if(clippedForModal) ApplyModalClip(child,true);
            return new HandleRef(this,child);
        }
        protected override void DestroyWindowCore(HandleRef hwnd) { EndGesture(hwnd.Handle); DestroyWindow(hwnd.Handle); }
        internal void SetWipeScale(float scale)
        {
            if(gripScale==scale) return;
            gripScale=scale;
            if(wipeActive && WipeGripChanged!=null) WipeGripChanged(gripState,scale);
            if(loupeVisible) PublishLoupe();
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
        internal void ConfigurePicture(string view,double position,double sourceAspect,bool horizontal)
        {
            bool wasActive=wipeActive;
            pictureAspect=sourceAspect>0 && !double.IsInfinity(sourceAspect) ? sourceAspect : 0;
            if(inspecting && (pictureAspect==0 || sideBySide!=(view=="side-by-side"))) EndGesture(Handle);
            sideBySide=view=="side-by-side";
            wipeActive=WipeLayout.IsSplit(view) && pictureAspect>0;
            if(dragging && (!wipeActive || horizontalWipe!=horizontal)) EndGesture(Handle);
            if(!wipeActive || horizontalWipe!=horizontal) SetGrip(0);
            if(wipeActive && !wasActive && WipeGripChanged!=null) WipeGripChanged(gripState,gripScale);
            horizontalWipe=horizontal;
            wipePosition=Math.Max(0,Math.Min(1,position));
        }
        Rectangle PictureAt(Rect client,int x)
        {
            // Fit the source to the live client. A fullscreen change or drawer resize
            // can reach this HWND before the next engine telemetry sample.
            if(pictureAspect==0 || client.Right<1 || client.Bottom<1) return Rectangle.Empty;
            int pane=client.Right,offset=0;
            if(sideBySide && pane>=2) {
                pane/=2;
                if(x>=client.Right-pane) offset=client.Right-pane;
            }
            int width=Math.Max(1,(int)Math.Round(Math.Min(pane,client.Bottom*pictureAspect)));
            int height=Math.Max(1,(int)Math.Round(Math.Min(client.Bottom,pane/pictureAspect)));
            return new Rectangle(offset+(pane-width)/2,(client.Bottom-height)/2,width,height);
        }
        bool HitDivider(IntPtr hwnd,int x,int y)
        {
            Rect client;
            if(!wipeActive || !GetClientRect(hwnd,out client)) return false;
            var picture=PictureAt(client,x);
            if(!picture.Contains(x,y)) return false;
            var layout=new WipeLayout(picture,horizontalWipe,wipePosition,gripScale);
            return Math.Abs((horizontalWipe ? y : x)-layout.Center)<=Math.Max(6,8*gripScale);
        }
        double PointerFraction(IntPtr hwnd,int x,int y)
        {
            Rect client;
            if(!GetClientRect(hwnd,out client)) return wipePosition;
            var picture=PictureAt(client,x);
            if(picture.IsEmpty) return wipePosition;
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
        void PublishLoupe()
        {
            if(LoupeChanged!=null) LoupeChanged(loupeVisible,loupeX,loupeY,loupeLevel,gripScale);
        }
        bool TrackLoupe(IntPtr hwnd,int x,int y,bool start)
        {
            Rect client;
            bool inside=GetClientRect(hwnd,out client) && PictureAt(client,x).Contains(x,y);
            if(start) {
                if(!inside) return false;
                SetCapture(hwnd);
                if(GetCapture()!=hwnd) return false;
                inspecting=true;
                wheelRemainder=0;
            }
            if(inside) {
                // Input and rendering agree on physical pixel centers at every DPI.
                loupeX=(x+.5f)/client.Right;
                loupeY=(y+.5f)/client.Bottom;
            }
            if(inside || loupeVisible) {
                loupeVisible=inside;
                PublishLoupe();
            }
            return true;
        }
        void EndGesture(IntPtr hwnd)
        {
            if(!inspecting) return;
            inspecting=loupeVisible=dragging=false;
            wheelRemainder=0;
            SetGrip(0);
            if(GetCapture()==hwnd) ReleaseCapture();
            PublishLoupe();
        }
        internal bool EndPointerGesture(bool restoreDivider=false)
        {
            if(!inspecting) return false;
            bool restore=restoreDivider && dragging;
            EndGesture(Handle);
            if(restore) ChangePosition(initialPosition);
            return true;
        }
        internal bool ZoomWheel(int delta)
        {
            if(!inspecting) return false;
            if(!loupeVisible) return true;
            wheelRemainder+=delta;
            int steps=wheelRemainder/120;
            wheelRemainder%=120;
            // Native is displayed as 0x. A single wheel notch takes it to 2x.
            int index=loupeLevel==0 ? 0 : loupeLevel-1;
            index=Math.Max(0,Math.Min(4,index+steps));
            int next=index==0 ? 0 : index+1;
            if(next!=loupeLevel) {loupeLevel=next; PublishLoupe();}
            return true;
        }
        protected override IntPtr WndProc(IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled)
        {
            const int MouseMove=0x200, ButtonDown=0x201, ButtonUp=0x202, DoubleClick=0x203;
            const int Cursor=0x20, CancelMode=0x1F, CaptureChanged=0x215;
            if(msg==0x0005 && ClientResized!=null) ClientResized(); // WM_SIZE
            if(msg==MouseMove || msg==ButtonDown || msg==ButtonUp || msg==DoubleClick) {
                int x=unchecked((short)(lp.ToInt64() & 0xffff));
                int y=unchecked((short)((lp.ToInt64() >> 16) & 0xffff));
                if(inspecting && (msg==ButtonDown || msg==DoubleClick)) {handled=true; return IntPtr.Zero;}
                if(inspecting && (msg==MouseMove || msg==ButtonUp)) {
                    if(dragging) ChangePosition(PointerFraction(hwnd,x,y)+grabOffset);
                    if(msg==ButtonUp) {EndGesture(hwnd); if(wipeActive) UpdateHover(hwnd,x,y);}
                    else TrackLoupe(hwnd,x,y,false);
                    handled=true; return IntPtr.Zero;
                }
                if(msg==MouseMove && !dragging && wipeActive) UpdateHover(hwnd,x,y);
                bool grabDivider=(msg==ButtonDown || msg==DoubleClick) && HitDivider(hwnd,x,y);
                if((msg==ButtonDown || grabDivider) && TrackLoupe(hwnd,x,y,true)) {
                    dragging=grabDivider;
                    if(dragging) {
                        initialPosition=wipePosition;
                        grabOffset=wipePosition-PointerFraction(hwnd,x,y);
                        SetCursor(ResizeCursor);
                    }
                    SetGrip(dragging ? 2 : 0);
                    handled=true; return IntPtr.Zero;
                }
            }
            if(msg==0x20A && ZoomWheel(unchecked((short)(wp.ToInt64()>>16)))) {handled=true; return IntPtr.Zero;}
            if(msg==Cursor && wipeActive && (dragging || !inspecting)) {
                PixelPoint pt;
                if(dragging || (GetCursorPos(out pt) && ScreenToClient(hwnd,ref pt) && HitDivider(hwnd,pt.X,pt.Y))) {
                    SetCursor(ResizeCursor); handled=true; return new IntPtr(1);
                }
            }
            if(msg==CaptureChanged || msg==0x0008) EndGesture(hwnd); // WM_CAPTURECHANGED / WM_KILLFOCUS
            if(msg==0x02A3 && !dragging) SetGrip(0); // WM_MOUSELEAVE
            if(msg==CancelMode && EndPointerGesture(true)) {handled=true; return IntPtr.Zero;}
            if(msg==0x203 && DoubleClicked!=null) {DoubleClicked(); handled=true; return IntPtr.Zero;}
            if(msg==15 || msg==20) {
                Paint paint=new Paint(); IntPtr dc=msg==15?BeginPaint(hwnd,out paint):wp;
                if(!HasPresentedVideo) {
                    Rect area; GetClientRect(hwnd,out area); FillRect(dc,ref area,GetStockObject(4));
                }
                if(msg==15) EndPaint(hwnd,ref paint);
                handled=true; return new IntPtr(1);
            }
            return base.WndProc(hwnd,msg,wp,lp,ref handled);
        }
        internal void Clear() { HasPresentedVideo=false; if(Handle!=IntPtr.Zero) RedrawWindow(Handle,IntPtr.Zero,IntPtr.Zero,0x105); }
    }
}
