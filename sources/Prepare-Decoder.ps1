# Prepare the FFmpeg shared libraries consumed by P5Cast's direct video decoder.
[CmdletBinding()]
param([string]$Archive)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'SourceTools.ps1')
$files=[ordered]@{
    'avcodec-63.dll'='930849fe09b054bcb3d238d4444b9aefddbc3c7b24aa07131d62b6579fcd7610'
    'avutil-61.dll'='e85de4242b3ed95834d6a77dcbdb9b567986100fc573d1b4584db1b709ece2df'
    'swresample-7.dll'='10d0de2dd3e5c2ff61ae58b9ef38197708a1a918ecc6581eea3c2048fd92409c'
}
$cache=Join-Path $env:LOCALAPPDATA 'Cadrivect\Decoder'
$version="ffmpeg-63.1.102-$chiakiVersion"

function Get-DecoderSha256([string]$Path) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try {
        $stream=[IO.File]::OpenRead($Path)
        try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
        finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
}
function Test-Libraries([string]$Folder,[switch]$IsRuntime) {
    if (!(Test-Path -LiteralPath $Folder -PathType Container)) {return $false}
    if ($IsRuntime) {
        $dir=Get-Item -LiteralPath $Folder -Force
        if (($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) -or @(Get-ChildItem -LiteralPath $Folder -Force).Count -ne $files.Count) {return $false}
    }
    foreach($name in $files.Keys) {
        $path=Join-Path $Folder $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {return $false}
        if (((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-DecoderSha256 $path) -ne $files[$name]) {return $false}
    }
    return $true
}
function Prepare-Libraries {
    if (![Environment]::Is64BitOperatingSystem) {throw 'Direct decoding needs 64-bit Windows.'}
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    foreach($dir in (Get-ChildItem -LiteralPath $cache -Directory -Filter "$version*" | Sort-Object LastWriteTimeUtc -Descending)) {
        if (Test-Libraries $dir.FullName -IsRuntime) {return $dir.FullName}
    }
    $stage=Join-Path $cache ('stage-'+[guid]::NewGuid().ToString('N'))
    $payload=Join-Path $stage 'runtime'
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    try {
        $legacy=Split-Path -Parent $chiakiExe
        if (!$Archive -and (Test-Libraries $legacy)) {
            foreach($name in $files.Keys) {[IO.File]::Copy((Join-Path $legacy $name),(Join-Path $payload $name))}
        } else {
            if ($Archive) {$zipPath=(Resolve-Path -LiteralPath $Archive).Path}
            else {
                $zipPath=Join-Path $stage 'upstream.zip'
                $client=New-Object Net.WebClient
                $protocol=[Net.ServicePointManager]::SecurityProtocol
                try {
                    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
                    $client.DownloadFile($chiakiArchiveUrl,$zipPath)
                } finally { $client.Dispose(); [Net.ServicePointManager]::SecurityProtocol=$protocol }
            }
            if ((Get-DecoderSha256 $zipPath) -ne $chiakiArchiveSha256) {throw 'The FFmpeg source archive did not match its pinned SHA-256.'}
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
            try {
                foreach($name in $files.Keys) {
                    $entry=$zip.GetEntry("chiaki-ng-Win/$name")
                    if ($null -eq $entry) {throw "The upstream archive is missing $name."}
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $payload $name))
                }
            } finally {$zip.Dispose()}
        }
        if (!(Test-Libraries $payload -IsRuntime)) {throw 'The staged FFmpeg libraries failed the pinned SHA-256 check.'}
        # A damaged directory may still have a DLL in use. Publish fresh verified files alongside it.
        $destination=Join-Path $cache $version
        if (Test-Path -LiteralPath $destination) {$destination+='-'+[guid]::NewGuid().ToString('N')}
        [IO.Directory]::Move($payload,$destination)
        return $destination
    } finally {
        $full=[IO.Path]::GetFullPath($stage)
        if ([IO.Path]::GetDirectoryName($full) -eq [IO.Path]::GetFullPath($cache) -and [IO.Path]::GetFileName($full) -match '^stage-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
$lock=New-Object Threading.Mutex($false,'Local\Cadrivect-Prepare-FFmpeg-63')
$owns=$false
try {
    try {$owns=$lock.WaitOne([TimeSpan]::FromMinutes(5))} catch [Threading.AbandonedMutexException] {$owns=$true}
    if (!$owns) {throw 'Another decoder installation is still running.'}
    $runtime=Prepare-Libraries
    @{runtime=$runtime;ok=$true} | ConvertTo-Json -Compress
} catch {
    @{ok=$false;error=$_.Exception.GetBaseException().Message} | ConvertTo-Json -Compress
    exit 1
} finally {
    if ($owns) {$lock.ReleaseMutex()}
    $lock.Dispose()
}
