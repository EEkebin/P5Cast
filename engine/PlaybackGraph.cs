using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed class PlaybackGraph : Control
{
    internal static readonly Color InputColor=Color.FromArgb(149,196,252), PresentColor=Color.FromArgb(147,232,208),
        GeneratedColor=Color.FromArgb(213,172,238), TargetColor=Color.FromArgb(242,209,145);
    const int Duration=30;
    readonly float[][] fps={new float[Duration],new float[Duration],new float[Duration]};
    int count,start,target;
    public PlaybackGraph()
    {
        DoubleBuffered=true; BackColor=Color.FromArgb(14,23,27); ForeColor=Color.FromArgb(182,201,204);
        AccessibleRole=AccessibleRole.Chart; AccessibleName="Recent frame rates"; TabStop=false;
    }
    public void Sample(float input,float presented,float generated,int requested)
    {
        int next=(start+count)%Duration;
        if(count==Duration) start=(start+1)%Duration; else count++;
        fps[0][next]=input; fps[1][next]=presented; fps[2][next]=generated; target=requested;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics; float scale=g.DpiX/96f;
        float right=Math.Max(2,Width-43*scale), top=5*scale, bottom=Math.Max(top+1,Height-9*scale);
        float max=Math.Max(30,target);
        foreach(var series in fps) foreach(float sample in series) max=Math.Max(max,sample);
        max=(float)Math.Ceiling(max/30)*30;
        using(var grid=new Pen(Color.FromArgb(47,67,72))) {
            for(int i=0;i<3;i++) { float y=top+(bottom-top)*i/2; g.DrawLine(grid,0,y,right,y); }
        }
        if(target>0) using(var desired=new Pen(TargetColor,scale)) {
            desired.DashStyle=DashStyle.Dash;
            float y=bottom-(bottom-top)*target/max; g.DrawLine(desired,0,y,right,y);
        }
        g.SmoothingMode=SmoothingMode.AntiAlias;
        for(int n=0;n<fps.Length;n++) {
            using(var line=new Pen(n==0 ? InputColor : n==1 ? PresentColor:GeneratedColor,1.8f*scale)) {
                PointF previous=PointF.Empty;
                for(int i=0;i<count;i++) {
                    var point=new PointF(right*(Duration-count+i)/(Duration-1),bottom-(bottom-top)*fps[n][(start+i)%Duration]/max);
                    if(i>0) g.DrawLine(line,previous,point); else g.DrawEllipse(line,point.X,point.Y,1,1);
                    previous=point;
                }
            }
        }
        using(var brush=new SolidBrush(ForeColor)) {
            g.DrawString(max.ToString("0"),Font,brush,right+4*scale,0);
            g.DrawString("0",Font,brush,right+4*scale,bottom-Font.Height+2*scale);
        }
    }
}

internal sealed class TimingBar : Control
{
    double milliseconds=-1;
    readonly Color fill;
    public TimingBar(Color fill) { this.fill=fill; DoubleBuffered=true; TabStop=false; }
    public void Sample(double value) { milliseconds=value; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(40,57,63));
        if(milliseconds<0 || double.IsNaN(milliseconds)) return;
        using(var brush=new SolidBrush(fill))
            e.Graphics.FillRectangle(brush,0,0,(float)(Width*Math.Min(milliseconds/100.0,1)),Height);
    }
}
