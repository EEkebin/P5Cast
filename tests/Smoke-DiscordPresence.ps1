# Compile a test-only assembly which always injects an isolated named-pipe server.
# This test does not start Discord or connect to the real Discord desktop client.
$ErrorActionPreference='Stop'
$stage=Join-Path ([IO.Path]::GetTempPath()) ('P5Cast-discord-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
$exe=Join-Path $stage 'DiscordPresenceProbe.exe'
try {
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /codepage:65001 /platform:x64 /optimize+ /r:System.Web.Extensions.dll "/out:$exe" (Join-Path $PSScriptRoot '..\app\DiscordPresence.cs') (Join-Path $PSScriptRoot 'DiscordPresenceProbe.cs')
    if($LASTEXITCODE) {throw 'Discord fake endpoint test did not compile.'}
    & $exe
    if($LASTEXITCODE) {throw 'Discord fake endpoint test failed.'}
} finally {
    [IO.File]::Delete($exe);[IO.Directory]::Delete($stage)
}
