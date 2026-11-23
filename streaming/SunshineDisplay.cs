using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Cadrivect.Streaming
{
    // Windows monitor identity must match Sunshine's pinned libdisplaydevice revision
    // 6e9722f89103320c948dc1199066c9e17a69e88a (WinApiLayer::getDeviceId).
    public static class SunshineDisplay
    {
        [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Explicit,Size=72)] struct DisplayPath
        {
            [FieldOffset(0)] public Luid SourceAdapter;
            [FieldOffset(8)] public uint SourceId;
            [FieldOffset(20)] public Luid TargetAdapter;
            [FieldOffset(28)] public uint TargetId;
        }
        [StructLayout(LayoutKind.Sequential,Size=64)] struct DisplayMode { public uint InfoType; }
        [StructLayout(LayoutKind.Sequential)] struct Header
        {
            public uint Type,Size;
            public Luid Adapter;
            public uint Id;
            public static Header Create<T>(uint type,Luid adapter,uint id)
            {
                return new Header { Type=type,Size=(uint)Marshal.SizeOf(typeof(T)),Adapter=adapter,Id=id };
            }
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Source
        {
            public Header Header;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Gdi;
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Target
        {
            public Header Header;
            public uint Flags,Technology;
            public ushort Manufacturer,Product;
            public uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Friendly;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Interface;
        }
        [StructLayout(LayoutKind.Sequential)] struct SetupEntry
        {
            public uint Size;
            public Guid ClassGuid;
            public uint Value;
            public IntPtr Reserved;
            public static SetupEntry Create() { return new SetupEntry { Size=(uint)Marshal.SizeOf(typeof(SetupEntry)) }; }
        }
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags,ref uint pathCount,[Out] DisplayPath[] paths,ref uint modeCount,[Out] DisplayMode[] modes,IntPtr topology);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetSource(ref Source source);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetTarget(ref Target target);
        [DllImport("setupapi.dll")] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid,IntPtr parent);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode)] static extern bool SetupDiOpenDeviceInterfaceW(IntPtr devices,string path,uint flags,ref SetupEntry detail);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devices,ref SetupEntry detail,IntPtr buffer,uint size,out uint needed,ref SetupEntry device);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceIdW(IntPtr devices,ref SetupEntry device,StringBuilder instance,uint size,out uint required);
        [DllImport("setupapi.dll")] static extern SafeRegistryHandle SetupDiOpenDevRegKey(IntPtr devices,ref SetupEntry device,uint scope,uint profile,uint type,int access);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

        static byte[] InstanceAndEdid(string path)
        {
            IntPtr devices=SetupDiCreateDeviceInfoList(IntPtr.Zero,IntPtr.Zero);
            if(devices==new IntPtr(-1)) return null;
            try
            {
                var iface=SetupEntry.Create();
                if(!SetupDiOpenDeviceInterfaceW(devices,path,0,ref iface)) return null;
                var device=SetupEntry.Create();
                uint needed;
                // The sizing call populates the associated SP_DEVINFO_DATA on ERROR_INSUFFICIENT_BUFFER.
                if(!SetupDiGetDeviceInterfaceDetailW(devices,ref iface,IntPtr.Zero,0,out needed,ref device) && Marshal.GetLastWin32Error()!=122) return null;
                var instance=new StringBuilder(512);
                if(!SetupDiGetDeviceInstanceIdW(devices,ref device,instance,(uint)instance.Capacity,out needed)) return null;
                byte[] edid;
                using(var handle=SetupDiOpenDevRegKey(devices,ref device,1,0,1,0x20019))
                {
                    if(handle==null || handle.IsInvalid) return null;
                    using(var key=RegistryKey.FromHandle(handle)) edid=key.GetValue("EDID") as byte[];
                }
                if(edid==null || edid.Length==0) return null;
                string id=instance.ToString();
                int first=id.IndexOf('&'), second=first<0 ? -1:id.IndexOf('&',first+1);
                int third=second<0 ? -1:id.IndexOf('&',second+1);
                if(third<0) return null;
                // Upstream includes the trailing UTF-16 NUL returned by SetupAPI.
                byte[] stable=Encoding.Unicode.GetBytes(id.Substring(0,second)+id.Substring(third)+"\0");
                byte[] identity=new byte[edid.Length+stable.Length];
                Buffer.BlockCopy(edid,0,identity,0,edid.Length);
                Buffer.BlockCopy(stable,0,identity,edid.Length,stable.Length);
                return identity;
            }
            finally { SetupDiDestroyDeviceInfoList(devices); }
        }

        static string DeviceId(string path)
        {
            var name=InstanceAndEdid(path) ?? Encoding.Unicode.GetBytes(path);
            var message=new byte[16+name.Length]; // UUIDv5 with the nil namespace
            Buffer.BlockCopy(name,0,message,16,name.Length);
            byte[] digest;
            using(var sha=SHA1.Create()) digest=sha.ComputeHash(message);
            digest[6]=(byte)((digest[6]&15)|0x50);
            digest[8]=(byte)((digest[8]&63)|0x80);
            string hex=BitConverter.ToString(digest,0,16).Replace("-","");
            return Guid.ParseExact(hex,"N").ToString("B");
        }

        public static string Resolve(string display)
        {
            for(int attempt=0;attempt<3;attempt++)
            {
                uint paths,modes;
                int status=GetDisplayConfigBufferSizes(2,out paths,out modes);
                if(status!=0) throw new Win32Exception(status);
                var active=new DisplayPath[paths];
                status=QueryDisplayConfig(2,ref paths,active,ref modes,new DisplayMode[modes],IntPtr.Zero);
                if(status==122) continue;
                if(status!=0) throw new Win32Exception(status);
                string resolved=null;
                for(int i=0;i<paths;i++)
                {
                    var p=active[i];
                    var source=new Source { Header=Header.Create<Source>(1,p.SourceAdapter,p.SourceId) };
                    if(GetSource(ref source)!=0 || !string.Equals(source.Gdi,display,StringComparison.OrdinalIgnoreCase)) continue;
                    var target=new Target { Header=Header.Create<Target>(2,p.TargetAdapter,p.TargetId) };
                    status=GetTarget(ref target);
                    if(status!=0) throw new Win32Exception(status);
                    if(string.IsNullOrEmpty(target.Interface)) throw new IOException("Windows did not return a monitor interface for "+display+".");
                    string id=DeviceId(target.Interface);
                    if(resolved!=null && resolved!=id) throw new IOException("The selected output is mirrored to multiple physical displays. Choose an extended display or use automatic sharing.");
                    resolved=id;
                }
                if(resolved==null) throw new IOException("The selected display is no longer active. Refresh the display selection and try again.");
                return resolved;
            }
            throw new IOException("The display configuration is changing. Refresh the display selection and try again.");
        }
    }
}
