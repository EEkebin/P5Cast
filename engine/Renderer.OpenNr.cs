using System;
using System.Drawing;
using SharpDX.DXGI;

internal sealed partial class Renderer
{
    NeuralRendering openNrSourceModel;
    GpuImage openNrGuide,openNrVectors,openNrPrediction,openNrStrokeMask;
    GpuImage openNrCurrent,openNrPrevious;
    int openNrSourceFrame=-1;
    bool HasPreviousOpenNrSource {get;set;}
    bool OpenNrSourceModelChanged {get {return openNrSourceModel!=null && openNrSourceModel.OpenModelChanged;}}

    void ResetOpenNrHistory()
    {
        openNrSourceFrame=-1;
        HasPreviousOpenNrSource=false;
    }

    void ReleaseOpenNrSource()
    {
        if(openNrSourceModel!=null) {openNrSourceModel.Dispose();openNrSourceModel=null;}
        foreach(var image in new[]{openNrGuide,openNrVectors,openNrPrediction,openNrStrokeMask,openNrCurrent,openNrPrevious})
            if(image!=null) image.Dispose();
        openNrGuide=openNrVectors=openNrPrediction=openNrStrokeMask=openNrCurrent=openNrPrevious=null;
        ResetOpenNrHistory();
    }

    // The caller keeps curr/prev on the original captured pictures for this pass.
    // Every downstream effect receives the same source size and color space it
    // normally consumes, including linear scRGB for HDR capture.
    void PrepareOpenNrSource()
    {
        if(!HasVendorBridge(Backend,"open-nr")) throw new InvalidOperationException(NeuralRendering.OpenBackendReason);
        if(OpenNrSourceModelChanged) ReleaseOpenNrSource();
        if(openNrSourceFrame==Captured) return;
        try {
            if(openNrSourceModel==null) {
                int w=srcSize.Width,h=srcSize.Height;
                // Use BGRA for an 8-bit captured source (e.g. NVIDIA optical flow).
                Format format=HdrEnabled ? HdrFloatFormat : Format.B8G8R8A8_UNorm;
                openNrCurrent=new GpuImage(Device,w,h,format,true);
                openNrPrevious=new GpuImage(Device,w,h,format,true);
                openNrGuide=new GpuImage(Device,w,h,HdrFloatFormat,true);
                openNrVectors=new GpuImage(Device,w,h,Format.R16G16_Float,true);
                openNrStrokeMask=new GpuImage(Device,w,h,Format.R8_UNorm,true);
                openNrPrediction=new GpuImage(Device,w,h,HdrFloatFormat,true,true);
                openNrSourceModel=new NeuralRendering(Device,w,h,true);
                RestoreDrawState();
            }
            var area=new Rectangle(Point.Empty,srcSize);
            HasPreviousOpenNrSource=HasPrevious && openNrSourceFrame==Captured-1;
            var swap=openNrPrevious;openNrPrevious=openNrCurrent;openNrCurrent=swap;
            Draw(HdrEnabled?"HdrNrGuide":"Original",openNrGuide.Write,area,curr.Read,1);
            PrepareVendorGuidance(openNrVectors,openNrStrokeMask,HasPreviousOpenNrSource);
            // This provider consumes color and motion only.
            openNrSourceModel.Evaluate(openNrGuide.Texture,openNrVectors.Texture,null,openNrPrediction.Texture,!HasPreviousOpenNrSource);
            RestoreDrawState();
            if(HdrEnabled) Draw("HdrNrReconstruct",openNrCurrent.Write,area,curr.Read,0,openNrPrediction.Read,openNrStrokeMask.Read);
            else Draw("PreserveNgxStrokes",openNrCurrent.Write,area,openNrPrediction.Read,0,openNrGuide.Read,openNrStrokeMask.Read);
            openNrSourceFrame=Captured;
            OpenNrEvaluations++;
        } catch {
            ReleaseOpenNrSource();
            throw;
        }
    }
}
