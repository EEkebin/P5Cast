[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$probe=Join-Path $env:TEMP ('P5Cast-Wake-'+[guid]::NewGuid().ToString('N')+'.exe')
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
try {
    & $csc /nologo /platform:x64 "/out:$probe" "$PSScriptRoot\ConsoleWakeProbe.cs" "$repo\app\ConsoleWake.cs" "$repo\app\DirectGameTitle.cs"
    if($LASTEXITCODE){throw 'Wake test compilation failed.'}
    & $probe
    if($LASTEXITCODE){throw 'Loopback wake test failed.'}
} finally {
    if(Test-Path -LiteralPath $probe){Remove-Item -LiteralPath $probe -Force}
}
