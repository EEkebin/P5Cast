[CmdletBinding()]
param([string]$OutDir=(Join-Path $PSScriptRoot '..\bin'))
$ErrorActionPreference='Stop'
$revision='7539ae8b6568ad29ed78a753404351daf3b5cca7'
$cache=Join-Path $env:LOCALAPPDATA 'P5Cast\build\fsr-2.4.0'
$cache=[IO.Directory]::CreateDirectory($cache).FullName
$out=[IO.Directory]::CreateDirectory($OutDir).FullName
$files=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sdk-files.json') -Raw | ConvertFrom-Json)
$fgManifest=Join-Path $PSScriptRoot 'framegeneration\sdk-files.json'
if(Test-Path -LiteralPath $fgManifest) { $files+=@(Get-Content -LiteralPath $fgManifest -Raw | ConvertFrom-Json) }
foreach($item in $files) {
    $file=Join-Path $cache $item.path
    if(!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $item.sha256) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file))
        $download="$file.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            & curl.exe --fail --silent --show-error --location --retry 2 "https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/$revision/$($item.path)" --output $download
            if($LASTEXITCODE -or (Get-FileHash -LiteralPath $download).Hash -ne $item.sha256) {throw "AMD SDK verification failed for $($item.path)."}
            Copy-Item -LiteralPath $download -Destination $file -Force
        } finally {if(Test-Path -LiteralPath $download) {Remove-Item -LiteralPath $download -Force}}
    }
    if($item.path -like '*/signedbin/*.dll') {
        $signature=Get-AuthenticodeSignature -LiteralPath $file
        if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Advanced Micro Devices(,|$)') {
            throw "AMD runtime publisher check failed for $($item.path)."
        }
        Copy-Item -LiteralPath $file -Destination $out -Force
    }
}
$stage=[IO.Directory]::CreateDirectory((Join-Path $cache ([Guid]::NewGuid().ToString('N')))).FullName
try {
    . (Join-Path $PSScriptRoot '..\nvidia\Use-Compiler.ps1')
    $src=@((Join-Path $PSScriptRoot 'FsrUpscaler.cpp'))
    $fgSource=Join-Path $PSScriptRoot 'framegeneration\FsrFrameGeneration.cpp'
    if(Test-Path -LiteralPath $fgSource) { $src+=$fgSource }
    Push-Location $stage
    try {
        & cl.exe /nologo /MT /EHsc /O2 /LD /D_WINDOWS "/I$cache\Kits\FidelityFX" "/I$PSScriptRoot" @src /link /MANIFEST:NO /BREPRO /NODEFAULTLIB:oldnames.lib /out:P5Cast.Fsr.dll
        if($LASTEXITCODE) {throw 'AMD FSR effects bridge failed to compile.'}
        Copy-Item -LiteralPath 'P5Cast.Fsr.dll' -Destination $out -Force
        . (Join-Path $PSScriptRoot '..\vulkan\Use-VulkanSdk.ps1')
        & cl.exe /nologo /MT /EHsc /O2 /LD /D_WINDOWS /DP5_FSR_VK "/I$cache\Kits\FidelityFX" "/I$p5VulkanInclude" @src /link /MANIFEST:NO /BREPRO /NODEFAULTLIB:oldnames.lib "$p5VulkanLibrary" /out:P5Cast.FsrVk.dll
        if($LASTEXITCODE){throw 'AMD FSR Vulkan bridge failed to compile.'}
        Copy-Item -LiteralPath 'P5Cast.FsrVk.dll' -Destination $out -Force
    } finally {Pop-Location}
    Copy-Item -LiteralPath (Join-Path $cache 'Kits\FidelityFX\docs\license.md') -Destination (Join-Path $out 'AMD-FSR-LICENSE.md') -Force
    Copy-Item -LiteralPath (Join-Path $cache '3rdpartynotice.md') -Destination (Join-Path $out 'AMD-FSR-NOTICES.md') -Force
} finally {
    if([IO.Path]::GetDirectoryName($stage) -eq $cache){Remove-Item -LiteralPath $stage -Recurse -Force}
}
