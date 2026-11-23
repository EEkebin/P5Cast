[CmdletBinding()]
param([string]$PlayerDir=(Join-Path $PSScriptRoot '..\bin'),[ValidateSet('d3d12','vulkan')][string]$Backend='d3d12')
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $PlayerDir).Path
$stage=[IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('P5Cast-Fsr-'+[Guid]::NewGuid().ToString('N')))).FullName
try {
    $refs=foreach($name in 'SharpDX','SharpDX.Direct3D11','SharpDX.DXGI'){
        $dll=Join-Path $root "$name.dll"
        Copy-Item -LiteralPath $dll -Destination $stage
        "/r:$dll"
    }
    foreach($name in 'P5Cast.Fsr.dll','Cadrivect.Ngx.dll','amd_fidelityfx_loader_dx12.dll','amd_fidelityfx_upscaler_dx12.dll'){
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage
    }
    if($Backend -eq 'vulkan') {
        foreach($name in 'P5Cast.Dxvk.dll','P5Cast.FsrVk.dll','amd_fidelityfx_loader_vk.dll','amd_fidelityfx_upscaler_vk.dll'){
            Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage
        }
    }
    $exe=Join-Path $stage 'VerifyFsr.exe'
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /platform:x64 @refs "/out:$exe" "$PSScriptRoot\..\D3D12Effects.cs" "$PSScriptRoot\..\VulkanEffects.cs" "$PSScriptRoot\FsrUpscaler.cs" "$PSScriptRoot\Verify.cs" "$PSScriptRoot\VerifyHud.cs"
    if($LASTEXITCODE){throw 'AMD FSR GPU fixture did not compile.'}
    & $exe $Backend
    if($LASTEXITCODE){throw 'AMD FSR GPU output failed validation.'}
} finally {
    if([IO.Path]::GetDirectoryName($stage) -eq [IO.Path]::GetTempPath().TrimEnd('\')) {Remove-Item -LiteralPath $stage -Recurse -Force}
}
