using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using Buffer = SharpDX.Direct3D11.Buffer;

internal sealed partial class Renderer
{
    [StructLayout(LayoutKind.Sequential)] struct LoupeInfo
    {
        public float CenterX,CenterY,CopyX,CopyY,Radius,Zoom,UiScale,LabelRow,LabelX,LabelY,Pad1,Pad2;
    }
    bool loupeActive;
    float loupeX,loupeY,loupeScale;
    int loupeLevel;
    GpuImage loupePixels;
    ShaderResourceView loupeLabels;
    Buffer loupeConstants;

    public void SetLoupe(bool active,float x,float y,int level,float scale)
    {
        loupeActive=active;
        loupeX=x; loupeY=y;
        loupeLevel=level;
        loupeScale=scale;
    }

    // One antialiased coverage atlas. The shader applies SDR or HDR UI colors after sampling it.
    ShaderResourceView CreateLoupeLabels()
    {
        const int width=100,rowHeight=40;
        string[] labels={"Native","2×","3×","4×","5×"};
        using(var bitmap=new Bitmap(width,rowHeight*labels.Length,PixelFormat.Format32bppArgb))
        {
            using(var graphics=Graphics.FromImage(bitmap))
            using(var font=new Font("Segoe UI",24,FontStyle.Bold,GraphicsUnit.Pixel))
            using(var alignment=new StringFormat {Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center}) {
                graphics.Clear(System.Drawing.Color.Black);
                graphics.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                for(int i=0;i<labels.Length;i++)
                    graphics.DrawString(labels[i],font,Brushes.White,new RectangleF(0,i*rowHeight,width,rowHeight),alignment);
            }
            var data=bitmap.LockBits(new Rectangle(0,0,width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try {
                var desc=new Texture2DDescription {Width=width,Height=bitmap.Height,ArraySize=1,MipLevels=1,Format=Format.B8G8R8A8_UNorm,
                    SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Immutable,BindFlags=BindFlags.ShaderResource};
                using(var texture=new Texture2D(Device,desc,new DataRectangle(data.Scan0,data.Stride)))
                    return new ShaderResourceView(Device,texture);
            } finally { bitmap.UnlockBits(data); }
        }
    }

    void DrawLoupe()
    {
        int x=Math.Min(screenSize.Width-1,(int)Math.Floor(loupeX*screenSize.Width));
        int y=Math.Min(screenSize.Height-1,(int)Math.Floor(loupeY*screenSize.Height));
        var pane=viewport.Contains(x,y) ? viewport : originalViewport;
        if(!pane.Contains(x,y)) return;
        int radius=Math.Min((int)Math.Round(86*loupeScale),Math.Min(pane.Width,pane.Height)/2);
        if(radius<2) return;
        int diameter=2*radius+1,zoom=Math.Max(1,loupeLevel);
        var area=Rectangle.Intersect(pane,new Rectangle(x-radius,y-radius,diameter,diameter));
        // Only the source footprint is copied. Keeping a fixed scratch capacity avoids
        // texture allocation when the pointer grazes an edge or the wheel changes level.
        int reach=(radius+zoom-1)/zoom;
        var copy=Rectangle.Intersect(pane,new Rectangle(x-reach,y-reach,reach*2+1,reach*2+1));
        var format=backBuffer.Description.Format;
        if(loupePixels==null || loupePixels.Texture.Description.Width!=diameter || loupePixels.Texture.Description.Format!=format) {
            if(loupePixels!=null) loupePixels.Dispose();
            loupePixels=new GpuImage(Device,diameter,diameter,format,false);
        }
        if(loupeLabels==null) loupeLabels=CreateLoupeLabels();
        if(loupeConstants==null) loupeConstants=new Buffer(Device,48,ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);
        context.CopySubresourceRegion(backBuffer,0,new ResourceRegion(copy.Left,copy.Top,0,copy.Right,copy.Bottom,1),loupePixels.Texture,0);
        float labelX=Math.Max(pane.Left+25*loupeScale,Math.Min(pane.Right-25*loupeScale,x+.5f));
        float labelOffset=radius*.64f;
        var info=new LoupeInfo { CenterX=x+.5f,CenterY=y+.5f,CopyX=copy.Left,CopyY=copy.Top,Radius=radius,Zoom=zoom,
            UiScale=loupeScale,LabelRow=zoom-1,LabelX=labelX,LabelY=y+.5f+(y+labelOffset+10*loupeScale<pane.Bottom ? labelOffset : -labelOffset) };
        context.UpdateSubresource(ref info,loupeConstants);
        context.PixelShader.SetConstantBuffer(1,loupeConstants);
        context.OutputMerger.SetBlendState(handleBlend);
        Draw("InspectionLoupe",display,area,loupePixels.Read,HdrEnabled ? 1 : 0,loupeLabels);
        context.OutputMerger.SetBlendState(null);
    }
}
