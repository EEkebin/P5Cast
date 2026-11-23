[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutDir)
$ErrorActionPreference='Stop'
$cache=Join-Path $env:LOCALAPPDATA 'Cadrivect\build\ffmpeg-7adfbe40'
[void][IO.Directory]::CreateDirectory($cache)
$revision='7adfbe40dcde598f93a6963d2f7b22cd75202ccc'
$hash='4BABDA050DCFCCB1194DD2A9E75B5E5EC1AA33B3E99D4F6A7106B6337B6B4A0C'
$archive=Join-Path $cache 'source.tar.gz'
if (!(Test-Path $archive) -or (Get-FileHash $archive).Hash -ne $hash) {
    & curl.exe -fsSL --retry 2 "https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/$revision" -o $archive
    if ($LASTEXITCODE -or (Get-FileHash $archive).Hash -ne $hash) {throw 'FFmpeg header archive verification failed.'}
}
& tar.exe -xf $archive -C $cache --strip-components 1 --include '*/libavutil/*.h' --include '*/libavcodec/*.h' --include '*/COPYING.LGPLv2.1'
if ($LASTEXITCODE) {throw 'FFmpeg header extraction failed.'}
[IO.File]::WriteAllLines((Join-Path $cache 'libavutil\avconfig.h'),@('#define AV_HAVE_BIGENDIAN 0','#define AV_HAVE_FAST_UNALIGNED 1'))
. (Join-Path $PSScriptRoot '..\nvidia\Use-Compiler.ps1')
& cl.exe /nologo /MT /EHsc /O2 /LD "/I$cache" "/Fo$cache\Decoder.obj" "$PSScriptRoot\Decoder.cpp" /link /BREPRO /NODEFAULTLIB:oldnames.lib "/implib:$cache\Decoder.lib" "/out:$OutDir\Cadrivect.Decoder.dll"
if ($LASTEXITCODE) {throw 'Direct video decoder compilation failed.'}
Copy-Item (Join-Path $cache 'COPYING.LGPLv2.1') (Join-Path $OutDir 'FFmpeg-LICENSE.txt') -Force
