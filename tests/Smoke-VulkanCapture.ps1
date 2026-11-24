[CmdletBinding()]
param([string]$PlayerExe=(Join-Path (Split-Path $PSScriptRoot) 'engine\bin\CadrivectPlayer.exe'))
$ErrorActionPreference='Stop'
$player=(Resolve-Path -LiteralPath $PlayerExe).Path
$bin=Split-Path $player
$id='P5Cast-VkCaptureTest-'+[guid]::NewGuid().ToString('N')
$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$stage=[IO.Directory]::CreateDirectory((Join-Path $temp $id)).FullName
try {
    $refs=foreach($name in 'SharpDX','SharpDX.DXGI','SharpDX.Direct3D11') {
        $lib=Join-Path $bin ($name+'.dll')
        Copy-Item -LiteralPath $lib -Destination $stage
        "/r:$lib"
    }
    $exe=Join-Path $stage 'VulkanCaptureProbe.exe'
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /optimize+ @refs "/out:$exe" "$PSScriptRoot\VulkanCaptureProbe.cs"
    if($LASTEXITCODE){throw 'Vulkan capture test compilation failed.'}
    & $exe $player
    if($LASTEXITCODE){throw 'Vulkan capture output verification failed.'}
} finally {
    if([IO.Path]::GetDirectoryName($stage) -eq $temp -and [IO.Path]::GetFileName($stage) -eq $id) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
