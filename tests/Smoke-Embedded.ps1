[CmdletBinding()]
param([string]$PlayerExe,[string]$DirectRuntime,[string]$OutputDirectory,
      [ValidateSet('auto','d3d11','d3d12','vulkan','opengl')][string]$Renderer='auto',[string]$HdrDisplay,[switch]$CheckComparison,[switch]$CheckNeuralRendering,[switch]$CheckFruc)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if (!$PlayerExe) { $PlayerExe=Join-Path $repo 'engine\bin\CadrivectPlayer.exe' }
if (!$DirectRuntime -and !$CheckNeuralRendering -and (!$CheckFruc -or $HdrDisplay)) {
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
$entry=if($CheckFruc -and !$HdrDisplay){'FrucPlaybackRunner'}else{'EmbeddedProbe'}
& "$framework\csc.exe" /nologo /optimize+ /platform:x64 "/main:$entry" @refs "/out:$probe" "$PSScriptRoot\EmbeddedProbe.cs" "$PSScriptRoot\ComparisonProbe.cs" "$PSScriptRoot\FrucPlaybackProbe.cs"
if ($LASTEXITCODE) { throw 'Embedded WPF probe compilation failed.' }
if ($CheckFruc -and !$HdrDisplay) {
    $PlayerExe=(Resolve-Path -LiteralPath $PlayerExe).Path
    if($Renderer -eq 'auto'){$Renderer='d3d11'}
    if($Renderer -notin 'd3d11','d3d12'){throw 'FRUC playback verification requires d3d11 or d3d12.'}
    $caps=(& $PlayerExe --capabilities --quiet --renderer $Renderer | Out-String | ConvertFrom-Json)
    if($null -eq $caps.frucFrameGeneration){throw 'The selected player does not report FRUC capabilities.'}
    $expected=if($caps.frucFrameGeneration){
        (@('available')+@('nis','dlaa','rtxvideo' | Where-Object {$caps.$_})) -join ','
    }else{'unavailable'}
    $amd=if($caps.amdFrameGeneration){'amd'}else{'none'}
    & $probe $PlayerExe $expected $Renderer $amd
    if($LASTEXITCODE){throw 'Embedded FRUC playback verification failed.'}
    return
}
if ($CheckNeuralRendering) {
    & $probe $PlayerExe unused unused unused "$OutputDirectory\neural.json" vulkan --neural
    if ($LASTEXITCODE) {throw "Embedded Neural Rendering verification failed. Inspect $OutputDirectory"}
    return
}
if ($CheckComparison) {
    # Midtone glyph edges leave headroom for sharpening at the native video resolution.
    & $probe $PlayerExe $DirectRuntime "$PSScriptRoot\fixtures\comparison.h264" "$PSScriptRoot\fixtures\comparison-inverted.h264" "$OutputDirectory\comparison.json" $Renderer --comparison
    if ($LASTEXITCODE) { throw "Embedded comparison test failed. Inspect $OutputDirectory" }
    return
}
if ($HdrDisplay) {
    $fixture=if($CheckFruc){'motion-pq10.hevc'}else{'text-pq10.hevc'}
    $effects=if($CheckFruc){'rtx-fruc'}else{'original'}
    & $probe $PlayerExe $DirectRuntime "$PSScriptRoot\fixtures\$fixture" $effects "$OutputDirectory\hdr.json" $Renderer "--hdr-display=$HdrDisplay"
    if ($LASTEXITCODE) { throw "Embedded HDR test failed. Inspect $OutputDirectory" }
    return
}
& $probe $PlayerExe $DirectRuntime "$PSScriptRoot\fixtures\text.h264" "$PSScriptRoot\fixtures\text-inverted.h264" "$OutputDirectory\report.json" $Renderer
if ($LASTEXITCODE) { throw "Embedded playback test failed. Inspect $OutputDirectory" }
& $probe $PlayerExe $DirectRuntime "$PSScriptRoot\fixtures\text.h264" "$PSScriptRoot\fixtures\text-inverted.h264" "$OutputDirectory\report.json" $Renderer --offscreen-capture
if ($LASTEXITCODE) { throw "Offscreen window-capture test failed. Inspect $OutputDirectory" }
