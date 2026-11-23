using System;
using System.IO;
using SharpDX.D3DCompiler;
class ShaderBake {
    static void Main(string[] args) {
        string root = args[0];
        string code = File.ReadAllText(Path.Combine(root,"shaders","Player.hlsl"));
        code += Environment.NewLine+File.ReadAllText(Path.Combine(root,"shaders","Hdr.hlsl"));
        foreach (string name in new [] { "ffx_a.h", "ffx_fsr1.h" })
            code = code.Replace("#include \"" + name + "\"", File.ReadAllText(Path.Combine(root,"third_party",name)));
        foreach (string entry in new [] { "Fullscreen","Original","Easu","Rcas","Sharp","Motion","Interpolate","Changed","OpticalField","NgxVectors",
            "HdrToPq","HdrToLinear2020","HdrLinear2020ToPq","HdrSharpToPq","HdrMotionGuide" })
            using (var result = ShaderBytecode.Compile(code, entry, entry == "Fullscreen" ? "vs_5_0" : entry == "Changed" ? "cs_5_0" : "ps_5_0", ShaderFlags.OptimizationLevel3))
                using (var output = File.Create(Path.Combine(args[1],entry+".cso"))) result.Bytecode.Save(output);
    }
}

