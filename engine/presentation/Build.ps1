[CmdletBinding()]
param([string]$OutDir = (Join-Path $PSScriptRoot '..\bin'), [switch]$VerifyHdr)
$ErrorActionPreference = 'Stop'
$version = '3.4.20'
$archiveHash = '1EC3009655EE37B1F73E52B3B120ACC5AFD8ABAF9532F3303B1BF1DFE91B5969'
$cache = [IO.Directory]::CreateDirectory((Join-Path $env:LOCALAPPDATA "P5Cast\build\sdl-$version")).FullName
$out = [IO.Directory]::CreateDirectory($OutDir).FullName
$fileName = "SDL3-devel-$version-VC.zip"
$archive = Join-Path $cache $fileName
$id = [Guid]::NewGuid().ToString('N')
$stage = [IO.Directory]::CreateDirectory((Join-Path $cache $id)).FullName
try {
    if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $archiveHash) {
        $url = "https://github.com/libsdl-org/SDL/releases/download/release-$version/$fileName"
        $download = Join-Path $stage $fileName
        & curl.exe --fail --silent --show-error --location --retry 2 $url --output $download
        if ($LASTEXITCODE -or (Get-FileHash -LiteralPath $download).Hash -ne $archiveHash) {
            throw 'SDL development archive verification failed.'
        }
        Copy-Item -LiteralPath $download -Destination $archive -Force
    }
    $sdkDir = "SDL3-$version"
    & tar.exe -xf $archive -C $stage "$sdkDir/include" "$sdkDir/lib/x64/SDL3.dll" "$sdkDir/lib/x64/SDL3.lib" "$sdkDir/LICENSE.txt"
    if ($LASTEXITCODE) { throw 'SDL development archive extraction failed.' }
    $sdk = Join-Path $stage $sdkDir
    . (Join-Path $PSScriptRoot '..\nvidia\Use-Compiler.ps1')
    Push-Location -LiteralPath $stage
    try {
        & cl.exe /nologo /std:c++17 /EHsc /utf-8 /MT /O2 /c "$PSScriptRoot\D3D12Presenter.cpp"
        if ($LASTEXITCODE) { throw 'The D3D12 presenter did not compile.' }
        & cl.exe /nologo /std:c11 /utf-8 /MT /O2 /LD "/I$sdk\include" "$PSScriptRoot\Presenter.c" "$PSScriptRoot\HdrGpu.c" "$PSScriptRoot\GlEffects.c" D3D12Presenter.obj /link /BREPRO /MANIFEST:NO /NODEFAULTLIB:oldnames.lib /OUT:P5Cast.Presentation.dll "$sdk\lib\x64\SDL3.lib" user32.lib opengl32.lib dxgi.lib
        if ($LASTEXITCODE) { throw 'The SDL presenter did not compile.' }
        Copy-Item -LiteralPath '.\P5Cast.Presentation.dll', "$sdk\lib\x64\SDL3.dll" -Destination $out -Force
        Copy-Item -LiteralPath "$sdk\LICENSE.txt" -Destination (Join-Path $out 'SDL3-LICENSE.txt') -Force
        if ($VerifyHdr) {
            & cl.exe /nologo /std:c++17 /EHsc /utf-8 /MT /O2 "$PSScriptRoot\VerifyD3D12.cpp" /link /NODEFAULTLIB:oldnames.lib /OUT:VerifyD3D12.exe dxgi.lib user32.lib
            if ($LASTEXITCODE) { throw 'The D3D12 swap-chain test did not compile.' }
            & '.\VerifyD3D12.exe' (Join-Path $out 'Cadrivect.Ngx.dll')
            if ($LASTEXITCODE) { throw 'The native D3D12 swap-chain test failed.' }
            $test = Join-Path $PSScriptRoot '..\..\tests\GpuHdrTransportProbe.c'
            & cl.exe /nologo /std:c11 /utf-8 /MT /O2 /DP5CAST_VERIFY_HDR /DP5CAST_PRESENTER_BUILD=1 "/I$sdk\include" "/I$PSScriptRoot" "$PSScriptRoot\Presenter.c" "$PSScriptRoot\HdrGpu.c" "$PSScriptRoot\GlEffects.c" $test /link /NODEFAULTLIB:oldnames.lib /OUT:GpuHdrTransportProbe.exe "$sdk\lib\x64\SDL3.lib" user32.lib opengl32.lib
            if ($LASTEXITCODE) { throw 'The HDR GPU transport probe did not compile.' }
            Copy-Item -LiteralPath "$sdk\lib\x64\SDL3.dll" -Destination $stage
            foreach ($driver in 'direct3d12','vulkan') {
                & '.\GpuHdrTransportProbe.exe' $driver
                if ($LASTEXITCODE) { throw "HDR10 transport verification failed for $driver." }
            }
        }
    } finally { Pop-Location }
} finally {
    if ([IO.Path]::GetDirectoryName($stage) -eq $cache -and [IO.Path]::GetFileName($stage) -eq $id) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
