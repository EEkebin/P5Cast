using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

internal static partial class WindowChromeProbe
{
    // One disposable process per actual Direct launch. It speaks the local video
    // pipe protocol and never opens a network socket or reads real pairing keys.
    static int RunReconnectHelper(string[] args)
    {
        string stage=Environment.GetEnvironmentVariable("P5CAST_RECONNECT_FIXTURE");
        if(string.IsNullOrEmpty(stage) || args.Length!=7 || args[1]!="127.0.0.1" || args[2]!="1") return 20;
        using(var input=Console.OpenStandardInput()) {
            var bytes=new byte[33];int n=0,read;
            while(n<bytes.Length && (read=input.Read(bytes,n,bytes.Length-n))>0) n+=read;
            if(n!=32 || Array.Exists(bytes,b=>b!=0)) return 20;
        }
        string mark=Path.Combine(stage,"next-exit");
        int code=File.Exists(mark) ? int.Parse(File.ReadAllText(mark)) : 0;
        if(code!=0) File.Delete(mark);
        File.AppendAllText(Path.Combine(stage,"attempts"),code+Environment.NewLine);
        try {
            using(var pipe=new NamedPipeClientStream(".",args[0],PipeDirection.Out)) {
                pipe.Connect(10000);
                using(var writer=new BinaryWriter(pipe)) {
                    writer.Write(0x31564443);writer.Write(1);writer.Flush();
                    if(code!=0) return code;
                    byte[] frame=File.ReadAllBytes(Path.Combine(stage,"text.h264"));
                    string name="Local\\P5Cast.Rest."+Process.GetCurrentProcess().Id+"."+args[0]+".disconnect";
                    using(var stop=EventWaitHandle.OpenExisting(name)) {
                        for(int i=0;i<1800 && !stop.WaitOne(33);i++) {
                            writer.Write(frame.Length);writer.Write(frame);writer.Flush();
                        }
                    }
                }
            }
        } catch(IOException) { }
        return code;
    }

    static async Task CheckDirectReconnect(Type type,object dashboard,string stage)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var production=type.Assembly;
        // Resolve the real renderer and its adjacent libraries before redirecting
        // helper/script launches into the fixture directory.
        var engine=production.GetType("Cadrivect.EngineSession",true);
        type.GetField("engine",flags).SetValue(dashboard,Activator.CreateInstance(engine,true));
        Directory.CreateDirectory(Path.Combine(stage,"engine","bin","direct"));
        Directory.CreateDirectory(Path.Combine(stage,"sources"));
        File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(stage,"engine","bin","direct","CadrivectDirect.exe"));
        File.WriteAllText(Path.Combine(stage,"sources","Prepare-Decoder.ps1"),
            "@{ok=$true;runtime=[IO.File]::ReadAllText((Join-Path $env:P5CAST_RECONNECT_FIXTURE 'runtime.txt'))}|ConvertTo-Json -Compress");
        Environment.SetEnvironmentVariable("P5CAST_RECONNECT_FIXTURE",stage);
        production.GetType("Cadrivect.Program",true).GetField("Root",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,stage);

        const string mac="020000000079";
        var consoleType=production.GetType("Cadrivect.DirectConsole",true);
        object console=Activator.CreateInstance(consoleType,true);
        consoleType.GetProperty("Name").SetValue(console,"Loopback reconnect fixture",null);
        consoleType.GetProperty("Mac").SetValue(console,mac,null);
        consoleType.GetProperty("Address").SetValue(console,"127.0.0.1",null);
        consoleType.GetProperty("ProtectedKeys").SetValue(console,Convert.ToBase64String(
            ProtectedData.Protect(new byte[32],null,DataProtectionScope.CurrentUser)),null);
        Find<ComboBox>("SourceChoice").SelectedIndex=0;
        var picker=Find<ComboBox>("ConsolePicker");picker.ItemsSource=new[]{console};picker.SelectedIndex=0;
        Find<TextBox>("ConsoleAddress").Text="127.0.0.1";
        Find<ComboBox>("HdrOutput").SelectedItem="SDR";Find<ComboBox>("DirectSdrCodec").SelectedIndex=1;
        var choices=(Dictionary<string,string>)type.GetField("stopChoices",flags).GetValue(dashboard);
        choices[mac]="keep";
        var attempts=Path.Combine(stage,"attempts");File.WriteAllText(attempts,"");
        Action<int> reject=code=>File.WriteAllText(Path.Combine(stage,"next-exit"),code.ToString());
        Func<int> count=()=>File.ReadAllLines(attempts).Length;
        Func<bool> playing=()=>Find<Button>("RestartStream").IsEnabled;
        Func<bool> idle=()=>Peer(Find<Button>("OpenSource")).GetItemStatus()=="Ready" && !OperationVisible;
        Func<string> status=()=>Find<TextBlock>("Status").Text;
        Func<Task> stop=async delegate {Click("OpenSource");await Until(idle,"Manual Stop failed.",10000);};

        Click("OpenSource");
        try {await Until(playing,"Loopback fixture did not present its first frame.",15000);}
        catch {Note("First-frame diagnostics: "+status()+"; helper launches: "+count());throw;}
        Require(count()==1,"Initial connection launched an unexpected number of helpers.");
        var session=type.GetField("engine",flags).GetValue(dashboard);
        var sample=(Func<string>)session.GetType().GetField("Snapshot",flags).GetValue(session);
        var meter=type.GetField("meter",flags).GetValue(dashboard);
        var cadence=meter.GetType().GetProperty("EstimateGameFps",flags);
        Require(sample().Contains("\"estimateGameFps\":false"),"Game FPS sampling should start off.");
        cadence.SetValue(meter,true,null);
        await Until(()=>sample().Contains("\"estimateGameFps\":true"),"Live enable did not reach the running decoder.");
        cadence.SetValue(meter,false,null);
        await Until(()=>sample().Contains("\"estimateGameFps\":false"),"Live disable did not reach the running decoder.");
        Require(count()==1,"Toggling game FPS restarted the stream.");
        reject(30);Click("RestartStream");
        await Until(()=>playing() && count()==3,"Restart did not recover from the busy response.",30000);
        var history=File.ReadAllLines(attempts);
        Require(history[1]=="30" && history[2]=="0","Unexpected restart attempt sequence.");
        await stop();reject(31);Click("OpenSource");
        await Until(()=>playing() && count()==5,"Immediate manual reconnect did not recover from refusal.",30000);
        await stop();reject(30);Click("OpenSource");
        await Until(()=>count()==6 && Find<TextBlock>("OperationDetail").Text.IndexOf("retry",StringComparison.OrdinalIgnoreCase)>=0,
            "No cancellable wait after a busy response.",18000);
        Click("CancelOperation");await Until(idle,"Cancelling the retry did not return to idle.");
        await Task.Delay(2400);Require(count()==6,"A helper was launched after retry cancellation.");
        reject(35);Click("OpenSource");
        await Until(()=>count()==7 && idle(),"Terminal authentication rejection did not return to idle.",18000);
        Require(status().IndexOf("saved registration",StringComparison.OrdinalIgnoreCase)>=0,"Authentication rejection lost its diagnostic: "+status());
        await Task.Delay(2400);Require(count()==7,"Authentication rejection was retried.");
        Note("Direct Restart and immediate manual reconnect recovered with fresh helpers after busy/refused responses; cancellation and authentication rejection stopped retrying.");
    }
}
