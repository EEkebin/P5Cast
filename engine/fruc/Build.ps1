[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\nvidia\Use-Compiler.ps1')
$cache=Join-Path $env:LOCALAPPDATA 'P5Cast\build\fruc'
[void][IO.Directory]::CreateDirectory($cache)
[void][IO.Directory]::CreateDirectory($OutDir)
& cl.exe /nologo /LD /EHsc /std:c++17 /MT /O2 "/Fo$cache\FrucClient.obj" "$PSScriptRoot\FrucClient.cpp" /link "/out:$OutDir\P5Cast.Fruc.dll" "/implib:$cache\P5Cast.Fruc.lib" /NODEFAULTLIB:oldnames.lib
if($LASTEXITCODE){throw 'NVIDIA FRUC adapter compilation failed.'}
& cl.exe /nologo /EHsc /std:c++17 /MT /O2 "/Fo$cache\" "$PSScriptRoot\Fruc.cpp" "$PSScriptRoot\FrucHost.cpp" /link "/out:$OutDir\P5Cast.FrucHost.exe" /subsystem:windows /NODEFAULTLIB:oldnames.lib d3d11.lib dxgi.lib wintrust.lib crypt32.lib
if($LASTEXITCODE){throw 'NVIDIA FRUC worker compilation failed.'}
