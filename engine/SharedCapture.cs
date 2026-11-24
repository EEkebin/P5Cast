using System;
using System.Drawing;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device=SharpDX.Direct3D11.Device;
using Device1=SharpDX.Direct3D11.Device1;

// WGC produces frames on system D3D11. The D3D11On12 rendering device reads the
// same GPU allocation after the capture device has copied the latest frame into it.
internal sealed class SharedCapture : ICaptureBridge
{
    public Device CaptureDevice {get;private set;}
    readonly Device1 reader;
    SharedFrame frame;

    public SharedCapture(Device renderDevice)
    {
        try {
            using(var dxgi=renderDevice.QueryInterface<SharpDX.DXGI.Device>())
            using(var adapter=dxgi.Adapter)
                CaptureDevice=new Device(adapter,DeviceCreationFlags.BgraSupport);
            reader=renderDevice.QueryInterface<Device1>();
        } catch {Dispose();throw;}
    }

    public bool Receive(Renderer renderer,Texture2D input,Rectangle crop,bool deduplicate)
    {
        var desc=input.Description;
        if(frame==null || !frame.Matches(desc)) {
            SharedFrame next;
            try { next=new SharedFrame(CaptureDevice,reader,desc); }
            catch(SharpDX.SharpDXException error) {
                throw new InvalidOperationException("Could not share the captured texture with Direct3D 12. Choose Direct3D 11 for window capture. "+error.Message,error);
            }
            if(frame!=null) frame.Dispose();
            frame=next;
        }
        return frame.Receive(CaptureDevice,renderer,input,crop,deduplicate);
    }

    public void Dispose()
    {
        if(frame!=null) {frame.Dispose();frame=null;}
        if(reader!=null) reader.Dispose();
        if(CaptureDevice!=null) {CaptureDevice.Dispose();CaptureDevice=null;}
    }

    sealed class SharedFrame : IDisposable
    {
        [DllImport("kernel32.dll",ExactSpelling=true)] static extern bool CloseHandle(IntPtr handle);
        readonly Texture2DDescription description;
        readonly Texture2D producer,consumer;
        readonly KeyedMutex writeLock,readLock;

        internal SharedFrame(Device device,Device1 reader,Texture2DDescription desc)
        {
            description=desc;
            desc.Usage=ResourceUsage.Default;
            desc.CpuAccessFlags=CpuAccessFlags.None;
            desc.BindFlags=BindFlags.RenderTarget|BindFlags.ShaderResource;
            desc.OptionFlags=ResourceOptionFlags.SharedNthandle|ResourceOptionFlags.SharedKeyedmutex;
            try {
                producer=new Texture2D(device,desc);
                using(var shared=producer.QueryInterface<SharpDX.DXGI.Resource1>()) {
                    var handle=shared.CreateSharedHandle(null,SharedResourceFlags.Read|SharedResourceFlags.Write,null);
                    try {consumer=reader.OpenSharedResource1<Texture2D>(handle);}
                    finally {CloseHandle(handle);}
                }
                writeLock=producer.QueryInterface<KeyedMutex>();
                readLock=consumer.QueryInterface<KeyedMutex>();
            } catch {Dispose();throw;}
        }

        internal bool Matches(Texture2DDescription desc)
        {
            return description.Width==desc.Width && description.Height==desc.Height && description.Format==desc.Format
                && description.ArraySize==desc.ArraySize && description.MipLevels==desc.MipLevels
                && description.SampleDescription.Equals(desc.SampleDescription);
        }

        static void Acquire(KeyedMutex mutex,long key)
        {
            // WAIT_TIMEOUT and WAIT_ABANDONED are positive return codes. Neither grants a usable lock.
            int code=mutex.Acquire(key,1000).Code;
            if(code!=0) throw new InvalidOperationException("Could not acquire the shared D3D12 capture texture (0x"+code.ToString("X8")+").");
        }

        internal bool Receive(Device capture,Renderer renderer,Texture2D input,Rectangle crop,bool deduplicate)
        {
            Acquire(writeLock,0);
            try {capture.ImmediateContext.CopyResource(input,producer);}
            finally {writeLock.Release(1);}
            Acquire(readLock,1);
            try {return renderer.Receive(consumer,crop,deduplicate);}
            finally {readLock.Release(0);}
        }

        public void Dispose()
        {
            if(readLock!=null) readLock.Dispose();
            if(writeLock!=null) writeLock.Dispose();
            if(consumer!=null) consumer.Dispose();
            if(producer!=null) producer.Dispose();
        }
    }
}
