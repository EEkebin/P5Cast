# Confirm the effects device actually created a Vulkan WSI swapchain. The
# embedded pixel checks alone also pass with an intermediate CPU transport.
[CmdletBinding()]
param([string]$PlayerExe,[string]$HdrDisplay,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(!$OutputDirectory) {
    $OutputDirectory=Join-Path $env:TEMP ('P5Cast-VulkanPresentation-'+[guid]::NewGuid().ToString('N'))
}
$OutputDirectory=[IO.Directory]::CreateDirectory($OutputDirectory).FullName
$logs=[IO.Directory]::CreateDirectory((Join-Path $OutputDirectory ('dxvk-'+[guid]::NewGuid().ToString('N')))).FullName
$previousLogs=$env:DXVK_LOG_PATH
$previousLevel=$env:DXVK_LOG_LEVEL
try {
    $env:DXVK_LOG_PATH=$logs
    $env:DXVK_LOG_LEVEL='info'
    $argsForTest=@{Renderer='vulkan';OutputDirectory=$OutputDirectory}
    if($PlayerExe) {$argsForTest.PlayerExe=$PlayerExe}
    if($HdrDisplay) {$argsForTest.HdrDisplay=$HdrDisplay} else {$argsForTest.CheckComparison=$true}
    & (Join-Path $PSScriptRoot 'Smoke-Embedded.ps1') @argsForTest
    $file=Join-Path $logs 'EmbeddedProbe_d3d11.log'
    if(![IO.File]::Exists($file)){throw 'The embedded player did not start the Vulkan effects device.'}
    $trace=[IO.File]::ReadAllText($file)
    if($trace -notmatch 'Color space:\s+VK_COLOR_SPACE_SRGB_NONLINEAR_KHR') {
        throw 'The Vulkan effects device did not present the SDR image on its own swapchain.'
    }
    if($HdrDisplay -and $trace -notmatch 'Format:\s+VK_FORMAT_A2(?:B10G10R10|R10G10B10)_UNORM_PACK32\s+info:\s+Color space:\s+VK_COLOR_SPACE_HDR10_ST2084_EXT') {
        throw 'The Vulkan effects device did not select a 10-bit PQ swapchain for HDR playback.'
    }
    Write-Output "Verified native Vulkan swapchain presentation. Device log: $file"
} finally {
    $env:DXVK_LOG_PATH=$previousLogs
    $env:DXVK_LOG_LEVEL=$previousLevel
}
