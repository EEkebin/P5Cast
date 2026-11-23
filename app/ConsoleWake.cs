using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cadrivect
{
    internal static class ConsoleWake
    {
        const int Port=9302;
        const string Version="00030010";
        static readonly byte[] search=Encoding.ASCII.GetBytes("SRCH * HTTP/1.1\ndevice-discovery-protocol-version:"+Version+"\n\0");
        enum State { Unknown, Ready, Rest, OtherConsole }

        internal static async Task<string> Prepare(string address,string mac,Func<byte[]> keys,bool wakeOnly,
            CancellationToken cancel,Action<string> status)
        {
            IPAddress numeric;
            IPAddress[] addresses;
            if(IPAddress.TryParse(address,out numeric)) addresses=new[]{numeric};
            else {
                var lookup=Dns.GetHostAddressesAsync(address);
                Observe(lookup); // Framework DNS can finish after cancellation or timeout.
                var delay=Task.Delay(1800,cancel);
                await Task.WhenAny(lookup,delay).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
                if(!lookup.IsCompleted) return Unconfirmed(address,wakeOnly);
                try {addresses=await lookup.ConfigureAwait(false);}
                catch(SocketException) {return Unconfirmed(address,wakeOnly);}
            }
            if(!addresses.Any(DirectGameTitle.Unicast)) throw new InvalidOperationException("Enter a unicast address for the paired PS5.");
            var peers=addresses.Where(DirectGameTitle.Unicast).Distinct().Take(8).Select(ip=>new IPEndPoint(ip,Port)).ToArray();
            return await Check(peers,address,mac,keys,wakeOnly,cancel,status,750,1400,28000).ConfigureAwait(false);
        }

        static void Observe(Task pending)
        {
            pending.ContinueWith(t=>{var ignored=t.Exception;}, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }

        static string Unconfirmed(string original,bool wakeOnly)
        {
            if(wakeOnly) throw new InvalidOperationException("The paired PS5 did not answer discovery. Check its address, UDP 9302, and network access in rest mode.");
            return original; // A routed network can permit Remote Play while filtering discovery.
        }

        internal static async Task<string> Check(IPEndPoint[] peers,string original,string mac,Func<byte[]> keys,bool wakeOnly,
            CancellationToken cancel,Action<string> status,int queryMs,int intervalMs,int waitMs)
        {
            cancel.ThrowIfCancellationRequested();
            var replies=await Task.WhenAll(peers.Select(p=>Task.Run(()=>Query(p,mac,queryMs,cancel),cancel))).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            int index=Array.IndexOf(replies,State.Ready);
            if(index>=0) return peers[index].Address.ToString();
            index=Array.IndexOf(replies,State.Rest);
            if(index<0) {
                if(Array.IndexOf(replies,State.OtherConsole)>=0) throw Mismatch();
                return Unconfirmed(original,wakeOnly);
            }
            var endpoint=peers[index];
            byte[] packet=Packet(keys);
            try {
                cancel.ThrowIfCancellationRequested();
                Send(endpoint,packet);
                status("Wake request sent. Waiting for your PS5…");
                var elapsed=Stopwatch.StartNew();
                bool retried=false;
                while(elapsed.ElapsedMilliseconds<waitMs) {
                    int left=Math.Max(1,waitMs-(int)elapsed.ElapsedMilliseconds);
                    await Task.Delay(Math.Min(intervalMs,left),cancel).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    State state=await Task.Run(()=>Query(endpoint,mac,queryMs,cancel),cancel).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    if(state==State.Ready) return endpoint.Address.ToString();
                    if(state==State.OtherConsole) throw Mismatch();
                    if(state==State.Rest && !retried && elapsed.ElapsedMilliseconds>=waitMs/4) {
                        Send(endpoint,packet);
                        retried=true;
                    }
                }
                if(wakeOnly) throw new TimeoutException("Wake was sent, but the PS5 hasn't confirmed it's ready. Check Enable Turning On PS5 from Network in its rest mode settings.");
                status("Wake was sent. Trying the Remote Play connection…");
                return endpoint.Address.ToString();
            } finally {Array.Clear(packet,0,packet.Length);}
        }

        static Exception Mismatch()
        {
            return new InvalidOperationException("A different console answered at this address. Select the matching paired PS5 or correct its address.");
        }

        static State Query(IPEndPoint endpoint,string mac,int timeout,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            try {
                using(var socket=new Socket(endpoint.AddressFamily,SocketType.Dgram,ProtocolType.Udp)) {
                    socket.Connect(endpoint);
                    socket.Send(search);
                    var data=new byte[4097];
                    var timer=Stopwatch.StartNew();
                    bool repeated=false;
                    while(timer.ElapsedMilliseconds<timeout) {
                        cancel.ThrowIfCancellationRequested();
                        if(!repeated && timer.ElapsedMilliseconds>=timeout/2) {
                            socket.Send(search);
                            repeated=true;
                        }
                        int tick=Math.Min(100,Math.Max(1,timeout/2));
                        if(!socket.Poll(Math.Min(tick,Math.Max(1,timeout-(int)timer.ElapsedMilliseconds))*1000,SelectMode.SelectRead)) continue;
                        int count=socket.Receive(data);
                        if(count>4096) continue;
                        State reply=Parse(data,count,mac);
                        if(reply!=State.Unknown) return reply;
                    }
                }
            } catch(SocketException) {}
            return State.Unknown;
        }

        static State Parse(byte[] data,int count,string mac)
        {
            string[] lines=Encoding.ASCII.GetString(data,0,count).TrimEnd('\0','\n','\r').Split('\n');
            string[] status=lines[0].TrimEnd('\r').Split(' ');
            if(status.Length<2 || status[0]!="HTTP/1.1" || (status[1]!="200" && status[1]!="620")) return State.Unknown;
            string host=null,version=null,type=null;
            foreach(string line in lines.Skip(1)) {
                int colon=line.IndexOf(':');
                if(colon<1) continue;
                string name=line.Substring(0,colon).ToLowerInvariant(),value=line.Substring(colon+1).Trim();
                if(name=="host-id") {if(host!=null) return State.Unknown; host=value;}
                if(name=="host-type") {if(type!=null) return State.Unknown; type=value;}
                if(name=="device-discovery-protocol-version") {if(version!=null) return State.Unknown; version=value;}
            }
            if(host==null || host.Length!=12 || !host.All(Uri.IsHexDigit)) return State.Unknown;
            if(!string.Equals(host,mac,StringComparison.OrdinalIgnoreCase) || (type!=null && type!="PS5")) return State.OtherConsole;
            if(version!=Version) return State.Unknown;
            return status[1]=="620"?State.Rest:State.Ready;
        }

        static byte[] Packet(Func<byte[]> readKeys)
        {
            byte[] payload=readKeys();
            try {
                // libchiaki stores up to eight ASCII hex digits in the 16-byte registration field.
                int length=0;
                uint credential=0;
                while(payload!=null && payload.Length>=16 && length<16 && payload[length]!=0) {
                    char ch=(char)payload[length++];
                    if(!Uri.IsHexDigit(ch) || length>8) throw InvalidRegistration();
                    credential=(credential<<4)|(uint)Uri.FromHex(ch);
                }
                if(length==0) throw InvalidRegistration();
                byte[] header=Encoding.ASCII.GetBytes("WAKEUP * HTTP/1.1\nclient-type:vr\nauth-type:R\nmodel:w\napp-type:r\nuser-credential:");
                byte[] tail=Encoding.ASCII.GetBytes("\ndevice-discovery-protocol-version:"+Version+"\n\0");
                int digits=1;
                for(uint v=credential;v>=10;v/=10) digits++;
                var result=new byte[header.Length+digits+tail.Length];
                header.CopyTo(result,0); tail.CopyTo(result,header.Length+digits);
                for(int i=header.Length+digits-1;i>=header.Length;--i) {result[i]=(byte)('0'+credential%10); credential/=10;}
                return result;
            } finally {if(payload!=null) Array.Clear(payload,0,payload.Length);}
        }

        static Exception InvalidRegistration() {return new InvalidDataException("Pair this PS5 again to refresh its wake credential.");}
        static void Send(IPEndPoint peer,byte[] packet)
        {
            using(var socket=new Socket(peer.AddressFamily,SocketType.Dgram,ProtocolType.Udp)) {
                socket.Connect(peer); socket.Send(packet);
            }
        }
    }
}
