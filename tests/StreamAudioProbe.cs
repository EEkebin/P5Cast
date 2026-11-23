using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Cadrivect;
internal static class StreamAudioProbe
{
    sealed class Fixture:IDisposable
    {
        internal readonly Process Process;
        internal Fixture(string path,string session){Process=Process.Start(new ProcessStartInfo(path,session){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true});}
        internal string Send(string command) {
            Process.StandardInput.WriteLine(command);
            string reply=Process.StandardOutput.ReadLine();
            if(reply==null) throw new Exception("Silent test fixture exited during "+command);
            return reply;
        }
        internal void Verify(int index,double target,bool mute,string endpoint)
        {
            var timer=Stopwatch.StartNew();
            string actual;
            do{
                actual=Send("get "+index);
                var fields=actual.Split(' ');
                if(Math.Abs(double.Parse(fields[0],CultureInfo.InvariantCulture)-target)<0.0002 &&
                   fields[1]==(mute?"1":"0") && endpoint==fields[2]+" "+fields[3])return;
                Thread.Sleep(30);
            }while(timer.ElapsedMilliseconds<4500);
            throw new Exception("Expected gain "+target+", mute "+mute+", endpoint "+endpoint+"; observed "+actual);
        }
        public void Dispose(){try{if(!Process.HasExited){Process.StandardInput.WriteLine("exit");Process.WaitForExit(1500);}}finally{if(!Process.HasExited)Process.Kill();Process.Dispose();}}
    }
    static int Main(string[] args){
        try{
            string shared=Guid.NewGuid().ToString("B");
            using(var target=new Fixture(args[0],shared))
            using(var unrelated=new Fixture(args[0],shared)){
                target.Send("open 0.8 0");unrelated.Send("open 0.61 0");
                unrelated.Send("shared 0.47 0");
                var initial=target.Send("get 0").Split(' ');var endpoint=initial[2]+" "+initial[3];
                using(var stream=new StreamAudio(target.Process.Id,50)){
                    target.Verify(0,.4,false,endpoint);unrelated.Verify(0,.61,false,endpoint);
                    target.Send("open 0.38 1");
                    target.Verify(1,.19,true,endpoint);
                    target.Send("shared 0.47 0");
                    target.Verify(2,.47,false,endpoint);
                    unrelated.Verify(1,.47,false,endpoint);
                    stream.SetPercent(0);target.Verify(0,0,false,endpoint);target.Verify(1,0,true,endpoint);
                    unrelated.Verify(0,.61,false,endpoint);
                    unrelated.Verify(1,.47,false,endpoint);target.Verify(2,.47,false,endpoint);
                    stream.SetPercent(100);target.Verify(0,.8,false,endpoint);target.Verify(1,.38,true,endpoint);
                    stream.SetPercent(50);target.Verify(0,.4,false,endpoint);
                    target.Send("set 0 0.7");target.Verify(0,.35,false,endpoint);
                }
                target.Verify(0,.7,false,endpoint);target.Verify(1,.38,true,endpoint);unrelated.Verify(0,.61,false,endpoint);
                using(var reconnected=new StreamAudio(target.Process.Id,25)){target.Verify(0,.175,false,endpoint);target.Verify(1,.095,true,endpoint);}
                target.Verify(0,.7,false,endpoint);unrelated.Verify(0,.61,false,endpoint);
                Console.WriteLine("Silent WASAPI test passed: late session discovery, independent gains, existing mute, live mute/unmute, external mixer changes, restoration, reconnect, unrelated process, shared session and endpoint isolation.");
            }
            return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
