[CmdletBinding()]
param(
    [string] $PlayerExe,
    [string] $DirectRuntime,
    [string[]] $H264Frames,
    [string] $OutputDirectory
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if (!$PlayerExe) { $PlayerExe=Join-Path $repo 'engine\bin\CadrivectPlayer.exe' }
if (!$DirectRuntime) {
    $runtime=& "$repo\sources\Prepare-Decoder.ps1" | ConvertFrom-Json
    if (!$runtime.ok) { throw $runtime.error }
    $DirectRuntime=$runtime.runtime
}
if (!$H264Frames) {
    $H264Frames=@(Get-ChildItem -Path (Join-Path $PSScriptRoot 'fixtures') -Filter '*.h264' | Sort-Object Name | ForEach-Object FullName)
}
if ($H264Frames.Count -lt 2) { throw 'Provide at least two independent H.264 IDR access units with -H264Frames.' }
if (!$OutputDirectory) { $OutputDirectory=Join-Path $env:TEMP ('Cadrivect-overlay-'+[guid]::NewGuid().ToString('N')) }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$packets=Join-Path $OutputDirectory 'fixture.bin'
$writer=New-Object IO.BinaryWriter([IO.File]::Create($packets))
try {
    foreach ($file in $H264Frames) {
        $bytes=[IO.File]::ReadAllBytes((Get-Item -LiteralPath $file).FullName)
        $writer.Write([int]$bytes.Length)
        $writer.Write($bytes)
    }
} finally { $writer.Dispose() }
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$probe=Join-Path $OutputDirectory 'OverlayProbe.exe'
& "$framework\csc.exe" /nologo /optimize+ /platform:x64 /r:System.Drawing.dll /r:System.Web.Extensions.dll "/out:$probe" "$PSScriptRoot\OverlayProbe.cs"
if ($LASTEXITCODE) { throw 'Overlay probe compilation failed.' }
& $probe (Get-Item -LiteralPath $PlayerExe).FullName $DirectRuntime $packets $OutputDirectory
if ($LASTEXITCODE) { throw "Playback control test failed. Inspect $OutputDirectory" }
