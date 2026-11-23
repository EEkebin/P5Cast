[CmdletBinding()]
param([string]$OutDir,[switch]$Verify)
$ErrorActionPreference='Stop'
if(!$OutDir){$OutDir=Join-Path $PSScriptRoot '..\..\bin'}
$OutDir=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$cache=Join-Path $env:LOCALAPPDATA 'P5Cast\build\neural-rendering-2'
$common=Join-Path $env:LOCALAPPDATA 'Cadrivect\build\ngx-310.9.16'
[void][IO.Directory]::CreateDirectory($cache)
[void][IO.Directory]::CreateDirectory($OutDir)
$archive=Join-Path $cache 'rtx-remix-ngx_sdk_dlnr@2.7z'
$hash='4BB8651B3AC2180F23DF1F39422C489810A13D445A94DC836C876C18C93ECEFE'
$lock=New-Object Threading.Mutex($false,'Local\P5Cast-Build-Remix-NR-2')
$acquired=$false
try {
    try {$acquired=$lock.WaitOne([TimeSpan]::FromMinutes(10))} catch [Threading.AbandonedMutexException] {$acquired=$true}
    if(!$acquired){throw 'Another neural rendering build is in progress.'}
    if(!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $hash) {
        $temp="$archive.$([guid]::NewGuid().ToString('N')).tmp"
        try {
            & curl.exe -fsSL --retry 2 'https://d1oqksizpb0e3d.cloudfront.net/rtx-remix-ngx_sdk_dlnr@2.7z' -o $temp
            if($LASTEXITCODE -or (Get-FileHash -LiteralPath $temp).Hash -ne $hash){throw 'NVIDIA RTX Remix NR build package verification failed.'}
            Copy-Item -LiteralPath $temp -Destination $archive -Force
        } finally {if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}}
    }
    $entries=Get-Content -Raw -LiteralPath "$PSScriptRoot\headers.json" | ConvertFrom-Json
    $entries+=@{path='../LICENSE.txt';sha256='3027F23CA5A46DD9CB8183FBD522983A86F64D7DAAC5982912BF9F214671F294'}
    $stale=@($entries | Where-Object {
        $item=Join-Path "$cache\include" $_.path
        !(Test-Path -LiteralPath $item) -or (Get-FileHash -LiteralPath $item).Hash -ne $_.sha256
    })
    if($stale.Count) {
        & tar.exe -xf $archive -C $cache include LICENSE.txt
        if($LASTEXITCODE){throw 'NVIDIA NR headers could not be extracted.'}
    }
    Copy-Item -LiteralPath "$cache\LICENSE.txt" -Destination "$OutDir\NVIDIA-NEURAL-RENDERING-LICENSE.txt" -Force
    if(!(Test-Path -LiteralPath "$common\nvsdk_ngx_s.lib")){& "$PSScriptRoot\..\Build.ps1" -OutDir $OutDir}
    . "$PSScriptRoot\..\Use-Compiler.ps1"
    . "$PSScriptRoot\..\..\vulkan\Use-VulkanSdk.ps1"
    $vkLib=Join-Path $cache 'nr-vulkan.lib'
    & lib.exe /nologo /machine:x64 "/def:$PSScriptRoot\vulkan.def" "/out:$vkLib"
    if($LASTEXITCODE){throw 'Could not create Vulkan loader import library.'}
    $flags=@('/nologo','/MT','/EHsc','/std:c++17','/O2',"/I$p5VulkanInclude","/I$cache\include","/I$common")
    $link=@('/MANIFEST:NO','/BREPRO','/NODEFAULTLIB:oldnames.lib',$vkLib,'user32.lib','advapi32.lib','wintrust.lib','crypt32.lib','bcrypt.lib')
    & cl.exe @flags /LD "/Fo$cache\NeuralVk.obj" "$PSScriptRoot\NeuralVk.cpp" /link @link "$common\nvsdk_ngx_s.lib" "/implib:$cache\NeuralVk.lib" "/out:$cache\P5Cast.NeuralVk.dll"
    if($LASTEXITCODE){throw 'Could not build DLSS neural rendering bridge.'}
    Copy-Item -LiteralPath "$cache\P5Cast.NeuralVk.dll" -Destination $OutDir -Force
    if($Verify){
        & cl.exe @flags "/Fo$cache\VerifyNr.obj" "$PSScriptRoot\VerifyNr.cpp" /link @link "/out:$cache\VerifyNr.exe"
        if($LASTEXITCODE){throw 'Could not build neural rendering GPU transport test.'}
        & "$cache\VerifyNr.exe" "$OutDir\P5Cast.Dxvk.dll" "$OutDir\P5Cast.NeuralVk.dll"
        if($LASTEXITCODE){throw 'Neural rendering GPU transport test failed.'}
    }
} finally {if($acquired){$lock.ReleaseMutex()};$lock.Dispose()}
