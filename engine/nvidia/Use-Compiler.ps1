# Dot-source this file in a Windows x64 build process. Compiler files stay in the user cache.
$ErrorActionPreference = 'Stop'
if ((Get-Command cl.exe -ErrorAction SilentlyContinue) -and $env:INCLUDE -and $env:LIB -and $env:VSCMD_ARG_TGT_ARCH -eq 'x64') { return }

$compilerCache = Join-Path $env:LOCALAPPDATA 'Cadrivect\build\msvc-14.44.35207-sdk-26100'
$compilerReady = Join-Path $compilerCache '.ready'
$compilerLock = New-Object Threading.Mutex($false,'Local\Cadrivect-Msvc-14.44.35207')
$ownsCompilerLock = $false
try {
    try { $ownsCompilerLock = $compilerLock.WaitOne([TimeSpan]::FromMinutes(10)) }
    catch [Threading.AbandonedMutexException] { $ownsCompilerLock = $true }
    if (!$ownsCompilerLock) { throw 'Another compiler setup is still in progress.' }
    if (!(Test-Path -LiteralPath $compilerReady)) {
        [IO.Directory]::CreateDirectory($compilerCache) | Out-Null
        $compilerPackages = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'compiler-files.json') -Raw | ConvertFrom-Json
        foreach ($package in $compilerPackages) {
            $archive = Join-Path $compilerCache ([IO.Path]::GetFileName($package.url))
            if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $package.sha256) {
                & curl.exe --fail --silent --show-error --location --retry 2 $package.url --output $archive
                if ($LASTEXITCODE) { throw "Could not download $($package.url)." }
            }
            if ((Get-FileHash -LiteralPath $archive).Hash -ne $package.sha256) { throw "Compiler archive hash mismatch: $archive" }
            $filters = @()
            foreach ($pattern in $package.include) { $filters += '--include',$pattern }
            & tar.exe -xf $archive -C $compilerCache @filters
            if ($LASTEXITCODE) { throw "Could not extract $archive." }
        }
        [IO.File]::WriteAllText($compilerReady,'14.44.35207')
    }
}
finally {
    if ($ownsCompilerLock) { $compilerLock.ReleaseMutex() }
    $compilerLock.Dispose()
}
$msvcRoot = Join-Path $compilerCache 'Contents\VC\Tools\MSVC\14.44.35207'
$windowsHeaders = Join-Path $compilerCache 'c\Include\10.0.26100.0'
$env:INCLUDE = "$msvcRoot\include;$windowsHeaders\um;$windowsHeaders\shared;$windowsHeaders\ucrt;$windowsHeaders\winrt"
$env:LIB = "$msvcRoot\lib\x64;$compilerCache\c\um\x64;$compilerCache\c\ucrt\x64"
$env:Path = "$msvcRoot\bin\Hostx64\x64;$env:Path"
$env:VSCMD_ARG_TGT_ARCH = 'x64'
