[CmdletBinding()]
param([string]$OutDir=(Join-Path $PSScriptRoot '..\bin'))
$ErrorActionPreference='Stop'
$out=[IO.Directory]::CreateDirectory($OutDir).FullName
. (Join-Path $PSScriptRoot '..\nvidia\Use-Compiler.ps1')
. (Join-Path $PSScriptRoot 'Use-VulkanSdk.ps1')
$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$build=[IO.Directory]::CreateDirectory((Join-Path $temp ('P5Cast-VkCapture-'+[guid]::NewGuid().ToString('N')))).FullName
Push-Location $build
try {
    & cl.exe /nologo /utf-8 /std:c++17 /MT /O2 /EHsc /LD /DNOMINMAX "/I$p5VulkanInclude" "$PSScriptRoot\CaptureImport.cpp" /link /BREPRO /MANIFEST:NO /NODEFAULTLIB:oldnames.lib /OUT:P5Cast.VulkanCapture.dll $p5VulkanLibrary
    if($LASTEXITCODE){throw 'The Vulkan Windows capture bridge failed to compile.'}
    Copy-Item -LiteralPath '.\P5Cast.VulkanCapture.dll' -Destination $out -Force
} finally {
    Pop-Location
    if([IO.Path]::GetDirectoryName($build) -eq $temp -and [IO.Path]::GetFileName($build) -match '^P5Cast-VkCapture-[a-f0-9]{32}$'){
        Remove-Item -LiteralPath $build -Recurse -Force
    }
}
