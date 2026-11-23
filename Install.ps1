[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$app = Join-Path $env:LOCALAPPDATA 'Programs\LumaRelay'
$installed = Join-Path $app 'LumaRelay.exe'
if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'LumaRelay.exe'))) { throw 'Extract the complete Windows ZIP or run Build.ps1 first.' }
if ([IO.Path]::GetFullPath($PSScriptRoot) -ne [IO.Path]::GetFullPath($app)) {
    if (Get-Process LumaRelay,LumaPlayer -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($app+'\', [StringComparison]::OrdinalIgnoreCase) }) {
        throw 'Close the installed LumaRelay dashboard and player, then retry.'
    }
    [IO.Directory]::CreateDirectory($app) | Out-Null
    foreach ($entry in 'LumaRelay.exe','app','engine','streaming','sources','README.md') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $entry) -Destination $app -Force -Recurse
    }
}
$menu = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$shell = New-Object -ComObject WScript.Shell
foreach ($name in 'LumaRelay','LumaRelay Client') {
    $shortcut = $shell.CreateShortcut((Join-Path $menu "$name.lnk"))
    $shortcut.TargetPath = $installed
    $shortcut.WorkingDirectory = $app
    if ($name -like '*Client') { $shortcut.Arguments = '--client' }
    $shortcut.Save()
}
Write-Output "Installed LumaRelay for the current user at $app."
