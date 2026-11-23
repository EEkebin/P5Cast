[CmdletBinding()]
param([string]$Version = '0.16.0',[string]$BuildDir=$PSScriptRoot)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[a-z0-9.-]+)?$') { throw 'Invalid version.' }
$dist = Join-Path $PSScriptRoot 'dist'
$stage = Join-Path $dist ('stage-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $stage 'P5Cast'
$package = Join-Path $dist "P5Cast-$Version-windows-x64.zip"
[IO.Directory]::CreateDirectory($bundle) | Out-Null
try {
    foreach ($folder in 'app\assets','sources','streaming','engine\bin') {
        [IO.Directory]::CreateDirectory((Join-Path $bundle $folder)) | Out-Null
    }
    foreach ($file in 'P5Cast.exe','Install.ps1','Install.cmd','README.md','app\Main.xaml','app\assets\aurora.jpg','app\assets\mark.ico') {
        $sourceRoot=if ($file -eq 'P5Cast.exe') {$BuildDir} else {$PSScriptRoot}
        Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination (Join-Path $bundle $file)
    }
    foreach ($folder in 'sources','streaming') {
        Get-ChildItem -File -LiteralPath (Join-Path $PSScriptRoot $folder) | Copy-Item -Destination (Join-Path $bundle $folder)
    }
    $runtimeFiles = @('CadrivectPlayer.exe','SharpDX.dll','SharpDX.Direct3D11.dll','SharpDX.DXGI.dll','SharpDX-LICENSE.txt','FSR-LICENSE.txt',
        'Cadrivect.Ngx.dll','nvngx_dlss.dll','NVIDIA-RTX-LICENSE.txt','Cadrivect.RtxVideo.dll','nvngx_vsr.dll',
        'NVIDIA_RTX_Video_SDK_License.pdf','NVIDIA-OPTICAL-FLOW-LICENSE.txt','Cadrivect.Decoder.dll','FFmpeg-LICENSE.txt',
        'P5Cast.Fsr.dll','amd_fidelityfx_loader_dx12.dll','amd_fidelityfx_upscaler_dx12.dll','amd_fidelityfx_framegeneration_dx12.dll','AMD-FSR-LICENSE.md','AMD-FSR-NOTICES.md',
        'P5Cast.FsrVk.dll','amd_fidelityfx_loader_vk.dll','amd_fidelityfx_upscaler_vk.dll','amd_fidelityfx_framegeneration_vk.dll',
        'P5Cast.Presentation.dll','P5Cast.Dxvk.dll','DXVK-LICENSE.txt','SDL3.dll','SDL3-LICENSE.txt')
    foreach ($file in $runtimeFiles) {
        Copy-Item -LiteralPath (Join-Path $BuildDir "engine\bin\$file") -Destination (Join-Path $bundle "engine\bin\$file")
    }
    Copy-Item -Recurse -LiteralPath (Join-Path $BuildDir 'engine\bin\direct') -Destination (Join-Path $bundle 'engine\bin\direct')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $stage 'release.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($bundle,$archive,[IO.Compression.CompressionLevel]::Optimal,$true)
    Copy-Item -LiteralPath $archive -Destination $package -Force
    $hash = (Get-FileHash $package).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$package.sha256","$hash  $([IO.Path]::GetFileName($package))" + [Environment]::NewLine)
    Get-Item -LiteralPath $package
} finally {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -eq [IO.Path]::GetFullPath($dist)) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
