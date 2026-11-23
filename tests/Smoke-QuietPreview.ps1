[CmdletBinding()]
param([string]$InstalledMoonlight)
$ErrorActionPreference = 'Stop'

# Load just the launch functions. This test never starts a streaming application.
$relay = Join-Path $PSScriptRoot '..\streaming\Relay.ps1'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($relay,[ref]$null,[ref]$parseErrors)
if ($parseErrors.Count) { throw $parseErrors[0] }
foreach ($name in 'Test-SamePcTarget','Confirm-QuietPreviewRuntime','Start-QuietPreview','Start-RelayClient','Write-Text') {
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
    $environmentKeys = @('ML_AUDIO','SDL_AUDIODRIVER','SDL_AUDIO_DRIVER')
    $prior = @($environmentKeys | ForEach-Object { [string][Environment]::GetEnvironmentVariable($_,'Process') })
    foreach ($case in @(@{address='127.0.0.1';expected=@('sdl','dummy','dummy')},@{address='192.0.2.123';expected=$prior})) {
        $Address=$case.address
        $reply=Start-RelayClient
        $child=Get-Process -Id $reply.processId -ErrorAction SilentlyContinue
        if ($child -and !$child.WaitForExit(5000)) { throw 'Test probe timed out.' }
        $actual=[IO.File]::ReadAllLines($result)
        if ($actual.Count -ne 4 -or $actual[3] -ne "stream $Address Cadrivect --audio-on-host") { throw 'Unexpected stream command.' }
        for ($i=0;$i -lt $environmentKeys.Count;$i++) {
            if ($actual[$i] -cne $case.expected[$i]) { throw "Incorrect child environment for $Address." }
            if ([string][Environment]::GetEnvironmentVariable($environmentKeys[$i],'Process') -cne $prior[$i]) { throw 'Child audio settings leaked into the relay.' }
        }
        if (($Address -eq '127.0.0.1') -ne ($reply.clientAudio -eq 'muted')) { throw 'Incorrect client audio status.' }
        Remove-Item -LiteralPath $result
    }
    'Passed local-address detection, unverified runtime refusal, child-only mute, and remote audio inheritance.'
} finally {
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($dir)) -eq $tempRoot -and [IO.Path]::GetFileName($dir) -match '^Cadrivect-audio-test-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
}
