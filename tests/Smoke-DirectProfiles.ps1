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
using System.Text;
using System.Linq;
using System.Reflection;

namespace Cadrivect {
    static class Program {
        public static readonly string Root=AppDomain.CurrentDomain.BaseDirectory;
        static readonly byte[] account=Enumerable.Range(10,8).Select(i=>(byte)i).ToArray();
        static readonly byte[] dummyKeys=Enumerable.Range(128,32).Select(i=>(byte)i).ToArray();

        // This test executable is also the private subprocess; it never opens a socket.
        static int Helper(string[] args) {
            using(var buffer=new MemoryStream()) {
                Console.OpenStandardInput().CopyTo(buffer); var request=buffer.ToArray();
                if(args[0]!="--register") return request.SequenceEqual(dummyKeys)?0:41;
                if(!request.SequenceEqual(account.Concat(Encoding.ASCII.GetBytes("01234567")))) return 42;
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
            foreach(string host in new string[]{"failed.invalid","truncated.invalid"}) {
                bool failed=false;
                try {DirectConsole.Pair(host,a,"0123 4567").GetAwaiter().GetResult();} catch(InvalidOperationException){failed=true;}
                Require(failed && !Directory.Exists(Path.Combine(local,"Cadrivect","consoles")),"Failed pairing persisted a record.");
            }
            var row=DirectConsole.Pair("fixture.invalid",a,"01234567").GetAwaiter().GetResult();
            Require(row.Name=="Fixture PS5" && row.AccountId==a && row.Mac=="010203040506","Registration metadata");
            string dir=Path.Combine(local,"Cadrivect","consoles");
            string persisted=File.ReadAllText(Directory.GetFiles(dir,"*.json").Single());
            Require(!persisted.Contains(Convert.ToBase64String(dummyKeys)) && !persisted.Contains("01234567"),"Clear pairing material persisted");
            using(var child=row.Connect("Cadrivect-ProfileFixture","fixture.invalid",2,60)) {
                Require(child.WaitForExit(4000) && child.ExitCode==0,"DPAPI / binary stdin round trip");
            }
            DirectConsole.Pair("fixture.invalid",a,"01234567").GetAwaiter().GetResult();
            Require(Directory.GetFiles(dir).Length==1,"Atomic pairing replacement");
            Console.WriteLine("Passed missing and ambiguous account IDs, failed and truncated pairing replies, private stdin, DPAPI recovery, and atomic pairing replacement.");
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
    & "$fx\csc.exe" /nologo /platform:x64 /target:exe "/out:$exe" /r:System.Web.Extensions.dll /r:System.Security.dll $fixture (Join-Path (Split-Path $PSScriptRoot) 'app\DirectConsole.cs')
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
