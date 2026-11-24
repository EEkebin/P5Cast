# Run after building the player. This renders into an offscreen HDR10 texture on the active GPU.
[CmdletBinding()]
param([string]$PlayerDirectory=(Join-Path $PSScriptRoot '..\bin'), [switch]$Nvidia, [switch]$Fruc, [switch]$FrucSubtitles,
      [ValidateSet('d3d11','d3d12')][string]$Backend='d3d11')
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
    & $compile /nologo /optimize+ /platform:x64 /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$testDir\HdrTest.exe" $refs "$PSScriptRoot\Verify.cs" "$PSScriptRoot\VerifyFruc.cs" "$PSScriptRoot\VerifySubtitles.cs"
    if ($LASTEXITCODE) { throw 'HDR verification did not compile.' }
    $options=@((Join-Path $bin 'CadrivectPlayer.exe'))
    if($Fruc){$options+='--fruc'}
    if($FrucSubtitles){$options+='--fruc-subtitles'}
    if($Backend -eq 'd3d12'){$options+='--d3d12'}
    if($Nvidia) {
        Copy-Item -LiteralPath (Join-Path $bin 'nvngx_dlss.dll'), (Join-Path $bin 'Cadrivect.Ngx.dll') -Destination $testDir
        $options+='--nvidia'
    }
    & "$testDir\HdrTest.exe" $options
    if ($LASTEXITCODE) { throw 'GPU picture verification failed.' }
} finally {
    $path=[IO.Path]::GetFullPath($testDir)
    if ([IO.Path]::GetDirectoryName($path) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($path) -eq "Cadrivect-HDR-$id") {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
