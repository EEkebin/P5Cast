[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$app = Join-Path $env:LOCALAPPDATA 'Programs\Cadrivect'
$installed = Join-Path $app 'Cadrivect.exe'
if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Cadrivect.exe'))) { throw 'Extract the complete Windows ZIP or run Build.ps1 first.' }
if ([IO.Path]::GetFullPath($PSScriptRoot) -ne [IO.Path]::GetFullPath($app)) {
    if (Get-Process Cadrivect,CadrivectPlayer -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($app+'\', [StringComparison]::OrdinalIgnoreCase) }) {
        throw 'Close the installed Cadrivect dashboard and player, then retry.'
    }
    [IO.Directory]::CreateDirectory($app) | Out-Null
    foreach ($entry in 'Cadrivect.exe','app','engine','streaming','sources','README.md') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $entry) -Destination $app -Force -Recurse
    }
}
$menu = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$shell = New-Object -ComObject WScript.Shell
foreach ($name in 'Cadrivect','Cadrivect Client') {
    $shortcut = $shell.CreateShortcut((Join-Path $menu "$name.lnk"))
    $shortcut.TargetPath = $installed
    $shortcut.IconLocation = "$installed,0"
    $shortcut.WorkingDirectory = $app
    if ($name -like '*Client') { $shortcut.Arguments = '--client' }
    $shortcut.Save()
}
foreach ($oldName in 'LumaRelay','LumaRelay Client') {
    $link=Join-Path $menu "$oldName.lnk"
    if ((Test-Path -LiteralPath $link) -and $shell.CreateShortcut($link).TargetPath -eq (Join-Path $env:LOCALAPPDATA 'Programs\LumaRelay\LumaRelay.exe')) {
        Remove-Item -LiteralPath $link
    }
}
Write-Output "Installed Cadrivect for the current user at $app."
