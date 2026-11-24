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
    $conflict=(& $shell -NoProfile -ExecutionPolicy Bypass -File $relay -Action StartHost -Address 127.0.0.1 | ConvertFrom-Json)
    if($conflict.ok -or $conflict.message -notmatch 'no matching session' -or (Test-Path (Join-Path (Split-Path $state) 'credentials.json'))){throw 'A second host could overwrite live credentials.'}
    $stateData.startTicks=$child.StartTime.ToUniversalTime().Ticks.ToString()
    [IO.File]::WriteAllText($state,($stateData | ConvertTo-Json))
    $active=Relay Status
    if(!$active.ok -or !$active.running){throw 'The current host was not recognized.'}
    $stopped=Relay StopHost
    if(!$stopped.ok -or !$child.WaitForExit(3000)){throw 'The verified host did not stop.'}
    # Interrupt the launcher while the fake host has no management endpoint.
    [IO.File]::Delete($state)
    $startupLog=Join-Path $fixture 'startup.json'
    $launcher=Start-Process -FilePath $shell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$relay+'"'),'-Action','StartHost','-Address','127.0.0.1') -WindowStyle Hidden -PassThru -RedirectStandardOutput $startupLog
    try {
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while(!(Test-Path -LiteralPath $state) -and !$launcher.HasExited -and $clock.ElapsedMilliseconds -lt 8000){Start-Sleep -Milliseconds 50}
        if (!(Test-Path -LiteralPath $state)) { throw "Missing startup record. $(Get-Content -LiteralPath $startupLog -Raw)" }
        $pending=Get-Content -Raw -LiteralPath $state | ConvertFrom-Json
        if(!$pending.starting){throw 'Host identity was not persisted before waiting for readiness.'}
        $launcher.Kill();$launcher.WaitForExit()
        $starting=Relay Status
        if(!$starting.running -or $starting.message -notmatch 'awaiting confirmation'){throw 'Interrupted startup was reported as ready or stopped.'}
        $recovered=Relay StopHost
        if(!$recovered.ok -or (Relay Status).running){throw 'Interrupted startup could not be stopped.'}
    } finally {
        if(!$launcher.HasExited){$launcher.Kill();$launcher.WaitForExit()}
        $launcher.Dispose()
    }
    $ast=[Management.Automation.Language.Parser]::ParseFile($relay,[ref]$null,[ref]$null)
    foreach ($name in 'Approve-Pin','Confirm-HostDisplay') {
        $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$false)
        . ([scriptblock]::Create($node.Extent.Text))
    }
    function Get-Session { @{address='127.0.0.1'} }
    function Invoke-HostApi($Session,$Route,$Body) {
        if($null -eq $Body){return @{pairings=$script:pairings}}
        $script:posts++
        return @{status=$script:pairStatus}
    }
    $Pin='1234'; $script:posts=0
    foreach ($badRequest in @($null,@{name='missing id'})) {
        $script:pairings=$badRequest
        $rejected=$false
        try {$null=Approve-Pin} catch {$rejected=$true}
        if(!$rejected -or $script:posts){throw 'A malformed pairing response reached approval.'}
    }
    $script:pairings=@{id='1';name='fixture'}; $script:pairStatus='false'; $rejected=$false
    try {$null=Approve-Pin} catch {$rejected=$true}
    if(!$rejected -or $script:posts -ne 1){throw 'Rejected PIN reported as a success.'}
    $script:pairStatus=$true
    if(!(Approve-Pin).ok){throw 'A valid PIN was rejected.'}
    $log=Join-Path $fixture 'display.log'
    if(Confirm-HostDisplay $log 'DISPLAY' 'fixture'){throw 'Missing log confirmed a display.'}
    [IO.File]::WriteAllText($log,"Currently available display devices:`n[")
    if(Confirm-HostDisplay $log 'DISPLAY' 'fixture'){throw 'Partial log confirmed a display.'}
    [IO.File]::AppendAllText($log,'{"display_name":"DISPLAY","device_id":"fixture"}' + [Environment]::NewLine + ']')
    if(!(Confirm-HostDisplay $log 'DISPLAY' 'fixture')){throw 'The completed inventory was not confirmed.'}
    'Passed relay startup interruption, process ownership, pairing response, and display readiness checks.'
} finally {
    if($child){if(!$child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
    $env:LOCALAPPDATA=$previous
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($fixture)) -eq $temp -and (Split-Path $fixture -Leaf) -match '^P5Cast-relay-session-[0-9a-f]{32}$' -and (Test-Path -LiteralPath $fixture)){
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
