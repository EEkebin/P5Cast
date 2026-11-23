# Inspect the actual WPF panels without showing a window or starting a session.
[CmdletBinding()]
param([string]$AppRoot)
$ErrorActionPreference='Stop'
if(!$AppRoot) {$AppRoot=Split-Path $PSScriptRoot}
if($PSVersionTable.PSEdition -eq 'Core' -or [threading.thread]::CurrentThread.ApartmentState -ne 'STA') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -ExecutionPolicy Bypass -File $PSCommandPath -AppRoot $AppRoot
    if($LASTEXITCODE){throw 'Settings check failed'}
    return
}
$id='P5Cast-settings-'+[guid]::NewGuid().ToString('N')
$tmp=Join-Path ([IO.Path]::GetTempPath()) $id
$oldLocal=$env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA=$tmp
    [void][IO.Directory]::CreateDirectory($tmp)
    Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml
    $asm=[Reflection.Assembly]::LoadFrom((Join-Path $AppRoot 'P5Cast.exe'))
    $asm.GetType('Cadrivect.Program').GetField('Root','Static,NonPublic').SetValue($null,$AppRoot)
    $file=[IO.File]::OpenRead((Join-Path $AppRoot 'app\Main.xaml'))
    try { $window=[Windows.Markup.XamlReader]::Load($file) } finally {$file.Dispose()}
    $type=$asm.GetType('Cadrivect.Dashboard')
    $dash=[Activator]::CreateInstance($type,[Reflection.BindingFlags]'Instance,NonPublic',$null,@($window,$false),$null)
    function Widget($name){$window.FindName($name)}
    function Click($name){(Widget $name).RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))}
    function Require($test,$message){if(!$test){throw $message}}
    function State($name,$value){$type.GetField($name,'Instance,NonPublic').SetValue($dash,$value)}
    function Call($name){$type.GetMethod($name,[Reflection.BindingFlags]'Instance,NonPublic').Invoke($dash,@())}
    function TryClose {
        $observed=[ComponentModel.CancelEventArgs]::new()
        $handler=[ComponentModel.CancelEventHandler]{param($sender,$eventArgs) $observed.Cancel=$eventArgs.Cancel}
        $window.add_Closing($handler)
        try {$window.Close()} finally {$window.remove_Closing($handler)}
        $observed.Cancel
    }
    function IsShown($name){(Widget $name).Visibility -eq 'Visible'}
    # Start the offline demo against a deliberately empty install root. The actual
    # failure handler should record the error and restore Play without a connection.
    (Widget SourceChoice).SelectedIndex=2
    $rootField=$asm.GetType('Cadrivect.Program').GetField('Root','Static,NonPublic')
    $rootField.SetValue($null,$tmp)
    try {$null=(Call StartPlayback).GetAwaiter().GetResult()} finally {$rootField.SetValue($null,$AppRoot)}
    $entries=(Widget StatusHistoryList).Items
    Require ($entries.Count -gt 0 -and $entries[0].IsError -and $entries[0].Message -eq (Widget Status).Text) 'Startup failures are visible in history'
    Require ((Widget OpenSource).IsEnabled -and [Windows.Automation.AutomationProperties]::GetItemStatus((Widget OpenSource)) -eq 'Ready') 'A failed start restores the play action'
    $show=$type.GetMethod('Show',[Reflection.BindingFlags]'Instance,NonPublic')
    for($n=1;$n -le 130;$n++) {$null=$show.Invoke($dash,@("Update $n",$false))}
    $null=$show.Invoke($dash,@('Update 130',$false))
    Require ($entries.Count -eq 120 -and $entries[0].Message -eq 'Update 130' -and $entries[119].Message -eq 'Update 11') 'History preserves a bounded, newest-first burst and suppresses consecutive duplicates'
    $null=$show.Invoke($dash,@('Update 130',$true))
    Require ($entries[0].IsError -and !$entries[1].IsError -and $entries[0].Timestamp -is [datetime]) 'History preserves severity changes and timestamps'
    # The arc counts submitted frames. Input and output can diverge while the stream is running.
    $meter=$type.GetField('meter','Instance,NonPublic').GetValue($dash)
    $dial=$meter.GetType().GetField('dial','Instance,NonPublic').GetValue($meter)
    $sample=[Collections.Generic.Dictionary[string,object]]::new()
    $sample['captured']=120; $sample['sourceFps']=90; $sample['submittedFps']=120; $sample['generatedFps']=60
    $meter.Update($sample)
    Require ([Windows.Automation.AutomationProperties]::GetHelpText($dial) -match 'Output 120 FPS\. Source 60 FPS\. Generated 60 FPS') 'Output must be the sum of the two submitted components'
    function RenderDial {
        $dial.Measure([Windows.Size]::new(260,150))
        $dial.Arrange([Windows.Rect]::new(0,0,260,141))
        $dial.UpdateLayout()
        $image=[Windows.Media.Imaging.RenderTargetBitmap]::new(260,141,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $image.Render($dial)
        return $image
    }
    function ArcPixel($image,$fraction) {
        $hub=$dial.ActualHeight-10
        $r=[Math]::Min($dial.ActualWidth/2-18,$hub-11)-18
        $x=[int][Math]::Round($dial.ActualWidth/2-$r*[Math]::Cos($fraction*[Math]::PI))
        $y=[int][Math]::Round($hub-$r*[Math]::Sin($fraction*[Math]::PI))
        $pixel=[byte[]]::new(4)
        $image.CopyPixels([Windows.Int32Rect]::new($x,$y,1,1),$pixel,4,0)
        return '{0:X2}{1:X2}{2:X2}' -f $pixel[2],$pixel[1],$pixel[0]
    }
    $image=RenderDial
    Require ((ArcPixel $image 0.125) -eq '8FE6D0' -and (ArcPixel $image 0.375) -eq 'EABC8E' -and (ArcPixel $image 0.75) -eq '2B3D43') 'Source and generated segments must share the same arc radius'
    $sample['generatedFps']=0
    $meter.Update($sample)
    Require ((ArcPixel (RenderDial) 0.375) -eq '8FE6D0') 'Disabling generation must produce one source-colored segment'
    $sample['captured']=0
    $meter.Update($sample)
    Require ((ArcPixel (RenderDial) 0.125) -eq '2B3D43') 'Waiting for video must clear the gauge'
    (Widget SourceChoice).SelectedIndex=0
    Click StreamGroup
    Require ((Widget DirectStreamSize).SelectedItem -eq '1080p') 'Existing settings default to a 1080p source'
    Require (((Widget DirectStreamSize).Items -join ',') -eq '1080p,720p,540p,360p') 'PS5 stream presets'
    (Widget DirectStreamSize).SelectedItem='720p'
    Require ((IsShown StreamSettings) -and !(IsShown ConsoleSettings)) 'Direct stream group'
    Require ((Widget DirectBitrate).SelectedItem -eq '100 Mbps') 'Maximum Direct request'
    Click DiscordGroup
    Require ((IsShown DiscordSettings) -and (Widget SharePresence).IsChecked -and (Widget ShareProject).IsChecked) 'Discord presence and project button default on'
    Require (!$type.GetField('discord','Instance,NonPublic').GetValue($dash)) 'No Discord client before the window opens'
    (Widget SharePresence).IsChecked=$false; (Widget ShareProject).IsChecked=$false
    Require ((Widget VideoRenderer).SelectedItem -eq 'Auto') 'Existing preferences use Auto'
    (Widget HdrOutput).SelectedIndex=2
    foreach($api in 'Direct3D 12','Vulkan'){
        (Widget VideoRenderer).SelectedItem=$api
        Require ((Widget HdrOutput).SelectedItem -eq 'HDR10' -and (Widget HdrOutput).IsEnabled) "$api allows explicit HDR10"
    }
    (Widget VideoRenderer).SelectedItem='OpenGL'
    Require ((Widget HdrOutput).SelectedItem -eq 'SDR' -and !(Widget HdrOutput).IsEnabled) 'OpenGL requests SDR'
    $quality=$meter.GetType().GetProperty('DlssQuality',[Reflection.BindingFlags]'Instance,NonPublic')
    Require ($quality.GetValue($meter) -eq 'auto') 'DLSS quality defaults to Auto'
    $quality.SetValue($meter,'balanced')
    $orientation=$meter.GetType().GetProperty('WipeHorizontal',[Reflection.BindingFlags]'Instance,NonPublic')
    Require (!$orientation.GetValue($meter)) 'Existing preferences open with a vertical divider'
    $orientation.SetValue($meter,$true)
    Call SaveSettings
    $secondFile=[IO.File]::OpenRead((Join-Path $AppRoot 'app\Main.xaml'))
    try {$second=[Windows.Markup.XamlReader]::Load($secondFile)} finally {$secondFile.Dispose()}
    $reopened=[Activator]::CreateInstance($type,[Reflection.BindingFlags]'Instance,NonPublic',$null,@($second,$false),$null)
    $restoredMeter=$type.GetField('meter','Instance,NonPublic').GetValue($reopened)
    Require ($orientation.GetValue($restoredMeter)) 'Horizontal divider survives reopening'
    Require ($quality.GetValue($restoredMeter) -eq 'balanced') 'DLSS quality survives reopening'
    $quality.SetValue($restoredMeter,'invalid')
    Require ($quality.GetValue($restoredMeter) -eq 'auto') 'Unknown DLSS quality reverts to Auto'
    Require (!$second.FindName('SharePresence').IsChecked -and !$second.FindName('ShareProject').IsChecked) 'Discord opt-outs survive reopening'
    Require ($second.FindName('VideoRenderer').SelectedItem -eq 'OpenGL' -and !$second.FindName('HdrOutput').IsEnabled) 'Renderer choice survives reopening'
    Require ($second.FindName('DirectStreamSize').SelectedItem -eq '720p') 'Saved 720p source survives reopening'
    $second.FindName('VideoRenderer').SelectedItem='Vulkan'
    Require ($second.FindName('HdrOutput').SelectedItem -eq 'HDR10' -and $second.FindName('HdrOutput').IsEnabled) 'Switching back restores HDR preference'
    State preparing $true; Call UpdateTransport
    Require (!(Widget VideoRenderer).IsEnabled) 'Connecting locks the renderer'
    State preparing $false; Call UpdateTransport
    Require ((Widget VideoRenderer).IsEnabled) 'Stopping unlocks the renderer'
    Click DisplayGroup
    Require ((IsShown DisplaySettings) -and !(IsShown StreamSettings)) 'Display group'
    Click HostNav; Click DevicesGroup
    Require ((IsShown DevicesSettings) -and !(IsShown SharingSettings)) 'Host device group'
    Click ClientNav; Click QualityGroup
    Require ((IsShown QualitySettings) -and (Widget NetworkBitrate).SelectedItem -eq '500 Mbps') 'PC relay quality group'
    Click PlayNav; Click ConsoleGroup
    $row=[Activator]::CreateInstance($asm.GetType('Cadrivect.DirectConsole'),$true)
    $row.Name='Fixture'; $row.Mac='000000000001'; $row.Address='fixture.invalid'
    (Widget ConsolePicker).ItemsSource=@($row); (Widget ConsolePicker).SelectedIndex=0
    State playingMac $row.Mac; State firstFrame $true; State directCore ([Diagnostics.Process]::GetCurrentProcess())
    (Widget ShowSettings).IsChecked=$false
    Require (!(IsShown SettingsDrawer)) 'The settings drawer can be closed before stop'

    $null=(Call UserStop).GetAwaiter().GetResult()
    Require ((IsShown StopPanel) -and (IsShown SettingsDrawer) -and (Widget ShowSettings).IsChecked -and (Widget RestOnStop).IsChecked -and !(Widget RememberStop).IsChecked) 'Stop reopens the drawer with rest selected and remember unchecked'
    (Widget RestOnStop).IsChecked=$false
    Require ((TryClose) -and !(Widget RestOnStop).IsChecked -and (Widget ConfirmStop).Content -eq 'Disconnect and close') 'Close keeps an unconfirmed leave-awake choice'
    Click CancelStop
    Require (!(IsShown StopPanel) -and (IsShown PlayPage)) 'Keep playing'
    Require ((TryClose) -and (IsShown StopPanel) -and (Widget RestOnStop).IsChecked -and !(Widget RememberStop).IsChecked) 'Closing an active Direct session offers rest by default'
    Click CancelStop
    $null=(Call UserStop).GetAwaiter().GetResult()
    Require ((Widget ConfirmStop).Content -eq 'Stop session') 'Canceling close leaves subsequent Stop actions in the dashboard'
    (Widget RememberStop).IsChecked=$true; Click ConfirmStop
    Require ((IsShown StopPanel) -and (Widget Status).Text -like 'Rest mode could not be confirmed*' -and (Widget StopBehavior).SelectedIndex -eq 0) 'Unacknowledged fixture rest stays cancellable'
    (Widget RestOnStop).IsChecked=$false; (Widget RememberStop).IsChecked=$true; Click ConfirmStop
    Require ((Widget StopBehavior).SelectedIndex -eq 1 -and !(IsShown StopPanel)) 'Remember leave-on choice'
    (Widget StopBehavior).SelectedIndex=2
    Require ((TryClose) -and (IsShown StopPanel) -and (Widget Status).Text -like 'Rest mode could not be confirmed*' -and (Widget ConfirmStop).Content -eq 'Disconnect and close') 'Closing with saved rest sends the request and allows recovery on failure'
    Click CancelStop
    $saved=Get-Content (Join-Path $tmp 'LumaRelay\dashboard.json') -Raw | ConvertFrom-Json
    Require ($saved.stopChoices.($row.Mac) -eq 'rest' -and $saved.directMbps -eq 100) 'Saved console preference and quality'
    (Widget StopBehavior).SelectedIndex=0
    $saved=Get-Content (Join-Path $tmp 'LumaRelay\dashboard.json') -Raw | ConvertFrom-Json
    Require (!$saved.stopChoices.($row.Mac)) 'Reset to ask every time'
    (Widget StopBehavior).SelectedIndex=1
    Require ((TryClose) -and !(IsShown StopPanel) -and $type.GetField('closing','Instance,NonPublic').GetValue($dash)) 'A saved leave-awake rule closes without a rest request or prompt'
    'Passed startup failure recovery, bounded status history, grouped controls, bitrate defaults, Stop and close prompts, and per-console preferences. No console session opened.'
} finally {
    $env:LOCALAPPDATA=$oldLocal
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($tmp)) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and (Split-Path $tmp -Leaf) -eq $id -and (Test-Path -LiteralPath $tmp)) {
        Remove-Item -LiteralPath $tmp -Recurse -Force
    }
}
