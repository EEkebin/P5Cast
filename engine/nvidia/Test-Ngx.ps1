[CmdletBinding()]
param([string]$PlayerDir,[ValidateSet('d3d11','d3d12')][string]$Backend='d3d11')
$ErrorActionPreference = 'Stop'
if (!$PlayerDir) { $PlayerDir=Join-Path $PSScriptRoot '..\bin' }
$testDir=Join-Path $env:LOCALAPPDATA 'Cadrivect\build\ngx-validation'
[IO.Directory]::CreateDirectory($testDir) | Out-Null
foreach ($name in 'Cadrivect.Ngx.dll','nvngx_dlss.dll','SharpDX.dll','SharpDX.DXGI.dll','SharpDX.Direct3D11.dll') {
    Copy-Item -LiteralPath (Join-Path $PlayerDir $name) -Destination $testDir -Force
}
$refs = 'SharpDX','SharpDX.DXGI','SharpDX.Direct3D11' | ForEach-Object { "/r:$testDir\$_.dll" }
$testExe=Join-Path $testDir 'VerifyNgx.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /platform:x64 @refs "/out:$testExe" "$PSScriptRoot\..\D3D12Effects.cs" "$PSScriptRoot\NgxVideo.cs" "$PSScriptRoot\VerifyNgx.cs"
if ($LASTEXITCODE) { throw 'NGX smoke test build failed.' }
& $testExe $Backend
if ($LASTEXITCODE) { throw 'NGX hardware validation failed.' }
