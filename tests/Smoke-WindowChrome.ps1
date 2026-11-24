# After Build.ps1. The WPF test only opens on a disposable, inactive Windows desktop.
[CmdletBinding()]
param([string]$AppRoot,[switch]$CheckRenderers,[switch]$CheckHdr,[switch]$CheckScaling,[switch]$CheckDrawers,[string]$Screenshot,[switch]$CheckWipeInput,[switch]$CheckTransport,[switch]$CheckSetup,[switch]$CheckStop,[switch]$CheckPopout)
$ErrorActionPreference='Stop'
if(!$AppRoot){$AppRoot=Split-Path $PSScriptRoot}
$AppRoot=(Resolve-Path -LiteralPath $AppRoot).Path
if(!(Test-Path -LiteralPath (Join-Path $AppRoot 'P5Cast.exe'))){throw 'Build P5Cast before running this test.'}
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$id='P5Cast-Chrome-'+[Guid]::NewGuid().ToString('N')
$stage=Join-Path $tempRoot $id
[void][IO.Directory]::CreateDirectory($stage)
try {
    $fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $manifest=Join-Path (Split-Path $PSScriptRoot) 'app\app.manifest'
    $refs=@('PresentationFramework','PresentationCore','WindowsBase','UIAutomationTypes','UIAutomationProvider') | ForEach-Object {"/r:$fx\WPF\$_.dll"}
    $probe=Join-Path $stage 'WindowChromeProbe.exe'
    & "$fx\csc.exe" /nologo /target:exe /optimize+ /platform:x64 /r:System.Xaml.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/win32manifest:$manifest" @refs "/out:$probe" "$PSScriptRoot\WindowChromeProbe.cs" "$PSScriptRoot\DrawerProbe.cs" "$PSScriptRoot\SettingsControlsProbe.cs" "$PSScriptRoot\WipeInputProbe.cs" "$PSScriptRoot\..\engine\WipeLayout.cs" "$PSScriptRoot\TransportProbe.cs" "$PSScriptRoot\VolumeControlsProbe.cs" "$PSScriptRoot\SetupWizardProbe.cs" "$PSScriptRoot\StopModalProbe.cs" "$PSScriptRoot\PopoutProbe.cs"
    if($LASTEXITCODE){throw 'Window chrome probe compilation failed.'}
    $prior=$env:P5CAST_PROBE_RENDERERS
    $priorScale=$env:P5CAST_PROBE_SCALING
    $priorDrawer=$env:P5CAST_PROBE_DRAWERS
    $priorShot=$env:P5CAST_SCREENSHOT
    $priorTransport=$env:P5CAST_PROBE_TRANSPORT
    $priorWipe=$env:P5CAST_PROBE_WIPE_INPUT
    $priorSetup=$env:P5CAST_PROBE_SETUP
    $priorStop=$env:P5CAST_PROBE_STOP
    $priorPopout=$env:P5CAST_PROBE_POPOUT
    try {
        $env:P5CAST_SCREENSHOT=if($Screenshot){[IO.Path]::GetFullPath($Screenshot)}else{$null}
        # Setup requires a fresh profile; the Stop fixture requires a configured demo.
        $cases=@()
        if($CheckSetup){$cases+='setup'}
        if($CheckStop){$cases+='stop'}
        if(!$cases.Count -or $CheckTransport -or $CheckDrawers -or $CheckScaling -or $CheckRenderers -or $CheckHdr -or $CheckWipeInput -or $CheckPopout -or $Screenshot){$cases+='window'}
        foreach($case in $cases){
            $caseDir=Join-Path $stage $case
            [void][IO.Directory]::CreateDirectory($caseDir)
            $env:P5CAST_PROBE_SETUP=if($case -eq 'setup'){'1'}else{'0'}
            $env:P5CAST_PROBE_STOP=if($case -eq 'stop'){'1'}else{'0'}
            $window=$case -eq 'window'
            $env:P5CAST_PROBE_POPOUT=if($window -and $CheckPopout){'1'}else{'0'}
            $env:P5CAST_PROBE_TRANSPORT=if($window -and $CheckTransport){'1'}else{'0'}
            $env:P5CAST_PROBE_DRAWERS=if($window -and $CheckDrawers){'1'}else{'0'}
            $env:P5CAST_PROBE_SCALING=if($window -and $CheckScaling){'1'}else{'0'}
            $env:P5CAST_PROBE_WIPE_INPUT=if($window -and $CheckWipeInput){'1'}else{'0'}
            $env:P5CAST_PROBE_RENDERERS=if($window -and $CheckHdr){'hdr'}elseif($window -and ($CheckRenderers -or $CheckWipeInput)){'1'}else{'0'}
            & $probe $AppRoot $caseDir ($id+'-'+$case)
            if($LASTEXITCODE){throw "Window chrome regression failed in the $case fixture."}
        }
    } finally {
        $env:P5CAST_PROBE_RENDERERS=$prior; $env:P5CAST_PROBE_SCALING=$priorScale
        $env:P5CAST_PROBE_DRAWERS=$priorDrawer; $env:P5CAST_SCREENSHOT=$priorShot
        $env:P5CAST_PROBE_TRANSPORT=$priorTransport
        $env:P5CAST_PROBE_WIPE_INPUT=$priorWipe
        $env:P5CAST_PROBE_SETUP=$priorSetup
        $env:P5CAST_PROBE_STOP=$priorStop
        $env:P5CAST_PROBE_POPOUT=$priorPopout
    }
} finally {
    $target=[IO.Path]::GetFullPath($stage)
    if([IO.Path]::GetDirectoryName($target) -eq $tempRoot -and [IO.Path]::GetFileName($target) -eq $id){
        Remove-Item -LiteralPath $target -Force -Recurse
    }
}
