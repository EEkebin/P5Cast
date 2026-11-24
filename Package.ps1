[CmdletBinding()]
param([string]$Version = '0.26.1',[string]$BuildDir=$PSScriptRoot)
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
    foreach ($file in 'sources\Get-SourceStatus.ps1','sources\Install-DlssNeural.ps1','sources\Install-Fruc.ps1',
             'sources\Install-OpenNrModel.ps1','sources\OpenNrModelData.cs',
             'sources\Prepare-Decoder.ps1','sources\SignalRun.cs','sources\SourceTools.ps1','sources\Start-Source.ps1',
             'streaming\HostApi.cs','streaming\Relay.ps1','streaming\SunshineDisplay.cs','streaming\packages.json') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $bundle $file)
    }
    $runtimeFiles = @('CadrivectPlayer.exe','SharpDX.dll','SharpDX.Direct3D11.dll','SharpDX.DXGI.dll','SharpDX-LICENSE.txt','FSR-LICENSE.txt','NVIDIA-NIS-LICENSE.txt',
        'P5Cast.NeuralVk.dll','P5Cast.OpenNr.dll','NVIDIA-NEURAL-RENDERING-LICENSE.txt','Cadrivect.Ngx.dll','nvngx_dlss.dll','NVIDIA-RTX-LICENSE.txt','Cadrivect.RtxVideo.dll','nvngx_vsr.dll',
        'NVIDIA_RTX_Video_SDK_License.pdf','NVIDIA-OPTICAL-FLOW-LICENSE.txt','P5Cast.Fruc.dll','P5Cast.FrucHost.exe','Cadrivect.Decoder.dll','FFmpeg-LICENSE.txt',
        'P5Cast.Fsr.dll','amd_fidelityfx_loader_dx12.dll','amd_fidelityfx_upscaler_dx12.dll','amd_fidelityfx_framegeneration_dx12.dll','AMD-FSR-LICENSE.md','AMD-FSR-NOTICES.md',
        'P5Cast.FsrVk.dll','amd_fidelityfx_loader_vk.dll','amd_fidelityfx_upscaler_vk.dll','amd_fidelityfx_framegeneration_vk.dll',
        'P5Cast.Presentation.dll','P5Cast.Dxvk.dll','P5Cast.DxgiVk.dll','P5Cast.VulkanCapture.dll','DXVK-LICENSE.txt','SDL3.dll','SDL3-LICENSE.txt')
    foreach ($file in $runtimeFiles) {
        Copy-Item -LiteralPath (Join-Path $BuildDir "engine\bin\$file") -Destination (Join-Path $bundle "engine\bin\$file")
    }
    Copy-Item -Recurse -LiteralPath (Join-Path $BuildDir 'engine\bin\direct') -Destination (Join-Path $bundle 'engine\bin\direct')
    # The open provider ships code and notices. A model is downloaded separately for each user.
    foreach ($part in 'shaders','ptx','licenses') {
        $source=Join-Path $BuildDir "engine\bin\open-nr\$part"
        $target=[IO.Directory]::CreateDirectory((Join-Path $bundle "engine\bin\open-nr\$part")).FullName
        $pattern=switch ($part) {'shaders' {'*.spv'} 'ptx' {'*.ptx'} 'licenses' {'*.txt'}}
        $assets=@(Get-ChildItem -LiteralPath $source -File -Filter $pattern)
        if (!$assets.Count) {throw "Missing OpenDLSS-NR $part."}
        $assets | Copy-Item -Destination $target
    }
    $files = @(Get-ChildItem -LiteralPath $bundle -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($bundle.Length+1); sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
    [IO.File]::WriteAllText((Join-Path $bundle 'payload.json'),(@{version=$Version;files=$files} | ConvertTo-Json -Depth 3))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $stage 'release.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($bundle,$archive,[IO.Compression.CompressionLevel]::Optimal,$true)
    $hash = (Get-FileHash $archive).Hash.ToLowerInvariant()
    $checksum = Join-Path $stage 'release.sha256'
    [IO.File]::WriteAllText($checksum,"$hash  $([IO.Path]::GetFileName($package))" + [Environment]::NewLine)
    $lock = New-Object Threading.Mutex($false,("Local\P5Cast.Package."+ $Version))
    $locked = $false
    try {
        try { $locked=$lock.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $locked=$true }
        if (!$locked) { throw "Another package operation for $Version is still running." }
        $backup = Join-Path $stage 'previous.zip'
        if ([IO.File]::Exists($package)) { [IO.File]::Replace($archive,$package,$backup) }
        else { [IO.File]::Move($archive,$package) }
        try {
            if ([IO.File]::Exists("$package.sha256")) { [IO.File]::Replace($checksum,"$package.sha256",[System.Management.Automation.Language.NullString]::Value) }
            else { [IO.File]::Move($checksum,"$package.sha256") }
        } catch {
            if ([IO.File]::Exists($backup)) { [IO.File]::Replace($backup,$package,[System.Management.Automation.Language.NullString]::Value) }
            else { [IO.File]::Delete($package) }
            throw
        }
    } finally {
        if ($locked) { $lock.ReleaseMutex() }
        $lock.Dispose()
    }
    Get-Item -LiteralPath $package
} finally {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -eq [IO.Path]::GetFullPath($dist)) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
