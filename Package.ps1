[CmdletBinding()]
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[a-z0-9.-]+)?$') { throw 'Invalid version.' }
$dist = Join-Path $PSScriptRoot 'dist'
$stage = Join-Path $dist ('stage-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $stage 'LumaRelay'
$package = Join-Path $dist "LumaRelay-$Version-windows-x64.zip"
[IO.Directory]::CreateDirectory($bundle) | Out-Null
try {
    foreach ($folder in 'app\assets','sources','streaming','engine\bin') {
        [IO.Directory]::CreateDirectory((Join-Path $bundle $folder)) | Out-Null
    }
    foreach ($file in 'LumaRelay.exe','Install.ps1','Install.cmd','README.md','app\Main.xaml','app\assets\aurora.jpg') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $bundle $file)
    }
    foreach ($folder in 'sources','streaming') {
        Get-ChildItem -File -LiteralPath (Join-Path $PSScriptRoot $folder) | Copy-Item -Destination (Join-Path $bundle $folder)
    }
    foreach ($file in 'LumaPlayer.exe','SharpDX.dll','SharpDX.Direct3D11.dll','SharpDX.DXGI.dll','SharpDX-LICENSE.txt','FSR-LICENSE.txt') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "engine\bin\$file") -Destination (Join-Path $bundle "engine\bin\$file")
    }
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
