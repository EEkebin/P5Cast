using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// Setup-only reader for the full WEIGHTS_HT data resource of the pinned NVIDIA package.
// Windows maps the signed file as data. Its executable entry points are never called.
public static class OpenNrModelData
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    static extern IntPtr FindResource(IntPtr module, string name, IntPtr type);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);

    sealed class Tensor
    {
        public string Name, Parameter;
        public int Block, Layer, Offset, Length;
    }

    static byte[] ReadWeights(string dll)
    {
        var module=LoadLibraryEx(dll,IntPtr.Zero,0x40 | 0x20); // AS_DATAFILE_EXCLUSIVE | AS_IMAGE_RESOURCE
        if(module==IntPtr.Zero) throw new IOException("Could not open the NVIDIA model resource.");
        try {
            var resource=FindResource(module,"WEIGHTS_HT",new IntPtr(10)); // RT_RCDATA
            if(resource==IntPtr.Zero || SizeofResource(module,resource)!=147695410)
                throw new InvalidDataException("The compatible full neural rendering model is missing.");
            var data=LockResource(LoadResource(module,resource));
            if(data==IntPtr.Zero) throw new IOException("Could not read the NVIDIA model resource.");
            var bytes=new byte[147695410];
            Marshal.Copy(data,bytes,0,bytes.Length);
            using(var hash=SHA256.Create()) {
                if(Hex(hash.ComputeHash(bytes))!="836f445d06ecd2e59bb9f17b84b91c143396fd76ccda1c9dc7fe81d5edd548f4")
                    throw new InvalidDataException("The NVIDIA full model checksum did not match.");
            }
            return bytes;
        } finally {FreeLibrary(module);}
    }

    static string Hex(byte[] data) {return BitConverter.ToString(data).Replace("-","").ToLowerInvariant();}
    static List<Tensor> ReadLayout(byte[] weights)
    {
        var tensors=new List<Tensor>();
        using(var reader=new BinaryReader(new MemoryStream(weights,false))) {
            var stream=reader.BaseStream;
            if(reader.ReadInt64()!=stream.Length) throw new InvalidDataException("Invalid model length.");
            while(stream.Position<stream.Length) {
                int nameLength=checked((int)reader.ReadUInt64());
                if(nameLength<1 || nameLength>128) throw new InvalidDataException("Invalid model tensor name.");
                string name=Encoding.ASCII.GetString(reader.ReadBytes(nameLength));
                var match=Regex.Match(name,@"^block(\d+)\.layer(\d+)\.(layer|blend_scale)$");
                if(!match.Success) throw new InvalidDataException("Unexpected model tensor.");
                long record=reader.ReadInt64(), repeated=reader.ReadInt64();
                int length=checked((int)reader.ReadUInt64());
                if(record!=repeated || record!=length+40L || reader.ReadInt32()!=1 || stream.Position+length+20>stream.Length)
                    throw new InvalidDataException("Invalid model tensor length.");
                tensors.Add(new Tensor {Name=name,Block=int.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture),
                    Layer=int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture),Parameter=match.Groups[3].Value,
                    Offset=(int)stream.Position,Length=length});
                stream.Position+=length+20;
            }
        }
        if(tensors.Count!=153 || tensors.Select(t=>t.Block).Distinct().Count()!=71)
            throw new InvalidDataException("Unsupported model graph.");
        return tensors;
    }

    public static void WriteDirectory(string signedDll,string directory)
    {
        byte[] data=ReadWeights(signedDll);
        var tensors=ReadLayout(data);
        var stages=new List<object>();
        var entries=new List<object>();
        int[] lastBlocks={4,8,14,22,30,38,47,55,61,65,70};
        string modelPath=Directory.CreateDirectory(Path.Combine(directory,"model")).FullName;
        int first=0;
        foreach(int last in lastBlocks) {
            string id="stage-"+stages.Count.ToString(CultureInfo.InvariantCulture);
            string file=id+".bin", path=Path.Combine(modelPath,file);
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                foreach(var t in tensors.Where(t=>t.Block>=first && t.Block<=last)
                    .OrderBy(t=>t.Block).ThenBy(t=>t.Layer).ThenBy(t=>t.Name,StringComparer.Ordinal)) {
                    entries.Add(new {name=t.Name,block=t.Block,layer=t.Layer,parameter=t.Parameter,
                        stage=id,stageOffset=output.Position,byteLength=t.Length});
                    output.Write(data,t.Offset,t.Length);
                }
            }
            using(var output=File.OpenRead(path))
            using(var hash=SHA256.Create())
                stages.Add(new {id,file,packedByteLength=output.Length,sha256=Hex(hash.ComputeHash(output))});
            first=last+1;
        }
        var manifest=new {totals=new {blockCount=71,tensorCount=entries.Count,packedByteLength=tensors.Sum(t=>t.Length)},
            stages,tensors=entries};
        File.WriteAllText(Path.Combine(directory,"manifest.json"),new JavaScriptSerializer().Serialize(manifest),new UTF8Encoding(false));
    }
}
