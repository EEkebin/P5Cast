using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Cadrivect
{
    internal sealed class DirectGameTitle
    {
        static readonly object gate=new object();
        static readonly UTF8Encoding utf8=new UTF8Encoding(false,true);
        static readonly byte[] search=Encoding.ASCII.GetBytes("SRCH * HTTP/1.1\ndevice-discovery-protocol-version:00030010\n\0");
        static DirectGameTitle current;
        readonly string address;
        readonly Process helper;
        IPAddress peer;
        Task<string> pending;

        DirectGameTitle(string address,Process helper) {this.address=address; this.helper=helper;}

        internal static void Track(string address,Process helper)
        {
            var session=new DirectGameTitle(address,helper);
            lock(gate) current=session;
            helper.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs args) {
                if(args.Data==null || args.Data.Length>80 || !args.Data.StartsWith("PEER ",StringComparison.Ordinal)) return;
                IPAddress target;
                if(!IPAddress.TryParse(args.Data.Substring(5),out target) || !Unicast(target)) return;
                lock(gate) if(current==session && session.peer==null) session.peer=target;
            };
            helper.Exited+=delegate {lock(gate) if(current==session) current=null;};
            helper.BeginOutputReadLine();
        }

        internal static bool Unicast(IPAddress target)
        {
            if(target.IsIPv4MappedToIPv6) target=target.MapToIPv4();
            if(target.AddressFamily==AddressFamily.InterNetworkV6)
                return !target.IsIPv6Multicast && !target.Equals(IPAddress.IPv6Any);
            byte first=target.GetAddressBytes()[0];
            return target.AddressFamily==AddressFamily.InterNetwork && first>0 && first<224;
        }

        bool Alive()
        {
            if(current!=this) return false;
            try {return !helper.HasExited;}
            catch(InvalidOperationException) {return false;}
        }

        internal static Task<string> ReadAsync(string address)
        {
            lock(gate) {
                var session=current;
                if(session==null || session.peer==null || !string.Equals(session.address,address,StringComparison.OrdinalIgnoreCase) || !session.Alive())
                    return Task.FromResult("");
                if(session.pending==null || session.pending.IsCompleted)
                    session.pending=Task.Run(delegate {
                        lock(gate) if(!session.Alive()) return "";
                        string title=Query(new IPEndPoint(session.peer,9302));
                        lock(gate) return session.Alive() ? title : "";
                    });
                return session.pending;
            }
        }

        internal static string Query(IPEndPoint endpoint)
        {
            if(!Unicast(endpoint.Address)) return "";
            try {
                using(var socket=new Socket(endpoint.AddressFamily,SocketType.Dgram,ProtocolType.Udp)) {
                    // Connected UDP accepts replies only from this numeric endpoint. Broadcast is disabled by default.
                    socket.SendTimeout=300;
                    socket.ReceiveTimeout=1200;
                    socket.Connect(endpoint);
                    socket.Send(search);
                    var data=new byte[4097];
                    int size=socket.Receive(data);
                    return Parse(data,size);
                }
            } catch(SocketException) {return "";}
        }

        internal static string Parse(byte[] data,int size)
        {
            if(size<=0 || size>4096 || size>data.Length) return "";
            while(size>0 && data[size-1]==0) size--;
            string text;
            try {text=utf8.GetString(data,0,size);}
            catch(DecoderFallbackException) {return "";}
            if(text.IndexOf('\0')>=0) return "";
            string[] lines=text.Split('\n');
            string status=lines[0].TrimEnd('\r');
            if(status!="HTTP/1.1 200" && !status.StartsWith("HTTP/1.1 200 ",StringComparison.Ordinal)) return "";
            string version=null,title=null;
            for(int i=1;i<lines.Length;i++) {
                string line=lines[i].TrimEnd('\r');
                if(line.Length==0) break;
                int colon=line.IndexOf(':');
                if(colon<1) continue;
                string key=line.Substring(0,colon), value=line.Substring(colon+1).Trim();
                if(key.Equals("device-discovery-protocol-version",StringComparison.OrdinalIgnoreCase)) {
                    if(version!=null) return "";
                    version=value;
                } else if(key.Equals("running-app-name",StringComparison.OrdinalIgnoreCase)) {
                    if(title!=null) return "";
                    title=value;
                }
            }
            if(version!="00030010" || string.IsNullOrWhiteSpace(title)) return "";
            var clean=new StringBuilder();
            bool space=false;
            for(int i=0;i<title.Length;) {
                var kind=CharUnicodeInfo.GetUnicodeCategory(title,i);
                int n=char.IsSurrogatePair(title,i) ? 2 : 1;
                if(char.IsWhiteSpace(title,i) || kind==UnicodeCategory.Control) space=clean.Length>0;
                else if(kind!=UnicodeCategory.Format || title[i]=='\u200c' || title[i]=='\u200d') {
                    if(space) clean.Append(' ');
                    clean.Append(title,i,n);
                    space=false;
                }
                i+=n;
            }
            var result=new StringBuilder();
            int bytes=0;
            string normalized;
            try {normalized=clean.ToString().Normalize();}
            catch(ArgumentException) {return "";}
            var elements=StringInfo.GetTextElementEnumerator(normalized);
            while(elements.MoveNext()) {
                string next=elements.GetTextElement();
                bytes+=utf8.GetByteCount(next);
                if(bytes>128) break;
                result.Append(next);
            }
            return result.ToString().TrimEnd();
        }
    }
}
