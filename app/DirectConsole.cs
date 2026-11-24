using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Cadrivect
{
    internal sealed class DirectConsole
    {
        static readonly ConditionalWeakTable<Process,RestControl> restControls=new ConditionalWeakTable<Process,RestControl>();
        sealed class RestControl : IDisposable
        {
            public readonly string Pipe;
            readonly string prefix;
            readonly EventWaitHandle[] events=new EventWaitHandle[3];
            MemoryMappedFile volumeMap;
            MemoryMappedViewAccessor volume;
            Task pending;
            public RestControl(string pipe,int pid,int? initialVolume)
            {
                Pipe=pipe;
                prefix="Local\\P5Cast.Rest."+pid.ToString(System.Globalization.CultureInfo.InvariantCulture)+"."+pipe+".";
                var acl=new EventWaitHandleSecurity();
                acl.SetAccessRuleProtection(true,false);
                using(var user=WindowsIdentity.GetCurrent())
                    acl.AddAccessRule(new EventWaitHandleAccessRule(user.User,EventWaitHandleRights.FullControl,AccessControlType.Allow));
                try {
                    string[] names={"request","accepted","rejected"};
                    for(int i=0;i<names.Length;i++) {
                        bool created;
                        events[i]=new EventWaitHandle(false,EventResetMode.ManualReset,prefix+names[i],out created,acl);
                        if(!created) throw new IOException("The console session controls are already in use.");
                    }
                    if(initialVolume.HasValue) {
                        var security=new MemoryMappedFileSecurity();
                        security.SetAccessRuleProtection(true,false);
                        using(var user=WindowsIdentity.GetCurrent())
                            security.AddAccessRule(new AccessRule<MemoryMappedFileRights>(user.User,MemoryMappedFileRights.FullControl,AccessControlType.Allow));
                        volumeMap=MemoryMappedFile.CreateNew(prefix+"volume",sizeof(int),MemoryMappedFileAccess.ReadWrite,
                            MemoryMappedFileOptions.None,security,HandleInheritability.None);
                        volume=volumeMap.CreateViewAccessor(0,sizeof(int),MemoryMappedFileAccess.ReadWrite);
                        SetVolume(initialVolume.Value);
                    }
                } catch {Dispose(); throw;}
            }
            public void SetVolume(int percent)
            {
                lock(this) {
                    if(volume!=null)volume.Write(0,Math.Max(0,Math.Min(100,percent)));
                }
            }
            public Task Request(Process owned)
            {
                lock(this) {
                    if(pending==null || pending.IsFaulted) {
                        if(owned.HasExited) throw new InvalidOperationException("The Direct session has ended.");
                        // A confirmed acceptance stays latched for the life of this session.
                        // A fresh request may, however, recover after a rejection or timeout.
                        events[2].Reset();
                        pending=Task.Run((Func<Task>)Send);
                    }
                    return pending;
                }
            }
            async Task Send()
            {
                // Duplicate handles stay valid if the helper exits immediately after acknowledging.
                using(var request=EventWaitHandle.OpenExisting(prefix+"request",EventWaitHandleRights.Modify))
                using(var accepted=EventWaitHandle.OpenExisting(prefix+"accepted",EventWaitHandleRights.Synchronize))
                using(var rejected=EventWaitHandle.OpenExisting(prefix+"rejected",EventWaitHandleRights.Synchronize)) {
                    request.Set();
                    try {
                        int result=WaitHandle.WaitAny(new WaitHandle[]{accepted,rejected},4000);
                        if(result==WaitHandle.WaitTimeout) throw new TimeoutException("The Direct session did not acknowledge the rest request.");
                        if(result!=0) throw new InvalidOperationException("The Direct session could not queue the PS5 rest request.");
                    } finally {request.Reset();}
                }
                // libchiaki's acknowledgement confirms local queueing. Keep the session alive while its control thread sends.
                await Task.Delay(1500).ConfigureAwait(false);
            }
            public void Dispose()
            {
                lock(this) {
                    if(volume!=null){volume.Dispose();volume=null;}
                    if(volumeMap!=null){volumeMap.Dispose();volumeMap=null;}
                }
                foreach(var handle in events) if(handle!=null) handle.Dispose();
            }
        }
        internal static void SetVolume(Process owned,int percent)
        {
            RestControl control;
            if(owned!=null && restControls.TryGetValue(owned,out control))control.SetVolume(percent);
        }
        public static Task RequestRestAsync(string pipe,Process owned)
        {
            RestControl control;
            if(owned==null || !restControls.TryGetValue(owned,out control) || pipe!=control.Pipe)
                throw new InvalidOperationException("The Direct session is no longer active.");
            return control.Request(owned);
        }
        public static Task<string> ReadGameTitleAsync(string address)
        {
            return DirectGameTitle.ReadAsync(address);
        }
        public string Name { get; set; }
        public string Mac { get; set; }
        public string Address { get; set; }
        public string ProtectedKeys { get; set; }
        public string AccountId { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore] public string RegistryPath { get; set; }
        public override string ToString() {return Name;}
        static string DirectoryPath {get {return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"),"Cadrivect","consoles");}}
        static string CorePath {get {return Path.Combine(Program.Root,"engine","bin","direct","CadrivectDirect.exe");}}
        static byte[] QtBytes(object value)
        {
            var raw=value as byte[];
            const string prefix="@ByteArray(";
            if(raw!=null) {
                // QSettings stores a UTF-16 wrapped QByteArray in REG_BINARY if its payload contains NUL.
                if(raw.Length<(prefix.Length+1)*2 || raw.Length%2!=0) return raw;
                for(int i=0;i<prefix.Length;i++) if(raw[i*2]!=prefix[i] || raw[i*2+1]!=0) return raw;
                int end=raw.Length-2;
                if(raw[end]==0 && raw[end+1]==0) end-=2;
                if(end<prefix.Length*2 || raw[end]!=')' || raw[end+1]!=0) return raw;
                var bytes=new byte[end/2-prefix.Length];
                for(int i=0;i<bytes.Length;i++) bytes[i]=raw[(prefix.Length+i)*2];
                Clear(raw);
                return bytes;
            }
            string text=value as string;
            if(text==null || !text.StartsWith(prefix,StringComparison.Ordinal) || !text.EndsWith(")",StringComparison.Ordinal)) return new byte[0];
            var decoded=new byte[text.Length-prefix.Length-1];
            for(int i=0;i<decoded.Length;i++) decoded[i]=(byte)text[prefix.Length+i];
            return decoded;
        }
        static string Hex(byte[] bytes) {return BitConverter.ToString(bytes).Replace("-","");}
        static void Clear(byte[] bytes) {if(bytes!=null) Array.Clear(bytes,0,bytes.Length);}
        public static List<DirectConsole> List()
        {
            return Registrations().GroupBy(c=>c.Mac,StringComparer.OrdinalIgnoreCase).Select(g=>g.First())
                .OrderBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static string KnownAccountId()
        {
            return SelectAccountId(Registrations().Select(row=>row.AccountId));
        }
        static string SelectAccountId(IEnumerable<string> candidates)
        {
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(string value in candidates) {
                if(string.IsNullOrWhiteSpace(value) || value.Length>32) continue;
                try {
                    var id=Convert.FromBase64String(value);
                    if(id.Length==8) ids.Add(Convert.ToBase64String(id));
                } catch(FormatException) {}
                if(ids.Count>1) return "";
            }
            return ids.Count==1 ? ids.First() : "";
        }
        static List<DirectConsole> Registrations(string registryRoot=@"Software\Chiaki")
        {
            var result=new List<DirectConsole>();
            if(Directory.Exists(DirectoryPath)) foreach(var path in Directory.GetFiles(DirectoryPath,"*.json")) {
                try {
                    var row=new JavaScriptSerializer().Deserialize<DirectConsole>(File.ReadAllText(path));
                    if(row!=null && row.Mac!=null && row.Mac.Length==12 && !string.IsNullOrEmpty(row.ProtectedKeys)) result.Add(row);
                } catch(ArgumentException) {} catch(IOException) {} catch(InvalidOperationException) {} catch(UnauthorizedAccessException) {}
            }
            // Import existing pairing metadata on demand. Read keys only when starting that console.
            using(var org=Registry.CurrentUser.OpenSubKey(registryRoot)) {
                if(org!=null) foreach(string profile in org.GetSubKeyNames().OrderBy(n=>n=="Chiaki"?0:1)) {
                    string prefix=registryRoot+"\\"+profile;
                    using(var registered=Registry.CurrentUser.OpenSubKey(prefix+@"\registered_hosts"))
                    using(var settings=Registry.CurrentUser.OpenSubKey(prefix+@"\settings"))
                    using(var manual=Registry.CurrentUser.OpenSubKey(prefix+@"\manual_hosts")) {
                        if(registered==null) continue;
                        string account=settings==null ? null : settings.GetValue("psn_account_id") as string;
                        var addresses=new Dictionary<string,string>();
                        if(manual!=null) foreach(string id in manual.GetSubKeyNames()) using(var r=manual.OpenSubKey(id)) {
                            if(r!=null) addresses[Hex(QtBytes(r.GetValue("registered_mac")))]=Convert.ToString(r.GetValue("host"));
                        }
                        foreach(string id in registered.GetSubKeyNames()) using(var r=registered.OpenSubKey(id)) {
                            int target;
                            if(r==null || !int.TryParse(Convert.ToString(r.GetValue("target"),System.Globalization.CultureInfo.InvariantCulture),
                                System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out target) || target<1000000) continue;
                            string mac=Hex(QtBytes(r.GetValue("server_mac")));
                            if(mac.Length!=12) continue;
                            string host; addresses.TryGetValue(mac,out host);
                            result.Add(new DirectConsole {Mac=mac,Name=Convert.ToString(r.GetValue("server_nickname")),Address=host,
                                AccountId=account,RegistryPath=prefix+@"\registered_hosts\"+id});
                        }
                    }
                }
            }
            return result;
        }
        internal byte[] Keys()
        {
            if(RegistryPath==null) return ProtectedData.Unprotect(Convert.FromBase64String(ProtectedKeys),null,DataProtectionScope.CurrentUser);
            using(var key=Registry.CurrentUser.OpenSubKey(RegistryPath)) {
                if(key==null) throw new InvalidOperationException("The PS5 pairing has been removed.");
                byte[] registration=null, transport=null;
                try {
                    registration=QtBytes(key.GetValue("rp_regist_key")); transport=QtBytes(key.GetValue("rp_key"));
                    if(registration.Length!=16 || transport.Length!=16) throw new InvalidDataException("Pair this console again to refresh its saved registration.");
                    string pin=Convert.ToString(key.GetValue("console_pin"));
                    bool usePin=pin.Length==4 && pin.All(n=>n>='0' && n<='9');
                    var payload=new byte[usePin?36:32];
                    Array.Copy(registration,payload,16); Array.Copy(transport,0,payload,16,16);
                    if(usePin) Encoding.ASCII.GetBytes(pin,0,4,payload,32);
                    return payload;
                } finally {Clear(registration); Clear(transport);}
            }
        }
        public static string Host(string raw)
        {
            string s=(raw??"").Trim();
            if(Uri.CheckHostName(s)==UriHostNameType.Unknown || s.Length>253 || s.StartsWith("-"))
                throw new ArgumentException("Enter the PS5’s LAN address or DNS name.");
            return s;
        }
        static Process Begin(string args,bool captureOutput,bool streamStats=false)
        {
            if(!File.Exists(CorePath)) throw new FileNotFoundException("The direct streaming engine is missing. Extract the complete P5Cast release.");
            var info=new ProcessStartInfo(CorePath,args) {WorkingDirectory=Path.GetDirectoryName(CorePath),UseShellExecute=false,
                CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=captureOutput};
            if(streamStats) info.EnvironmentVariables["P5CAST_DIRECT_STATS_VERSION"]="3";
            return Process.Start(info);
        }
        static void WriteSecrets(Process p,byte[] secret)
        {
            using(var input=p.StandardInput.BaseStream) input.Write(secret,0,secret.Length);
        }
        static void StopHelper(Process p)
        {
            try {if(!p.HasExited) {p.Kill(); p.WaitForExit(2000);}} catch(InvalidOperationException) {}
            catch(System.ComponentModel.Win32Exception) {if(!p.HasExited) throw;}
        }
        internal static readonly int[] StreamHeights={1080,720,540,360};

        public Process Connect(string pipe,string host,int codec,int fps,int videoKbps=0,int streamHeight=1080)
        {
            return Connect(pipe,host,codec,fps,videoKbps,streamHeight,null);
        }

        internal Process Connect(string pipe,string host,int codec,int fps,int videoKbps,int streamHeight,int? volume)
        {
            if(Array.IndexOf(StreamHeights,streamHeight)<0)
                throw new ArgumentOutOfRangeException("streamHeight","Choose a supported PS5 stream resolution.");
            if(videoKbps!=0 && (videoKbps<2000 || videoKbps>100000))
                throw new ArgumentOutOfRangeException("videoKbps","Choose a video bitrate from 2 to 100 Mbps or the console profile default.");
            if(string.IsNullOrEmpty(pipe) || pipe.Length>120 || pipe.Any(c=>!(c>='A' && c<='Z' || c>='a' && c<='z' || c>='0' && c<='9' || c=='-' || c=='_')))
                throw new ArgumentException("Invalid Direct session pipe.");
            string address=Host(host);
            string arguments=pipe+" "+address+" "+codec+" "+fps+" "+videoKbps.ToString(System.Globalization.CultureInfo.InvariantCulture)+" "+streamHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)+(volume.HasValue?" --session-control":" --rest-control");
            byte[] secret=Keys(); Process p=null;
            try {
                if(secret.Length!=32 && secret.Length!=36) throw new InvalidDataException("This registration needs to be paired again.");
                p=Begin(arguments,true,true);
                var control=new RestControl(pipe,p.Id,volume);
                try {
                    restControls.Add(p,control);
                    p.Exited+=delegate {control.Dispose();};
                    DirectGameTitle.Track(address,p);
                    p.EnableRaisingEvents=true;
                    WriteSecrets(p,secret); return p;
                } catch {control.Dispose(); throw;}
            }
            catch {if(p!=null){StopHelper(p); p.Dispose();} throw;}
            finally {Clear(secret);}
        }
        public static Task<DirectConsole> Pair(string host,string account,string pin,CancellationToken cancel=default(CancellationToken))
        {
            cancel.ThrowIfCancellationRequested();
            string address=Host(host);
            byte[] accountId;
            try {accountId=Convert.FromBase64String((account??"").Trim());} catch(FormatException) {throw new ArgumentException("Enter your eight-byte PSN Account ID in Base64.");}
            pin=(pin??"").Replace(" ","");
            if(accountId.Length!=8 || pin.Length!=8 || pin.Any(c=>c<'0'||c>'9')) throw new ArgumentException("Enter the PSN Account ID and the eight-digit Link Device code.");
            string canonicalId=Convert.ToBase64String(accountId);
            var request=new byte[16];
            Array.Copy(accountId,request,8); Encoding.ASCII.GetBytes(pin,0,8,request,8);
            Clear(accountId);
            return Task.Run(delegate {
                var data=new byte[74]; var keys=new byte[32];
                try {
                    cancel.ThrowIfCancellationRequested();
                    using(var p=Begin("--register "+address,true)) {
                        try {
                            WriteSecrets(p,request); Clear(request);
                            var wait=Stopwatch.StartNew();
                            while(!p.WaitForExit(100)) {
                                cancel.ThrowIfCancellationRequested();
                                if(wait.ElapsedMilliseconds>=60000) throw new TimeoutException("Pairing timed out. Get a fresh Link Device code and try again.");
                            }
                            cancel.ThrowIfCancellationRequested();
                        } catch {StopHelper(p); throw;}
                        using(var reader=new BinaryReader(p.StandardOutput.BaseStream)) {
                            int length=0; while(length<data.Length) {int n=reader.Read(data,length,data.Length-length); if(n==0) break; length+=n;}
                            if(p.ExitCode!=0 || length!=data.Length || Encoding.ASCII.GetString(data,0,4)!="CDR1")
                                throw new InvalidOperationException("The PS5 did not accept registration. Check its address, account and Link Device code.");
                        }
                    }
                    Array.Copy(data,42,keys,0,32);
                    var item=new DirectConsole {Name=Encoding.UTF8.GetString(data,10,32).TrimEnd('\0'),Mac=Hex(data.Skip(4).Take(6).ToArray()),Address=address,
                        AccountId=canonicalId,ProtectedKeys=Convert.ToBase64String(ProtectedData.Protect(keys,null,DataProtectionScope.CurrentUser))};
                    Directory.CreateDirectory(DirectoryPath);
                    string file=Path.Combine(DirectoryPath,item.Mac+".json"), temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try {
                        File.WriteAllText(temp,new JavaScriptSerializer().Serialize(item));
                        cancel.ThrowIfCancellationRequested();
                        try {File.Move(temp,file);} catch(IOException) {if(!File.Exists(file)) throw; File.Replace(temp,file,null);}
                    } finally {if(File.Exists(temp)) File.Delete(temp);}
                    return item;
                } finally {Clear(request); Clear(data); Clear(keys);}
            });
        }
    }
}
