using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Cadrivect
{
    // Discord's native desktop RPC protocol: https://docs.discord.com/developers/topics/rpc
    // Construction has no side effects. The caller opts in by publishing a session.
    internal sealed class DiscordPresence : IDisposable
    {
        internal const string ApplicationId="1574360484390371419";
        const string ProjectUrl="https://github.com/EEkebin/P5Cast";
        readonly object gate=new object();
        readonly AutoResetEvent changed=new AutoResetEvent(false);
        readonly string[] endpoints;
        readonly Func<NamedPipeClientStream,bool> verify;
        readonly int retryMs,timeoutMs,pid;
        Thread worker;
        Link activeLink;
        string title,status="Off";
        bool button,closing,published,publishing;
        long revision;
        public string Status {get {lock(gate) return status;}}
        public bool IsPublished {get {lock(gate) return published;}}

        public DiscordPresence():this(DesktopEndpoints(),IsDiscordServer,10000,3500) {}
        internal DiscordPresence(string[] endpoints,Func<NamedPipeClientStream,bool> verify,int retryMs,int timeoutMs)
        {
            this.endpoints=endpoints; this.verify=verify; this.retryMs=retryMs; this.timeoutMs=timeoutMs;
            using(var self=Process.GetCurrentProcess()) pid=self.Id;
        }
        static string[] DesktopEndpoints()
        {
            var names=new string[10];
            for(int i=0;i<names.Length;i++) names[i]="discord-ipc-"+i;
            return names;
        }
        static string CleanTitle(string text)
        {
            text=Regex.Replace(text ?? "",@"[\s\p{Cc}\p{Cf}]+"," ").Trim();
            var output=new StringBuilder(); int bytes=0;
            var elements=StringInfo.GetTextElementEnumerator(text);
            while(elements.MoveNext()) {
                string element=elements.GetTextElement();
                bytes+=Encoding.UTF8.GetByteCount(element); if(bytes>128) break;
                output.Append(element);
            }
            // A single ASCII character is shorter than Discord's activity-details minimum.
            string result=output.ToString().TrimEnd();
            return result.Length==1 ? "Playing "+result : result;
        }
        public void SetActivity(string gameTitle,bool includeProjectButton=false)
        {
            string next=CleanTitle(gameTitle);
            lock(gate) {
                if(closing || (title==next && button==includeProjectButton)) return;
                title=next; button=includeProjectButton; revision++; status="Updating Discord activity";
                if(worker==null) {
                    worker=new Thread(Run) {IsBackground=true,Name="P5Cast Discord presence"};
                    worker.Start();
                }
                changed.Set();
            }
        }
        public void Clear()
        {
            Link unacknowledged=null;
            lock(gate) {
                if(closing || title==null) return;
                title=null; revision++; status=published ? "Clearing Discord activity" : "Off"; changed.Set();
                // An in-flight write may have reached Discord without an ACK. Disconnecting it now
                // prevents an old title from remaining on the client while the response times out.
                if(publishing) unacknowledged=activeLink;
            }
            if(unacknowledged!=null) unacknowledged.Dispose();
        }
        public void Dispose()
        {
            Thread thread;
            lock(gate) {
                if(closing) return;
                closing=true; title=null; revision++; thread=worker; changed.Set();
                if(thread==null) {changed.Dispose(); return;}
            }
            // Try to acknowledge a clear during ordinary shutdown. Closing the pipe also releases
            // the PID-scoped activity if the desktop client has stopped responding.
            if(!thread.Join(450)) {
                Link link; lock(gate) link=activeLink;
                if(link!=null) link.Dispose();
            }
        }
        Link Connect()
        {
            foreach(string endpoint in endpoints) {
                lock(gate) if(closing || title==null) return null;
                var pipe=new NamedPipeClientStream(".",endpoint,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
                bool keep=false;
                try {
                    pipe.Connect(45);
                    if(!verify(pipe)) continue;  // Verify the local peer before sending even the public app ID.
                    var link=new Link(pipe,timeoutMs); keep=true; return link;
                } catch(TimeoutException) {} catch(IOException) {} catch(UnauthorizedAccessException) {}
                finally {if(!keep) pipe.Dispose();}
            }
            return null;
        }
        void Run()
        {
            Link link=null;
            long acknowledged=-1;
            try {
                for(;;) {
                    string desired; bool withButton,stop; long version;
                    lock(gate) {desired=title; withButton=button; stop=closing; version=revision;}
                    try {
                        if(desired==null) {
                            if(link!=null) {
                                if(acknowledged>=0) link.Activity(pid,null,false);
                                link.Dispose(); link=null; acknowledged=-1;
                            }
                            lock(gate) {activeLink=null; published=false; if(title==null) status="Off";}
                            if(stop) return;
                            changed.WaitOne(); continue;
                        }
                        if(link==null) {
                            link=Connect();
                            if(link==null) throw new IOException();
                            lock(gate) activeLink=link;
                            link.Handshake();
                            continue; // Opt-out or a new title may have arrived during the handshake.
                        }
                        if(acknowledged!=version) {
                            lock(gate) {
                                if(closing || title==null || revision!=version) continue;
                                publishing=true;
                            }
                            link.Activity(pid,desired,withButton);
                            acknowledged=version;
                            lock(gate) {publishing=false; published=true; if(revision==version) status="Shared with Discord";}
                            continue;
                        }
                        link.WaitForChange(changed);
                    } catch(Exception e) {
                        if(link!=null) {link.Dispose();link=null;}
                        acknowledged=-1;
                        bool retry;
                        lock(gate) {
                            activeLink=null; published=false; publishing=false; retry=!closing && title!=null;
                            status=retry ? (e is Rejected ? "Discord rejected the activity" : "Waiting for Discord desktop") : "Off";
                        }
                        if(retry) changed.WaitOne(retryMs);
                    }
                }
            } finally {
                if(link!=null) link.Dispose();
                lock(gate) {activeLink=null; published=false; status="Off"; changed.Dispose();}
            }
        }

        sealed class Rejected:IOException {}
        sealed class Packet {public int Op; public byte[] Bytes;}
        sealed class Link : IDisposable
        {
            readonly NamedPipeClientStream pipe;
            readonly JavaScriptSerializer json=new JavaScriptSerializer {MaxJsonLength=65536,RecursionLimit=32};
            readonly int timeout;
            Task<Packet> pending;
            int disposed;
            public Link(NamedPipeClientStream pipe,int timeout) {this.pipe=pipe; this.timeout=timeout;}
            public void Dispose() {if(Interlocked.Exchange(ref disposed,1)==0) pipe.Dispose();}
            static async Task ReadFully(Stream stream,byte[] bytes)
            {
                for(int pos=0;pos<bytes.Length;) {
                    int n=await stream.ReadAsync(bytes,pos,bytes.Length-pos).ConfigureAwait(false);
                    if(n==0) throw new EndOfStreamException();
                    pos+=n;
                }
            }
            async Task<Packet> Read()
            {
                var header=new byte[8]; await ReadFully(pipe,header).ConfigureAwait(false);
                int size=BitConverter.ToInt32(header,4);
                if(size<0 || size>65536) throw new InvalidDataException("Invalid local RPC frame length.");
                var bytes=new byte[size]; await ReadFully(pipe,bytes).ConfigureAwait(false);
                return new Packet {Op=BitConverter.ToInt32(header,0),Bytes=bytes};
            }
            void Send(int op,byte[] data)
            {
                var packet=new byte[8+data.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(op),0,packet,0,4);
                Buffer.BlockCopy(BitConverter.GetBytes(data.Length),0,packet,4,4);
                Buffer.BlockCopy(data,0,packet,8,data.Length);
                var write=pipe.WriteAsync(packet,0,packet.Length);
                if(!write.Wait(timeout)) throw new TimeoutException();
            }
            void Json(int op,object payload) {Send(op,Encoding.UTF8.GetBytes(json.Serialize(payload)));}
            static string Field(Dictionary<string,object> data,string name) {object v; return data.TryGetValue(name,out v) ? v as string : null;}
            Dictionary<string,object> Consume()
            {
                var read=pending; pending=null;
                var p=read.GetAwaiter().GetResult();
                if(p.Op==2) throw new EndOfStreamException();
                if(p.Op==3) {Send(4,p.Bytes);return null;}
                if(p.Op==4) return null;
                if(p.Op!=1) throw new InvalidDataException("Unexpected Discord opcode.");
                var payload=json.Deserialize<Dictionary<string,object>>(new UTF8Encoding(false,true).GetString(p.Bytes));
                if(payload==null) throw new InvalidDataException("Empty Discord response.");
                return payload;
            }
            void Await(string nonce)
            {
                var clock=Stopwatch.StartNew();
                while(clock.ElapsedMilliseconds<timeout) {
                    if(pending==null) pending=Read();
                    if(!pending.Wait(Math.Max(0,timeout-(int)clock.ElapsedMilliseconds))) break;
                    var f=Consume(); if(f==null) continue;
                    string evt=Field(f,"evt"), cmd=Field(f,"cmd");
                    if(nonce==null) {
                        if(evt=="READY" && cmd=="DISPATCH") return;
                        if(evt=="ERROR") throw new Rejected();
                    } else if(Field(f,"nonce")==nonce && cmd=="SET_ACTIVITY") {
                        if(evt=="ERROR") throw new Rejected();
                        if(evt==null) return;
                    }
                }
                throw new TimeoutException();
            }
            public void Handshake() {Json(0,new {v=1,client_id=ApplicationId}); Await(null);}
            public void Activity(int pid,string title,bool button)
            {
                Dictionary<string,object> activity=null;
                if(title!=null) {
                    activity=new Dictionary<string,object>{{"type",0},{"details",title.Length==0 ? "PS5 Remote Play" : title}};
                    if(title.Length>0) activity["state"]="PS5 Remote Play";
                    if(button) activity["buttons"]=new[]{new {label="View project",url=ProjectUrl}};
                }
                string nonce=Guid.NewGuid().ToString("N");
                Json(1,new {cmd="SET_ACTIVITY",args=new {pid=pid,activity=activity},nonce=nonce}); Await(nonce);
            }
            public void WaitForChange(WaitHandle change)
            {
                if(pending==null) pending=Read();
                if(WaitHandle.WaitAny(new[]{change,((IAsyncResult)pending).AsyncWaitHandle})==1) Consume();
            }
        }

        [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint id);
        [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint pid,out uint session);
        [DllImport("kernel32.dll")] static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(SafeProcessHandle process,int flags,StringBuilder path,ref int length);
        [DllImport("advapi32.dll")] static extern bool OpenProcessToken(SafeProcessHandle process,int access,out SafeAccessTokenHandle token);
        internal static bool IsDiscordServer(NamedPipeClientStream pipe)
        {
            try {
                uint id,session;
                if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out id) || !ProcessIdToSessionId(id,out session)) return false;
                using(var self=Process.GetCurrentProcess()) if(session!=self.SessionId) return false;
                using(var owner=OpenProcess(0x1000,false,id)) {
                    if(owner.IsInvalid) return false;
                    SafeAccessTokenHandle token;
                    if(!OpenProcessToken(owner,8,out token)) return false;
                    using(token) using(var server=new WindowsIdentity(token.DangerousGetHandle()))
                    using(var local=WindowsIdentity.GetCurrent()) if(local.User==null || !local.User.Equals(server.User)) return false;
                    var buf=new StringBuilder(32768); int size=buf.Capacity;
                    if(!QueryFullProcessImageName(owner,0,buf,ref size)) return false;
                    string file=buf.ToString(),name=Path.GetFileNameWithoutExtension(file);
                    if(!(name.Equals("Discord",StringComparison.OrdinalIgnoreCase) || name.Equals("DiscordPTB",StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("DiscordCanary",StringComparison.OrdinalIgnoreCase))) return false;
                    return SignedByDiscord(file);
                }
            } catch(SystemException) {return false;}
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct TrustFile {public int Size; public string Path; public IntPtr Handle,Subject;}
        [StructLayout(LayoutKind.Sequential)]
        struct TrustData {
            public int Size; public IntPtr Policy,Sip; public int Ui,Revocation,Choice; public IntPtr File;
            public int State; public IntPtr StateData,Url; public int Flags,Context; public IntPtr Settings;
        }
        [DllImport("wintrust.dll",ExactSpelling=true)] static extern int WinVerifyTrust(IntPtr hwnd,ref Guid action,ref TrustData data);
        static bool SignedByDiscord(string path)
        {
            using(var stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var signer=X509Certificate.CreateFromSignedFile(path))
            using(var certificate=new X509Certificate2(signer)) {
                if(!string.Equals(certificate.GetNameInfo(X509NameType.SimpleName,false),"Discord Inc.",StringComparison.OrdinalIgnoreCase)) return false;
                var file=new TrustFile {Size=Marshal.SizeOf(typeof(TrustFile)),Path=path,Handle=stream.SafeFileHandle.DangerousGetHandle()};
                IntPtr memory=Marshal.AllocHGlobal(file.Size);
                try {
                    Marshal.StructureToPtr(file,memory,false);
                    // WTD_CACHE_ONLY_URL_RETRIEVAL: publisher verification must not make network requests.
                    var trust=new TrustData {Size=Marshal.SizeOf(typeof(TrustData)),Ui=2,Choice=1,File=memory,Flags=0x1000};
                    var action=new Guid("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
                    return WinVerifyTrust(new IntPtr(-1),ref action,ref trust)==0;
                } finally {Marshal.DestroyStructure(memory,typeof(TrustFile)); Marshal.FreeHGlobal(memory);}
            }
        }
    }
}
