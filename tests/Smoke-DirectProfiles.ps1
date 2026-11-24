[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$qa=Join-Path ([IO.Path]::GetTempPath()) ('Cadrivect-profile-fixture-'+[guid]::NewGuid().ToString('N'))
$bin=Join-Path $qa 'engine\bin\direct'
[IO.Directory]::CreateDirectory($bin)|Out-Null
try {
    $code=@'
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Diagnostics;

namespace Cadrivect {
    static class Program {
        public static readonly string Root=AppDomain.CurrentDomain.BaseDirectory;
        static readonly byte[] account=Enumerable.Range(10,8).Select(i=>(byte)i).ToArray();
        static readonly byte[] dummyKeys=Enumerable.Range(128,32).Select(i=>(byte)i).ToArray();

        // This test executable is also the private subprocess; it never opens a socket.
        static int Helper(string[] args) {
            using(var buffer=new MemoryStream()) {
                Console.OpenStandardInput().CopyTo(buffer); var request=buffer.ToArray();
                if(args[0]!="--register") {
                    if(Environment.GetEnvironmentVariable("P5CAST_DUALSENSE_FEEDBACK")!=(args[0]=="AudioVolume"?"0":"1")) return 43;
                    string expected=args[0]=="Cadrivect-BitrateFixture" ? "100000" : "0";
                    string height=args[0]=="Cadrivect-BitrateFixture" ? "720" : "1080";
                    if(args.Length!=7 || args[3]!="60" || args[4]!=expected || args[5]!=height || args[6]!=(args[0]=="AudioVolume"?"--session-control":"--rest-control") || !request.SequenceEqual(dummyKeys)) return 41;
                    string prefix="Local\\P5Cast.Rest."+Process.GetCurrentProcess().Id+"."+args[0]+".";
                    if(args[0]=="AudioVolume") {
                        using(var map=MemoryMappedFile.OpenExisting(prefix+"volume",MemoryMappedFileRights.Read))
                        using(var view=map.CreateViewAccessor(0,4,MemoryMappedFileAccess.Read))
                        using(var ready=new EventWaitHandle(false,EventResetMode.ManualReset,"Local\\P5Cast.VolumeFixture."+Process.GetCurrentProcess().Id)) {
                            if(view.ReadInt32(0)!=0)return 48;
                            ready.Set();
                            for(int i=0;i<200;i++) {
                                if(view.ReadInt32(0)==35)return 0;
                                Thread.Sleep(10);
                            }
                            return 49;
                        }
                    }
                    using(var signal=EventWaitHandle.OpenExisting(prefix+"request"))
                    using(var ack=EventWaitHandle.OpenExisting(prefix+"accepted"))
                    using(var rejected=EventWaitHandle.OpenExisting(prefix+"rejected")) {
                        if(args[0]=="DisconnectFixture") {
                            using(var stop=EventWaitHandle.OpenExisting(prefix+"disconnect"))
                                return stop.WaitOne(4000) && !signal.WaitOne(0) ? 0 : 50;
                        } else if(args[0]=="RestRetry") {
                            if(!signal.WaitOne(4000)) return 46;
                            signal.Reset();rejected.Set();
                            if(!signal.WaitOne(4000) || rejected.WaitOne(0)) return 47;
                            signal.Reset();ack.Set();
                        } else if(args[0]=="RestAccept" || args[0]=="RestReject" || args[0]=="RestTimeout") {
                            if(!signal.WaitOne(4000)) return 43;
                            if(args[0]=="RestTimeout") return signal.WaitOne(0) ? 0 : 44;
                            (args[0]=="RestAccept" ? ack : rejected).Set();
                        } else if(signal.WaitOne(100)) return 45;
                    }
                    return 0;
                }
                if(!request.SequenceEqual(account.Concat(Encoding.ASCII.GetBytes("01234567")))) return 42;
                if(args[1]=="slow.invalid") {
                    using(var ready=EventWaitHandle.OpenExisting(Environment.GetEnvironmentVariable("P5CAST_PAIR_FIXTURE_READY"))) ready.Set();
                    Thread.Sleep(30000);
                }
                if(args[1]=="failed.invalid") return 27;
                byte[] answer=new byte[74]; Encoding.ASCII.GetBytes("CDR1").CopyTo(answer,0);
                new byte[]{1,2,3,4,5,6}.CopyTo(answer,4);
                Encoding.UTF8.GetBytes("Fixture PS5").CopyTo(answer,10); dummyKeys.CopyTo(answer,42);
                Console.OpenStandardOutput().Write(answer,0,args[1]=="truncated.invalid"?73:74);
                return 0;
            }
        }

        static void Require(bool condition,string message) {if(!condition) throw new Exception(message);}
        static void Test() {
            string local=Path.Combine(Root,"profile");
            Environment.SetEnvironmentVariable("LOCALAPPDATA",local);
            string a=Convert.ToBase64String(account);
            var resolver=typeof(DirectConsole).GetMethod("SelectAccountId",BindingFlags.NonPublic|BindingFlags.Static);
            Func<string[],string> select=rows=>(string)resolver.Invoke(null,new object[]{rows});
            Require(select(new string[]{null,"invalid"})=="","Invalid IDs");
            Require(select(new string[]{a," "+a+" "})==a,"Repeated ID");
            Require(select(new string[]{a,Convert.ToBase64String(new byte[8])})=="","Ambiguous IDs");
            string registryRoot=@"Software\P5Cast.ProfileTest."+Guid.NewGuid().ToString("N");
            using(var org=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(registryRoot)) {
                try {
                    using(var bad=org.CreateSubKey(@"Chiaki\registered_hosts\1")) bad.SetValue("target","damaged");
                    using(var good=org.CreateSubKey(@"Chiaki\registered_hosts\2")) {
                        good.SetValue("target",1000000);
                        good.SetValue("server_mac",new byte[]{1,2,3,4,5,6});
                    }
                    var import=typeof(DirectConsole).GetMethod("Registrations",BindingFlags.NonPublic|BindingFlags.Static);
                    var items=(System.Collections.Generic.List<DirectConsole>)import.Invoke(null,new object[]{registryRoot});
                    Require(items.Count==1 && items[0].Mac=="010203040506","A damaged import must not hide a usable console");
                } finally {Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registryRoot);}
            }
            foreach(string host in new string[]{"failed.invalid","truncated.invalid"}) {
                bool failed=false;
                try {DirectConsole.Pair(host,a,"0123 4567").GetAwaiter().GetResult();} catch(InvalidOperationException){failed=true;}
                Require(failed && !Directory.Exists(Path.Combine(local,"Cadrivect","consoles")),"Failed pairing persisted a record.");
            }
            string eventName="Local\\P5Cast.PairFixture."+Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable("P5CAST_PAIR_FIXTURE_READY",eventName);
            using(var ready=new EventWaitHandle(false,EventResetMode.ManualReset,eventName))
            using(var cancel=new CancellationTokenSource()) {
                var pending=DirectConsole.Pair("slow.invalid",a,"01234567",cancel.Token);
                Require(ready.WaitOne(4000),"Pairing fixture did not start");
                cancel.Cancel();
                Require(((IAsyncResult)pending).AsyncWaitHandle.WaitOne(3000),"Cancelled pairing left its helper running");
                bool cancelled=false;
                try {pending.GetAwaiter().GetResult();} catch(OperationCanceledException){cancelled=true;}
                Require(cancelled && !Directory.Exists(Path.Combine(local,"Cadrivect","consoles")),"Cancelled pair was persisted");
            }
            var row=DirectConsole.Pair("fixture.invalid",a,"01234567").GetAwaiter().GetResult();
            Require(row.Name=="Fixture PS5" && row.AccountId==a && row.Mac=="010203040506","Registration metadata");
            string dir=Path.Combine(local,"Cadrivect","consoles");
            string persisted=File.ReadAllText(Directory.GetFiles(dir,"*.json").Single());
            Require(!persisted.Contains(Convert.ToBase64String(dummyKeys)) && !persisted.Contains("01234567"),"Clear pairing material persisted");
            using(var child=row.Connect("Cadrivect-ProfileFixture","fixture.invalid",2,60)) {
                Require(child.WaitForExit(4000) && child.ExitCode==0,"DPAPI / binary stdin round trip");
            }
            using(var child=row.Connect("Cadrivect-BitrateFixture","fixture.invalid",2,60,100000,720)) {
                Require(child.WaitForExit(4000) && child.ExitCode==0,"Explicit bitrate did not reach the helper");
            }
            using(var child=row.Connect("AudioVolume","fixture.invalid",2,60,0,1080,0,false)) {
                using(var ready=new EventWaitHandle(false,EventResetMode.ManualReset,"Local\\P5Cast.VolumeFixture."+child.Id))
                    Require(ready.WaitOne(4000),"Saved mute available before the first audio frame");
                DirectConsole.SetVolume(child,35);
                Require(child.WaitForExit(4000) && child.ExitCode==0,"Live mapped Direct volume update");
            }
            bool rejected=false;
            try {row.Connect("Cadrivect-BitrateFixture","fixture.invalid",2,60,100001);} catch(ArgumentOutOfRangeException){rejected=true;}
            Require(rejected,"Out-of-range source bitrate");
            using(var first=row.Connect("DisconnectFixture","fixture.invalid",2,60))
            using(var other=row.Connect("DisconnectFixture","fixture.invalid",2,60)) {
                DirectConsole.RequestDisconnect(first);
                Require(first.WaitForExit(2000) && first.ExitCode==0,"Disconnect reached its session without requesting rest");
                DirectConsole.RequestDisconnect(first);
                Require(!other.HasExited,"Disconnect reached another helper sharing the same pipe name");
                DirectConsole.RequestDisconnect(other);
                Require(other.WaitForExit(2000) && other.ExitCode==0,"Second session should also exit normally");
            }
            using(var child=row.Connect("RestAccept","fixture.invalid",2,60)) {
                bool mismatch=false;
                try {DirectConsole.RequestRestAsync("RestOther",child);} catch(InvalidOperationException){mismatch=true;}
                Require(mismatch,"Other session's rest request accepted");
                var rest=DirectConsole.RequestRestAsync("RestAccept",child);
                Require(Object.ReferenceEquals(rest,DirectConsole.RequestRestAsync("RestAccept",child)),"Duplicate rest command");
                rest.GetAwaiter().GetResult();
                Require(child.WaitForExit(1000) && child.ExitCode==0,"Rest acknowledgement");
            }
            foreach(string pipe in new[]{"RestReject","RestTimeout"}) {
                using(var child=row.Connect(pipe,"fixture.invalid",2,60)) {
                    bool failed=false;
                    try {DirectConsole.RequestRestAsync(pipe,child).GetAwaiter().GetResult();}
                    catch(TimeoutException){failed=pipe=="RestTimeout";}
                    catch(InvalidOperationException){failed=pipe=="RestReject";}
                    Require(failed && child.WaitForExit(1000) && child.ExitCode==0,"Unacknowledged rest request reported success");
                }
            }
            using(var child=row.Connect("RestRetry","fixture.invalid",2,60)) {
                bool retryNeeded=false;
                try {DirectConsole.RequestRestAsync("RestRetry",child).GetAwaiter().GetResult();}
                catch(InvalidOperationException) {retryNeeded=true;}
                Require(retryNeeded,"The first rest attempt should report a rejection.");
                DirectConsole.RequestRestAsync("RestRetry",child).GetAwaiter().GetResult();
                Require(child.WaitForExit(1000) && child.ExitCode==0,"A rejected rest request could not be retried on the same session.");
            }
            DirectConsole.Pair("fixture.invalid",a,"01234567").GetAwaiter().GetResult();
            Require(Directory.GetFiles(dir).Length==1,"Atomic pairing replacement");
            Console.WriteLine("Passed private registration, DPAPI, bitrate forwarding, scoped rest acknowledgement, rejection, timeout, and ordinary exit without rest.");
        }
        static int Main(string[] args) {
            try {if(args.Length!=0) return Helper(args); Test(); return 0;}
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
}
'@
    $fixture=Join-Path $qa 'Fixture.cs'
    [IO.File]::WriteAllText($fixture,$code)
    $exe=Join-Path $qa 'Profiles.exe'
    $fx=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
    & "$fx\csc.exe" /nologo /platform:x64 /target:exe "/out:$exe" /r:System.Web.Extensions.dll /r:System.Security.dll $fixture (Join-Path (Split-Path $PSScriptRoot) 'app\DirectConsole.cs') (Join-Path (Split-Path $PSScriptRoot) 'app\DirectGameTitle.cs')
    if($LASTEXITCODE -ne 0){throw 'The profile smoke fixture did not compile.'}
    Copy-Item -LiteralPath $exe -Destination (Join-Path $bin 'CadrivectDirect.exe')
    & $exe
    if($LASTEXITCODE -ne 0){throw 'The Direct profile smoke fixture failed.'}
} finally {
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($qa)) -eq $temp -and [IO.Path]::GetFileName($qa) -match '^Cadrivect-profile-fixture-[a-f0-9]{32}$'){
        Remove-Item -LiteralPath $qa -Recurse -Force
    }
}
