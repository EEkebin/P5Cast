# Functions shared by the source launch and discovery commands.
$sourceCache = Join-Path $env:LOCALAPPDATA 'LumaRelay\Sources'
$chiakiVersion = '1.10.1'
$chiakiArchiveUrl = "https://github.com/chiaki-ng/chiaki-ng/releases/download/v$chiakiVersion/chiaki-ng-win_x64-MSYS2-portable-$chiakiVersion.zip"
$chiakiArchiveSha256 = '6ca3f5e6780608104d79345ab7755cfa77fefc33a41ae10651a874c43f54ea57'
$chiakiDirectory = Join-Path $sourceCache "chiaki-ng-$chiakiVersion-x64"
$chiakiExe = Join-Path $chiakiDirectory 'chiaki-ng-Win\chiaki.exe'
$demoCode = Join-Path $PSScriptRoot 'SignalRun.cs'

function Find-SonyRemotePlay {
    $paths = @(
        "${env:ProgramFiles(x86)}\Sony\PS Remote Play\RemotePlay.exe",
        "$env:ProgramFiles\Sony\PS Remote Play\RemotePlay.exe"
    )
    $installed = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq 'PS Remote Play' -and $_.InstallLocation }
    foreach ($package in $installed) {
        $paths += (Join-Path $package.InstallLocation 'RemotePlay.exe')
    }
    $paths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}

function Get-DemoPath {
    $hash = (Get-FileHash -LiteralPath $demoCode -Algorithm SHA256).Hash.Substring(0,16)
    Join-Path $sourceCache "signal-run-$hash\SignalRun.exe"
}

function Install-SourceTool([string]$source) {
    [IO.Directory]::CreateDirectory($sourceCache) | Out-Null
    if ($source -eq 'Direct') {
        $destination = $chiakiExe
        $folder = $chiakiDirectory
    } else {
        $destination = Get-DemoPath
        $folder = Split-Path $destination
    }
    if (Test-Path -LiteralPath $destination -PathType Leaf) { return $destination }

    # Stage on the same volume so simultaneous launchers can publish atomically.
    $stage = Join-Path $sourceCache ([Guid]::NewGuid().ToString('N'))
    $payload = Join-Path $stage 'payload'
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    try {
        if ($source -eq 'Direct') {
            if (-not [Environment]::Is64BitOperatingSystem) { throw 'The bundled chiaki-ng package requires x64 Windows.' }
            $archive = Join-Path $stage 'chiaki.zip'
            $previousProtocols = [Net.ServicePointManager]::SecurityProtocol
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $client = New-Object Net.WebClient
                try { $client.DownloadFile($chiakiArchiveUrl, $archive) } finally { $client.Dispose() }
            } finally { [Net.ServicePointManager]::SecurityProtocol = $previousProtocols }
            if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $chiakiArchiveSha256) {
                throw 'The chiaki-ng download did not match the pinned SHA-256.'
            }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [IO.Compression.ZipFile]::ExtractToDirectory($archive, $payload)
            if (-not (Test-Path -LiteralPath (Join-Path $payload 'chiaki-ng-Win\chiaki.exe'))) {
                throw 'The verified chiaki-ng archive did not have the expected layout.'
            }
        } else {
            $framework = if ([Environment]::Is64BitOperatingSystem) { 'Framework64' } else { 'Framework' }
            $compiler = Join-Path $env:WINDIR "Microsoft.NET\$framework\v4.0.30319\csc.exe"
            $output = Join-Path $payload 'SignalRun.exe'
            $messages = & $compiler /nologo /target:winexe /optimize+ /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$output" $demoCode 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Could not build Signal Run. $messages" }
        }
        try { [IO.Directory]::Move($payload, $folder) }
        catch { if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw } }
        return $destination
    } finally {
        $checkedStage = [IO.Path]::GetFullPath($stage)
        if ([IO.Path]::GetDirectoryName($checkedStage) -eq [IO.Path]::GetFullPath($sourceCache)) {
            Remove-Item -LiteralPath $checkedStage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Find-SourceWindow([string]$path) {
    $name = [IO.Path]::GetFileNameWithoutExtension($path)
    Get-Process -Name $name -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -eq $path } |
        Sort-Object StartTime -Descending | Select-Object -First 1
}
