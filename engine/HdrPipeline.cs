using System;
using System.Drawing;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

internal sealed partial class Renderer
{
    const Format HdrFloatFormat = Format.R16G16B16A16_Float;
    GpuImage hdrGuideCurrent, hdrGuidePrevious, hdrMixed, hdrFsrInput, hdrFsrScaled, hdrProcessed;
    GpuImage sdrPreviewPixels;
    int hdrGuideFrame = -1;

    void InitHdrShaders()
    {
        shaders.Add("SdrPreview",new PixelShader(Device,Shader("SdrPreview")));
        foreach (string name in new[] {"HdrToPq", "HdrToLinear2020", "HdrLinear2020ToPq", "HdrLinear2020ToScRgb", "HdrSharpToPq", "HdrMotionGuide", "HdrVsrReconstruct", "FrucHdrMidpoint","HdrNrGuide","HdrNrReconstruct","FitNrHdrIntermediate"})
            shaders.Add(name, new PixelShader(Device, Shader(name)));
    }

    void HdrScratch(ref GpuImage image, Size size, Format format)
    {
        if (image != null && image.Texture.Description.Width == size.Width && image.Texture.Description.Height == size.Height)
            return;
        if (image != null) image.Dispose();
        image = new GpuImage(Device,size.Width,size.Height,format,true);
    }

    void ReleaseHdr()
    {
        if(sdrPreviewPixels!=null) {sdrPreviewPixels.Dispose(); sdrPreviewPixels=null;}
        foreach (GpuImage image in new[] {hdrGuideCurrent,hdrGuidePrevious,hdrMixed,hdrFsrInput,hdrFsrScaled,hdrProcessed})
            if (image != null) image.Dispose();
        hdrGuideCurrent=hdrGuidePrevious=hdrMixed=hdrFsrInput=hdrFsrScaled=hdrProcessed=null;
        hdrGuideFrame=-1;
    }
    void DrawSdrPreview(Rectangle area)
    {
        // Take the finished picture before the divider and loupe. Only the
        // preview rectangle crosses this GPU copy, even while dragging it.
        if(sdrPreviewPixels==null || sdrPreviewPixels.Texture.Description.Width!=viewport.Width ||
            sdrPreviewPixels.Texture.Description.Height!=viewport.Height) {
            var resized=new GpuImage(Device,viewport.Width,viewport.Height,backBuffer.Description.Format,false);
            if(sdrPreviewPixels!=null) sdrPreviewPixels.Dispose();
            sdrPreviewPixels=resized;
        }
        var copy=new ResourceRegion(area.Left,area.Top,0,area.Right,area.Bottom,1);
        context.CopySubresourceRegion(backBuffer,0,copy,sdrPreviewPixels.Texture,0);
        Draw("SdrPreview",display,area,sdrPreviewPixels.Read,DirectWhiteNits);
    }

    void PrepareHdrMotion()
    {
        if (motionReady || !HasPrevious) return;
        HdrScratch(ref hdrGuideCurrent,srcSize,Format.B8G8R8A8_UNorm);
        HdrScratch(ref hdrGuidePrevious,srcSize,Format.B8G8R8A8_UNorm);
        var area=new Rectangle(Point.Empty,srcSize);
        if (hdrGuideFrame==Captured-1)
        {
            var old=hdrGuidePrevious;
            hdrGuidePrevious=hdrGuideCurrent;
            hdrGuideCurrent=old;
        }
        else Draw("HdrMotionGuide",hdrGuidePrevious.Write,area,prev.Read,1);
        Draw("HdrMotionGuide",hdrGuideCurrent.Write,area,curr.Read,1);
        hdrGuideFrame=Captured;
        PrepareMotion(hdrGuideCurrent,hdrGuidePrevious);
    }

    void DrawHdr(string mode,float phase,bool finishPrevious,Rectangle area,Rectangle? clip=null,ShaderResourceView sourceOverride=null)
    {
        var srcArea=new Rectangle(Point.Empty,srcSize);
        ShaderResourceView source=sourceOverride ?? (finishPrevious ? prev.Read : curr.Read);
        if (phase<1 && HasPrevious)
        {
            PrepareHdrMotion();
            HdrScratch(ref hdrMixed,srcSize,HdrFloatFormat);
            Draw("Interpolate",hdrMixed.Write,srcArea,curr.Read,phase,prev.Read,motion.Read);
            source=hdrMixed.Read;
            Generated++;
        }
        if(mode=="nis") {
            DrawNis(source,phase,area,clip);
            return;
        }
        if(mode=="original") {
            Draw("HdrToPq",display,area,source,1,clip:clip);
            return;
        }
        var outputArea=new Rectangle(Point.Empty,outSize);
        bool separateOutput=outSize!=area.Size;
        if (separateOutput) HdrScratch(ref hdrProcessed,outSize,HdrFloatFormat);
        var target=separateOutput ? hdrProcessed.Write : display;
        var targetArea=separateOutput ? outputArea : area;
        if (mode=="fsr")
        {
            // EASU/RCAS require a bounded perceptual signal. Store PQ BT.2020 in FP16 throughout both passes.
            HdrScratch(ref hdrFsrInput,srcSize,HdrFloatFormat);
            Draw("HdrToPq",hdrFsrInput.Write,srcArea,source,1);
            source=hdrFsrInput.Read;
            if(srcSize!=outSize)
            {
                HdrScratch(ref hdrFsrScaled,outSize,HdrFloatFormat);
                Draw("Easu",hdrFsrScaled.Write,outputArea,source,1);
                source=hdrFsrScaled.Read;
            }
            Draw("Rcas",target,targetArea,source,1);
        }
        else
        {
            if (mode!="sharp") throw new ArgumentException("The selected enhancement needs its own HDR rendering path.");
            Draw("HdrSharpToPq",target,targetArea,source,1);
        }
        if (separateOutput) Draw("Original",display,area,hdrProcessed.Read,1,clip:clip);
    }
}
