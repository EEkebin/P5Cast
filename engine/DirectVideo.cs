using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer=SharpDX.Direct3D11.Buffer;
using Rectangle=System.Drawing.Rectangle;

internal sealed class DirectVideo : IDisposable
{
    internal sealed class AudioTelemetry
    {
        public readonly double QueueMs=-1, TargetMs=-1, RttMs=-1, BitrateKbps=-1;
        public readonly long Underruns=-1, SampleRate=-1, Channels=-1;
        public readonly long VideoFramesLost=-1, AudioConcealments=-1, AudioQueueTrims=-1, SlowVideoHandoffs=-1;
        public readonly double AudioFrameGapMaxMs=-1, AudioCallbackGapMaxMs=-1;
        public AudioTelemetry() { }
        public AudioTelemetry(BinaryReader input,uint version) {
            QueueMs=Scaled(input,1000); Underruns=Value(input); RttMs=Scaled(input,1000); TargetMs=Scaled(input,1000);
            if(version>=0x32564443) {
                // CDV2 appends measured Opus payload bits/s, negotiated sample rate and channel count.
                BitrateKbps=Scaled(input,1000); SampleRate=Value(input); Channels=Value(input);
                if(SampleRate==0) SampleRate=-1;
                if(Channels==0) Channels=-1;
            }
            if(version==0x33564443) {
                VideoFramesLost=Value(input); AudioConcealments=Value(input); AudioQueueTrims=Value(input); SlowVideoHandoffs=Value(input);
                AudioFrameGapMaxMs=Scaled(input,1000); AudioCallbackGapMaxMs=Scaled(input,1000);
            }
        }
        static long Value(BinaryReader input) { uint raw=input.ReadUInt32(); return raw==uint.MaxValue ? -1L:raw; }
        static double Scaled(BinaryReader input,double divisor) { long raw=Value(input); return raw<0 ? -1:raw/divisor; }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Picture {
        public IntPtr Y,U,V;
        public int YStride,UStride,VStride,Width,Height,Depth,FullRange,Primaries,Transfer,Matrix;
    }
    const string Dll="Cadrivect.Decoder.dll";
    [DllImport(Dll, CharSet=CharSet.Unicode,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
    static extern IntPtr CreateDecoder(string codec,string util,int kind);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern int SubmitPacket(IntPtr decoder,byte[] bytes,int length);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern int NextPicture(IntPtr decoder,out IntPtr frame,out Picture picture);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern void FreePicture(IntPtr decoder,IntPtr frame);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern void DestroyDecoder(IntPtr decoder);
    readonly object gate=new object();
    GameCadence cadence;
    readonly NamedPipeServerStream pipe;
    readonly Thread worker;
    readonly string runtime;
    IntPtr decoder,pending;
    Picture picture;
    double arrival;
    volatile bool closed,stopping;
    volatile string error="",codec="";
    volatile AudioTelemetry audio=new AudioTelemetry();
    int decodeUs=-1,rateKbps;
    const int CorruptAccessUnit=-3;
    internal const string RecoveryError="The Direct stream could not recover a decoded picture for 30 seconds after damaged video.";
    int codecId,decodedFrames,replacedFrames,corruptUnits;
    long corruptSince=-1;
    public int CorruptUnits { get {return Volatile.Read(ref corruptUnits);} }
    public bool RecoveryTimedOut {
        get {
            long start=Volatile.Read(ref corruptSince);
            return start>=0 && Stopwatch.GetTimestamp()-start>=30L*Stopwatch.Frequency;
        }
    }
    public int DecodedFrames { get {return Volatile.Read(ref decodedFrames);} }
    public int ReplacedFrames { get {return Volatile.Read(ref replacedFrames);} }
    public bool EstimateGameFps {
        get {return Volatile.Read(ref cadence)!=null;}
        set {GameCadence.SetEnabled(ref cadence,value);}
    }
    public double GameFps {
        get {
            var active=Volatile.Read(ref cadence);
            return active==null ? -1 : active.GameFps(Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency);
        }
    }
    public bool Closed { get { lock(gate) return closed && pending==IntPtr.Zero; } }
    public string Error { get { return error; } }
    public string CodecName { get { return codec; } }
    public Size StreamSize { get { lock(gate) return new Size(picture.Width,picture.Height); } }
    public double BitrateMbps { get { return Volatile.Read(ref rateKbps)/1000.0; } }
    public double DecodeMs { get {return Millis(ref decodeUs);} }
    public AudioTelemetry Audio { get {return audio;} }
    public double AudioQueueMs { get {return audio.QueueMs;} }
    public double AudioTargetMs { get {return audio.TargetMs;} }
    public double NetworkRttMs { get {return audio.RttMs;} }
    public long AudioUnderruns { get {return audio.Underruns;} }
    static double Millis(ref int us) {int value=Volatile.Read(ref us); return value<0 ? -1 : value/1000.0;}
    public DirectVideo(string name,string runtimeDir)
    {
        runtime=Path.GetFullPath(runtimeDir);
        var acl=new PipeSecurity();
        acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
        pipe=new NamedPipeServerStream(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536,acl);
        worker=new Thread(Decode) {IsBackground=true,Name="PS5 video decoder"};
        worker.Start();
    }
    static void ReadRemaining(BinaryReader reader,byte[] buffer,int offset,int length)
    {
        while(offset<length) {
            int count=reader.Read(buffer,offset,length-offset);
            if(count==0) throw new EndOfStreamException();
            offset+=count;
        }
    }
    void DropCorruptUnit(long timestamp)
    {
        var active=Volatile.Read(ref cadence);
        if(active!=null) active.Reset();
        Interlocked.Increment(ref corruptUnits);
        if(corruptSince<0) Volatile.Write(ref corruptSince,timestamp);
    }
    void Decode()
    {
        try {
            pipe.EndWaitForConnection(pipe.BeginWaitForConnection(null,null));
            var reader=new BinaryReader(pipe);
            uint version=reader.ReadUInt32();
            if(version!=0x31564443 && version!=0x32564443 && version!=0x33564443) throw new InvalidDataException("Unexpected direct video stream.");
            codecId=reader.ReadInt32();
            if(codecId<1 || codecId>3) throw new InvalidDataException("Unknown console codec.");
            codec=codecId==1 ? "H.264" : codecId==2 ? "HEVC" : "HEVC HDR";
            decoder=CreateDecoder(Path.Combine(runtime,"avcodec-63.dll"),Path.Combine(runtime,"avutil-61.dll"),codecId);
            if(decoder==IntPtr.Zero) throw new InvalidOperationException("The compatible FFmpeg decoder could not be loaded.");
            var buffer=new byte[65536]; var clock=Stopwatch.StartNew(); long bytes=0;
            while(!stopping) {
                // Only EOF between records is a clean disconnect. Pipes may split a four-byte prefix.
                int prefix=reader.Read(buffer,0,sizeof(int));
                if(prefix==0) {
                    if(corruptSince>=0) throw new InvalidDataException("The Direct stream ended before video recovered from a damaged frame.");
                    break;
                }
                if(RecoveryTimedOut) throw new InvalidDataException(RecoveryError);
                ReadRemaining(reader,buffer,prefix,sizeof(int));
                int length=BitConverter.ToInt32(buffer,0);
                if(length==0) {
                    audio=new AudioTelemetry(reader,version);
                    continue;
                }
                if(length<0 || length>16*1024*1024) throw new InvalidDataException("Invalid console video packet size.");
                if(length>buffer.Length) Array.Resize(ref buffer,length);
                ReadRemaining(reader,buffer,0,length);
                long start=Stopwatch.GetTimestamp();
                bytes+=length;
                if(clock.ElapsedMilliseconds>=1000) {
                    Volatile.Write(ref rateKbps,(int)(8*bytes/Math.Max(1,clock.ElapsedMilliseconds)));
                    clock.Restart(); bytes=0;
                }
                int submitted=SubmitPacket(decoder,buffer,length);
                if(submitted==CorruptAccessUnit) {DropCorruptUnit(start); continue;}
                if(submitted<0) throw new InvalidDataException("The decoder rejected a console video packet.");
                while(!stopping) {
                    IntPtr frame; Picture next;
                    int result=NextPicture(decoder,out frame,out next);
                    if(result==0) break;
                    if(result==CorruptAccessUnit) {DropCorruptUnit(start); break;}
                    if(result<0) throw new InvalidDataException("Unsupported or damaged console video frame.");
                    if(corruptSince>=0) Volatile.Write(ref corruptSince,-1);
                    long decodedAt=Stopwatch.GetTimestamp();
                    Volatile.Write(ref decodeUs,(int)((decodedAt-start)*1000000.0/Stopwatch.Frequency));
                    var active=Volatile.Read(ref cadence);
                    if(active!=null) active.Observe(next.Y,next.YStride,next.Width,next.Height,next.Depth,decodedAt*1000.0/Stopwatch.Frequency);
                    lock(gate) {
                        Interlocked.Increment(ref decodedFrames);
                        if(pending!=IntPtr.Zero) {
                            FreePicture(decoder,pending);
                            Interlocked.Increment(ref replacedFrames);
                        }
                        pending=frame; picture=next; arrival=start*1000.0/Stopwatch.Frequency;
                    }
                }
            }
        } catch(EndOfStreamException) { if(!stopping) error="The Direct stream ended in the middle of a record."; }
        catch(Exception ex) { if(!stopping) error=ex.Message; }
        finally { closed=true; }
    }
    readonly DirectPlanes planes=new DirectPlanes();
    public bool Read(Renderer renderer,bool deduplicate,out double timestamp)
    {
        Picture p; IntPtr frame;
        lock(gate) {frame=pending; pending=IntPtr.Zero; p=picture; timestamp=arrival;}
        if(frame==IntPtr.Zero) return false;
        try {return planes.Upload(renderer,p,deduplicate,codecId);}
        finally {FreePicture(decoder,frame);}
    }
    public void Dispose()
    {
        stopping=true; pipe.Dispose(); worker.Join();
        if(pending!=IntPtr.Zero) FreePicture(decoder,pending);
        if(decoder!=IntPtr.Zero) DestroyDecoder(decoder);
        planes.Dispose();
    }
}

internal sealed class DirectPlanes : IDisposable
{
    readonly GpuImage[] channels=new GpuImage[3];
    GpuImage output;
    Buffer color;
    PixelShader shader;
    [StructLayout(LayoutKind.Sequential)]
    struct Conversion { public float Max,Black,White,Neutral,Range,Kr,Kb,Pq,OutputHdr,WhiteNits,Hlg,Wide; }
    public bool Upload(Renderer renderer,DirectVideo.Picture p,bool deduplicate,int codec)
    {
        var device=renderer.Device; var ctx=device.ImmediateContext;
        var format=p.Depth==10 ? Format.R16_UNorm:Format.R8_UNorm;
        var outputFormat=renderer.HdrEnabled ? Format.R16G16B16A16_Float:Format.B8G8R8A8_UNorm;
        if(channels[0]==null || channels[0].Texture.Description.Width!=p.Width || channels[0].Texture.Description.Height!=p.Height ||
            channels[0].Texture.Description.Format!=format || output.Texture.Description.Format!=outputFormat) {
            Dispose();
            for(int i=0;i<3;++i) channels[i]=new GpuImage(device,i==0?p.Width:(p.Width+1)/2,i==0?p.Height:(p.Height+1)/2,format,false);
            output=new GpuImage(device,p.Width,p.Height,outputFormat,true);
            shader=new PixelShader(device,Renderer.DirectShader());
            color=new Buffer(device,48,ResourceUsage.Default,BindFlags.ConstantBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);
        }
        ctx.UpdateSubresource(new DataBox(p.Y,p.YStride,0),channels[0].Texture);
        ctx.UpdateSubresource(new DataBox(p.U,p.UStride,0),channels[1].Texture);
        ctx.UpdateSubresource(new DataBox(p.V,p.VStride,0),channels[2].Texture);
        float m=p.Depth==10?4:1;
        bool pq=p.Transfer==16 || (p.Transfer==2 && codec==3);
        bool wide=p.Primaries==9 || p.Matrix==9 || (p.Primaries==2 && pq);
        bool sd=p.Matrix==5 || p.Matrix==6;
        var settings=new Conversion { Max=p.Depth==10?65535:255, Black=p.FullRange!=0?0:16*m,
            White=p.FullRange!=0?(p.Depth==10?1023:255):235*m,Neutral=128*m,Range=p.FullRange!=0?(p.Depth==10?1023:255):224*m,
            Kr=wide?0.2627f:sd?0.299f:0.2126f,Kb=wide?0.0593f:sd?0.114f:0.0722f,Pq=pq?1:0,Hlg=p.Transfer==18?1:0,
            Wide=wide?1:0,OutputHdr=renderer.HdrEnabled?1:0,WhiteNits=renderer.DirectWhiteNits };
        ctx.UpdateSubresource(ref settings,color);
        ctx.PixelShader.SetConstantBuffer(1,color);
        ctx.Rasterizer.SetViewport(0,0,p.Width,p.Height);
        ctx.OutputMerger.SetRenderTargets(output.Write);
        for(int i=0;i<3;++i) ctx.PixelShader.SetShaderResource(i,channels[i].Read);
        ctx.PixelShader.Set(shader); ctx.Draw(3,0);
        for(int i=0;i<3;++i) ctx.PixelShader.SetShaderResource(i,null);
        ctx.OutputMerger.ResetTargets();
        return renderer.Receive(output.Texture,new Rectangle(0,0,p.Width,p.Height),deduplicate);
    }
    public void Dispose()
    {
        foreach(var plane in channels) if(plane!=null) plane.Dispose();
        Array.Clear(channels,0,channels.Length);
        if(output!=null) output.Dispose(); output=null;
        if(shader!=null) shader.Dispose(); shader=null;
        if(color!=null) color.Dispose(); color=null;
    }
}
internal sealed partial class Renderer
{
    internal static byte[] DirectShader() {return Shader("DirectPlanes");}
    internal float DirectWhiteNits {get {return Math.Max(80,outputInfo.SdrWhiteNits);}}
}
