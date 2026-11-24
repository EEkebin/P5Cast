[CmdletBinding()]
param([string]$Decoder=(Join-Path $PSScriptRoot '..\engine\bin\Cadrivect.Decoder.dll'),[string]$Runtime)
$ErrorActionPreference='Stop'
$exercisePreparation=!$Runtime
if (!$Runtime) {
    $prepared=& (Join-Path $PSScriptRoot '..\sources\Prepare-Decoder.ps1') | ConvertFrom-Json
    if (!$prepared.ok) {throw $prepared.error}
    $Runtime=$prepared.runtime
}
$id=[guid]::NewGuid().ToString('N')
$stage=Join-Path ([IO.Path]::GetTempPath()) "Cadrivect-decoder-test-$id"
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    if ($exercisePreparation) {
        $local=Join-Path $stage 'profile'
        $cache=Join-Path $local 'Cadrivect\Decoder'
        $version=[IO.Path]::GetFileName($Runtime) -replace '-[0-9a-f]{32}$',''
        $good=Join-Path $cache $version
        $locked=Join-Path $cache ($version+'-'+[guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($good)
        [void][IO.Directory]::CreateDirectory($locked)
        foreach($name in 'avcodec-63.dll','avutil-61.dll','swresample-7.dll') {
            [IO.File]::Copy((Join-Path $Runtime $name),(Join-Path $good $name))
            [IO.File]::WriteAllText((Join-Path $locked $name),'unusable candidate')
        }
        [IO.Directory]::SetLastWriteTimeUtc($good,[DateTime]::UtcNow.AddHours(-1))
        $previous=$env:LOCALAPPDATA
        $handle=[IO.File]::Open((Join-Path $locked 'avcodec-63.dll'),'Open','Read','None')
        try {
            $env:LOCALAPPDATA=$local
            $shell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $result=& $shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '..\sources\Prepare-Decoder.ps1') -Archive (Join-Path $stage 'missing.zip') | ConvertFrom-Json
            if($LASTEXITCODE -ne 0 -or !$result.ok -or $result.runtime -ne $good){throw 'A locked cache entry hid a valid decoder runtime.'}
        } finally { $handle.Dispose(); $env:LOCALAPPDATA=$previous }
        'Recovered the older verified decoder runtime behind a locked cache entry.'
    }
    Copy-Item -LiteralPath $Decoder -Destination (Join-Path $stage 'Cadrivect.Decoder.dll')
    $harness=Join-Path $stage 'VerifyDecoder.exe'
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /platform:x64 /optimize+ "/out:$harness" (Join-Path $PSScriptRoot 'VerifyDecoder.cs')
    if ($LASTEXITCODE) {throw 'Decoder smoke test failed to compile.'}
    & $harness $Runtime (Join-Path $PSScriptRoot 'fixtures')
    if ($LASTEXITCODE) {throw 'Decoded pixels or metadata failed verification.'}
} finally {
    $full=[IO.Path]::GetFullPath($stage)
    if ([IO.Path]::GetDirectoryName($full) -eq [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -and [IO.Path]::GetFileName($full) -eq "Cadrivect-decoder-test-$id") {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
