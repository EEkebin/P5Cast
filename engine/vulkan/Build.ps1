[CmdletBinding()]
param([string]$OutDir)
$ErrorActionPreference='Stop'
if(!$OutDir){$OutDir=Join-Path $PSScriptRoot '..\bin'}
$OutDir=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$version='3.1.1'
$cache=Join-Path $env:LOCALAPPDATA "LumaRelay\build\dxvk-$version"
[void][IO.Directory]::CreateDirectory($cache)
[void][IO.Directory]::CreateDirectory($OutDir)
$archive=Join-Path $cache "dxvk-$version.tar.gz"
$dll=Join-Path $cache "dxvk-$version\x64\d3d11.dll"
$dxgi=Join-Path $cache "dxvk-$version\x64\dxgi.dll"
$archiveHash='40565B4A724AADC4433FA4E010B4B23916D9B1F1BAEEE64E17186DB94F54E608'
$dllHash='0A203B6255C893430F0A8461C618962DF04E62AA41538D5A875B21868317CF9F'
$dxgiHash='E3C1178EB7F0DD59F91BCFCC257549E5B9CFDB5E8508719293DE7F8704F53160'
function MatchesHash($file,$hash){
    (Test-Path -LiteralPath $file -PathType Leaf) -and (Get-FileHash -LiteralPath $file).Hash -eq $hash
}
if(!(MatchesHash $dll $dllHash) -or !(MatchesHash $dxgi $dxgiHash)){
    if(!(MatchesHash $archive $archiveHash)){
        Invoke-WebRequest -UseBasicParsing "https://github.com/doitsujin/dxvk/releases/download/v$version/dxvk-$version.tar.gz" -OutFile $archive
    }
    if(!(MatchesHash $archive $archiveHash)){throw 'DXVK release archive checksum mismatch.'}
    & tar.exe -xf $archive -C $cache "dxvk-$version/x64/d3d11.dll" "dxvk-$version/x64/dxgi.dll"
    if($LASTEXITCODE -or !(MatchesHash $dll $dllHash) -or !(MatchesHash $dxgi $dxgiHash)){throw 'DXVK x64 library extraction failed.'}
}
# Retain Windows DXGI and never shadow system d3d11.dll in the application directory.
$target=Join-Path $OutDir 'P5Cast.Dxvk.dll'
if(!(MatchesHash $target $dllHash)){Copy-Item -LiteralPath $dll -Destination $target -Force}
$factory=Join-Path $OutDir 'P5Cast.DxgiVk.dll'
if(!(MatchesHash $factory $dxgiHash)){Copy-Item -LiteralPath $dxgi -Destination $factory -Force}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DXVK-LICENSE.txt') -Destination $OutDir -Force
