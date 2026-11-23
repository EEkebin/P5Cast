# Run after building the player. This renders into an offscreen HDR10 texture on the active GPU.
[CmdletBinding()]
param([string]$PlayerDirectory=(Join-Path $PSScriptRoot '..\bin'), [switch]$Nvidia)
$ErrorActionPreference='Stop'
$bin=(Resolve-Path -LiteralPath $PlayerDirectory).Path
$id=[Guid]::NewGuid().ToString('N')
$testDir=Join-Path ([IO.Path]::GetTempPath()) "Cadrivect-HDR-$id"
[IO.Directory]::CreateDirectory($testDir) | Out-Null
try {
    $refs=foreach($name in @('SharpDX','SharpDX.Direct3D11','SharpDX.DXGI')) {
        $path=Join-Path $bin "$name.dll"
        Copy-Item -LiteralPath $path -Destination $testDir
        "/r:$path"
    }
    $compile=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compile /nologo /optimize+ /platform:x64 "/out:$testDir\HdrTest.exe" $refs "$PSScriptRoot\Verify.cs"
    if ($LASTEXITCODE) { throw 'HDR verification did not compile.' }
    $options=@((Join-Path $bin 'CadrivectPlayer.exe'))
    if($Nvidia) {
        Copy-Item -LiteralPath (Join-Path $bin 'nvngx_dlss.dll'), (Join-Path $bin 'Cadrivect.Ngx.dll') -Destination $testDir
        $options+='--nvidia'
    }
    & "$testDir\HdrTest.exe" $options
    if ($LASTEXITCODE) { throw 'HDR GPU results fell outside the expected PQ tolerance.' }
} finally {
    $path=[IO.Path]::GetFullPath($testDir)
    if ([IO.Path]::GetDirectoryName($path) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($path) -eq "Cadrivect-HDR-$id") {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
