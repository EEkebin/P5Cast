using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Cadrivect
{
    internal sealed class DirectConsole
    {
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
        static List<DirectConsole> Registrations()
        {
            var result=new List<DirectConsole>();
            if(Directory.Exists(DirectoryPath)) foreach(var path in Directory.GetFiles(DirectoryPath,"*.json")) {
                try {
                    var row=new JavaScriptSerializer().Deserialize<DirectConsole>(File.ReadAllText(path));
                    if(row!=null && row.Mac!=null && row.Mac.Length==12 && !string.IsNullOrEmpty(row.ProtectedKeys)) result.Add(row);
                } catch(ArgumentException) {} catch(IOException) {} catch(InvalidOperationException) {}
            }
            // Import existing pairing metadata on demand. Read keys only when starting that console.
            using(var org=Registry.CurrentUser.OpenSubKey(@"Software\Chiaki")) {
                if(org!=null) foreach(string profile in org.GetSubKeyNames().OrderBy(n=>n=="Chiaki"?0:1)) {
                    string prefix=@"Software\Chiaki\"+profile;
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
                            if(r==null || Convert.ToInt32(r.GetValue("target",0))<1000000) continue;
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
        byte[] Keys()
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
        static Process Begin(string args,bool captureOutput)
        {
            if(!File.Exists(CorePath)) throw new FileNotFoundException("The direct streaming engine is missing. Extract the complete P5Cast release.");
            var info=new ProcessStartInfo(CorePath,args) {WorkingDirectory=Path.GetDirectoryName(CorePath),UseShellExecute=false,
                CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=captureOutput};
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
        public Process Connect(string pipe,string host,int codec,int fps)
        {
            string arguments=pipe+" "+Host(host)+" "+codec+" "+fps;
            byte[] secret=Keys(); Process p=null;
            try {
                if(secret.Length!=32 && secret.Length!=36) throw new InvalidDataException("This registration needs to be paired again.");
                p=Begin(arguments,false); WriteSecrets(p,secret); return p;
            }
            catch {if(p!=null){StopHelper(p); p.Dispose();} throw;}
            finally {Clear(secret);}
        }
        public static Task<DirectConsole> Pair(string host,string account,string pin)
        {
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
                try {using(var p=Begin("--register "+address,true)) {
                    try {
                        WriteSecrets(p,request); Clear(request);
                        if(!p.WaitForExit(60000)) throw new TimeoutException("Pairing timed out. Get a fresh Link Device code and try again.");
                    } catch {StopHelper(p); throw;}
                    using(var reader=new BinaryReader(p.StandardOutput.BaseStream)) {
                        int length=0; while(length<data.Length) {int n=reader.Read(data,length,data.Length-length); if(n==0) break; length+=n;}
                        if(p.ExitCode!=0 || length!=data.Length || Encoding.ASCII.GetString(data,0,4)!="CDR1")
                            throw new InvalidOperationException("The PS5 did not accept registration. Check its address, account and Link Device code.");
                    }
                    Array.Copy(data,42,keys,0,32);
                    var item=new DirectConsole {Name=Encoding.UTF8.GetString(data,10,32).TrimEnd('\0'),Mac=Hex(data.Skip(4).Take(6).ToArray()),Address=address,
                        AccountId=canonicalId,ProtectedKeys=Convert.ToBase64String(ProtectedData.Protect(keys,null,DataProtectionScope.CurrentUser))};
                    Directory.CreateDirectory(DirectoryPath);
                    string file=Path.Combine(DirectoryPath,item.Mac+".json"), temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try {
                        File.WriteAllText(temp,new JavaScriptSerializer().Serialize(item));
                        try {File.Move(temp,file);} catch(IOException) {if(!File.Exists(file)) throw; File.Replace(temp,file,null);}
                    } finally {if(File.Exists(temp)) File.Delete(temp);}
                    return item;
                }} finally {Clear(request); Clear(data); Clear(keys);}
            });
        }
    }
}
