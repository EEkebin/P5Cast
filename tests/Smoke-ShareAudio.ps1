# Test Discord's documented application + children capture model with Windows'
# process-loopback API. Uses only the isolated fixture tree and a quiet synthetic tone.
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$sdl=Join-Path $repo 'engine\bin\direct\SDL2.dll'
if(!(Test-Path -LiteralPath $sdl)){throw 'Build the Direct runtime before running the share-audio smoke.'}
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$work=Join-Path $tempRoot ('P5Cast-share-audio-'+[Guid]::NewGuid().ToString('N'))
$core=Join-Path $work 'engine\bin\direct'
[IO.Directory]::CreateDirectory($core) | Out-Null
try {
    . (Join-Path $repo 'engine\nvidia\Use-Compiler.ps1')
    $native=Join-Path $work 'ShareAudioMeter.dll'
    & cl.exe /nologo /EHsc /std:c++17 /O2 /MT /LD "/Fo$work\ShareAudioMeter.obj" (Join-Path $PSScriptRoot 'ShareAudioMeter.cpp') /link "/OUT:$native" "/IMPLIB:$work\ShareAudioMeter.lib" /NODEFAULTLIB:oldnames.lib ole32.lib mmdevapi.lib
    if($LASTEXITCODE){throw 'The WASAPI process-loopback fixture did not compile.'}
    $exe=Join-Path $work 'ShareAudioFixture.exe'
    $csc=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /platform:x64 /target:exe "/out:$exe" /r:System.Web.Extensions.dll /r:System.Security.dll (Join-Path $PSScriptRoot 'ShareAudioFixture.cs') (Join-Path $repo 'app\DirectConsole.cs') (Join-Path $repo 'app\DirectGameTitle.cs')
    if($LASTEXITCODE){throw 'The synthetic launcher fixture did not compile.'}
    Copy-Item -LiteralPath $exe -Destination (Join-Path $core 'CadrivectDirect.exe')
    Copy-Item -LiteralPath $sdl -Destination $core
    & $exe
    if($LASTEXITCODE){throw 'The Direct application audio tree test failed.'}
} finally {
    $target=[IO.Path]::GetFullPath($work)
    if([IO.Path]::GetDirectoryName($target) -eq $tempRoot -and [IO.Path]::GetFileName($target) -match '^P5Cast-share-audio-[0-9a-f]{32}$'){
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
