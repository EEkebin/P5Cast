[CmdletBinding()]
param(
    [ValidateSet('StartHost','StopHost','Status','OpenClient','PairClient','Play','ApprovePin','InstallClient','InstallHost','ListClients','RevokeClient')]
    [string] $Action = 'Status',
    [string] $Address,
    [ValidatePattern('^[0-9]{4}$')][string] $Pin,
    [guid] $ClientId,
    [ValidateSet('720','1080','1440','4K')][string] $Resolution,
    [ValidateRange(10,240)][int] $Fps,
    [ValidateRange(1,250)][int] $Mbps,
    [ValidateSet('auto','H.264','HEVC','AV1')][string] $Codec,
    [switch] $Windowed
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Join-Path $env:LOCALAPPDATA 'LumaRelay\Streaming'
$private = Join-Path $root 'host'
$sessionFile = Join-Path $private 'session.json'
$certificate = Join-Path $private 'host.pem'
$secretFile = Join-Path $private 'management.dpapi'
$port = 47989
$packages = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'packages.json') | ConvertFrom-Json
$utf8 = New-Object Text.UTF8Encoding($false)

function Write-Text($File, $Value) {
    [IO.File]::WriteAllText($File, $Value, $utf8)
}
function Get-BinaryPath($Name) {
    $pkg = $packages.$Name
    Join-Path (Join-Path $root ("{0}-{1}" -f $Name,$pkg.version)) $pkg.executable
}
function Install-Binary($Name) {
    $binary = Get-BinaryPath $Name
    if (Test-Path -LiteralPath $binary -PathType Leaf) { return $binary }
    $pkg = $packages.$Name
    $id = "{0}-{1}" -f $Name,$pkg.version
    $cache = Join-Path $root 'cache'
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    $archive = Join-Path $cache "$id.zip"
    $working = Join-Path $root ([guid]::NewGuid().ToString())
    [IO.Directory]::CreateDirectory($working) | Out-Null
    try {
        if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $pkg.sha256) {
            $partial = Join-Path $working 'download.zip'
            & "$env:SystemRoot\System32\curl.exe" --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 2 --connect-timeout 15 --max-time 300 --output $partial $pkg.url
            if ($LASTEXITCODE -ne 0) { throw "Could not download the official $Name package. Check network access and try again." }
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $pkg.sha256) { throw "$Name archive checksum mismatch." }
            Move-Item -LiteralPath $partial -Destination $archive -Force
        }
        $staging = Join-Path $working 'extracted'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
        if (!(Test-Path -LiteralPath (Join-Path $staging $pkg.executable) -PathType Leaf)) { throw "Unexpected $Name package layout." }
        [IO.Directory]::Move($staging, (Join-Path $root $id))
    } finally {
        # The staging directory has an unguessable, locally generated name directly under our data root.
        $resolved = [IO.Path]::GetFullPath($working)
        if ([IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetFullPath($root)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    return $binary
}
function Test-PrivateAddress([string]$Value) {
    $ip = [Net.IPAddress]::None
    if (![Net.IPAddress]::TryParse($Value,[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork' -or $ip.ToString() -ne $Value) { return $false }
    $b = $ip.GetAddressBytes()
    return ($Value -eq '127.0.0.1' -or $b[0] -eq 10 -or ($b[0] -eq 192 -and $b[1] -eq 168) -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 100 -and $b[1] -ge 64 -and $b[1] -le 127))
}
function Get-LocalAddresses {
    $adapters = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object OperationalStatus -eq Up
    foreach ($adapter in $adapters) {
        $properties = $adapter.GetIPProperties()
        foreach ($item in $properties.UnicastAddresses) {
            $ip = $item.Address.ToString()
            if ($ip -ne '127.0.0.1' -and (Test-PrivateAddress $ip)) {
                [pscustomobject]@{ address=$ip; adapter=$adapter.Name; hasGateway=($properties.GatewayAddresses.Count -gt 0) }
            }
        }
    }
}
function Get-Session {
    if (!(Test-Path -LiteralPath $sessionFile)) { return }
    $session = Get-Content -LiteralPath $sessionFile -Raw | ConvertFrom-Json
    $proc = Get-Process -Id $session.processId -ErrorAction SilentlyContinue
    if ($proc -and $proc.Path -eq (Get-BinaryPath sunshine) -and $proc.StartTime.ToUniversalTime().Ticks.ToString() -eq $session.startTicks) { return $session }
}
function New-HostCredential {
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $passwordBytes = New-Object byte[] 32
    $saltBytes = New-Object byte[] 16
    try { $random.GetBytes($passwordBytes); $random.GetBytes($saltBytes) } finally { $random.Dispose() }
    $password = [Convert]::ToBase64String($passwordBytes)
    $salt = [Convert]::ToBase64String($saltBytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = $sha.ComputeHash($utf8.GetBytes($password+$salt)) } finally { $sha.Dispose() }
    # Sunshine's util::hex serializes this digest from the final byte to the first.
    [Array]::Reverse($digest)
    $creds = @{username='lumarelay'; salt=$salt; password=[BitConverter]::ToString($digest).Replace('-','')}
    Write-Text (Join-Path $private 'credentials.json') ($creds | ConvertTo-Json -Compress)
    Write-Text $secretFile (ConvertFrom-SecureString (ConvertTo-SecureString $password -AsPlainText -Force))
}
function Invoke-HostApi($Session, $Route, $Body) {
    if (!('LumaRelay.Streaming.HostApi' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'HostApi.cs') }
    $protectedPassword = Get-Content -Raw -LiteralPath $secretFile | ConvertTo-SecureString
    $cred = New-Object Net.NetworkCredential('lumarelay', $protectedPassword)
    $url = 'https://{0}:{1}/api/{2}' -f $Session.address,($port+1),$Route
    $method = [LumaRelay.Streaming.HostApi]
    if ($null -eq $Body) { $raw = $method::Request($url,$certificate,$cred.Password,[System.Management.Automation.Language.NullString]::Value) }
    else { $raw = $method::Request($url,$certificate,$cred.Password,($Body | ConvertTo-Json -Compress)) }
    return ($raw | ConvertFrom-Json)
}
function Start-RelayHost {
    $chosen = $Address
    $available = @(Get-LocalAddresses | Sort-Object hasGateway -Descending)
    if (!$chosen -and $available.Count) { $chosen = $available[0].address }
    if (!$chosen) { $chosen = '127.0.0.1' }
    if (!(Test-PrivateAddress $chosen) -or ($chosen -ne '127.0.0.1' -and $available.address -notcontains $chosen)) { throw 'Choose a private IPv4 address assigned to an active LAN or VPN adapter on this host.' }
    $current = Get-Session
    if ($current) {
        if ($current.address -ne $chosen) { throw "Already hosting on $($current.address). Stop hosting before changing the address." }
        return @{ok=$true; message="Already hosting on $chosen."; address=$chosen}
    }
    $binary = Install-Binary sunshine
    [IO.Directory]::CreateDirectory($private) | Out-Null
    New-HostCredential
    $apps = Join-Path $private 'apps.json'
    Write-Text $apps '{"env":{},"apps":[{"name":"LumaRelay","image-path":"desktop.png"}]}'
    $configPath = Join-Path $private 'sunshine.conf'
    $log = Join-Path $private 'sunshine.log'
    $config = @(
        'sunshine_name = LumaRelay', "port = $port", "bind_address = $chosen", 'address_family = ipv4',
        "external_ip = $chosen", 'upnp = disabled', 'origin_web_ui_allowed = lan',
        'lan_encryption_mode = 2', 'wan_encryption_mode = 2', 'system_tray = disabled',
        'install_steam_audio_drivers = disabled', 'nvenc_opengl_vulkan_on_dxgi = disabled', 'nvenc_latency_over_power = disabled',
        'gamepad = ds4', 'gamepad_driver = all', 'ds4_back_as_touchpad_click = enabled',
        "pkey = $private\host.key", "cert = $certificate", "credentials_file = $private\credentials.json",
        "file_state = $private\paired.json", "file_apps = $apps", "log_path = $log"
    ) -join [Environment]::NewLine
    Write-Text $configPath $config
    $proc = Start-Process -FilePath $binary -ArgumentList ('"{0}"' -f $configPath) -WorkingDirectory (Split-Path $binary) -WindowStyle Hidden -PassThru
    try {
        $session = @{processId=$proc.Id; startTicks=$proc.StartTime.ToUniversalTime().Ticks.ToString(); address=$chosen}
        $deadline = [DateTime]::UtcNow.AddSeconds(75)
        do {
            if ($proc.HasExited) { throw "Sunshine exited ($($proc.ExitCode)). Check $log for an occupied port or encoder error." }
            try {
                if ((Test-Path -LiteralPath $certificate) -and $null -ne (Invoke-HostApi $session 'pin' $null)) {
                    Write-Text $sessionFile ($session | ConvertTo-Json -Compress)
                    return @{ok=$true; message="Hosting on $chosen. Pair a client, then open your game on this display."; address=$chosen; log=$log}
                }
            } catch { $lastFailure = $_.Exception.GetBaseException().Message }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Host management did not become ready. $lastFailure See $log."
    } catch {
        if (!$proc.HasExited) { Stop-OwnedHost $proc }
        throw
    }
}
function Stop-OwnedHost($Process) {
    if (!('LumaRelay.Streaming.HostApi' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'HostApi.cs') }
    if ([LumaRelay.Streaming.HostApi]::RequestStop($Process.Id) -and $Process.WaitForExit(12000)) { return }
    $Process.Refresh()
    if (!$Process.HasExited) { $Process.Kill() }
    if (!$Process.WaitForExit(3000)) { throw 'Sunshine is still shutting down.' }
}
function Get-Status {
    $session = Get-Session
    $addresses = @(Get-LocalAddresses | Sort-Object hasGateway -Descending)
    $vigem = Get-Service -Name ViGEmBus -ErrorAction SilentlyContinue
    return @{ok=$true; message=$(if ($session) {"Hosting on $($session.address)."} else {'Host is stopped.'});
        running=[bool]$session; address=$session.address; addresses=$addresses;
        controllerDriver=[bool]$vigem; controllerDriverName='ViGEmBus';
        hostInstalled=(Test-Path -LiteralPath (Get-BinaryPath sunshine)); clientInstalled=(Test-Path -LiteralPath (Get-BinaryPath moonlight))}
}
function Start-RelayClient {
    if ($Action -ne 'OpenClient' -and (!$Address -or $Address -notmatch '^(?=.{1,253}$)[a-zA-Z0-9][a-zA-Z0-9.-]*$')) { throw 'Enter the host IPv4 address or DNS name.' }
    $binary = Install-Binary moonlight
    $prefsDir = Join-Path (Split-Path $binary) 'Moonlight Game Streaming Project'
    $prefs = Join-Path $prefsDir 'Moonlight.ini'
    if (!(Test-Path -LiteralPath $prefs)) {
        [IO.Directory]::CreateDirectory($prefsDir) | Out-Null
        Write-Text $prefs (('[General]','richpresence=false') -join [Environment]::NewLine)
    }
    $arguments = @()
    if ($Action -eq 'PairClient') {
        $arguments = @('pair',$Address)
        if ($Pin) { $arguments += @('--pin',$Pin) }
    } elseif ($Action -eq 'Play') {
        $arguments = @('stream',$Address,'LumaRelay','--audio-on-host')
        if ($Resolution) { $arguments += "--$Resolution" }
        if ($Fps) { $arguments += @('--fps',"$Fps") }
        if ($Mbps) { $arguments += @('--bitrate',([string]($Mbps*1000))) }
        if ($Codec) { $arguments += @('--video-codec',$Codec) }
        if ($Windowed) { $arguments += @('--display-mode','windowed') }
    }
    $launch = @{FilePath=$binary; WorkingDirectory=(Split-Path $binary); PassThru=$true}
    if ($arguments.Count) { $launch.ArgumentList = $arguments }
    $proc = Start-Process @launch
    return @{ok=$true; message=$(if ($Action -eq 'PairClient') {'Enter the PIN shown by Moonlight on the host.'} elseif ($Action -eq 'Play') {'Connecting in Moonlight.'} else {'Moonlight opened.'}); processId=$proc.Id}
}
function Approve-Pin {
    if (!$Pin) { throw 'Enter the four-digit PIN displayed on the client.' }
    $session = Get-Session
    if (!$session) { throw 'Start the host first.' }
    $pending = @((Invoke-HostApi $session 'pin' $null).pairings)
    if ($pending.Count -eq 0) { throw 'No pending request. Select Pair on the client first.' }
    if ($pending.Count -gt 1) { throw 'Multiple clients are requesting pairing. Cancel the other requests and try again.' }
    $request = $pending[0]
    $result = Invoke-HostApi $session 'pin' @{pairing_id=$request.id; pin=$Pin; name=$request.name}
    if (!$result.status) { throw 'The pairing handshake failed. Start pairing again and confirm the new PIN.' }
    return @{ok=$true; message="Paired $($request.name)."; client=$request.name}
}

$mutex = $null
$locked = $false
try {
    if ($Action -ne 'Status') {
        $mutex = New-Object Threading.Mutex($false,'Local\LumaRelayStreaming')
        try { $locked = $mutex.WaitOne(310000) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (!$locked) { throw 'Another streaming action is still running.' }
    }
    $result = switch ($Action) {
        'StartHost' { Start-RelayHost }
        'StopHost' {
            $session = Get-Session
            if ($session) {
                $proc = Get-Process -Id $session.processId
                Stop-OwnedHost $proc
            }
            @{ok=$true; message='Host stopped.'}
        }
        'Status' { Get-Status }
        'InstallHost' { @{ok=$true; path=(Install-Binary sunshine); message='Sunshine is ready.'} }
        'InstallClient' { @{ok=$true; path=(Install-Binary moonlight); message='Moonlight is ready.'} }
        'ApprovePin' { Approve-Pin }
        'ListClients' {
            $session = Get-Session
            if (!$session) { throw 'Start the host first.' }
            $clients = @((Invoke-HostApi $session 'clients/list' $null).named_certs | Select-Object name,uuid,enabled)
            @{ok=$true; message='Paired clients.'; clients=$clients}
        }
        'RevokeClient' {
            if (!$ClientId -or $ClientId -eq [guid]::Empty) { throw 'Specify the paired client UUID from ListClients.' }
            $session = Get-Session
            if (!$session) { throw 'Start the host first.' }
            $paired = (Invoke-HostApi $session 'clients/list' $null).named_certs | Where-Object uuid -eq $ClientId.ToString() | Select-Object -First 1
            if (!$paired) { throw 'That client pairing was not found.' }
            $response = Invoke-HostApi $session 'clients/unpair' @{uuid=$paired.uuid}
            if (!$response.status) { throw 'The host could not revoke that client.' }
            @{ok=$true; message='Client pairing revoked.'}
        }
        default { Start-RelayClient }
    }
    $result | ConvertTo-Json -Depth 4 -Compress
} catch {
    @{ok=$false; message=$_.Exception.Message} | ConvertTo-Json -Compress
    exit 1
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
