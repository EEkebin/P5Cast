[CmdletBinding()]
param([string]$PlayerExe,
      [string]$Display,[switch]$MeasureOnly,[string]$OutputDirectory,[switch]$CheckNis)
$ErrorActionPreference='Stop'
if(!$PlayerExe){$PlayerExe=Join-Path (Split-Path $PSScriptRoot) 'engine\bin\CadrivectPlayer.exe'}
$PlayerExe=(Resolve-Path -LiteralPath $PlayerExe).Path
$bin=Split-Path $PlayerExe
$tmpRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$id='P5Cast-NgxQuality-'+[guid]::NewGuid().ToString('N')
$stage=Join-Path $tmpRoot $id
[void][IO.Directory]::CreateDirectory($stage)
try {
    $fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $refs='System.Xaml','System.Web.Extensions','System.Windows.Forms','System.Drawing' | ForEach-Object {"/r:$_.dll"}
    $refs+='PresentationCore','PresentationFramework','WindowsBase' | ForEach-Object {"/r:$fx\WPF\$_.dll"}
    foreach($dll in 'SharpDX','SharpDX.DXGI','SharpDX.Direct3D11'){
        $path=Join-Path $bin "$dll.dll"
        Copy-Item -LiteralPath $path -Destination $stage
        $refs+="/r:$path"
    }
    $exe=Join-Path $stage 'NgxQualityProbe.exe'
    $entry=if($CheckNis){'NisQualityRunner'}else{'NgxQualityRunner'}
    & "$fx\csc.exe" /nologo /optimize+ /platform:x64 "/main:$entry" @refs "/out:$exe" "$PSScriptRoot\EmbeddedProbe.cs" "$PSScriptRoot\ComparisonProbe.cs" "$PSScriptRoot\NgxQualityProbe.cs" "$PSScriptRoot\NisQualityProbe.cs"
    if($LASTEXITCODE){throw "Could not compile $entry."}
    if(!$Display){$Display='auto'}
    $mode=if($MeasureOnly){'measure'}else{'verify'}
    $crops=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{'-'}
    & $exe $PlayerExe $Display $mode $crops
    if($LASTEXITCODE){throw "$entry failed."}
} finally {
    $target=[IO.Path]::GetFullPath($stage)
    if([IO.Path]::GetDirectoryName($target) -eq $tmpRoot -and [IO.Path]::GetFileName($target) -eq $id){Remove-Item -Recurse -Force -LiteralPath $target}
}
