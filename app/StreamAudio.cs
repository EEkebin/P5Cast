using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Cadrivect
{
    // Core Audio's session gain is independent of both the device master and the
    // session mute flag. Only a session owned exclusively by the watched PID qualifies.
    internal sealed class StreamAudio : IDisposable
    {
        const int AllRenderEndpoints=0, ActiveEndpoint=1;
        const int DiscoveryMs=350;
        readonly int processId;
        readonly SafeWaitHandle process;
        readonly AutoResetEvent changed=new AutoResetEvent(false);
        readonly Thread worker;
        volatile int percent;
        volatile bool stopping;
        readonly Dictionary<string,Session> sessions=new Dictionary<string,Session>();
        readonly Dictionary<string,Gain> previous=new Dictionary<string,Gain>();

        internal StreamAudio(int pid,int initialPercent)
        {
            processId=pid;
            percent=Limit(initialPercent);
            // A retained process handle identifies this specific process even after
            // its numeric PID becomes eligible for reuse.
            process=OpenProcess(0x00100000,false,(uint)pid);
            worker=new Thread(Run);
            worker.IsBackground=true;
            worker.Name="P5Cast stream audio";
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }
        internal void SetPercent(int value)
        {
            percent=Limit(value);
            if(!stopping) changed.Set();
        }
        static int Limit(int value) {return Math.Max(0,Math.Min(100,value));}

        sealed class Gain
        {
            public float Baseline, Last;
            public bool Adjusted;
            public Gain(float baseline) {Baseline=Last=baseline;}
            public bool Matches(float value) {return Math.Abs(value-Last)<0.0001f;}
        }
        sealed class Session
        {
            public readonly object Control;
            public readonly IAudioSessionControl2 Info;
            public readonly ISimpleAudioVolume Volume;
            public readonly Gain Gain;
            public Session(object control,Gain gain)
            {
                Control=control;Info=(IAudioSessionControl2)control;Volume=(ISimpleAudioVolume)control;Gain=gain;
            }
            public void Apply(int amount)
            {
                float current;
                if(Volume.GetMasterVolume(out current)<0)return;
                // Windows mixer and source-app changes are the user's new baseline.
                if(!Gain.Matches(current)){Gain.Baseline=Gain.Last=current;Gain.Adjusted=false;}
                float wanted=Gain.Baseline*amount/100f;
                if(Math.Abs(current-wanted)<0.0001f)return;
                if(Volume.SetMasterVolume(wanted,IntPtr.Zero)>=0) {
                    Gain.Last=wanted;
                    Gain.Adjusted=Math.Abs(wanted-Gain.Baseline)>=0.0001f;
                }
            }
            public void Release()
            {
                try {
                    float current;
                    // A mixer edit after our last pass takes precedence on disconnect.
                    if(Gain.Adjusted && Volume.GetMasterVolume(out current)>=0 && Gain.Matches(current) &&
                        Volume.SetMasterVolume(Gain.Baseline,IntPtr.Zero)>=0) {
                        Gain.Last=Gain.Baseline;Gain.Adjusted=false;
                    }
                } finally {Free(Control);}
            }
        }

        void Run()
        {
            IMMDeviceEnumerator enumerator=null;
            var time=Stopwatch.StartNew();
            long nextScan=0;
            try {
                while(!stopping && !process.IsInvalid && WaitForSingleObject(process,0)==258) {
                    if(percent==100) {
                        ReleaseSessions();
                        previous.Clear();
                        nextScan=0;
                        if(!stopping) changed.WaitOne(1000);
                        continue;
                    }
                    try {
                        if(time.ElapsedMilliseconds>=nextScan) {
                            nextScan=time.ElapsedMilliseconds+DiscoveryMs;
                            if(enumerator==null) enumerator=(IMMDeviceEnumerator)new DeviceEnumerator();
                            Discover(enumerator);
                        }
                        foreach(var session in sessions.Values) {
                            uint pid;
                            try {
                                if(session.Info.GetProcessId(out pid)==0 && pid==processId)session.Apply(percent);
                            } catch(COMException) {
                                // A device may disconnect between discovery and a slider update.
                            }
                        }
                    } catch(COMException) {
                        Free(enumerator);enumerator=null;
                    }
                    if(!stopping) changed.WaitOne((int)Math.Max(1,nextScan-time.ElapsedMilliseconds));
                }
            } finally {
                ReleaseSessions();Free(enumerator);
            }
        }

        void ReleaseSessions()
        {
            foreach(var session in sessions.Values) {
                try {session.Release();} catch(COMException) {}
            }
            sessions.Clear();
        }

        void Discover(IMMDeviceEnumerator enumerator)
        {
            IMMDeviceCollection endpoints=null;
            try {
                if(enumerator.EnumAudioEndpoints(AllRenderEndpoints,ActiveEndpoint,out endpoints)<0)return;
                uint count;
                if(endpoints.GetCount(out count)<0)return;
                for(uint i=0;i<count && !stopping;i++) {
                    IMMDevice endpoint=null;
                    try {
                        if(endpoints.Item(i,out endpoint)>=0) DiscoverEndpoint(endpoint);
                    } catch(COMException) {
                        // Output devices may disappear while the enumerator is open.
                    } finally {Free(endpoint);}
                }
                var expired=new List<string>();
                foreach(var entry in sessions) {
                    int state;uint pid;
                    if(entry.Value.Info.GetState(out state)<0 || state==2 ||
                        entry.Value.Info.GetProcessId(out pid)!=0 || pid!=processId) expired.Add(entry.Key);
                }
                foreach(string id in expired) {
                    var session=sessions[id];sessions.Remove(id);
                    try {session.Release();} catch(COMException) {}
                }
            } finally {Free(endpoints);}
        }
        void DiscoverEndpoint(IMMDevice endpoint)
        {
            object manager=null;
            IAudioSessionEnumerator list=null;
            try {
                Guid iid=typeof(IAudioSessionManager2).GUID;
                if(endpoint.Activate(ref iid,1,IntPtr.Zero,out manager)<0)return;
                if(((IAudioSessionManager2)manager).GetSessionEnumerator(out list)<0)return;
                int count;
                if(list.GetCount(out count)<0)return;
                for(int i=0;i<count && !stopping;i++) {
                    object control=null;
                    try {
                        if(list.GetSession(i,out control)<0)continue;
                        var info=control as IAudioSessionControl2;
                        var volume=control as ISimpleAudioVolume;
                        uint pid;int state;string instance,id;float current;
                        // GetProcessId returns a positive success code for multi-process
                        // sessions. Such a session could also carry an unrelated app.
                        if(info==null || volume==null || info.GetProcessId(out pid)!=0 || pid!=processId ||
                            info.GetState(out state)<0 || state==2 ||
                            info.GetSessionInstanceIdentifier(out instance)<0 || string.IsNullOrEmpty(instance) || sessions.ContainsKey(instance) ||
                            info.GetSessionIdentifier(out id)<0 || string.IsNullOrEmpty(id) || volume.GetMasterVolume(out current)<0)
                            continue;
                        Gain prior;
                        // A newly created WASAPI session may inherit the last adjusted
                        // Windows mixer value when the prior session disappears.
                        var gain=previous.TryGetValue(id,out prior) && prior.Adjusted && prior.Matches(current)
                            ? new Gain(prior.Baseline){Last=current,Adjusted=true} : new Gain(current);
                        previous[id]=gain;
                        sessions.Add(instance,new Session(control,gain));
                        control=null; // The session owns this COM reference until release.
                    } finally {Free(control);}
                }
            } finally {Free(list);Free(manager);}
        }
        static void Free(object value) {if(value!=null) Marshal.ReleaseComObject(value);}
        public void Dispose()
        {
            if(stopping)return;
            stopping=true;changed.Set();worker.Join();
            changed.Dispose();process.Dispose();
        }

        [DllImport("kernel32.dll",SetLastError=true)]
        static extern SafeWaitHandle OpenProcess(uint access,bool inherit,uint id);
        [DllImport("kernel32.dll")]
        static extern uint WaitForSingleObject(SafeWaitHandle handle,uint timeout);

        [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class DeviceEnumerator {}
        [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow,int mask,out IMMDeviceCollection devices);
        }
        [ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index,out IMMDevice endpoint);
        }
        [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr activationParams,[MarshalAs(UnmanagedType.IUnknown)]out object result);
        }
        [ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr session,uint flags,out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr session,uint flags,out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }
        [ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index,[MarshalAs(UnmanagedType.IUnknown)]out object control);
        }
        [ComImport,Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name,IntPtr context);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path,IntPtr context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping,IntPtr context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)]out string identifier);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)]out string identifier);
            [PreserveSig] int GetProcessId(out uint pid);
        }
        [ComImport,Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float value,IntPtr context);
            [PreserveSig] int GetMasterVolume(out float value);
        }
    }

}
