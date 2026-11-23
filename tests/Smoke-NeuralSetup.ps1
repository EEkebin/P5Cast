[CmdletBinding()]
param([string]$ArchivePath)
$ErrorActionPreference='Stop'
$savedLocal=$env:LOCALAPPDATA
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$id='P5Cast-NeuralSetup-'+[Guid]::NewGuid().ToString('N')
$testArea=Join-Path $tempRoot $id
$powershell=Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
try {
    $bin=[IO.Directory]::CreateDirectory((Join-Path $testArea 'engine\bin')).FullName
    $scripts=[IO.Directory]::CreateDirectory((Join-Path $testArea 'sources')).FullName
    $script=Join-Path $scripts 'Install-DlssNeural.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\sources\Install-DlssNeural.ps1') -Destination $script
    $source=Join-Path $testArea 'Fixture.cs'
    @'
using System;
using System.IO;
internal static class Fixture {
    static int Main(string[] args) {
        var file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fixture.json");
        File.Copy(file,args[Array.IndexOf(args,"--report")+1]);
        return 0;
    }
}
'@ | Set-Content -LiteralPath $source -Encoding UTF8
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe "/out:$bin\CadrivectPlayer.exe" $source
    if($LASTEXITCODE){throw 'Could not compile the capability fixture.'}
    $env:LOCALAPPDATA=Join-Path $testArea 'userdata'
    $fixture=Join-Path $bin 'fixture.json'
    $expected=Join-Path $env:LOCALAPPDATA 'P5Cast\providers\dlss-nr\310.9.16'
    '{"adapter":"NVIDIA GeForce RTX 4090","dlssNeuralRuntimeDownloadAvailable":false}' | Set-Content -LiteralPath $fixture
    $result=& $powershell -NoProfile -ExecutionPolicy Bypass -File $script -ArchivePath (Join-Path $testArea 'missing.7z')
    if(!$LASTEXITCODE -or (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'P5Cast\providers')) -or ($result|ConvertFrom-Json).message -notmatch 'did not qualify') {
        throw "Ineligible fixture was not rejected before accessing the archive. $result"
    }
    if($ArchivePath) {
        '{"dlssNeuralRuntimeDownloadAvailable":true}' | Set-Content -LiteralPath $fixture
        foreach($attempt in 1..2) {
            $result=& $powershell -NoProfile -ExecutionPolicy Bypass -File $script -ArchivePath $ArchivePath
            if($LASTEXITCODE -or !($result|ConvertFrom-Json).ok){throw "Offline installation failed on attempt $attempt. $result"}
        }
        if((Get-FileHash -LiteralPath "$expected\nvngx_dlssnr.dll").Hash -ne '2BC19BC09A7B89DD44F73E2AC0108FCC586E2154BDD0830F242F4F040E28789F' -or
            !(Test-Path -LiteralPath "$expected\LICENSE.txt")) {throw 'The installed runtime or license is missing or incorrect.'}
        Write-Output 'Verified the signed NVIDIA runtime installation and retry against the supplied archive.'
    }
    Write-Output 'Verified the unsupported adapter performs no setup.'
} finally {
    $env:LOCALAPPDATA=$savedLocal
    $resolvedTestArea=[IO.Path]::GetFullPath($testArea)
    if([IO.Path]::GetDirectoryName($resolvedTestArea) -eq $tempRoot -and [IO.Path]::GetFileName($resolvedTestArea) -eq $id -and [IO.Directory]::Exists($resolvedTestArea)) {
        Remove-Item -LiteralPath $resolvedTestArea -Recurse -Force
    }
}
