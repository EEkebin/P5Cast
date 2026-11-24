# Dot-source after the repository MSVC toolchain setup.
$p5VulkanInclude=Join-Path $env:LOCALAPPDATA 'LumaRelay\build\dxvk-3.1.1'
$p5VulkanHeaders=@{
    'vulkan_win32.h'='6E89BADBA188F17275CA666CCF5B2F882F175FA88845C64B4C7436AD7130309C'
    'vk_platform.h'='6103E0DD164567CAAF887A18EA0F8C267E227AD4AFAF94437BD9919B8D50D050'
    'vulkan_core.h'='D5D48FA8FEDF9804E1DCF17AF4C1E177BA20BBD3C38F7F619B8CF378FEB7C332'
    'vulkan.h'='D2B65515FB1B65C7EC38B69047F2E728FF77FBB05C2D74C1F8CE2533DE82E67D'
}
[void][IO.Directory]::CreateDirectory((Join-Path $p5VulkanInclude 'vulkan'))
foreach($entry in $p5VulkanHeaders.GetEnumerator()) {
    $p5Header=Join-Path $p5VulkanInclude "vulkan\$($entry.Key)"
    if(!(Test-Path -LiteralPath $p5Header) -or (Get-FileHash -LiteralPath $p5Header).Hash -ne $entry.Value) {
        $p5Download="$p5Header.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            & curl.exe -fsSL --retry 2 "https://raw.githubusercontent.com/KhronosGroup/Vulkan-Headers/v1.3.204/include/vulkan/$($entry.Key)" --output $p5Download
            if($LASTEXITCODE -or (Get-FileHash -LiteralPath $p5Download).Hash -ne $entry.Value){throw "Vulkan header verification failed for $($entry.Key)."}
            Copy-Item -LiteralPath $p5Download -Destination $p5Header -Force
        } finally {if(Test-Path -LiteralPath $p5Download){Remove-Item -LiteralPath $p5Download -Force}}
    }
}
$p5VulkanDef=Join-Path $PSScriptRoot 'loader.def'
$p5VulkanLibrary=Join-Path $p5VulkanInclude ("p5loader-"+(Get-FileHash -LiteralPath $p5VulkanDef).Hash.Substring(0,12)+".lib")
if(!(Test-Path -LiteralPath $p5VulkanLibrary)){
    & lib.exe /nologo /machine:x64 "/def:$p5VulkanDef" "/out:$p5VulkanLibrary"
    if($LASTEXITCODE){throw 'Vulkan loader import library creation failed.'}
}
