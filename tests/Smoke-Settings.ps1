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
    $neuralPanel=$meter.GetType().GetField('neuralSetup','Instance,NonPublic').GetValue($meter)
    $neuralButton=$meter.GetType().GetField('neuralDownload','Instance,NonPublic').GetValue($meter)
    $caps=[Collections.Generic.Dictionary[string,object]]::new()
    $meter.SetCapabilities($caps)
    Require ($neuralPanel.Visibility -eq 'Collapsed') 'A missing capability report must not offer the optional download'
    $caps['dlssNeuralRuntimeDownloadAvailable']=$false; $caps['adapter']='NVIDIA GeForce RTX 4090'
    $meter.SetCapabilities($caps)
    Require ($neuralPanel.Visibility -eq 'Collapsed') 'An ineligible adapter must not offer the runtime'
    $caps['dlssNeuralRuntimeDownloadAvailable']=$true
    $meter.SetCapabilities($caps)
    Require ($neuralPanel.Visibility -eq 'Visible' -and $neuralButton.IsEnabled) 'An affirmative runtime-missing report offers optional setup'
    $busyMethod=$meter.GetType().GetMethod('SetNeuralSetupBusy',[Reflection.BindingFlags]'Instance,NonPublic')
    $null=$busyMethod.Invoke($meter,@($true))
    Require (!$neuralButton.IsEnabled) 'An active runtime download cannot be started twice'
    $caps['dlssNeuralRuntimeDownloadAvailable']=$false; $caps['dlss-nr']=$true
    $meter.SetCapabilities($caps)
    $null=$busyMethod.Invoke($meter,@($false))
    Require ($neuralPanel.Visibility -eq 'Collapsed') 'Completed setup removes the download action'
    $pictureButtons=$meter.GetType().GetField('modes','Instance,NonPublic').GetValue($meter)
    Require (!$pictureButtons[8].IsEnabled) 'NIS waits for the selected graphics device probe'
    $caps['adapter']='AMD test adapter'; $caps['nis']=$true
    $caps['fsr-temporal']=$true; $caps['fsrVersion']='4.1.2'
    $meter.SetCapabilities($caps)
    Require ($pictureButtons[8].IsEnabled -and $pictureButtons[8].ToolTip -like 'NVIDIA Image Scaling*') 'NIS eligibility follows shader capability on any GPU vendor'
    Require ($pictureButtons[2].Content -like 'FSR 1*Spatial' -and $pictureButtons[6].Content -like 'FSR 4.1.2*Temporal') 'Spatial FSR is separate from the detected temporal provider'
    $meter.SetCapabilities($null)
    Require ($pictureButtons[6].Content -eq 'FSR Temporal') 'Switching backends clears a stale temporal version'
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
    $scale=$meter.GetType().GetField('dialScale','Instance,NonPublic').GetValue($meter)
    $scaleInput=$meter.GetType().GetField('dialScaleInput','Instance,NonPublic').GetValue($meter)
    Require ($scale.Minimum -eq 120 -and $scale.Maximum -eq 1000 -and $scale.Value -eq 240 -and $scaleInput.Text -eq '240') 'An existing profile without a dial preference starts at 240 FPS'
    $scaleInput.Text='1000'
    $highScale=RenderDial
    Require ($scale.Value -eq 1000 -and (ArcPixel $highScale 0.03) -eq '8FE6D0' -and (ArcPixel $highScale 0.09) -eq 'EABC8E' -and (ArcPixel $highScale 0.3) -eq '2B3D43') 'Typed maximum rescales the same measured data immediately'
    $scale.Value=120
    $sample['submittedFps']=400;$sample['generatedFps']=100;$sample['targetPresentedFps']=480
    $meter.Update($sample)
    $full=RenderDial
    Require ($scaleInput.Text -eq '120' -and (ArcPixel $full 0.60) -eq '8FE6D0' -and (ArcPixel $full 0.90) -eq 'EABC8E' -and
        [Windows.Automation.AutomationProperties]::GetHelpText($dial) -match 'Output 400 FPS\. Source 300 FPS\. Generated 100 FPS\. Target 480 FPS\. Dial maximum 120 FPS') 'Saturated dial preserves the measured 3:1 proportions and reports actual output/target'
    $hubY=$dial.ActualHeight-10
    $radius=[Math]::Min($dial.ActualWidth/2-18,$hubY-11)
    $marker=[byte[]]::new(4)
    $targetPixelCount=0
    foreach($offset in 8..12){foreach($row in -1..2) {
        $full.CopyPixels([Windows.Int32Rect]::new([int]($dial.ActualWidth/2+$radius-$offset),[int]$hubY+$row,1,1),$marker,4,0)
        if(('{0:X2}{1:X2}{2:X2}' -f $marker[2],$marker[1],$marker[0]) -eq 'E8F0F1'){$targetPixelCount++}
    }}
    Require ($targetPixelCount -ge 2) 'Above-scale target stays at the rightmost marker position'
    $scaleInput.Text='241'
    Require ($scale.Value -eq 241) 'The dial accepts any whole-number maximum in range'
    $scale.Value=240; $sample['submittedFps']=120; $sample['generatedFps']=60
    $null=$sample.Remove('targetPresentedFps')
    $meter.Update($sample)
    $null=$show.Invoke($dash,@('Theme preview',$false))
    (Widget ColorTheme).SelectedItem='Daylight'
    Require ($window.Background.Color.ToString() -eq '#FFF1F4F8' -and (Widget VideoHost).Background.Color.ToString() -eq '#FF000000') 'Daylight changes window chrome and preserves black video'
    Require ((ArcPixel (RenderDial) 0.125) -eq '145E87' -and (ArcPixel (RenderDial) 0.375) -eq '864588') 'Changing themes repaints both FPS segments live'
    (Widget ColorTheme).SelectedItem='Custom JSON'
    $themeFile=Join-Path $tmp 'P5Cast\themes\custom.json'
    Require ((Get-Content -LiteralPath $themeFile -Raw|ConvertFrom-Json).base -eq 'daylight') 'First edit starts from the displayed palette'
    '{"base":"ember","Accent":"#02BAD1","Generated":"#CC2277","Source":"#00EE88","Scrim":"#ffffff"}' | Set-Content -LiteralPath $themeFile
    Click ReloadTheme
    Require ((Widget ThemeStatus).Text -like '*Unknown theme color*' -and (ArcPixel (RenderDial) 0.125) -eq '145E87') 'An unknown color keeps the complete last working theme'
    '{"base":"ember","Accent":"#02BAD1","Generated":"#CC2277","Source":"#00EE88"}' | Set-Content -LiteralPath $themeFile
    Click ReloadTheme
    Require ((Widget Indicator).Fill.Color.ToString() -eq '#FF02BAD1' -and (Widget OpenSource).Background.Color.ToString() -eq '#FF02BAD1') 'Custom accent reaches status and play controls'
    Require ((ArcPixel (RenderDial) 0.125) -eq '00EE88' -and (ArcPixel (RenderDial) 0.375) -eq 'CC2277') 'Custom dial colors reload in place'
    Require ((Widget SetupOverlay).Background.Color.ToString() -eq '#EB211A1B' -and (Widget StopPanel).Background.Color.ToString() -eq '#EB211A1B') 'Wizard and stop modal use the current scrim'
    Require ((Widget VolumeReadout).Foreground.Color.ToString() -eq '#FF02BAD1') 'Volume popover resolves window theme brushes'
    $badTheme='{"Accent":"ultraviolet"}'
    Set-Content -LiteralPath $themeFile -Value $badTheme
    Click ReloadTheme
    Require ((Widget ThemeStatus).Text -like '*#8FE6D0*' -and (Widget Indicator).Fill.Color.ToString() -eq '#FF02BAD1') 'An invalid color displays a repair message without partially switching'
    Set-Content -LiteralPath $themeFile -Value '{"base":"ember","Source":"#00EE88"}'
    Click ReloadTheme
    $scale.Value=355
    $null=$type.GetMethod('SaveSettings',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($dash,@())
    $preferences=Get-Content (Join-Path $tmp 'LumaRelay\dashboard.json') -Raw|ConvertFrom-Json
    Require ($preferences.theme -eq 'custom') 'Custom selection persists with dashboard preferences'
    function ReopenTheme {
        $inputXaml=[IO.File]::OpenRead((Join-Path $AppRoot 'app\Main.xaml'))
        try {$testWindow=[Windows.Markup.XamlReader]::Load($inputXaml)}finally{$inputXaml.Dispose()}
        $null=[Activator]::CreateInstance($type,[Reflection.BindingFlags]'Instance,NonPublic',$null,@($testWindow,$false),$null)
        return $testWindow
    }
    $themeWindow=ReopenTheme
    $restoredFpsMeter=$themeWindow.FindName('MeterHost').Child
    Require ($preferences.fpsDialMaximum -eq 355 -and $restoredFpsMeter.GetType().GetField('dialScale','Instance,NonPublic').GetValue($restoredFpsMeter).Value -eq 355) 'Dial maximum survives closing and reopening the app'
    $scale.Value=240
    Require ($themeWindow.FindName('ColorTheme').SelectedItem -eq 'Custom JSON' -and $themeWindow.Background.Color.ToString() -eq '#FF211A1B') 'Reopening loads the saved custom file independently'
    Set-Content -LiteralPath $themeFile -Value '{"Accent":'
    $themeWindow=ReopenTheme
    Require ($themeWindow.FindName('ColorTheme').SelectedItem -eq 'Custom JSON' -and $themeWindow.Background.Color.ToString() -eq '#FF10171C' -and
        $themeWindow.FindName('ThemeStatus').Text -like '*custom.json*' -and (Get-Content -LiteralPath $themeFile) -ceq '{"Accent":') 'An invalid saved custom file remains editable and the app opens with a readable palette'
    (Widget ColorTheme).SelectedItem='Mint'
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
    $model=$meter.GetType().GetProperty('DlssModelHint',[Reflection.BindingFlags]'Instance,NonPublic')
    Require ($model.GetValue($meter) -eq 'auto') 'Driver default is the initial DLSS model preference'
    $model.SetValue($meter,'m')
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
    Require ($model.GetValue($restoredMeter) -eq 'm') 'DLSS model preference survives reopening'
    $model.SetValue($restoredMeter,'invalid')
    Require ($model.GetValue($restoredMeter) -eq 'auto') 'Unknown DLSS model uses the driver default'
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
    $sourcePicker=Widget SourceChoice
    $previousSource=$sourcePicker.SelectedIndex
    $sectionPicker=Widget PlaySectionPicker
    try {
        foreach($sourceIndex in @(0,1,0)) {
            $sourcePicker.SelectedIndex=$sourceIndex
            $expected=if($sourceIndex -eq 0){'Console,Stream,Display,Interface,Discord'}else{'Display,Interface,Discord'}
            $actual=($sectionPicker.Items | ForEach-Object {$_.Tag}) -join ','
            Require ($actual -eq $expected) 'Compact settings follow Direct and Sony source availability'
            $sectionPicker.SelectedIndex=$sectionPicker.Items.Count-1
            Require ((IsShown DiscordSettings) -and !(IsShown ConsoleSettings)) 'Compact selection opens the corresponding settings group'
            Click DisplayGroup
            Require ($sectionPicker.SelectedItem.Tag -eq 'Display') 'Rail selection synchronizes the compact selector'
        }
    } finally {$sourcePicker.SelectedIndex=$previousSource}
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
    Require ((IsShown StopPanel) -and !(IsShown SettingsDrawer) -and !(Widget ShowSettings).IsChecked -and (Widget RestOnStop).IsChecked -and !(Widget RememberStop).IsChecked) 'Stop opens in its own overlay and preserves the closed drawer, with rest selected and remember unchecked'
    (Widget RestOnStop).IsChecked=$false
    Require ((TryClose) -and !(Widget RestOnStop).IsChecked -and (Widget ConfirmStop).Content -eq 'Disconnect and close') 'Close keeps an unconfirmed leave-awake choice'
    Click CancelStop
    Require (!(IsShown StopPanel) -and (IsShown PlayPage)) 'Keep playing'
    Require ((TryClose) -and (IsShown StopPanel) -and (Widget RestOnStop).IsChecked -and !(Widget RememberStop).IsChecked) 'Closing an active Direct session offers rest by default'
    Click CancelStop
    $null=(Call UserStop).GetAwaiter().GetResult()
    Require ((Widget ConfirmStop).Content -eq 'Stop session') 'Canceling close leaves subsequent Stop actions in the dashboard'
    (Widget RememberStop).IsChecked=$true; Click ConfirmStop
    Require ((IsShown StopPanel) -and (Widget StopMessage).Text -like 'Could not send the rest request*' -and (Widget Status).Text -eq (Widget StopMessage).Text -and (Widget RememberStop).IsChecked -and (Widget StopBehavior).SelectedIndex -eq 0) 'Unacknowledged rest keeps its choices and exposes the error inside the modal'
    (Widget RestOnStop).IsChecked=$false; (Widget RememberStop).IsChecked=$true; Click ConfirmStop
    Require ((Widget StopBehavior).SelectedIndex -eq 1 -and !(IsShown StopPanel)) 'Remember leave-on choice'
    (Widget StopBehavior).SelectedIndex=2
    Require ((TryClose) -and (IsShown StopPanel) -and (Widget Status).Text -like 'Could not send the rest request*' -and (Widget ConfirmStop).Content -eq 'Disconnect and close') 'Closing with saved rest sends the request and allows recovery on failure'
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
