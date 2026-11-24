[CmdletBinding()]
param([string]$OutDir=(Join-Path $PSScriptRoot '..\bin'),[switch]$Verify)
$ErrorActionPreference='Stop'
$out=[IO.Directory]::CreateDirectory($OutDir).FullName
& (Join-Path $PSScriptRoot '..\nvidia\Build.ps1') -OutDir $out
$cache=Join-Path $env:LOCALAPPDATA 'Cadrivect\build\ngx-310.9.16'
$stage=[IO.Directory]::CreateDirectory((Join-Path $cache ([guid]::NewGuid().ToString('N')))).FullName
try {
    $vendor=Join-Path $cache 'nvngx_dlssg.dll'
    $sig=Get-AuthenticodeSignature -LiteralPath $vendor
    if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch '(^|, )O=NVIDIA Corporation(,|$)'){throw 'NGX Frame Generation runtime signature failed.'}
    $runtime=[IO.Directory]::CreateDirectory((Join-Path $out 'dlss-fg')).FullName
    Copy-Item -LiteralPath $vendor -Destination $runtime -Force
    Copy-Item -LiteralPath (Join-Path $cache 'LICENSE.txt') -Destination (Join-Path $runtime 'NVIDIA-RTX-LICENSE.txt') -Force
    . (Join-Path $PSScriptRoot '..\nvidia\Use-Compiler.ps1')
    Push-Location -LiteralPath $stage
    try {
        & cl.exe /nologo /std:c++17 /EHsc /O2 /MT /LD /utf-8 "$PSScriptRoot\DlssFg.cpp" /link /BREPRO /MANIFEST:NO /NODEFAULTLIB:oldnames.lib /OUT:P5Cast.DlssFg.dll user32.lib shell32.lib ole32.lib
        if($LASTEXITCODE){throw 'Could not compile DLSS Frame Generation.'}
        Copy-Item -LiteralPath 'P5Cast.DlssFg.dll' -Destination $out -Force
        if($Verify) {
            & cl.exe /nologo /std:c++17 /EHsc /O2 /MT "$PSScriptRoot\Verify.cpp" /link /NODEFAULTLIB:oldnames.lib /OUT:Verify.exe user32.lib dxgi.lib
            if($LASTEXITCODE){throw 'Could not compile the DLSS Frame Generation GPU test.'}
            # NVIDIA can start a driver helper that inherits the working directory.
            # Keep it outside the disposable compiler directory.
            Push-Location -LiteralPath $cache
            try {
                foreach($mode in 'sdr','hdr') {
                    & (Join-Path $stage 'Verify.exe') "$out\Cadrivect.Ngx.dll" "$out\P5Cast.DlssFg.dll" $mode
                    if($LASTEXITCODE){throw "DLSS-G $mode GPU test failed."}
                }
            } finally {Pop-Location}
        }
    } finally {Pop-Location}
} finally {
    if([IO.Path]::GetDirectoryName($stage) -eq $cache -and [IO.Path]::GetFileName($stage) -match '^[a-f0-9]{32}$'){Remove-Item -LiteralPath $stage -Recurse -Force}
}
