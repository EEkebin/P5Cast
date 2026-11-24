[CmdletBinding()]
param([string]$OutDir=(Join-Path $PSScriptRoot '..\bin'),[switch]$Verify,[switch]$VerifyHdr,
    [string]$DeviceBridge=(Join-Path $PSScriptRoot '..\bin\Cadrivect.Ngx.dll'))
$ErrorActionPreference='Stop'
$release='3.0.2'
$checksum='88B8A373F30E33F3558A77A93E634F11B8132FC3047EA1A8EDEEAD32B8471990'
$cache=[IO.Directory]::CreateDirectory((Join-Path $env:LOCALAPPDATA "P5Cast\build\xess-fg-$release")).FullName
$output=[IO.Directory]::CreateDirectory($OutDir).FullName
$stage=[IO.Directory]::CreateDirectory((Join-Path $cache ([guid]::NewGuid().ToString('N')))).FullName
try {
    $name="XeSS_SDK_$release.zip"
    $archive=Join-Path $cache $name
    if(!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $checksum){
        $download=Join-Path $stage $name
        & curl.exe -fsSL --retry 2 "https://github.com/intel/xess/releases/download/v$release/$name" -o $download
        if($LASTEXITCODE -or (Get-FileHash -LiteralPath $download).Hash -ne $checksum){throw 'Intel SDK download verification failed.'}
        Copy-Item -LiteralPath $download -Destination $archive -Force
    }
    & tar.exe -xf $archive -C $stage --include 'inc/*' --include 'bin/libxell.dll' --include 'bin/libxess_fg.dll' --include 'LICENSE.txt' --include 'third-party-programs.txt'
    if($LASTEXITCODE){throw 'Intel SDK extraction failed.'}
    . (Join-Path $PSScriptRoot '..\nvidia\Use-Compiler.ps1')
    Push-Location -LiteralPath $stage
    try {
        & cl.exe /nologo /std:c++17 /EHsc /MT /utf-8 /O2 /LD /Iinc "$PSScriptRoot\XessFg.cpp" /link /BREPRO /MANIFEST:NO /NODEFAULTLIB:oldnames.lib /OUT:P5Cast.XessFg.dll dxgi.lib user32.lib
        if($LASTEXITCODE){throw 'Intel XeSS-FG bridge build failed.'}
    } finally { Pop-Location }
    $runtimes=[IO.Directory]::CreateDirectory((Join-Path $output 'xess-fg')).FullName
    foreach($part in 'bin\libxell.dll','bin\libxess_fg.dll','LICENSE.txt','third-party-programs.txt'){
        Copy-Item -LiteralPath (Join-Path $stage $part) -Destination $runtimes -Force
    }
    Copy-Item -LiteralPath (Join-Path $stage 'P5Cast.XessFg.dll') -Destination $output -Force
    if($Verify -or $VerifyHdr){
        Push-Location -LiteralPath $stage
        try {
            & cl.exe /nologo /std:c++17 /EHsc /MT /O2 "$PSScriptRoot\Verify.cpp" /link /NODEFAULTLIB:oldnames.lib /OUT:Verify.exe dxgi.lib user32.lib
            if($LASTEXITCODE){throw 'Intel presentation verifier build failed.'}
            $device=(Resolve-Path -LiteralPath $DeviceBridge).Path
            $bridge=Join-Path $output 'P5Cast.XessFg.dll'
            if($Verify){& .\Verify.exe $device $bridge; if($LASTEXITCODE){throw 'Intel SDR frame generation test failed.'}}
            if($VerifyHdr){& .\Verify.exe $device $bridge hdr; if($LASTEXITCODE){throw 'Intel HDR10 frame generation test failed.'}}
        } finally {Pop-Location}
    }
} finally {
    if([IO.Path]::GetDirectoryName($stage) -eq $cache){Remove-Item -LiteralPath $stage -Recurse -Force}
}
