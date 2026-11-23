[CmdletBinding()]
param([string]$PlayerExe=(Join-Path (Split-Path $PSScriptRoot) 'engine\bin\CadrivectPlayer.exe'))
$ErrorActionPreference='Stop'
$player=(Resolve-Path -LiteralPath $PlayerExe).Path
$bin=Split-Path $player
$id='P5Cast-gl-test-'+[guid]::NewGuid().ToString('N')
$stage=[IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) $id)).FullName
try {
    $refs=@('/r:System.Drawing.dll','/r:System.Windows.Forms.dll')
    foreach($name in 'SharpDX','SharpDX.DXGI','SharpDX.Direct3D11') {
        $lib=Join-Path $bin ($name+'.dll')
        Copy-Item -LiteralPath $lib -Destination $stage
        $refs+="/r:$lib"
    }
    $probe=Join-Path $stage 'OpenGlEffectsProbe.exe'
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /optimize+ @refs "/out:$probe" "$PSScriptRoot\OpenGlEffectsProbe.cs"
    if($LASTEXITCODE){throw 'OpenGL pixel test compile failed.'}
    & $probe $player
    if($LASTEXITCODE){throw 'OpenGL pixel test failed.'}
} finally {
    if([IO.Path]::GetDirectoryName($stage) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and [IO.Path]::GetFileName($stage) -eq $id) {
        Remove-Item -LiteralPath $stage -Force -Recurse
    }
}
