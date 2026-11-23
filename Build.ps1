[CmdletBinding()]
param([switch]$DashboardOnly)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$options = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',
    ("/win32icon:" + (Join-Path $PSScriptRoot 'app\assets\mark.ico')),
    ("/out:" + (Join-Path $PSScriptRoot 'Cadrivect.exe')), '/reference:System.Web.Extensions.dll','/reference:System.Xaml.dll',
    ("/win32manifest:" + (Join-Path $PSScriptRoot 'app\app.manifest')))
foreach ($lib in @('WindowsBase','PresentationCore','PresentationFramework')) {
    $options += '/reference:' + (Join-Path $framework "WPF\$lib.dll")
}
& $compiler @options /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'app\Program.cs') (Join-Path $PSScriptRoot 'engine\DisplayTarget.cs')
if ($LASTEXITCODE -ne 0) { throw 'The dashboard did not compile.' }
if (!$DashboardOnly) {
    & (Join-Path $PSScriptRoot 'engine\Build.ps1')
}
