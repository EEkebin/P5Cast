[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$work=Join-Path ([IO.Path]::GetTempPath()) ('P5Cast-title-fixture-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
try {
    $source=@'
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cadrivect {
    static class TitleFixture {
        const string host="ps5-title-fixture.invalid";
        const string ready="HTTP/1.1 200 OK\r\ndevice-discovery-protocol-version:00030010\r\n";
        const string game="Astro: \u30dc\u30c3\u30c8 \U0001F680";
        static readonly byte[] reply=Encoding.UTF8.GetBytes(ready+"running-app-name:"+game+"\r\n\0");
        static void Check(bool value,string reason) { if(!value) throw new Exception(reason); }
        static string Parse(string text) {
            var data=Encoding.UTF8.GetBytes(text); return DirectGameTitle.Parse(data,data.Length);
        }
        static void TestParsing() {
            Check(DirectGameTitle.Parse(reply,reply.Length)==game,"Unicode game title");
            Check(Parse(ready+"running-app-name:A\u202e B\t  C\n")=="A B C","Controls and spaces");
            Check(Parse(ready+"running-app-name:Cafe\u0301\n")=="Caf\u00e9","Unicode normalization");
            Check(Parse(ready+"running-app-name:"+new string('\u30ab',100)+"\n").Length==42,"128 UTF-8 byte bound");
            Check(Parse(ready+"running-app-titleid:ABCD\n")=="","ID used as title");
            Check(Parse(ready+"\r\nrunning-app-name:Body\n")=="","Body interpreted as a header");
            Check(Parse(ready+"running-app-name:One\nrunning-app-name:Two")=="","Duplicate title");
            Check(Parse(ready.Replace("200","620")+"running-app-name:Standby")=="","Standby");
            Check(Parse(ready.Replace("00030010","00020020")+"running-app-name:PS4")=="","Wrong discovery protocol");
            Check(Parse(ready+"running-app-name:A\0B")=="","Embedded NUL");
            byte[] invalid=(byte[])reply.Clone(); invalid[invalid.Length-4]=255;
            Check(DirectGameTitle.Parse(invalid,invalid.Length)=="","Malformed UTF-8");
            foreach(string ip in new[]{"0.0.0.0","255.255.255.255","224.0.0.1","::","ff02::1","::ffff:255.255.255.255"})
                Check(!DirectGameTitle.Unicast(IPAddress.Parse(ip)),"Non-unicast destination "+ip);
        }
        static Task<string> BeginRead(Socket server) {
            var deadline=Stopwatch.StartNew();
            while(deadline.ElapsedMilliseconds<2000) {
                var next=DirectGameTitle.ReadAsync(host);
                if(server.Poll(20000,SelectMode.SelectRead)) return next;
            }
            throw new Exception("No request from connected session");
        }
        static EndPoint Receive(Socket server) {
            byte[] data=new byte[512]; EndPoint from=new IPEndPoint(IPAddress.Any,0);
            int size=server.ReceiveFrom(data,ref from);
            Check(Encoding.ASCII.GetString(data,0,size)=="SRCH * HTTP/1.1\ndevice-discovery-protocol-version:00030010\n\0","Unexpected discovery payload");
            return from;
        }
        static void Run() {
            TestParsing();
            // Reserve the production port on loopback before any lookup. No external interface is used.
            using(var server=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)) {
                server.ExclusiveAddressUse=true;
                server.Bind(new IPEndPoint(IPAddress.Loopback,9302));
                Check(DirectGameTitle.ReadAsync(host).Result=="","Unconnected lookup");
                var start=new ProcessStartInfo(typeof(TitleFixture).Assembly.Location,"peer") {
                    UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardInput=true,CreateNoWindow=true};
                using(var process=Process.Start(start)) {
                    try {
                        DirectGameTitle.Track(host,process); process.EnableRaisingEvents=true;
                        Check(DirectGameTitle.ReadAsync("other.invalid").Result=="","Foreign session");
                        var read=BeginRead(server);
                        Check(Object.ReferenceEquals(read,DirectGameTitle.ReadAsync(host)),"Concurrent polls were not coalesced");
                        EndPoint destination=Receive(server);
                        using(var stranger=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)) {
                            stranger.Bind(new IPEndPoint(IPAddress.Loopback,0));
                            stranger.SendTo(Encoding.UTF8.GetBytes(ready+"running-app-name:wrong\n"),destination);
                        }
                        server.SendTo(reply,destination);
                        Check(read.Result==game,"Verified peer title");
                        var timer=Stopwatch.StartNew();
                        var timeout=BeginRead(server);
                        Receive(server);
                        Check(timeout.Result=="" && timer.ElapsedMilliseconds<3500,"No-response bound");
                        Check(!server.Poll(25000,SelectMode.SelectRead),"Unexpected retry");
                        var stale=BeginRead(server); destination=Receive(server);
                        process.StandardInput.Close();
                        Check(process.WaitForExit(2000),"Fixture exit");
                        server.SendTo(reply,destination);
                        Check(stale.Result=="" && DirectGameTitle.ReadAsync(host).Result=="","Stale title after disconnection");
                    } finally {
                        if(!process.HasExited) {process.StandardInput.Close(); if(!process.WaitForExit(2000)) process.Kill();}
                    }
                }
            }
        }
        static int Main(string[] args) {
            if(args.Length>0) {Console.WriteLine("PEER 127.0.0.1"); Console.Out.Flush(); Console.In.ReadLine(); return 0;}
            try {Run(); Console.WriteLine("Passed loopback-only PS5 title lookup, Unicode, request bounds, peer filtering, stale sessions and unicast validation."); return 0;}
            catch(Exception error) {Console.Error.WriteLine(error); return 1;}
        }
    }
}
'@
    $fixture=Join-Path $work 'Fixture.cs'
    [IO.File]::WriteAllText($fixture,$source)
    $exe=Join-Path $work 'TitleFixture.exe'
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /platform:x64 /target:exe "/out:$exe" $fixture (Join-Path $PSScriptRoot '..\app\DirectGameTitle.cs')
    if($LASTEXITCODE) {throw 'Title fixture did not compile.'}
    & $exe
    if($LASTEXITCODE) {throw 'Title lookup fixture failed.'}
} finally {
    $root=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($work)) -eq $root -and [IO.Path]::GetFileName($work) -match '^P5Cast-title-fixture-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
