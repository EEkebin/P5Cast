[CmdletBinding()]
param([Parameter(Mandatory)][string]$Setup)
$ErrorActionPreference='Stop'
$exe=(Resolve-Path -LiteralPath $Setup).Path
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unknown setup version: $version" }
$registration='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\EEkebin.P5Cast_is1'
if (Test-Path -LiteralPath $registration) { throw 'Run the setup smoke test in a Windows account without an existing P5Cast setup registration.' }
$group='P5Cast-setup-test-'+[guid]::NewGuid().ToString('N')
$dest=Join-Path ([IO.Path]::GetTempPath()) $group
$shortcut=Join-Path ([Environment]::GetFolderPath('Programs')) "$group\P5Cast.lnk"
$app=Join-Path $dest "versions\$version"
$remove=Join-Path $dest 'unins000.exe'
function Run-Setup([string]$file,[string]$extra) {
    $p=Start-Process -WindowStyle Hidden -FilePath $file -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS '+$extra) -PassThru -Wait
    if ($p.ExitCode) { throw "Setup returned $($p.ExitCode)." }
}
try {
    $options='/DIR="'+$dest+'" /GROUP="'+$group+'"'
    Run-Setup $exe $options
    if (!(Test-Path -LiteralPath $shortcut) -or (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut).TargetPath -ne (Join-Path $app 'P5Cast.exe')) { throw 'Incorrect Start menu shortcut.' }
    if ((Get-ItemProperty -LiteralPath $registration).DisplayVersion -ne $version) { throw 'Incorrect Installed apps registration.' }
    # Reinstall with a corrupted file to exercise the normal repair path.
    [IO.File]::WriteAllText((Join-Path $app 'README.md'),'damaged test file')
    Run-Setup $exe $options
    $manifest=Get-Content -LiteralPath (Join-Path $app 'payload.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $app $file.path)).Hash -ne $file.sha256) { throw "Installed hash mismatch: $($file.path)" }
    }
    & (Join-Path $PSScriptRoot 'Smoke-Settings.ps1') -AppRoot $app
} finally {
    if ([IO.File]::Exists($remove)) { Run-Setup $remove '' }
}
if ((Test-Path -LiteralPath $shortcut) -or (Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $dest)) { throw 'Uninstall left setup-owned files or registration.' }
"Verified setup, repair, $($manifest.files.Count) installed files, dashboard settings, Start menu shortcut and uninstall."
