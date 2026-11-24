[CmdletBinding()]
param([string]$OutDir)
if (!$OutDir) { $OutDir = Join-Path $PSScriptRoot 'bin' }
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
$cache = Join-Path $env:LOCALAPPDATA 'LumaRelay\build\sharpdx-4.2.0'
$intermediate = Join-Path $cache 'compiled-shaders'
New-Item -ItemType Directory -Force $OutDir,$cache,$intermediate | Out-Null
$packages = @{
    'SharpDX' = '117126040200FAAA118F1312AD5A5B1E7D067C90ABF6E726F18B6D1635216FC3'
    'SharpDX.DXGI' = 'CB4A31BD0137EC8845B234621A690E5AB2B45830BDE0A3D27CAABC5BCED5FC97'
    'SharpDX.Direct3D11' = '21DEDCF8B1DDB50EA3FF81F61D45511AEC79D727D15BDA9AE45C05D5058EE219'
    'SharpDX.D3DCompiler' = 'CA86D6A315FDEA7C0CC7C25299B66FA4F8DC8BAED5B7C3ED918FEC9BBABFC726'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($name in $packages.Keys) {
    $archive = Join-Path $cache "$name.zip"
    if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash $archive).Hash -ne $packages[$name]) {
        $id = $name.ToLowerInvariant()
        Invoke-WebRequest -UseBasicParsing "https://api.nuget.org/v3-flatcontainer/$id/4.2.0/$id.4.2.0.nupkg" -OutFile $archive
    }
    if ((Get-FileHash $archive).Hash -ne $packages[$name]) { throw "Hash mismatch in $name" }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $zip.GetEntry("lib/net45/$name.dll")
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $intermediate "$name.dll"), $true)
        if ($name -ne 'SharpDX.D3DCompiler') {
            Copy-Item (Join-Path $intermediate "$name.dll") -Destination $OutDir -Force
        }
    } finally { $zip.Dispose() }
}
$shaderTool = Join-Path $intermediate 'ShaderBake.exe'
& $csc /nologo /optimize+ "/r:$intermediate\SharpDX.dll" "/r:$intermediate\SharpDX.D3DCompiler.dll" "/out:$shaderTool" "$PSScriptRoot\ShaderBake.cs"
if ($LASTEXITCODE) { throw 'Shader compiler build failed.' }
& $shaderTool $PSScriptRoot $intermediate
if ($LASTEXITCODE) { throw 'HLSL compile failed.' }
. (Join-Path $PSScriptRoot 'nvidia\Use-Compiler.ps1')
$nisBake=Join-Path $intermediate 'BakeNisCoefficients.exe'
$nisTables=Join-Path $intermediate 'NisCoefficients.bin'
& cl.exe /nologo /std:c++17 /EHsc /MT /O2 "/Fo$intermediate\BakeNisCoefficients.obj" "/Fe$nisBake" "$PSScriptRoot\nis\BakeCoefficients.cpp" /link /NODEFAULTLIB:oldnames.lib
if ($LASTEXITCODE) { throw 'NIS coefficient exporter build failed.' }
& $nisBake $nisTables
if ($LASTEXITCODE) { throw 'NIS coefficient export failed.' }
$refs = @('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll',"/r:$framework\System.Runtime.dll","/r:$framework\System.Runtime.WindowsRuntime.dll")
$refs += Get-ChildItem (Join-Path $env:WINDIR 'System32\WinMetadata') -Filter '*.winmd' | ForEach-Object { "/r:$($_.FullName)" }
$refs += 'SharpDX','SharpDX.DXGI','SharpDX.Direct3D11' | ForEach-Object { "/r:$intermediate\$_.dll" }
$resources = Get-ChildItem $intermediate -Filter '*.cso' | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" }
$resources += "/resource:$nisTables,NisCoefficients.bin"
$resources += "/resource:$PSScriptRoot\shaders\Effects.glsl,Effects.glsl","/resource:$PSScriptRoot\third_party\ffx_a.h,ffx_a.h","/resource:$PSScriptRoot\third_party\ffx_fsr1.h,ffx_fsr1.h"
$playerFiles=@('Player.cs','FramePacing.cs','PictureLabels.cs','D3D12Effects.cs','NativeD3D12Presentation.cs','VulkanEffects.cs','VulkanSwapChain.cs','OpenGlEffects.cs','EmbeddedPlayback.cs','PlaybackControls.cs','PlaybackGraph.cs','DirectVideo.cs','DemoVideo.cs','GpuCapabilities.cs','Renderer.cs','NisScaler.cs','NisConfig.cs','Renderer.Loupe.cs','WipeLayout.cs','Presentation.cs','Capture.cs','SharedCapture.cs','VulkanCapture.cs','Renderer.Nvidia.cs','HdrPipeline.cs','DisplayHdr.cs','DisplayTarget.cs',
    'optical\NvidiaOpticalFlow.cs','FrucFrames.cs','nvidia\NgxVideo.cs','nvidia\DlssModel.cs','nvidia\NeuralRendering.cs','amd\FsrUpscaler.cs','amd\framegeneration\FsrFrameGeneration.cs','Renderer.FrameGeneration.cs','rtxvideo\RtxVideo.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $csc /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$PSScriptRoot\..\app\assets\mark.ico" @refs @resources "/out:$OutDir\CadrivectPlayer.exe" @playerFiles
if ($LASTEXITCODE) { throw 'Player compilation failed.' }
Copy-Item "$PSScriptRoot\third_party\SharpDX-LICENSE.txt","$PSScriptRoot\third_party\FSR-LICENSE.txt" -Destination $OutDir -Force
Copy-Item "$PSScriptRoot\optical\NVIDIA-OPTICAL-FLOW-LICENSE.txt" -Destination $OutDir -Force
Copy-Item -LiteralPath "$PSScriptRoot\third_party\nis\LICENSE.txt" -Destination (Join-Path $OutDir 'NVIDIA-NIS-LICENSE.txt') -Force
& "$PSScriptRoot\amd\Build.ps1" -OutDir $OutDir
& "$PSScriptRoot\fruc\Build.ps1" -OutDir $OutDir
& "$PSScriptRoot\vulkan\Build.ps1" -OutDir $OutDir
& "$PSScriptRoot\vulkan\Build-Capture.ps1" -OutDir $OutDir
& "$PSScriptRoot\nvidia\Build.ps1" -OutDir $OutDir
& "$PSScriptRoot\nvidia\neural\Build.ps1" -OutDir $OutDir
& "$PSScriptRoot\open-nr\Build.ps1" -OutDir $OutDir
& "$PSScriptRoot\rtxvideo\Build.ps1" -OutDir $OutDir
& "$PSScriptRoot\direct\Build.ps1" -OutDir $OutDir
& "$PSScriptRoot\presentation\Build.ps1" -OutDir $OutDir
Write-Output (Join-Path $OutDir 'CadrivectPlayer.exe')
