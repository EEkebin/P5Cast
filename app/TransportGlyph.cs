using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Cadrivect
{
    // All three icons share one contour. Opening the center and shortening its sweep
    // turns the solid play or stop silhouette into a rotating arc.
    internal sealed class TransportGlyph : FrameworkElement
    {
        internal enum Shape { Play, Spinner, Stop }
        const double Turn=2*Math.PI, MorphSeconds=.24;
        const int Segments=96;
        static readonly Geometry SpinnerShape=Contour(1,0,0);
        readonly RotateTransform spinTransform=new RotateTransform();
        double spinner,square,startSpinner,startSquare,angle,startTime,lastTime;
        bool ticking;
        internal Shape State {get; private set;}
        internal bool IsMorphing {get; private set;}

        internal TransportGlyph()
        {
            Width=22; Height=22; IsHitTestVisible=false;
            IsVisibleChanged+=delegate {RefreshAnimation();};
            Loaded+=delegate {
                SystemParameters.StaticPropertyChanged+=MotionSettingChanged;
                RefreshAnimation();
            };
            Unloaded+=delegate {
                SystemParameters.StaticPropertyChanged-=MotionSettingChanged;
                SetTicking(false);
            };
        }

        static double Now {get {return (double)Stopwatch.GetTimestamp()/Stopwatch.Frequency;}}
        void MotionSettingChanged(object sender,System.ComponentModel.PropertyChangedEventArgs e)
        {
            if(e.PropertyName=="ClientAreaAnimation") RefreshAnimation();
        }
        internal void SetState(Shape next)
        {
            if(next==State) return;
            double now=Now;
            if(ticking) Advance(now);
            startSpinner=spinner; startSquare=square;
            startTime=lastTime=now; State=next; IsMorphing=true;
            RefreshAnimation();
        }
        void RefreshAnimation()
        {
            bool motion=IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation;
            if(!motion) {
                spinner=State==Shape.Spinner ? 1 : 0;
                square=State==Shape.Stop ? 1 : 0;
                angle=0; IsMorphing=false;
            }
            SetTicking(motion && (IsMorphing || State==Shape.Spinner));
            InvalidateVisual();
        }
        void SetTicking(bool run)
        {
            if(ticking==run) return;
            ticking=run;
            if(run) {lastTime=Now; CompositionTarget.Rendering+=Frame;}
            else CompositionTarget.Rendering-=Frame;
        }
        void Advance(double now)
        {
            if(IsMorphing) {
                double t=Math.Min(1,(now-startTime)/MorphSeconds);
                double ease=t*t*(3-2*t);
                spinner=startSpinner+((State==Shape.Spinner ? 1 : 0)-startSpinner)*ease;
                square=startSquare+((State==Shape.Stop ? 1 : 0)-startSquare)*ease;
                IsMorphing=t<1;
            }
            angle=spinner>0 ? (angle+(now-lastTime)*Turn*1.15*spinner)%Turn : 0;
            lastTime=now;
        }
        void Frame(object sender,EventArgs e)
        {
            Advance(Now);
            InvalidateVisual();
            if(!IsMorphing && State!=Shape.Spinner) SetTicking(false);
        }

        static Geometry Contour(double spinner,double square,double angle)
        {
            double start=angle-Math.PI/2, sweep=Turn*(1-.22*spinner);
            double inner=5.55*spinner;
            var shape=new StreamGeometry();
            using(var path=shape.Open()) {
                Point first=new Point(),last=new Point();
                for(int i=0;i<=Segments;i++) {
                    double a=start+sweep*i/Segments, x=Math.Cos(a), y=Math.Sin(a);
                    // Distance along this ray to each silhouette at its resting size.
                    double triangle=4.35/Math.Max(-x,.5*x+.8660254038*Math.Abs(y));
                    double stop=6.2/Math.Pow(Math.Pow(x,8)+Math.Pow(y,8),.125);
                    double radius=triangle*(1-spinner-square)+8*spinner+stop*square;
                    last=new Point(x*radius,y*radius);
                    if(i==0) {first=last; path.BeginFigure(first,true,true);}
                    else path.LineTo(last,false,false);
                }
                if(inner>0) {
                    double end=start+sweep;
                    var tip=new Point(inner*Math.Cos(end),inner*Math.Sin(end));
                    RoundCap(path,last,tip,end,spinner);
                    for(int i=Segments-1;i>=0;i--) {
                        double a=start+sweep*i/Segments;
                        path.LineTo(new Point(inner*Math.Cos(a),inner*Math.Sin(a)),false,false);
                    }
                    RoundCap(path,new Point(inner*Math.Cos(start),inner*Math.Sin(start)),first,start,-spinner);
                }
            }
            shape.Freeze();
            return shape;
        }
        static void RoundCap(StreamGeometryContext path,Point from,Point to,double angle,double amount)
        {
            double bend=(to-from).Length*(2.0/3)*amount;
            var tangent=new Vector(-Math.Sin(angle)*bend,Math.Cos(angle)*bend);
            path.BezierTo(from+tangent,to+tangent,to,false,false);
        }
        protected override void OnRender(DrawingContext dc)
        {
            dc.PushTransform(new TranslateTransform(ActualWidth/2,ActualHeight/2));
            if(spinner==1) {
                spinTransform.Angle=angle*360/Turn;
                dc.PushTransform(spinTransform);
                dc.DrawGeometry(TextElement.GetForeground(this),null,SpinnerShape);
                dc.Pop();
            } else dc.DrawGeometry(TextElement.GetForeground(this),null,Contour(spinner,square,angle));
            dc.Pop();
        }
    }
}
