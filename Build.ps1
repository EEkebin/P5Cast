[CmdletBinding()]
param([switch]$DashboardOnly,[string]$OutDir=$PSScriptRoot)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
[void][IO.Directory]::CreateDirectory($OutDir)
$options = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',
    ("/win32icon:" + (Join-Path $PSScriptRoot 'app\assets\mark.ico')),
    ("/out:" + (Join-Path $OutDir 'P5Cast.exe')), '/reference:System.Web.Extensions.dll','/reference:System.Xaml.dll','/reference:System.Security.dll',
    ("/win32manifest:" + (Join-Path $PSScriptRoot 'app\app.manifest')))
foreach ($lib in @('WindowsBase','PresentationCore','PresentationFramework')) {
    $options += '/reference:' + (Join-Path $framework "WPF\$lib.dll")
}
$appSources=Get-ChildItem (Join-Path $PSScriptRoot 'app') -File -Filter '*.cs' | ForEach-Object FullName
& $compiler @options /reference:System.Drawing.dll @appSources (Join-Path $PSScriptRoot 'engine\DisplayTarget.cs')
if ($LASTEXITCODE -ne 0) { throw 'The dashboard did not compile.' }
if (!$DashboardOnly) {
    & (Join-Path $PSScriptRoot 'engine\Build.ps1') -OutDir (Join-Path $OutDir 'engine\bin')
    & (Join-Path $PSScriptRoot 'sources\native\Build.ps1')
    $coreDir=Join-Path $OutDir 'engine\bin\direct'
    [void][IO.Directory]::CreateDirectory($coreDir)
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'sources\native\bin') | Copy-Item -Destination $coreDir -Recurse -Force
}
