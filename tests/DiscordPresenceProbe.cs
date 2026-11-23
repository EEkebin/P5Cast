using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Cadrivect;

// Every connection comes from the injected GUID endpoint. This test never opens a discord-ipc-* pipe.
internal static class DiscordPresenceProbe
{
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static string Endpoint() {return "P5Cast-Fake-Discord-"+Guid.NewGuid().ToString("N");}
    static NamedPipeServerStream Server(string name) {return new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);}
    static void Must(bool condition,string message) {if(!condition) throw new InvalidOperationException(message);}
    static void Until(Func<bool> condition,string message)
    {
        var clock=Stopwatch.StartNew();
        while(!condition()) {if(clock.ElapsedMilliseconds>3000) throw new TimeoutException(message); Thread.Sleep(10);}
    }
    static void ReadFully(Stream s,byte[] data)
    {
        for(int offset=0;offset<data.Length;) {
            var read=s.ReadAsync(data,offset,data.Length-offset);
            if(!read.Wait(3000)) throw new TimeoutException("Missing client packet.");
            if(read.Result==0) throw new EndOfStreamException();
            offset+=read.Result;
        }
    }
    static byte[] Read(Stream s,int op)
    {
        var header=new byte[8];ReadFully(s,header);
        Must(BitConverter.ToInt32(header,0)==op,"Wrong IPC opcode.");
        var size=BitConverter.ToInt32(header,4);Must(size>=0&&size<=65536,"Invalid payload length.");
        var data=new byte[size];ReadFully(s,data);return data;
    }
    static Dictionary<string,object> Object(Stream s,int op) {return Json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(Read(s,op)));}
    static void Send(Stream s,int op,byte[] data)
    {
        byte[] header=new byte[8];Buffer.BlockCopy(BitConverter.GetBytes(op),0,header,0,4);Buffer.BlockCopy(BitConverter.GetBytes(data.Length),0,header,4,4);
        // Split the header and body to exercise byte-stream framing.
        s.Write(header,0,3);s.Write(header,3,5);if(data.Length>0) {s.Write(data,0,1);s.Write(data,1,data.Length-1);}
    }
    static void Frame(Stream s,object value) {Send(s,1,Encoding.UTF8.GetBytes(Json.Serialize(value)));}
    static void Handshake(Stream s)
    {
        var h=Object(s,0);
        Must(h.Count==2 && (string)h["client_id"]==DiscordPresence.ApplicationId && Convert.ToInt32(h["v"])==1,"Unexpected handshake.");
        Frame(s,new {cmd="DISPATCH",evt="READY",data=new {}});
    }
    static void Ack(Stream s,Dictionary<string,object> request) {Frame(s,new {cmd="SET_ACTIVITY",nonce=request["nonce"],data=new {}});}
    static Dictionary<string,object> Activity(Dictionary<string,object> request)
    {
        Must((string)request["cmd"]=="SET_ACTIVITY" && Guid.ParseExact((string)request["nonce"],"N")!=Guid.Empty,"Invalid command.");
        var args=(Dictionary<string,object>)request["args"];
        using(var self=Process.GetCurrentProcess()) Must(Convert.ToInt32(args["pid"])==self.Id,"Wrong PID.");
        return (Dictionary<string,object>)args["activity"];
    }
    static void Sharing()
    {
        string name=Endpoint();
        using(var s=Server(name))
        using(var presence=new DiscordPresence(new[]{name},delegate{return true;},60,500)) {
            var accept=s.WaitForConnectionAsync();
            presence.Clear();Must(!accept.Wait(100),"Opening settings connected to IPC.");
            presence.SetIdle(false);
            Must(accept.Wait(2000),"No connection.");Handshake(s);
            var request=Object(s,1);var a=Activity(request);
            Must((string)a["details"]=="Idle" && (string)a["state"]=="Waiting for PS5 connection" && !a.ContainsKey("buttons"),"Startup idle activity was incorrect.");
            Ack(s,request);Until(()=>presence.IsPublished,"Idle was not acknowledged.");
            presence.SetActivity(null);request=Object(s,1);a=Activity(request);
            Must(a.Count==2 && (string)a["details"]=="PS5 Remote Play" && Convert.ToInt32(a["type"])==0,"Generic activity disclosed unexpected metadata.");
            Frame(s,new {cmd="SET_ACTIVITY",nonce="other-request"});Thread.Sleep(40);Must(presence.Status!="Shared with Discord","Accepted unrelated ACK.");
            Ack(s,request);Until(()=>presence.IsPublished,"Missing ACK state.");
            byte[] ping={4,3,2,1};Send(s,3,ping);Must(BitConverter.ToString(Read(s,4))=="04-03-02-01","Ping not echoed.");
            presence.SetActivity("  Astro\n  Bot  ",true);
            request=Object(s,1);a=Activity(request);
            var buttons=(System.Collections.IList)a["buttons"];var button=(Dictionary<string,object>)buttons[0];
            Must((string)a["details"]=="Astro Bot" && (string)a["state"]=="PS5 Remote Play" && buttons.Count==1 &&
                (string)button["label"]=="View project" && (string)button["url"]=="https://github.com/EEkebin/P5Cast","Incorrect optional title or button.");
            Ack(s,request);Until(()=>presence.Status=="Shared with Discord","Title update missing ACK.");
            presence.SetIdle(true);request=Object(s,1);a=Activity(request);
            var idleButton=(Dictionary<string,object>)((System.Collections.IList)a["buttons"])[0];
            Must((string)a["details"]=="Idle" && (string)a["state"]=="Waiting for PS5 connection" &&
                (string)idleButton["url"]=="https://github.com/EEkebin/P5Cast","Disconnect lost the idle activity or project link.");
            Ack(s,request);Until(()=>presence.Status=="Shared with Discord","Idle update missing ACK.");presence.SetIdle(true);presence.Clear();
            request=Object(s,1);Must(Activity(request)==null,"Clear did not remove the activity.");Ack(s,request);
            Until(()=>!presence.IsPublished && presence.Status=="Off","Clear not acknowledged.");
        }
        Console.WriteLine("Opt-in, generic payload, title and button, nonce ACK, ping, duplicate suppression and clear passed.");
    }
    static void Reconnect()
    {
        string name=Endpoint();
        using(var presence=new DiscordPresence(new[]{name},delegate{return true;},100,250)) {
            using(var s=Server(name)) {
                var ready=s.WaitForConnectionAsync();presence.SetActivity("Offline fixture");
                Must(ready.Wait(2000),"No fake endpoint.");Handshake(s);var r=Object(s,1);
                Frame(s,new {cmd="SET_ACTIVITY",evt="ERROR",data=new {code=4000,message="test rejection"},nonce=r["nonce"]});
                Until(()=>presence.Status=="Discord rejected the activity","Discord error not reported.");Must(!presence.IsPublished,"Rejected publish was marked successful.");
            }
            using(var s=Server(name)) {
                Must(s.WaitForConnectionAsync().Wait(2500),"No reconnect after error.");Handshake(s);Object(s,1);
                Until(()=>presence.Status=="Waiting for Discord desktop","Missing bounded ACK timeout.");
                Must(!presence.IsPublished,"Unacknowledged activity marked successful.");
            }
            using(var s=Server(name)) {
                Must(s.WaitForConnectionAsync().Wait(2500),"No reconnect after timeout.");Handshake(s);var r=Object(s,1);
                Must((string)Activity(r)["details"]=="Offline fixture","Reconnect lost current title.");Ack(s,r);
                Until(()=>presence.IsPublished,"Reconnect did not recover.");
                var disposing=Task.Run(()=>presence.Dispose());
                var clear=Object(s,1);Must(Activity(clear)==null,"Shutdown did not clear.");Ack(s,clear);
                Must(disposing.Wait(1000),"Shutdown hung.");
            }
        }
        Console.WriteLine("RPC error, timeout, automatic reconnect, re-publication and acknowledged shutdown passed.");
    }
    static void RejectFake()
    {
        string name=Endpoint();
        using(var s=Server(name))
        using(var presence=new DiscordPresence(new[]{name},DiscordPresence.IsDiscordServer,500,300)) {
            var accept=s.WaitForConnectionAsync();presence.SetActivity("Never transmit");
            Must(accept.Wait(2000),"No access check.");
            var one=new byte[1];var read=s.ReadAsync(one,0,1);
            Must(read.Wait(2000) && read.Result==0 && !presence.IsPublished,"Untrusted peer received data.");
        }
        Console.WriteLine("Production same-session, same-user verifier rejected the fake server before handshake.");
    }
    static void CancelUnacknowledgedPublish()
    {
        string name=Endpoint();
        using(var s=Server(name))
        using(var presence=new DiscordPresence(new[]{name},delegate{return true;},500,5000)) {
            var accept=s.WaitForConnectionAsync();presence.SetActivity("Temporary fixture");
            Must(accept.Wait(2000),"No fake connection.");Handshake(s);Object(s,1);
            // Simulate a desktop client that received our activity but never replied to it.
            presence.Clear();
            var read=s.ReadAsync(new byte[1],0,1);
            Must(read.Wait(1000) && read.Result==0,"Opt-out waited on a stalled publish.");
            Until(()=>presence.Status=="Off" && !presence.IsPublished,"Opt-out did not reset state.");
        }
        Console.WriteLine("Opt-out immediately disconnected an unacknowledged publish.");
    }
    static int Main()
    {
        try {Sharing();Reconnect();RejectFake();CancelUnacknowledgedPublish();return 0;}
        catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
