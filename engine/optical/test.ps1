$ErrorActionPreference = 'Stop'
$bin = Join-Path (Split-Path $PSScriptRoot) 'bin'
$out = Join-Path $env:LOCALAPPDATA 'LumaRelay\build\optical-test'
[IO.Directory]::CreateDirectory($out) | Out-Null
$dependencies = 'SharpDX.dll','SharpDX.DXGI.dll','SharpDX.Direct3D11.dll'
$refs = foreach ($dll in $dependencies) {
    Copy-Item (Join-Path $bin $dll) $out -Force
    '/r:' + (Join-Path $out $dll)
}
$exe = Join-Path $out 'OpticalFlowProbe.exe'
$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /platform:x64 /optimize+ @refs "/out:$exe" "$PSScriptRoot\NvidiaOpticalFlow.cs" "$PSScriptRoot\OpticalFlowProbe.cs"
if ($LASTEXITCODE) { throw 'Optical flow probe build failed.' }
& $exe
if ($LASTEXITCODE) { throw 'Optical flow hardware validation failed.' }
