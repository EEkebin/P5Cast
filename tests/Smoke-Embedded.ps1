[CmdletBinding()]
param([string]$PlayerExe,[string]$DirectRuntime,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if (!$PlayerExe) { $PlayerExe=Join-Path $repo 'engine\bin\CadrivectPlayer.exe' }
if (!$DirectRuntime) {
    $runtime=& "$repo\sources\Prepare-Decoder.ps1" | ConvertFrom-Json
    if (!$runtime.ok) { throw $runtime.error }
    $DirectRuntime=$runtime.runtime
}
if (!$OutputDirectory) { $OutputDirectory=Join-Path $env:TEMP ('Cadrivect-embedded-'+[guid]::NewGuid().ToString('N')) }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.Xaml','System.Web.Extensions','System.Windows.Forms','System.Drawing') | ForEach-Object { "/r:$_.dll" }
$refs+=@('PresentationCore','PresentationFramework','WindowsBase') | ForEach-Object { "/r:$framework\WPF\$_.dll" }
$probe=Join-Path $OutputDirectory 'EmbeddedProbe.exe'
& "$framework\csc.exe" /nologo /optimize+ /platform:x64 @refs "/out:$probe" "$PSScriptRoot\EmbeddedProbe.cs"
if ($LASTEXITCODE) { throw 'Embedded WPF probe compilation failed.' }
& $probe $PlayerExe $DirectRuntime "$PSScriptRoot\fixtures\text.h264" "$PSScriptRoot\fixtures\text-inverted.h264" "$OutputDirectory\report.json"
if ($LASTEXITCODE) { throw "Embedded playback test failed. Inspect $OutputDirectory" }
& $probe $PlayerExe $DirectRuntime "$PSScriptRoot\fixtures\text.h264" "$PSScriptRoot\fixtures\text-inverted.h264" "$OutputDirectory\report.json" --offscreen-capture
if ($LASTEXITCODE) { throw "Offscreen window-capture test failed. Inspect $OutputDirectory" }
