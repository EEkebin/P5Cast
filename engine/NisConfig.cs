using System;
using System.IO;

internal static class NisConfig
{
    const int CoefficientsPerFilter=64*8;

    // The cbuffer in NIS_Main.hlsl is 112 bytes. The native NISConfig struct
    // adds trailing alignment padding that the GPU does not read.
    internal static byte[] Constants(int inputWidth,int inputHeight,int outputWidth,int outputHeight,bool pq,float sharpness=0.5f)
    {
        if(inputWidth<=0 || inputHeight<=0 || outputWidth<inputWidth || outputHeight<inputHeight ||
            (long)outputWidth>inputWidth*2L || (long)outputHeight>inputHeight*2L)
            throw new ArgumentException("NIS needs source and output dimensions between native size and 2x enlargement per pass.");
        if(float.IsNaN(sharpness) || float.IsInfinity(sharpness)) throw new ArgumentOutOfRangeException("sharpness");

        // Full-frame viewport specialization of NVIDIA's NVScalerUpdateConfig.
        float slider=Math.Max(0f,Math.Min(1f,sharpness))-0.5f;
        float minScale=slider>=0 ? 1.25f : 1f;
        float maxScale=slider>=0 ? 1.25f : 1.75f;
        float minContrast=pq?1.5f:2f, maxContrast=pq?5f:10f;
        float startY=pq?0.35f:0.45f, endY=pq?0.55f:0.9f;
        float strengthMin=Math.Max(0f,0.4f+slider*minScale*(pq?1.1f:1.2f));
        float strengthMax=(pq?2.2f:1.6f)+slider*maxScale*1.8f;
        float limitMin=Math.Max(pq?0.06f:0.1f,(pq?0.1f:0.14f)+slider*minScale*(pq?0.28f:0.32f));
        float limitMax=(pq?0.6f:0.5f)+slider*minScale*0.6f;
        var bytes=new byte[112];
        using(var writer=new BinaryWriter(new MemoryStream(bytes))) {
            writer.Write(2f*1127f/1024f); writer.Write((pq?32f:64f)/1024f);
            writer.Write(minContrast); writer.Write(1f/(maxContrast-minContrast));
            writer.Write(1f); writer.Write(1f/255f); writer.Write(startY); writer.Write(1f/(endY-startY));
            writer.Write(strengthMin); writer.Write(strengthMax-strengthMin);
            writer.Write(limitMin); writer.Write(limitMax-limitMin);
            writer.Write((float)inputWidth/outputWidth); writer.Write((float)inputHeight/outputHeight);
            writer.Write(1f/outputWidth); writer.Write(1f/outputHeight);
            writer.Write(1f/inputWidth); writer.Write(1f/inputHeight);
            writer.Write(0); writer.Write(0); writer.Write(inputWidth); writer.Write(inputHeight);
            writer.Write(0); writer.Write(0); writer.Write(outputWidth); writer.Write(outputHeight);
            // The remaining two reserved floats stay zero.
        }
        return bytes;
    }

    internal static float[] Coefficients(bool usm)
    {
        using(var data=typeof(NisConfig).Assembly.GetManifestResourceStream("NisCoefficients.bin")) {
            if(data==null || data.Length!=2L*CoefficientsPerFilter*sizeof(float))
                throw new InvalidDataException("The NVIDIA Image Scaling filter coefficients are missing or incomplete.");
            data.Position=usm ? CoefficientsPerFilter*sizeof(float) : 0;
            using(var reader=new BinaryReader(data)) {
                var coefficients=new float[CoefficientsPerFilter];
                for(int i=0;i<coefficients.Length;i++) coefficients[i]=reader.ReadSingle();
                return coefficients;
            }
        }
    }
}
