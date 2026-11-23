[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$app = Join-Path $env:LOCALAPPDATA 'Programs\P5Cast'
$installed = Join-Path $app 'P5Cast.exe'
if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'P5Cast.exe'))) { throw 'Extract the complete Windows ZIP or run Build.ps1 first.' }
if ([IO.Path]::GetFullPath($PSScriptRoot) -ne [IO.Path]::GetFullPath($app)) {
    if (Get-Process P5Cast,CadrivectPlayer,CadrivectDirect -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($app+'\', [StringComparison]::OrdinalIgnoreCase) }) {
        throw 'Close the installed P5Cast dashboard and player, then retry.'
    }
    [IO.Directory]::CreateDirectory($app) | Out-Null
    foreach ($entry in 'P5Cast.exe','app','engine','streaming','sources','README.md') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $entry) -Destination $app -Force -Recurse
    }
}
$menu = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$group = Join-Path $menu 'P5Cast'
[IO.Directory]::CreateDirectory($group) | Out-Null
$shell = New-Object -ComObject WScript.Shell
foreach ($name in 'P5Cast','P5Cast Client') {
    $shortcut = $shell.CreateShortcut((Join-Path $group "$name.lnk"))
    $shortcut.TargetPath = $installed
    $shortcut.IconLocation = "$installed,0"
    $shortcut.WorkingDirectory = $app
    $shortcut.Arguments = if ($name -like '*Client') { '--client' } else { '' }
    $shortcut.Save()
}
foreach ($oldProduct in 'Cadrivect','LumaRelay') {
    $previous = Join-Path $env:LOCALAPPDATA "Programs\$oldProduct\$oldProduct.exe"
    foreach ($oldName in $oldProduct,"$oldProduct Client") {
        $link = Join-Path $menu "$oldName.lnk"
        if ((Test-Path -LiteralPath $link -PathType Leaf) -and $shell.CreateShortcut($link).TargetPath -eq $previous) {
            Remove-Item -LiteralPath $link
        }
    }
}
Write-Output "Installed P5Cast for the current user at $app."
