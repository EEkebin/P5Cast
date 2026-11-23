[CmdletBinding()]
param([string] $PlayerExe,[ValidateSet('auto','off')][string] $Hdr='auto')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if (!$PlayerExe) { $PlayerExe=Join-Path $repo 'engine\bin\CadrivectPlayer.exe' }
$player=(Get-Item -LiteralPath $PlayerExe).FullName
$targetDir=Join-Path $env:TEMP ('Cadrivect-display-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($targetDir) | Out-Null
$probe=Join-Path $targetDir 'DisplayProbe.exe'
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /optimize+ /platform:x64 /r:System.Drawing.dll /r:System.Web.Extensions.dll "/out:$probe" "$PSScriptRoot\DisplayProbe.cs" "$repo\engine\DisplayTarget.cs"
if ($LASTEXITCODE) { throw 'Display probe compilation failed.' }
foreach ($destination in 'alternate','primary') {
    $report=Join-Path $targetDir "display-$destination.json"
    & $probe $player $report $Hdr $destination
    if ($LASTEXITCODE) { throw "Display selection smoke test failed on the $destination trip." }
    if (!(Test-Path -LiteralPath $report)) { continue } # single-monitor return trip
    $result=Get-Content -Raw -Encoding UTF8 -LiteralPath $report | ConvertFrom-Json
    if ($result.fullscreen -or !$result.display -or $result.renderWidth -ne 1280 -or $result.renderHeight -ne 720 -or $result.presented -lt 8) {
        throw "Unexpected renderer report. See $report"
    }
    $result
}
