using System;
using System.IO;
using SharpDX.D3DCompiler;
class ShaderBake {
    static void BakeNis(string root,string destination) {
        string folder=Path.Combine(root,"third_party","nis");
        string code=File.ReadAllText(Path.Combine(folder,"NIS_Main.hlsl")).Replace("#include \"NIS_Scaler.h\"",File.ReadAllText(Path.Combine(folder,"NIS_Scaler.h")));
        foreach(bool hdr in new[]{false,true}) foreach(bool scale in new[]{false,true}) {
            // Explicitly match the host dispatch dimensions and full-precision SM5 shader on all backends.
            string options="#define NIS_SCALER "+(scale?1:0)+"\n#define NIS_HDR_MODE "+(hdr?2:0)+
                "\n#define NIS_BLOCK_WIDTH 32\n#define NIS_BLOCK_HEIGHT "+(scale?24:32)+"\n#define NIS_THREAD_GROUP_SIZE 128\n#define NIS_VIEWPORT_SUPPORT 1\n#define NIS_CLAMP_OUTPUT 1\n";
            string name=(scale?"NVScaler":"NVSharpen")+(hdr?"PQ":"SDR");
            using(var result=ShaderBytecode.Compile(options+code,"main","cs_5_0",ShaderFlags.OptimizationLevel3))
            using(var output=File.Create(Path.Combine(destination,name+".cso"))) result.Bytecode.Save(output);
        }
    }
    static void Main(string[] args) {
        BakeNis(args[0],args[1]);
        string root = args[0];
        string code = File.ReadAllText(Path.Combine(root,"shaders","Player.hlsl"));
        code += Environment.NewLine+File.ReadAllText(Path.Combine(root,"shaders","Hdr.hlsl"));
        code += Environment.NewLine+File.ReadAllText(Path.Combine(root,"shaders","Loupe.hlsl"));
        code += Environment.NewLine+File.ReadAllText(Path.Combine(root,"shaders","Direct.hlsl"));
        code += Environment.NewLine+File.ReadAllText(Path.Combine(root,"shaders","FrucHdr.hlsl"));
        code += Environment.NewLine+File.ReadAllText(Path.Combine(root,"shaders","Demo.hlsl"));
        foreach (string name in new [] { "ffx_a.h", "ffx_fsr1.h" })
            code = code.Replace("#include \"" + name + "\"", File.ReadAllText(Path.Combine(root,"third_party",name)));
        foreach (string entry in new [] { "Fullscreen","Original","Easu","Rcas","Sharp","Motion","Interpolate","Changed","OpticalField","NgxVectors","PreserveNgxStrokes",
            "HdrToPq","HdrToLinear2020","HdrLinear2020ToPq","HdrLinear2020ToScRgb","FitNgxFrame","HdrSharpToPq","HdrMotionGuide","HdrVsrReconstruct","FrucHdrMidpoint","FitNrIntermediate","HdrNrGuide","HdrNrReconstruct","FitNrHdrIntermediate","DirectPlanes","SdrPreview","DemoScene","WipeDivider","WipeHandle","InspectionLoupe" })
            using (var result = ShaderBytecode.Compile(code, entry, entry == "Fullscreen" ? "vs_5_0" : entry == "Changed" ? "cs_5_0" : "ps_5_0", ShaderFlags.OptimizationLevel3))
                using (var output = File.Create(Path.Combine(args[1],entry+".cso"))) result.Bytecode.Save(output);
    }
}

