[CmdletBinding()]
param([string]$Bin=(Join-Path $PSScriptRoot '..\..\bin'),[string]$Display,[ValidateSet('d3d12','vulkan')][string]$Backend='d3d12')
$ErrorActionPreference='Stop'
$binPath=(Resolve-Path -LiteralPath $Bin).Path
$id='P5Cast-AMD-FG-'+[guid]::NewGuid().ToString('N')
$stage=[IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) $id)).FullName
try {
    $refs=@()
    foreach($lib in 'SharpDX','SharpDX.DXGI','SharpDX.Direct3D11') {
        Copy-Item -LiteralPath (Join-Path $binPath "$lib.dll") -Destination $stage
        $refs+="/r:$stage\$lib.dll"
    }
    $native=if($Backend -eq 'vulkan'){
        'P5Cast.Dxvk.dll','P5Cast.FsrVk.dll','amd_fidelityfx_loader_vk.dll','amd_fidelityfx_framegeneration_vk.dll'
    }else{'Cadrivect.Ngx.dll','P5Cast.Fsr.dll','amd_fidelityfx_loader_dx12.dll','amd_fidelityfx_framegeneration_dx12.dll'}
    foreach($name in $native) {
        Copy-Item -LiteralPath (Join-Path $binPath $name) -Destination $stage
    }
    $probe=Join-Path $stage 'VerifyFrameGeneration.exe'
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /optimize+ /platform:x64 @refs "/out:$probe" "$PSScriptRoot\..\..\D3D12Effects.cs" "$PSScriptRoot\..\..\VulkanEffects.cs" "$PSScriptRoot\FsrFrameGeneration.cs" "$PSScriptRoot\VerifyFrameGeneration.cs"
    if($LASTEXITCODE){throw 'AMD frame generation fixture failed to compile.'}
    & $probe $Backend
    if($LASTEXITCODE){throw 'AMD frame generation fixture failed.'}
    if($Display) {
        $framework=Split-Path $csc
        $playbackRefs='System.Xaml','System.Web.Extensions','System.Windows.Forms','System.Drawing' | ForEach-Object {"/r:$_.dll"}
        $playbackRefs+='PresentationCore','PresentationFramework','WindowsBase' | ForEach-Object {"/r:$framework\WPF\$_.dll"}
        $tests=Join-Path $PSScriptRoot '..\..\..\tests'
        $playback=Join-Path $stage 'VerifyAmdPlayback.exe'
        & $csc /nologo /optimize+ /platform:x64 /main:AmdFgRunner @playbackRefs "/out:$playback" "$tests\EmbeddedProbe.cs" "$tests\ComparisonProbe.cs" "$PSScriptRoot\VerifyAmdPlayback.cs"
        if($LASTEXITCODE){throw 'AMD embedded playback fixture failed to compile.'}
        & $playback (Join-Path $binPath 'CadrivectPlayer.exe') $Display $Backend
        if($LASTEXITCODE){throw 'AMD SDR/HDR playback fixture failed.'}
    }
} finally {
    if([IO.Path]::GetDirectoryName($stage) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and [IO.Path]::GetFileName($stage) -eq $id) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
