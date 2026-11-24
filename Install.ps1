[CmdletBinding()]
param([switch]$Launch)
$ErrorActionPreference = 'Stop'

function Install-Payload([string]$Source,[string]$App,[string]$Group) {
    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Source 'payload.json') | ConvertFrom-Json
    $version = [string]$manifest.version
    if ($version -notmatch '^\d+\.\d+\.\d+(-[a-z0-9.-]+)?$') { throw 'Invalid package version.' }
    $files = @{}
    foreach ($entry in $manifest.files) {
        $path = ([string]$entry.path).Replace('/','\')
        $parts = $path.Split('\')
        if ($path -match '[:*?"<>|\x00-\x1f]' -or @($parts | Where-Object { !$_ -or $_ -match '[. ]$' }).Count -or
            $files.ContainsKey($path) -or $path -eq 'payload.json' -or [string]$entry.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw 'Invalid file entry in the package manifest.'
        }
        $files[$path] = $entry.sha256
    }
    foreach ($required in 'P5Cast.exe','app\Main.xaml','engine\bin\CadrivectPlayer.exe','engine\bin\direct\CadrivectDirect.exe') {
        if (!$files.ContainsKey($required)) { throw "The package manifest is missing $required." }
    }

    $versions = [IO.Directory]::CreateDirectory((Join-Path $App 'versions')).FullName
    $stage = Join-Path $versions ('.install-'+[guid]::NewGuid().ToString('N'))
    $tempLink = Join-Path $Group ('P5Cast-'+[guid]::NewGuid().ToString('N')+'.lnk')
    [void][IO.Directory]::CreateDirectory($stage)
    try {
        foreach ($path in $files.Keys) {
            $target = Join-Path $stage $path
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy((Join-Path $Source $path),$target)
            if ((Get-FileHash -LiteralPath $target).Hash -ne $files[$path]) { throw "Incomplete or damaged release file: $path" }
        }
        [IO.File]::Copy((Join-Path $Source 'payload.json'),(Join-Path $stage 'payload.json'))
        $dest = Join-Path $versions $version
        if ([IO.Directory]::Exists($dest)) {
            # Reuse a verified installation, including a repaired copy. An older running copy can finish normally.
            $match = $null
            $manifestHash = (Get-FileHash -LiteralPath (Join-Path $stage 'payload.json')).Hash
            $candidates = @(Get-Item -LiteralPath $dest) + @(Get-ChildItem -Directory -LiteralPath $versions -Filter "$version-repair-*")
            foreach ($candidate in $candidates) {
                $candidateManifest = Join-Path $candidate.FullName 'payload.json'
                if (![IO.File]::Exists($candidateManifest) -or (Get-FileHash -LiteralPath $candidateManifest -ErrorAction SilentlyContinue).Hash -ne $manifestHash) { continue }
                $identical = $true
                foreach ($path in $files.Keys) {
                    $file = Join-Path $candidate.FullName $path
                    if (![IO.File]::Exists($file) -or (Get-FileHash -LiteralPath $file -ErrorAction SilentlyContinue).Hash -ne $files[$path]) { $identical=$false; break }
                }
                if ($identical) { $match=$candidate.FullName; break }
            }
            $dest = if ($match) {$match} else {Join-Path $versions ($version+'-repair-'+[guid]::NewGuid().ToString('N'))}
        }
        if (![IO.Directory]::Exists($dest)) { [IO.Directory]::Move($stage,$dest) }
        $installed = Join-Path $dest 'P5Cast.exe'
        [void][IO.Directory]::CreateDirectory($Group)
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($tempLink)
        $shortcut.TargetPath=$installed
        $shortcut.WorkingDirectory=$dest
        $shortcut.IconLocation="$installed,0"
        $shortcut.Save()
        $link = Join-Path $Group 'P5Cast.lnk'
        if ([IO.File]::Exists($link)) { [IO.File]::Replace($tempLink,$link,[System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($tempLink,$link) }
        return $installed
    } finally {
        if (([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -eq $versions) -and [IO.Directory]::Exists($stage)) {
            Remove-Item -LiteralPath $stage -Force -Recurse
        }
        if ([IO.File]::Exists($tempLink)) { [IO.File]::Delete($tempLink) }
    }
}

if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'payload.json'))) {
    # In a source checkout install exactly the files shipped by Package.ps1.
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Package.ps1'))) { throw 'Extract the entire P5Cast release, including payload.json, and retry.' }
    $archive = & (Join-Path $PSScriptRoot 'Package.ps1')
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $work = Join-Path $temp ('P5Cast-install-'+[guid]::NewGuid().ToString('N'))
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($archive.FullName,$work)
        & (Join-Path $work 'P5Cast\Install.ps1') -Launch:$Launch
    } finally {
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($work)) -eq $temp -and [IO.Directory]::Exists($work)) { Remove-Item -LiteralPath $work -Recurse -Force }
    }
    return
}

$app = Join-Path $env:LOCALAPPDATA 'Programs\P5Cast'
$menu = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$group = Join-Path $menu 'P5Cast'
$mutex = New-Object Threading.Mutex($false,('Local\P5Cast.Install.'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
$locked = $false
try {
    try { $locked=$mutex.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $locked=$true }
    if (!$locked) { throw 'Another P5Cast installation is still running.' }
    $installed = Install-Payload $PSScriptRoot $app $group
    $shell = New-Object -ComObject WScript.Shell
    $oldClientShortcut = Join-Path $group 'P5Cast Client.lnk'
    if ([IO.File]::Exists($oldClientShortcut) -and $shell.CreateShortcut($oldClientShortcut).TargetPath.StartsWith($app+'\',[StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $oldClientShortcut
    }
    foreach ($oldProduct in 'Cadrivect','LumaRelay') {
        $previous = Join-Path $env:LOCALAPPDATA "Programs\$oldProduct\$oldProduct.exe"
        foreach ($oldName in $oldProduct,"$oldProduct Client") {
            $link = Join-Path $menu "$oldName.lnk"
            if ([IO.File]::Exists($link) -and $shell.CreateShortcut($link).TargetPath -eq $previous) { Remove-Item -LiteralPath $link }
        }
    }
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
Write-Output "Installed P5Cast for the current user at $installed."
if ($Launch) { Start-Process -FilePath $installed -WorkingDirectory ([IO.Path]::GetDirectoryName($installed)) }
