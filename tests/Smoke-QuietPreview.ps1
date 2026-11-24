[CmdletBinding()]
param([string]$InstalledMoonlight)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @PSBoundParameters
    if ($LASTEXITCODE) { throw 'Quiet preview smoke test failed.' }
    return
}

# Load just the launch functions. This test never starts a streaming application.
$relay = Join-Path $PSScriptRoot '..\streaming\Relay.ps1'
$command=Get-Command $relay
$range=$command.Parameters['Mbps'].Attributes | Where-Object { $_ -is [Management.Automation.ValidateRangeAttribute] }
if ($range.MaxRange -ne 500) { throw 'The relay must accept Moonlight stream bitrates up to 500 Mbps.' }
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($relay,[ref]$null,[ref]$parseErrors)
if ($parseErrors.Count) { throw $parseErrors[0] }
foreach ($name in 'Test-SamePcTarget','Confirm-QuietPreviewRuntime','Start-QuietPreview','Start-RelayClient','Write-Text','Write-HostApplications') {
    $node = $ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$false)
    . ([scriptblock]::Create($node.Extent.Text))
}
foreach ($address in '127.0.0.1','127.5.6.7','0.0.0.0','localhost.','host.localhost',[Environment]::MachineName) {
    if (!(Test-SamePcTarget $address)) { throw "Missed local target $address." }
}
foreach ($nic in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    foreach ($entry in $nic.GetIPProperties().UnicastAddresses) {
        if (!(Test-SamePcTarget $entry.Address.ToString())) { throw "Missed address assigned to $($nic.Name)." }
    }
}
if (Test-SamePcTarget '192.0.2.123') { throw 'Test-net remote address classified as local.' }
if ($InstalledMoonlight) { Confirm-QuietPreviewRuntime $InstalledMoonlight }

$dir = Join-Path ([IO.Path]::GetTempPath()) ('Cadrivect-audio-test-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($dir) | Out-Null
$fixture = Join-Path $dir 'Moonlight.exe'
$result = Join-Path $dir 'environment.txt'
$utf8 = New-Object Text.UTF8Encoding($false)
try {
    $appsFile = Join-Path $dir 'apps.json'
    Write-HostApplications $appsFile
    $apps = @( (Get-Content -Raw -LiteralPath $appsFile | ConvertFrom-Json).apps )
    if ($apps.Count -ne 1 -or $apps[0].name -ne 'P5Cast') { throw 'A fresh host advertised the wrong application.' }
    Write-Text $appsFile '{"env":{},"apps":[{"name":"Cadrivect","image-path":"desktop.png"}]}'
    Write-HostApplications $appsFile
    Write-HostApplications $appsFile
    $apps = @( (Get-Content -Raw -LiteralPath $appsFile | ConvertFrom-Json).apps )
    if ($apps.Count -ne 2 -or $apps[0].name -ne 'P5Cast' -or $apps[1].name -ne 'Cadrivect' -or $apps[1].'image-path' -ne 'desktop.png') {
        throw 'The upgraded host did not keep the previous stream entry.'
    }
    Add-Type -OutputType WindowsApplication -OutputAssembly $fixture -TypeDefinition @'
using System;
using System.IO;
internal static class Probe {
    [STAThread] static void Main(string[] args) {
        File.WriteAllLines("environment.txt", new string[] {
            Environment.GetEnvironmentVariable("ML_AUDIO"),
            Environment.GetEnvironmentVariable("SDL_AUDIODRIVER"),
            Environment.GetEnvironmentVariable("SDL_AUDIO_DRIVER"),
            String.Join(" ", args)
        });
    }
}
'@
    function Install-Binary($Name) { return $fixture }
    $Action='Play'
    $Address='127.0.0.1'
    $refused=$false
    try { Start-RelayClient | Out-Null } catch { $refused=$_.Exception.Message -like '*Use the Play page*' }
    if (!$refused -or (Test-Path -LiteralPath $result)) { throw 'Unknown runtime passed the same-PC launch check.' }

    # Substitute the harmless verified test executable for the production hash gate.
    function Confirm-QuietPreviewRuntime($Binary) { if ($Binary -ne $fixture) { throw 'Unexpected test executable.' } }
    $Action='PreparePlay'
    $Hdr=$true
    $Codec='HEVC'
    $plan=Start-RelayClient
    if(!$plan.muted -or $plan.pairing -or $plan.arguments -notcontains '--hdr' -or $plan.arguments -notcontains 'windowed' -or (Test-Path -LiteralPath $result)) {
        throw 'Embedded local stream preparation did not preserve mute, HDR and windowed launch settings.'
    }
    if($plan.arguments -contains '--audio-config') { throw 'Omitting the audio layout must preserve the Moonlight preference.' }
    $Mbps=500
    foreach ($AudioLayout in 'stereo','5.1-surround','7.1-surround') {
        $plan=Start-RelayClient
        $layoutIndex=[Array]::IndexOf($plan.arguments,'--audio-config')
        $bitrateIndex=[Array]::IndexOf($plan.arguments,'--bitrate')
        if($layoutIndex -lt 0 -or $plan.arguments[$layoutIndex+1] -ne $AudioLayout -or
           $bitrateIndex -lt 0 -or $plan.arguments[$bitrateIndex+1] -ne '500000') {
            throw "Stream plan failed to forward the $AudioLayout layout or 500 Mbps bitrate."
        }
    }
    $Action='PreparePair'
    $pair=Start-RelayClient
    if(!$pair.pairing -or $pair.muted -or $pair.arguments -contains '--audio-config' -or $pair.arguments -contains '--bitrate' -or
       $pair.arguments[-1] -notmatch '^[0-9]{4}$' -or !$pair.message.Contains($pair.arguments[-1]) -or (Test-Path -LiteralPath $result)) {
        throw 'Embedded pairing plan did not provide its PIN without launching the client.'
    }
    $Action='Play'; $Hdr=$false; $Codec=$null; $AudioLayout=$null; $Mbps=0
    $environmentKeys = @('ML_AUDIO','SDL_AUDIODRIVER','SDL_AUDIO_DRIVER')
    $prior = @($environmentKeys | ForEach-Object { [string][Environment]::GetEnvironmentVariable($_,'Process') })
    foreach ($case in @(@{address='127.0.0.1';expected=@('sdl','dummy','dummy')},@{address='192.0.2.123';expected=$prior})) {
        $Address=$case.address
        $reply=Start-RelayClient
        $child=Get-Process -Id $reply.processId -ErrorAction SilentlyContinue
        if ($child -and !$child.WaitForExit(5000)) { throw 'Test probe timed out.' }
        $actual=[IO.File]::ReadAllLines($result)
        if ($actual.Count -ne 4 -or $actual[3] -ne "stream $Address P5Cast --audio-on-host") { throw 'Unexpected stream command.' }
        for ($i=0;$i -lt $environmentKeys.Count;$i++) {
            if ($actual[$i] -cne $case.expected[$i]) { throw "Incorrect child environment for $Address." }
            if ([string][Environment]::GetEnvironmentVariable($environmentKeys[$i],'Process') -cne $prior[$i]) { throw 'Child audio settings leaked into the relay.' }
        }
        if (($Address -eq '127.0.0.1') -ne ($reply.clientAudio -eq 'muted')) { throw 'Incorrect client audio status.' }
        Remove-Item -LiteralPath $result
    }
    'Passed host entries, local-address and mute handling, 500 Mbps, and all three Moonlight audio layouts.'
} finally {
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($dir)) -eq $tempRoot -and [IO.Path]::GetFileName($dir) -match '^Cadrivect-audio-test-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
}
