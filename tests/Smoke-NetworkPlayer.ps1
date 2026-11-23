[CmdletBinding()]
param([string]$InstalledMoonlight = (Join-Path $env:LOCALAPPDATA 'LumaRelay\Streaming\moonlight-v6.2.0'))
$ErrorActionPreference='Stop'
$workspace=Split-Path $PSScriptRoot
$qa=Join-Path ([IO.Path]::GetTempPath()) ('Cadrivect-net-window-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($qa)|Out-Null
try {
    # Only load the pinned SDL window library, never Moonlight or its network stack.
    $hashes=@{'SDL2.dll'='1DF1B9A1E7C13B2429135DBEB9DECFA4617CD31A1A674A43DDDB8C013CEB211D'
        'SDL3.dll'='AF1671D2A250EBC4606423E34886ADBF3E709D7820CF74E3FD43A25273996D87'}
    foreach($name in $hashes.Keys){
        $path=Join-Path $InstalledMoonlight $name
        if((Get-FileHash -LiteralPath $path).Hash -ne $hashes[$name]){throw "Missing pinned $name for the fixture."}
        Copy-Item -LiteralPath $path -Destination $qa
    }
    $fx=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
    $exe=Join-Path $qa 'NetworkFixture.exe'
    & "$fx\csc.exe" /nologo /platform:x64 /target:exe "/out:$exe" "/reference:$fx\WPF\WindowsBase.dll" (Join-Path $workspace 'app\NetworkPlayer.cs') (Join-Path $PSScriptRoot 'NetworkPlayerFixture.cs')
    if($LASTEXITCODE -ne 0){throw 'Network surface fixture did not compile.'}
    & $exe
    if($LASTEXITCODE -ne 0){throw 'Network surface fixture failed.'}
} finally {
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($qa)) -eq $temp -and [IO.Path]::GetFileName($qa) -match '^Cadrivect-net-window-[0-9a-f]{32}$'){
        Remove-Item -LiteralPath $qa -Recurse -Force
    }
}
