[CmdletBinding()]
param([string]$Decoder=(Join-Path $PSScriptRoot '..\engine\bin\Cadrivect.Decoder.dll'),[string]$Runtime)
$ErrorActionPreference='Stop'
if (!$Runtime) {
    $prepared=& (Join-Path $PSScriptRoot '..\sources\Prepare-Decoder.ps1') | ConvertFrom-Json
    if (!$prepared.ok) {throw $prepared.error}
    $Runtime=$prepared.runtime
}
$id=[guid]::NewGuid().ToString('N')
$stage=Join-Path ([IO.Path]::GetTempPath()) "Cadrivect-decoder-test-$id"
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    Copy-Item -LiteralPath $Decoder -Destination (Join-Path $stage 'Cadrivect.Decoder.dll')
    $harness=Join-Path $stage 'VerifyDecoder.exe'
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /platform:x64 /optimize+ "/out:$harness" (Join-Path $PSScriptRoot 'VerifyDecoder.cs')
    if ($LASTEXITCODE) {throw 'Decoder smoke test failed to compile.'}
    & $harness $Runtime (Join-Path $PSScriptRoot 'fixtures')
    if ($LASTEXITCODE) {throw 'Decoded pixels or metadata failed verification.'}
} finally {
    foreach($file in 'Cadrivect.Decoder.dll','VerifyDecoder.exe') {[IO.File]::Delete((Join-Path $stage $file))}
    [IO.Directory]::Delete($stage)
}
