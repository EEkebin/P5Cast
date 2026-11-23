using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cadrivect;

internal static class ConsoleWakeProbe
{
    const string Mac="0011AABB34CD";
    const string Search="SRCH * HTTP/1.1\ndevice-discovery-protocol-version:00030010\n\0";
    const string TestKey="aD7BeF01";
    static readonly string expectedWake="WAKEUP * HTTP/1.1\nclient-type:vr\nauth-type:R\nmodel:w\napp-type:r\nuser-credential:"+
        Convert.ToUInt32(TestKey,16).ToString(CultureInfo.InvariantCulture)+"\ndevice-discovery-protocol-version:00030010\n\0";
    static string Reply(int code,string mac)
    {
        return "HTTP/1.1 "+code+" "+(code==200?"Ok":"Server Standby")+"\r\nhost-id:"+mac+
            "\r\nhost-type:PS5\r\ndevice-discovery-protocol-version:00030010\r\n\0";
    }
    static void Require(bool ok,string reason) {if(!ok) throw new Exception(reason);}
    static void Throws<T>(Action run) where T:Exception
    {
        try {run();} catch(T) {return;}
        throw new Exception("Expected "+typeof(T).Name);
    }
    sealed class Fake : IDisposable
    {
        readonly UdpClient udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        readonly Task server;
        public readonly IPEndPoint Endpoint;
        public volatile int Wakes, Searches;
        int keyReads;
        public int ReadyAfter;
        public string Id=Mac.ToLowerInvariant(), Key=TestKey;
        public bool Quiet, DropFirstSearch, ChangeIdentity;
        public byte[] LastKey;
        public Fake()
        {
            Endpoint=(IPEndPoint)udp.Client.LocalEndPoint;
            server=Task.Run((Action)Serve);
        }
        void Serve()
        {
            try {
                for(;;) {
                    var source=new IPEndPoint(IPAddress.Any,0);
                    string packet=Encoding.ASCII.GetString(udp.Receive(ref source));
                    if(packet==Search) {
                        Searches++;
                        if(Quiet || (DropFirstSearch && Searches==1)) continue;
                        var response=Encoding.ASCII.GetBytes(Reply(Wakes>=ReadyAfter?200:620,ChangeIdentity && Wakes>0?"AABBCCDDEEFF":Id));
                        udp.Send(response,response.Length,source);
                    } else {
                        Require(packet==expectedWake,"Unexpected wake datagram");
                        Wakes++;
                    }
                }
            } catch(ObjectDisposedException) {} catch(SocketException ex) {
                if(ex.SocketErrorCode!=SocketError.Interrupted && ex.SocketErrorCode!=SocketError.OperationAborted) throw;
            }
        }
        byte[] ReadKeys() {
            keyReads++;
            LastKey=new byte[36];
            Encoding.ASCII.GetBytes(Key).CopyTo(LastKey,0);
            for(int i=16;i<36;++i) LastKey[i]=0xBD;
            return LastKey;
        }
        public Task<string> Check(bool explicitWake,CancellationToken token,int wait=700)
        {
            return ConsoleWake.Check(new[]{Endpoint},"fixture-address",Mac,ReadKeys,explicitWake,token,
                delegate {},80,35,wait);
        }
        public void Counts(int reads,int wakes) {
            Require(keyReads==reads && Wakes==wakes,"Unexpected key read or wake count");
            if(LastKey!=null) Require(LastKey.All(x=>x==0),"Key buffer was not cleared");
        }
        public void Dispose() {udp.Close(); server.GetAwaiter().GetResult();}
    }
    static int Main()
    {
        try {
            var none=CancellationToken.None;
            using(var fake=new Fake()) {
                Require(fake.Check(false,none).Result=="127.0.0.1","Ready endpoint");
                Require(fake.Check(true,none).Result=="127.0.0.1","Explicit ready check"); fake.Counts(0,0);
            }
            using(var fake=new Fake {ReadyAfter=1}) {
                Require(fake.Check(false,none).Result=="127.0.0.1","Wake on Connect"); fake.Counts(1,1);
            }
            using(var fake=new Fake {DropFirstSearch=true}) {
                Require(fake.Check(false,none).Result=="127.0.0.1" && fake.Searches>=2,"Lost first discovery probe");
                fake.Counts(0,0);
            }
            using(var fake=new Fake {ReadyAfter=2}) {
                Require(fake.Check(true,none).Result=="127.0.0.1","Retry standby"); fake.Counts(1,2);
            }
            using(var fake=new Fake {Quiet=true}) {
                Require(fake.Check(false,none).Result=="fixture-address","Direct fallback");
                Throws<InvalidOperationException>(()=>fake.Check(true,none).GetAwaiter().GetResult()); fake.Counts(0,0);
            }
            using(var fake=new Fake {Id="112233445566"}) {
                Throws<InvalidOperationException>(()=>fake.Check(false,none).GetAwaiter().GetResult()); fake.Counts(0,0);
            }
            using(var fake=new Fake {ReadyAfter=1,ChangeIdentity=true}) {
                Throws<InvalidOperationException>(()=>fake.Check(true,none).GetAwaiter().GetResult()); fake.Counts(1,1);
            }
            using(var fake=new Fake {ReadyAfter=1,Key="123456789"}) {
                Throws<InvalidDataException>(()=>fake.Check(false,none).GetAwaiter().GetResult()); fake.Counts(1,0);
            }
            using(var fake=new Fake {ReadyAfter=99}) {
                Throws<TimeoutException>(()=>fake.Check(true,none,250).GetAwaiter().GetResult()); fake.Counts(1,2);
            }
            using(var fake=new Fake {ReadyAfter=99})
            using(var cancel=new CancellationTokenSource()) {
                Task<string> pending=fake.Check(true,cancel.Token,5000);
                Require(SpinWait.SpinUntil(()=>fake.Wakes==1,1000),"First wake timed out");
                cancel.Cancel();
                Throws<OperationCanceledException>(()=>pending.GetAwaiter().GetResult()); fake.Counts(1,1);
            }
            Console.WriteLine("Loopback passed for discovery, matching identity, key conversion, wake, retries, cancellation and Direct fallback.");
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e); return 1;}
    }
}
