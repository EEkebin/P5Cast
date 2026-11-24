[CmdletBinding()]
param([string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'Cadrivect\build\direct-core'), [switch]$Verify)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($CacheDirectory)
[IO.Directory]::CreateDirectory($work) | Out-Null
$base = Join-Path $work 'msys64'
$bash = Join-Path $base 'usr\bin\bash.exe'
$revision = 'f30da0aaeb96fc0026cc03683153f682352e7d7c'
function Get-CheckedFile($Url,$Path,$Expected) {
    if ((Test-Path -LiteralPath $Path -PathType Leaf) -and (Get-FileHash -LiteralPath $Path).Hash -eq $Expected) { return }
    $partial=$Path+'.partial'
    & "$env:SystemRoot\System32\curl.exe" --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 2 --output $partial $Url
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $partial).Hash -ne $Expected) { throw "Could not verify $(Split-Path -Leaf $Path)." }
    Move-Item -LiteralPath $partial -Destination $Path -Force
}
if (!(Test-Path -LiteralPath $bash)) {
    $extractor=Join-Path $work 'msys.sfx.exe'
    Get-CheckedFile 'https://repo.msys2.org/distrib/x86_64/msys2-base-x86_64-20261120.sfx.exe' $extractor '4f6dadfdb2c9be5fc1938e30b635cb72bb4344c35e8b6fd225bc3250fcab5897'
    & $extractor -y "-o$work" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not unpack the private MSYS2 build environment.' }
}
$cache=Join-Path $base 'var\cache\pacman\pkg'
[IO.Directory]::CreateDirectory($cache) | Out-Null
foreach ($line in (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packages.sha256'))) {
    $digest,$file=$line -split '  ',2
    Get-CheckedFile ('https://repo.msys2.org/mingw/ucrt64/'+[uri]::EscapeDataString($file)) (Join-Path $cache $file) $digest
}
$upstream=Join-Path $work 'upstream'
if (!(Test-Path -LiteralPath $upstream)) {
    & git clone --branch v1.10.1 --depth 1 https://github.com/chiaki-ng/chiaki-ng.git $upstream
    if ($LASTEXITCODE -ne 0) { throw 'Could not obtain the official chiaki-ng sources.' }
}
if ((& git -C $upstream rev-parse HEAD) -ne $revision) { throw 'Unexpected chiaki-ng revision.' }
$reconnectPatch = Join-Path $PSScriptRoot 'chiaki-reconnect.patch'
& git -C $upstream apply --reverse --check --quiet $reconnectPatch
if ($LASTEXITCODE -ne 0) {
    & git -C $upstream apply $reconnectPatch
    if ($LASTEXITCODE -ne 0) { throw 'Could not apply the pinned libchiaki reconnect fixes.' }
}
& git -C $upstream submodule update --init --depth 1 --jobs 3 third-party/nanopb third-party/gf-complete third-party/jerasure
if ($LASTEXITCODE -ne 0) { throw 'Could not obtain the pinned chiaki-ng submodules.' }
$env:MSYSTEM='UCRT64'
$env:CHERE_INVOKING='1'
$env:CADRIVECT_NATIVE_SRC=$PSScriptRoot.Replace('\','/')
$env:CADRIVECT_NATIVE_BUILD=$work.Replace('\','/')
$env:CADRIVECT_NATIVE_VERIFY=if ($Verify) {'1'} else {'0'}
& $bash -l ($env:CADRIVECT_NATIVE_SRC+'/build-native.sh')
if ($LASTEXITCODE -ne 0) { throw 'The native Direct backend build failed.' }
