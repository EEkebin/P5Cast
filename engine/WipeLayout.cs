using System;
using System.Drawing;

// Native presentation and mouse input use the same physical image rectangle.
internal struct WipeLayout
{
    public static bool IsSplit(string view) {return view=="wipe" || view=="hdr-sdr";}
    public readonly int Center;
    public readonly Rectangle Rail,Handle,Original;
    readonly int minimum,maximum;

    public WipeLayout(Rectangle picture,bool horizontal,double position,double scale=1)
    {
        int axisStart=horizontal ? picture.Top : picture.Left;
        int axis=horizontal ? picture.Height : picture.Width;
        int cross=horizontal ? picture.Width : picture.Height;
        int handleAlong=Math.Min(axis,Math.Max(1,(int)Math.Round(3*scale)));
        int handleAcross=Math.Min(cross,Math.Max(1,(int)Math.Round(36*scale)));
        int railWidth=Math.Min(axis,Math.Max(1,(int)Math.Round(scale)));
        int margin=Math.Min((axis-handleAlong)/2,(int)Math.Ceiling(8*scale));
        minimum=axisStart+handleAlong/2+margin;
        maximum=axisStart+axis-(handleAlong-handleAlong/2)-margin;
        Center=minimum+(int)Math.Round((maximum-minimum)*Math.Max(0,Math.Min(1,position)));
        int grip=Center-handleAlong/2;
        int rail=Center-railWidth/2;
        int crossOffset=(cross-handleAcross)/2;
        if(horizontal) {
            Handle=new Rectangle(picture.Left+crossOffset,grip,handleAcross,handleAlong);
            Rail=new Rectangle(picture.Left,rail,cross,railWidth);
            Original=new Rectangle(picture.Left,picture.Top,cross,Center-axisStart);
        } else {
            Handle=new Rectangle(grip,picture.Top+crossOffset,handleAlong,handleAcross);
            Rail=new Rectangle(rail,picture.Top,railWidth,cross);
            Original=new Rectangle(picture.Left,picture.Top,Center-axisStart,cross);
        }
    }

    // Unclamped so a grab slightly off-center retains its pointer offset.
    public double PositionAt(double axisCoordinate)
    {
        return maximum==minimum ? 0.5 : (axisCoordinate-minimum)/(maximum-minimum);
    }
}
