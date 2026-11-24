[CmdletBinding()]
param(
    [ValidateSet('StartHost','StopHost','Status','ResolveDisplay','OpenClient','PairClient','Play','PreparePair','PreparePlay','ApprovePin','InstallClient','InstallHost','ListClients','RevokeClient')]
    [string] $Action = 'Status',
    [string] $Address,
    [ValidatePattern('^[0-9]{4}$')][string] $Pin,
    [guid] $ClientId,
    [ValidateSet('720','1080','1440','4K')][string] $Resolution,
    [ValidateRange(10,240)][int] $Fps,
    [ValidateRange(1,500)][int] $Mbps,
    [ValidateSet('stereo','5.1-surround','7.1-surround')][string] $AudioLayout,
    [ValidateSet('auto','H.264','HEVC','AV1')][string] $Codec,
    [ValidatePattern('^\\\\\.\\DISPLAY[1-9][0-9]*$')][string] $DisplayDevice,
    [switch] $Hdr,
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
    $stream = [IO.File]::Open($sessionFile,'Open','Read',([IO.FileShare]::Read -bor [IO.FileShare]::Delete))
    try {
        $reader = New-Object IO.StreamReader($stream)
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    try { $session = $text | ConvertFrom-Json -ErrorAction Stop } catch { $session = $null }
    $binary = Get-BinaryPath sunshine
    $processId = 0
    if (![int]::TryParse([string]$session.processId,[ref]$processId) -or $processId -le 0 -or !$session.startTicks) {
        if (Get-Process -Name sunshine -ErrorAction SilentlyContinue | Where-Object Path -eq $binary) {
            throw 'The P5Cast host is still running and its saved session is unreadable. Close that Sunshine process before restarting the host.'
        }
        return
    }
    $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if (!$proc) { return }
    $owned = $false
    try {
        # Hold the same OS process handle through identity verification and StopHost.
        if ($proc.Path -ne $binary) { return }
        $null = $proc.Handle
        if (!$proc.HasExited -and $proc.Path -eq $binary -and $proc.StartTime.ToUniversalTime().Ticks.ToString() -eq $session.startTicks) {
            $session | Add-Member -NotePropertyName OwnedProcess -NotePropertyValue $proc -Force
            $owned = $true
            return $session
        }
    } catch [InvalidOperationException] { if (!$proc.HasExited) { throw } }
    finally { if (!$owned) { $proc.Dispose() } }
}
function New-HostCredential {
    Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
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
    Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
    if (!('Cadrivect.Streaming.HostApi' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'HostApi.cs') }
    $protectedPassword = Get-Content -Raw -LiteralPath $secretFile | ConvertTo-SecureString
    $cred = New-Object Net.NetworkCredential('lumarelay', $protectedPassword)
    $url = 'https://{0}:{1}/api/{2}' -f $Session.address,($port+1),$Route
    $method = [Cadrivect.Streaming.HostApi]
    if ($null -eq $Body) { $raw = $method::Request($url,$certificate,$cred.Password,[System.Management.Automation.Language.NullString]::Value) }
    else { $raw = $method::Request($url,$certificate,$cred.Password,($Body | ConvertTo-Json -Compress)) }
    return ($raw | ConvertFrom-Json)
}
function Resolve-HostDisplay([string]$Device) {
    if (!$Device) { throw 'Choose a Windows display to resolve.' }
    if (!('Cadrivect.Streaming.SunshineDisplay' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'SunshineDisplay.cs') }
    return [Cadrivect.Streaming.SunshineDisplay]::Resolve($Device)
}
function Confirm-HostDisplay($Log,$Device,$Id) {
    # Check the current binary's own documented startup inventory before announcing readiness.
    if (![IO.File]::Exists($Log)) { return $false }
    $stream = [IO.File]::Open($Log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object IO.StreamReader($stream)
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    $marker = 'Currently available display devices:'
    $start = $text.LastIndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { return $false }
    $json = [regex]::Match($text.Substring($start+$marker.Length),'(?ms)^\s*(\[.*?^\])')
    if (!$json.Success) { return $false }
    $selectedOutputs = @(($json.Groups[1].Value | ConvertFrom-Json) | Where-Object { $_.display_name -eq $Device })
    if ($selectedOutputs.Count -ne 1 -or $selectedOutputs[0].device_id -ne $Id) { throw 'Sunshine could not confirm the requested display. Refresh the display selection and restart the host.' }
    return $true
}
function Write-HostApplications($Path) {
    $entries = @(@{name='P5Cast'; 'image-path'='desktop.png'})
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        try { $prior = (Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -ErrorAction Stop).apps }
        catch { $prior = @() }
        foreach ($legacy in 'Cadrivect','LumaRelay') {
            if (@($prior.name) -contains $legacy) { $entries += @{name=$legacy; 'image-path'='desktop.png'} }
        }
    }
    Write-Text $Path (@{env=@{}; apps=$entries} | ConvertTo-Json -Depth 4 -Compress)
}
function Save-HostSession($Session) {
    $tempSession = Join-Path $private ([guid]::NewGuid().ToString('N')+'.tmp')
    try {
        Write-Text $tempSession ($Session | ConvertTo-Json -Compress)
        if ([IO.File]::Exists($sessionFile)) { [IO.File]::Replace($tempSession,$sessionFile,[System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($tempSession,$sessionFile) }
    } finally { [IO.File]::Delete($tempSession) }
}
function Start-HostProcess($chosen,$displayId,$log) {
    $binary = Install-Binary sunshine
    if (Get-Process -Name sunshine -ErrorAction SilentlyContinue | Where-Object Path -eq $binary) {
        throw 'An existing P5Cast host has no matching session record. Close that Sunshine process before restarting the host.'
    }
    [IO.Directory]::CreateDirectory($private) | Out-Null
    New-HostCredential
    $apps = Join-Path $private 'apps.json'
    Write-HostApplications $apps
    $configPath = Join-Path $private 'sunshine.conf'
    $config = @(
        'sunshine_name = P5Cast', "port = $port", "bind_address = $chosen", 'address_family = ipv4',
        "external_ip = $chosen", 'upnp = disabled', 'origin_web_ui_allowed = lan',
        'lan_encryption_mode = 2', 'wan_encryption_mode = 2', 'system_tray = disabled',
        'install_steam_audio_drivers = disabled', 'nvenc_opengl_vulkan_on_dxgi = disabled', 'nvenc_latency_over_power = disabled',
        'gamepad = ds4', 'gamepad_driver = all', 'ds4_back_as_touchpad_click = enabled',
        "pkey = $private\host.key", "cert = $certificate", "credentials_file = $private\credentials.json",
        "file_state = $private\paired.json", "file_apps = $apps", "log_path = $log"
    ) -join [Environment]::NewLine
    if ($displayId) { $config += [Environment]::NewLine + "output_name = $displayId" }
    Write-Text $configPath $config
    Start-Process -FilePath $binary -ArgumentList ('"{0}"' -f $configPath) -WorkingDirectory (Split-Path $binary) -WindowStyle Hidden -PassThru
}
function Start-RelayHost {
    $chosen = $Address
    $available = @(Get-LocalAddresses | Sort-Object hasGateway -Descending)
    if (!$chosen -and $available.Count) { $chosen = $available[0].address }
    if (!$chosen) { $chosen = '127.0.0.1' }
    if (!(Test-PrivateAddress $chosen) -or ($chosen -ne '127.0.0.1' -and $available.address -notcontains $chosen)) { throw 'Choose a private IPv4 address assigned to an active LAN or VPN adapter on this host.' }
    $current = Get-Session
    $displayId = if ($DisplayDevice) { Resolve-HostDisplay $DisplayDevice } else { '' }
    if ($current) {
        if ($current.address -ne $chosen) { throw "Already hosting on $($current.address). Stop hosting before changing the address." }
        if ([string]$current.displayId -ne $displayId) { throw 'Stop the host before changing the shared display.' }
        if (!$current.starting) { return @{ok=$true; message="Already hosting on $chosen."; address=$chosen; displayDevice=$current.displayDevice; displayId=$current.displayId} }
    }
    $log = Join-Path $private 'sunshine.log'
    $proc = if ($current) { $current.OwnedProcess } else { Start-HostProcess $chosen $displayId $log }
    try {
        $session = @{processId=$proc.Id; startTicks=$proc.StartTime.ToUniversalTime().Ticks.ToString(); address=$chosen; displayDevice=$DisplayDevice; displayId=$displayId; starting=$true}
        if (!$current) { Save-HostSession $session }
        $deadline = [DateTime]::UtcNow.AddSeconds(75)
        do {
            if ($proc.HasExited) { throw "Sunshine exited ($($proc.ExitCode)). Check $log for an occupied port or encoder error." }
            $ready = $false
            try { $ready = (Test-Path -LiteralPath $certificate) -and $null -ne (Invoke-HostApi $session 'pin' $null) }
            catch { $lastFailure = $_.Exception.GetBaseException().Message }
            if ($ready -and $displayId) {
                $ready = Confirm-HostDisplay $log $DisplayDevice $displayId
                if (!$ready) { $lastFailure = 'Waiting for Sunshine to finish reporting its active displays.' }
            }
            if ($ready) {
                $session.starting=$false
                Save-HostSession $session
                return @{ok=$true; message="Hosting on $chosen. Pair a client, then open your game on the shared display."; address=$chosen; log=$log; displayDevice=$DisplayDevice; displayId=$displayId}
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Host management did not become ready. $lastFailure See $log."
    } catch {
        if (!$proc.HasExited) { Stop-OwnedHost $proc }
        throw
    } finally { $proc.Dispose() }
}
function Stop-OwnedHost($Process) {
    if (!('Cadrivect.Streaming.HostApi' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'HostApi.cs') }
    if ([Cadrivect.Streaming.HostApi]::RequestStop($Process.Id) -and $Process.WaitForExit(12000)) { return }
    $Process.Refresh()
    if (!$Process.HasExited) { $Process.Kill() }
    if (!$Process.WaitForExit(3000)) { throw 'Sunshine is still shutting down.' }
}
function Get-Status {
    $session = Get-Session
    $addresses = @(Get-LocalAddresses | Sort-Object hasGateway -Descending)
    $vigem = Get-Service -Name ViGEmBus -ErrorAction SilentlyContinue
    return @{ok=$true; message=$(if ($session.starting) {"Host startup on $($session.address) is awaiting confirmation."} elseif ($session) {"Hosting on $($session.address)."} else {'Host is stopped.'});
        running=[bool]$session; address=$session.address; addresses=$addresses; displayDevice=$session.displayDevice; displayId=$session.displayId;
        controllerDriver=[bool]$vigem; controllerDriverName='ViGEmBus';
        hostInstalled=(Test-Path -LiteralPath (Get-BinaryPath sunshine)); clientInstalled=(Test-Path -LiteralPath (Get-BinaryPath moonlight))}
}
function Test-SamePcTarget([string]$Target) {
    $name = $Target.TrimEnd('.')
    if ($name -eq 'localhost' -or $name.EndsWith('.localhost',[StringComparison]::OrdinalIgnoreCase) -or $name -eq [Environment]::MachineName) { return $true }
    $literal = [Net.IPAddress]::None
    if ([Net.IPAddress]::TryParse($Target,[ref]$literal)) { $resolved = @($literal) }
    else {
        try { $resolved = [Net.Dns]::GetHostAddresses($Target) }
        catch { throw 'Could not resolve the host name. Enter its LAN or VPN address to connect.' }
        if (!$resolved.Count) { throw 'Could not resolve the host name. Enter its LAN or VPN address to connect.' }
    }
    $local = @(
        foreach ($nic in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            foreach ($entry in $nic.GetIPProperties().UnicastAddresses) { $entry.Address.ToString() }
        }
    )
    foreach ($ip in $resolved) {
        if ($ip.IsIPv4MappedToIPv6) { $ip = $ip.MapToIPv4() }
        if ([Net.IPAddress]::IsLoopback($ip) -or $ip.Equals([Net.IPAddress]::Any) -or $ip.Equals([Net.IPAddress]::IPv6Any) -or $local -contains $ip.ToString()) { return $true }
    }
    return $false
}
function Confirm-QuietPreviewRuntime([string]$Binary) {
    # Verified entries in the pinned Moonlight 6.2.0 portable archive. Recheck SDL's
    # dummy selection before updating these hashes to support a new runtime.
    $expected = @{
        'Moonlight.exe'='2F2536A5670A9C3F2A2A9C5E9FB46DFD684CCD9721BE8F2D5AC590B81B7CB0DB'
        'SDL2.dll'='1DF1B9A1E7C13B2429135DBEB9DECFA4617CD31A1A674A43DDDB8C013CEB211D'
        'SDL3.dll'='AF1671D2A250EBC4606423E34886ADBF3E709D7820CF74E3FD43A25273996D87'
    }
    foreach ($name in $expected.Keys) {
        $file = Join-Path (Split-Path $Binary) $name
        if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected[$name]) {
            throw 'The silent preview could not verify its audio runtime. Use the Play page on this PC.'
        }
    }
}
function Start-QuietPreview($Launch) {
    # Relay runs as a dedicated child of the dashboard. Keep Start-Process's
    # detached launch so the dashboard can finish reading the relay's stdout.
    $settings = @{ML_AUDIO='sdl'; SDL_AUDIODRIVER='dummy'; SDL_AUDIO_DRIVER='dummy'}
    $prior = @{}
    foreach ($key in $settings.Keys) { $prior[$key] = [Environment]::GetEnvironmentVariable($key,'Process') }
    try {
        foreach ($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key,$settings[$key],'Process') }
        return Start-Process @Launch
    } finally {
        foreach ($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key,$prior[$key],'Process') }
    }
}
function Start-RelayClient {
    if ($Action -ne 'OpenClient' -and (!$Address -or $Address -notmatch '^(?=.{1,253}$)[a-zA-Z0-9][a-zA-Z0-9.-]*$')) { throw 'Enter the host IPv4 address or DNS name.' }
    $playback = $Action -in 'Play','PreparePlay'
    $pairing = $Action -in 'PairClient','PreparePair'
    if ($playback -and $Hdr -and $Codec -eq 'H.264') { throw 'Choose Auto, HEVC, or AV1 for HDR streaming.' }
    $samePc = $playback -and (Test-SamePcTarget $Address)
    $binary = Install-Binary moonlight
    if ($samePc) { Confirm-QuietPreviewRuntime $binary }
    $prefsDir = Join-Path (Split-Path $binary) 'Moonlight Game Streaming Project'
    $prefs = Join-Path $prefsDir 'Moonlight.ini'
    if (!(Test-Path -LiteralPath $prefs)) {
        [IO.Directory]::CreateDirectory($prefsDir) | Out-Null
        Write-Text $prefs (('[General]','richpresence=false') -join [Environment]::NewLine)
    }
    $arguments = @()
    if ($pairing) {
        if ($Action -eq 'PreparePair' -and !$Pin) {
            $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
            try {
                $digits = New-Object byte[] 2
                do { $rng.GetBytes($digits); $number = [BitConverter]::ToUInt16($digits,0) } while ($number -ge 60000)
                $Pin = ($number % 10000).ToString('0000')
            } finally { $rng.Dispose() }
        }
        $arguments = @('pair',$Address)
        if ($Pin) { $arguments += @('--pin',$Pin) }
    } elseif ($playback) {
        $arguments = @('stream',$Address,'P5Cast','--audio-on-host')
        if ($Resolution) { $arguments += "--$Resolution" }
        if ($Fps) { $arguments += @('--fps',"$Fps") }
        if ($Mbps) { $arguments += @('--bitrate',([string]($Mbps*1000))) }
        if ($AudioLayout) { $arguments += @('--audio-config',$AudioLayout) }
        if ($Codec) { $arguments += @('--video-codec',$Codec) }
        if ($Hdr) { $arguments += '--hdr' }
        if ($Windowed -or $Action -eq 'PreparePlay') { $arguments += @('--display-mode','windowed') }
    }
    if ($Action -in 'PreparePlay','PreparePair') {
        $message = if ($pairing) { "Enter PIN $Pin on the host's Approve PIN screen." }
            elseif ($samePc) { 'Connecting to this PC with client audio muted to prevent feedback.' }
            else { 'Connecting to the streaming host.' }
        return @{ok=$true; executable=$binary; arguments=$arguments; muted=[bool]$samePc; pairing=[bool]$pairing; message=$message}
    }
    $launch = @{FilePath=$binary; WorkingDirectory=(Split-Path $binary); PassThru=$true}
    if ($arguments.Count) { $launch.ArgumentList = $arguments }
    if ($samePc) {
        $proc = Start-QuietPreview $launch
        return @{ok=$true; message='Same-PC video preview. Client audio is muted to prevent feedback.'; clientAudio='muted'; processId=$proc.Id}
    }
    $proc = Start-Process @launch
    return @{ok=$true; message=$(if ($Action -eq 'PairClient') {'Enter the PIN shown by Moonlight on the host.'} elseif ($Action -eq 'Play') {'Connecting in Moonlight.'} else {'Moonlight opened.'}); processId=$proc.Id}
}
function Approve-Pin {
    if (!$Pin) { throw 'Enter the four-digit PIN displayed on the client.' }
    $session = Get-Session
    if (!$session) { throw 'Start the host first.' }
    $pairings = (Invoke-HostApi $session 'pin' $null).pairings
    $pending = @($pairings | Where-Object { $null -ne $_ })
    if ($pending.Count -eq 0) { throw 'No pending request. Select Pair on the client first.' }
    if ($pending.Count -gt 1) { throw 'Multiple clients are requesting pairing. Cancel the other requests and try again.' }
    $request = $pending[0]
    if ([string]::IsNullOrWhiteSpace([string]$request.id)) { throw 'The pending pairing has no request ID. Pair again on the client.' }
    $result = Invoke-HostApi $session 'pin' @{pairing_id=$request.id; pin=$Pin; name=$request.name}
    if ([string]$result.status -notin 'true','1') { throw 'The pairing handshake failed. Start pairing again and confirm the new PIN.' }
    return @{ok=$true; message="Paired $($request.name)."; client=$request.name}
}

$mutex = $null
$locked = $false
try {
    if ($Action -notin 'Status','ResolveDisplay') {
        $mutex = New-Object Threading.Mutex($false,'Local\CadrivectStreaming')
        try { $locked = $mutex.WaitOne(310000) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (!$locked) { throw 'Another streaming action is still running.' }
    }
    $result = switch ($Action) {
        'StartHost' { Start-RelayHost }
        'StopHost' {
            $session = Get-Session
            if ($session) {
                try { if (!$session.OwnedProcess.HasExited) { Stop-OwnedHost $session.OwnedProcess } }
                finally { $session.OwnedProcess.Dispose() }
            }
            @{ok=$true; message='Host stopped.'}
        }
        'Status' { Get-Status }
        'ResolveDisplay' { @{ok=$true; displayDevice=$DisplayDevice; displayId=(Resolve-HostDisplay $DisplayDevice); message='Shared display resolved.'} }
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
            if ([string]$response.status -notin 'true','1') { throw 'The host could not revoke that client.' }
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
