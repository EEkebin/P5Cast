[CmdletBinding()]
param([string]$OutDir,[switch]$Verify)
$ErrorActionPreference = 'Stop'
if (!$OutDir) { $OutDir = Join-Path $PSScriptRoot '..\bin' }
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$cache = Join-Path $env:LOCALAPPDATA 'Cadrivect\build\ngx-310.9.16'
[IO.Directory]::CreateDirectory($cache) | Out-Null
[IO.Directory]::CreateDirectory($OutDir) | Out-Null
$revision = '9981c6350dc58c0ce21f024fd65e31e851c31689'
$nativeBuildLock = New-Object Threading.Mutex($false,'Local\Cadrivect-DLSS-SDK-310.9.16')
$ownsNativeBuildLock = $false
try {
    try { $ownsNativeBuildLock = $nativeBuildLock.WaitOne([TimeSpan]::FromMinutes(10)) }
    catch [Threading.AbandonedMutexException] { $ownsNativeBuildLock = $true }
    if (!$ownsNativeBuildLock) { throw 'Another NGX renderer build is still in progress.' }
    $files = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sdk-files.json') -Raw | ConvertFrom-Json
    foreach ($item in $files) {
        $file = Join-Path $cache ([IO.Path]::GetFileName($item.path))
        if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $item.sha256) {
            $tempDownload = "$file.$([Guid]::NewGuid().ToString('N')).tmp"
            try {
                & curl.exe -fsSL --retry 2 "https://raw.githubusercontent.com/NVIDIA/DLSS/$revision/$($item.path)" --output $tempDownload
                if ($LASTEXITCODE -or (Get-FileHash -LiteralPath $tempDownload).Hash -ne $item.sha256) { throw "NVIDIA SDK download verification failed: $($item.path)" }
                Copy-Item -LiteralPath $tempDownload -Destination $file -Force
            } finally { if (Test-Path -LiteralPath $tempDownload) { Remove-Item -LiteralPath $tempDownload -Force } }
        }
    }
    $runtime = Join-Path $cache 'nvngx_dlss.dll'
    $signature = Get-AuthenticodeSignature -LiteralPath $runtime
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=NVIDIA Corporation(,|$)') { throw 'Could not verify the NVIDIA runtime publisher.' }
    $source = Join-Path $PSScriptRoot 'NgxVideo.cpp'
    $object = Join-Path $cache 'NgxVideo.obj'
    $native = Join-Path $cache 'Cadrivect.Ngx.dll'
    . (Join-Path $PSScriptRoot 'Use-Compiler.ps1')
    & cl.exe /nologo /MT /EHsc /O2 /LD "/I$cache" "/Fo$object" $source /link /MANIFEST:NO /BREPRO /NODEFAULTLIB:oldnames.lib "/implib:$cache\Cadrivect.Ngx.lib" "/out:$native" (Join-Path $cache 'nvsdk_ngx_s.lib') advapi32.lib wintrust.lib crypt32.lib user32.lib
    if ($LASTEXITCODE) { throw 'Could not build the NGX renderer bridge.' }
    foreach ($item in @(
        @{ Source=$native; Name='Cadrivect.Ngx.dll' },
        @{ Source=$runtime; Name='nvngx_dlss.dll' },
        @{ Source=(Join-Path $cache 'LICENSE.txt'); Name='NVIDIA-RTX-LICENSE.txt' }
    )) {
        $destination = Join-Path $OutDir $item.Name
        if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $item.Source).Hash) {
            Copy-Item -LiteralPath $item.Source -Destination $destination -Force
        }
    }
    if ($Verify) { & (Join-Path $PSScriptRoot 'Test-Ngx.ps1') -PlayerDir $OutDir }
} finally {
    if ($ownsNativeBuildLock) { $nativeBuildLock.ReleaseMutex() }
    $nativeBuildLock.Dispose()
}
