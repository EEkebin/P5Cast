[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$relay=Join-Path $repo 'streaming\Relay.ps1'
$shell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture=Join-Path $temp ('P5Cast-relay-session-'+[guid]::NewGuid().ToString('N'))
$previous=$env:LOCALAPPDATA
$child=$null
function Relay($action) {
    $raw=& $shell -NoProfile -ExecutionPolicy Bypass -File $relay -Action $action
    $raw | ConvertFrom-Json
}
try {
    $env:LOCALAPPDATA=$fixture
    $state=Join-Path $fixture 'LumaRelay\Streaming\host\session.json'
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($state))
    [IO.File]::WriteAllText($state,'{"processId":')
    $idle=Relay Status
    if(!$idle.ok -or $idle.running){throw 'A truncated record prevented recovery.'}
    $pkg=(Get-Content (Join-Path $repo 'streaming\packages.json') -Raw | ConvertFrom-Json).sunshine
    $binary=Join-Path (Join-Path $fixture ('LumaRelay\Streaming\sunshine-'+$pkg.version)) $pkg.executable
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($binary))
    $src=Join-Path $fixture 'Sleeper.cs'
    [IO.File]::WriteAllText($src,'class Sleeper { static void Main() { System.Threading.Thread.Sleep(30000); } }')
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo "/out:$binary" $src
    if($LASTEXITCODE){throw 'Host fixture compilation failed.'}
    $child=Start-Process -FilePath $binary -WindowStyle Hidden -PassThru
    $unknown=Relay StopHost
    if($unknown.ok -or $child.HasExited){throw 'A host with unreadable ownership should be preserved and reported.'}
    $stateData=@{processId=$child.Id; startTicks='1'; address='127.0.0.1'}
    [IO.File]::WriteAllText($state,($stateData | ConvertTo-Json))
    $stale=Relay StopHost
    if(!$stale.ok -or $child.HasExited){throw 'A stale record stopped a different process.'}
    $stateData.startTicks=$child.StartTime.ToUniversalTime().Ticks.ToString()
    [IO.File]::WriteAllText($state,($stateData | ConvertTo-Json))
    $active=Relay Status
    if(!$active.ok -or !$active.running){throw 'The current host was not recognized.'}
    $stopped=Relay StopHost
    if(!$stopped.ok -or !$child.WaitForExit(3000)){throw 'The verified host did not stop.'}
    'Passed relay session recovery and process identity checks.'
} finally {
    if($child){if(!$child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
    $env:LOCALAPPDATA=$previous
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($fixture)) -eq $temp -and (Split-Path $fixture -Leaf) -match '^P5Cast-relay-session-[0-9a-f]{32}$' -and (Test-Path -LiteralPath $fixture)){
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
