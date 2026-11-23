[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$stage=Join-Path ([IO.Path]::GetTempPath()) ("P5Cast-stream-volume-"+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
try{
    . "$repo\engine\nvidia\Use-Compiler.ps1"
    $fixture=Join-Path $stage 'SilentAudioSession.exe'
    & cl.exe /nologo /MT /O2 /EHsc "/Fo$stage\SilentAudioSession.obj" "$PSScriptRoot\SilentAudioSession.cpp" /link /NODEFAULTLIB:oldnames.lib "/out:$fixture" ole32.lib
    if($LASTEXITCODE){throw 'Could not build silent audio sessions.'}
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $test=Join-Path $stage 'StreamAudioProbe.exe'
    & $csc /nologo /target:exe /optimize+ /platform:x64 "/out:$test" "$repo\app\StreamAudio.cs" "$PSScriptRoot\StreamAudioProbe.cs"
    if($LASTEXITCODE){throw 'Could not build process-session audio probe.'}
    & $test $fixture
    if($LASTEXITCODE){throw 'Process-session volume test failed.'}
}finally{
    Get-ChildItem -LiteralPath $stage -File | Remove-Item -Force
    Remove-Item -LiteralPath $stage -Force
}
