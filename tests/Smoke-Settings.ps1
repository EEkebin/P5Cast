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
$oldOpenNrModel=$env:P5CAST_OPEN_NR_MODEL
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
    $traceType=$asm.GetType('Cadrivect.DirectDiagnostics',$true)
    $flags=[Reflection.BindingFlags]'Instance,NonPublic'
    $traceDir=Join-Path $tmp 'direct-stats'
    [void][IO.Directory]::CreateDirectory($traceDir)
    foreach($day in 1..4) {
        $name='direct-2000010{0}T000000000-{1}.csv' -f $day,([guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText((Join-Path $traceDir $name),'seed')
    }
    $unrelated=Join-Path $traceDir ('direct-00000000T000000000-'+('x'*32)+'.csv')
    [IO.File]::WriteAllText($unrelated,'preserve')
    $trace=$traceType.GetConstructor($flags,$null,@([string]),$null).Invoke(@([string]$traceDir))
    $sample=New-Object 'Collections.Generic.Dictionary[string,object]'
    $sample.Add('sampleId',1); $sample.Add('videoFramesLost',0); $sample.Add('address','private-fixture')
    $sample.Add('sampleUtc','2020-02-03T04:05:06.1234567Z')
    $sample.Add('mode','dlss'); $sample.Add('effectivePictureMode','original'); $sample.Add('comparisonView','original')
    $sample.Add('frameGenerationProvider','private-fixture'); $sample.Add('generatedFps',0); $sample.Add('outputWidth',1920)
    $sample.Add('hdrEnabled',$false); $sample.Add('renderer','d3d12')
    $record=$traceType.GetMethod('Record',$flags)
    $record.Invoke($trace,@($sample.psobject.BaseObject)); $record.Invoke($trace,@($sample.psobject.BaseObject))
    $sample['sampleId']=2; $sample['audioUnderruns']=3; [void]$sample.Remove('sampleUtc')
    $record.Invoke($trace,@($sample.psobject.BaseObject)); $traceType.GetMethod('Finish',$flags).Invoke($trace,$null)
    $wait=[Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 20
        $file=Get-ChildItem $traceDir -Filter '*.csv' | Where-Object {$_.Name -notmatch '^direct-(200001|00000000)'} | Select-Object -First 1
        $rows=if($file){@(Import-Csv $file.FullName)}
    } while(@($rows).Count -ne 2 -and $wait.ElapsedMilliseconds -lt 3000)
    Require (@($rows).Count -eq 2 -and $rows[0].videoFramesLost -eq '0' -and $rows[0].audioUnderruns -eq '' -and
        $rows[1].audioUnderruns -eq '3' -and !((Get-Content $file.FullName -Raw).Contains('private-fixture'))) 'Direct diagnostics retain unique numeric samples, distinguish missing counters, and omit identifiers'
    Require ($rows[0].utc -eq '2020-02-03T04:05:06.1234567Z' -and $rows[1].utc -match 'Z$') 'Preserve measurement time and support older snapshots'
    Require ($rows[0].mode -eq 'dlss' -and $rows[0].effectivePictureMode -eq 'original' -and
        $rows[0].comparisonView -eq 'original' -and $rows[0].generatedFps -eq '0' -and
        $rows[0].outputWidth -eq '1920' -and $rows[0].frameGenerationProvider -eq '' -and
        $rows[0].hdrEnabled -eq '0' -and $rows[0].renderer -eq 'd3d12') 'Record renderer decisions and omit unknown identifiers'
    Require ((Test-Path -LiteralPath $unrelated) -and @(Get-ChildItem $traceDir -Filter '*.csv').Count -eq 5) 'Rotate only the four owned trace files'
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
    Require ($pictureButtons[8].Parent -eq $pictureButtons[2].Parent) 'NIS and spatial FSR share a row'
    Require ($pictureButtons[3].Parent -eq $pictureButtons[6].Parent -and $pictureButtons[6].Parent -ne $pictureButtons[2].Parent) 'RTX Video SR and temporal FSR share a separate row'
    Require ($pictureButtons[4].Parent -eq $pictureButtons[5].Parent -and $pictureButtons[5].Parent -eq $pictureButtons[7].Parent) 'The DLSS family shares a group'
    $pictureGroup=$pictureButtons[0].GroupName
    Require ($pictureButtons.Count -eq 9 -and $pictureGroup -and @($pictureButtons | Where-Object {$_.GroupName -ne $pictureGroup}).Count -eq 0) 'Every picture method belongs to the same single-choice radio group'
    (Widget HdrOutput).SelectedItem='SDR'
    $openToggle=$meter.GetType().GetField('useOpenNr',$flags).GetValue($meter)
    $openSetup=$meter.GetType().GetField('openNrSetup',$flags).GetValue($meter)
    $openStatus=$meter.GetType().GetField('openNrStatus',$flags).GetValue($meter)
    $openDownload=$meter.GetType().GetField('openNrDownload',$flags).GetValue($meter)
    $readMode={ $type.GetField('mode',$flags).GetValue($dash) }
    Require (!$openToggle.IsChecked -and $openSetup.Visibility -eq 'Collapsed') 'The NVIDIA neural provider is the default'
    $pictureButtons[1].IsChecked=$true
    $openToggle.IsChecked=$true
    Require ((&$readMode) -eq 1 -and $openSetup.Visibility -eq 'Visible') 'Selecting an NR implementation preserves Sharpen'
    $caps['open-nrReason']='Choose a compatible model.'; $caps['openNrModelImportAvailable']=$true
    $meter.SetCapabilities($caps)
    Require (!$pictureButtons[7].IsEnabled -and $openDownload.Visibility -eq 'Visible' -and $openStatus.Text -eq $caps['open-nrReason']) 'A missing model explains eligibility and offers setup'
    $caps['dlss-nr']=$false; $caps['open-nr']=$true; $caps['openNrModelImportAvailable']=$false
    $meter.SetCapabilities($caps); $pictureButtons[7].IsChecked=$true
    Require ($pictureButtons[7].IsEnabled -and (&$readMode) -eq 9 -and @($pictureButtons|Where-Object IsChecked).Count -eq 1) 'A compatible open provider uses the single DLSS 5 NR row independently of NVIDIA eligibility'
    $openToggle.IsChecked=$false
    Require ((&$readMode) -eq 0 -and $pictureButtons[0].IsChecked -and !$pictureButtons[7].IsEnabled) 'Switching to unavailable NVIDIA NR safely restores Original'
    $caps['dlss-nr']=$true; $meter.SetCapabilities($caps); $pictureButtons[7].IsChecked=$true
    $openToggle.IsChecked=$true
    Require ((&$readMode) -eq 9 -and $pictureButtons[7].IsChecked) 'Changing provider sends the alternate mode when NR was active'
    (Widget HdrOutput).SelectedItem='HDR10'
    Require ((&$readMode) -eq 0 -and !$pictureButtons[7].IsEnabled -and $openStatus.Text -like '*SDR*') 'HDR masks OpenDLSS-NR before connection and releases the active choice'
    (Widget HdrOutput).SelectedItem='SDR'
    $pictureButtons[7].IsChecked=$true
    $openToggle.IsChecked=$false
    Require ((&$readMode) -eq 7 -and $pictureButtons[7].IsChecked) 'Returning to NVIDIA keeps NR selected'
    foreach($index in 1,2,7,0) {
        $pictureButtons[$index].IsChecked=$true
        Require ($pictureButtons[$index].IsChecked -and @($pictureButtons | Where-Object {$_.IsChecked}).Count -eq 1) 'Selecting a picture method clears the previous selection across categories'
        foreach($button in $pictureButtons) {
            [void]$button.ApplyTemplate()
            $dot=$button.Template.FindName('RadioDot',$button)
            Require ($dot -and (($dot.Visibility -eq 'Visible') -eq [bool]$button.IsChecked)) 'Exactly the active picture method displays its radio indicator'
        }
    }
    $viewButtons=$meter.GetType().GetField('views','Instance,NonPublic').GetValue($meter)
    $toggleOriginal=$meter.GetType().GetMethod('ToggleOriginal')
    $null=$toggleOriginal.Invoke($meter,@())
    Require (@($viewButtons | Where-Object {$_.IsEnabled}).Count -eq 1 -and [Windows.Automation.AutomationProperties]::GetAutomationId(($viewButtons | Where-Object {$_.IsChecked})) -eq 'ViewOriginal') 'The comparison shortcut retains Original while every effect is off'
    $fruc=$meter.GetType().GetField('frucGeneration','Instance,NonPublic').GetValue($meter)
    $amd=$meter.GetType().GetField('amdGeneration','Instance,NonPublic').GetValue($meter)
    $frucSetup=$meter.GetType().GetField('frucSetup','Instance,NonPublic').GetValue($meter)
    $provider=$meter.GetType().GetProperty('FrameGenerationProvider',[Reflection.BindingFlags]'Instance,NonPublic')
    Require ($amd.Parent -eq $fruc.Parent -and $amd.GroupName -eq $fruc.GroupName) 'AMD and NVIDIA share one mutually exclusive vendor interpolation group'
    Require (!$fruc.IsEnabled -and $frucSetup.Visibility -eq 'Collapsed') 'FRUC waits for its own GPU probe'
    $caps['frucRuntimeImportAvailable']=$true; $caps['frucFrameGenerationReason']='Import the NVIDIA runtime.'
    $meter.SetCapabilities($caps)
    Require (!$fruc.IsEnabled -and $fruc.ToolTip -eq 'Import the NVIDIA runtime.' -and $frucSetup.Visibility -eq 'Visible') 'A missing FRUC runtime explains how to import it'
    (Widget HdrOutput).SelectedItem='SDR'
    $caps['amdFrameGeneration']=$true; $caps['frucFrameGeneration']=$true; $caps['frucRuntimeImportAvailable']=$false
    $meter.SetCapabilities($caps); $amd.IsChecked=$true; $fruc.IsChecked=$true
    Require ($fruc.IsEnabled -and !$amd.IsChecked -and $provider.GetValue($meter) -eq 'fruc' -and $frucSetup.Visibility -eq 'Collapsed') 'Selecting FRUC replaces AMD with NVIDIA at 2x'
    $viewButtons[1].IsChecked=$true
    $guide=$meter.GetType().GetField('comparisonGuide','Instance,NonPublic').GetValue($meter)
    Require ($meter.ComparisonView -eq 'original' -and $guide.Visibility -eq 'Visible' -and $guide.Text -like '*incoming frame rate*') 'Original explains the bypass with only frame generation selected'
    $amd.IsChecked=$true
    Require ($meter.ComparisonView -eq 'enhanced') 'Choosing a frame generator from Original previews generated frames'
    $viewButtons[3].IsChecked=$true
    $fruc.IsChecked=$true
    Require ($meter.ComparisonView -eq 'wipe') 'Switching frame generators preserves the draggable comparison'
    $viewButtons[1].IsChecked=$true
    $saveProvider=$meter.GetType().GetMethod('SavedProvider',[Reflection.BindingFlags]'Static,NonPublic')
    Require ($saveProvider.Invoke($null,@('fruc',2)) -eq 'fruc' -and $saveProvider.Invoke($null,@('amd',2)) -eq 'amd' -and $saveProvider.Invoke($null,@('fruc',4)) -eq 'p5cast') 'FRUC and existing AMD selections round-trip at valid multipliers'
    $caps['rtxvideo']=$true
    $meter.SetCapabilities($caps); $pictureButtons[3].IsChecked=$true
    Require ($meter.ComparisonView -eq 'enhanced') 'Choosing an upscaler from Original previews the new picture'
    $viewButtons[1].IsChecked=$true
    $pictureButtons[3].RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent))
    Require ($meter.ComparisonView -eq 'enhanced') 'Clicking an already selected upscaler resumes its preview from Original'
    $viewButtons[2].IsChecked=$true
    $pictureButtons[2].IsChecked=$true; $pictureButtons[3].IsChecked=$true
    Require ($meter.ComparisonView -eq 'side-by-side') 'Switching upscalers preserves a side-by-side comparison'
    (Widget HdrOutput).SelectedItem='HDR10'
    Require ($fruc.IsEnabled -and $fruc.IsChecked -and $provider.GetValue($meter) -eq 'fruc') 'Requesting HDR10 keeps the NVIDIA FRUC provider selected'
    Require ($pictureButtons[3].IsEnabled -and $pictureButtons[3].IsChecked) 'RTX Video SR stays selected alongside FRUC when HDR10 is requested'
    (Widget HdrOutput).SelectedItem='SDR'
    Require ($fruc.IsChecked -and $pictureButtons[3].IsChecked) 'Returning to SDR preserves both selected NVIDIA effects'
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
    $openToggle.IsChecked=$true
    $modelPath=Join-Path $tmp 'local model'
    State openNrModelDirectory $modelPath
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
    Require ($preferences.useOpenNr -and $preferences.openNrModelDirectory -eq $modelPath -and
        $restoredFpsMeter.GetType().GetField('useOpenNr',$flags).GetValue($restoredFpsMeter).IsChecked -and
        $env:P5CAST_OPEN_NR_MODEL -eq $modelPath) 'The implementation choice and user model folder survive reopening independently of the active effect'
    $openToggle.IsChecked=$false
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
    $settingsFile=Join-Path $tmp 'LumaRelay\dashboard.json'
    $prior=[IO.File]::ReadAllText($settingsFile)
    $lock=[IO.File]::Open($settingsFile,'Open','Read','None')
    try {Call SaveSettings} finally {$lock.Dispose()}
    Require ([IO.File]::ReadAllText($settingsFile) -eq $prior -and !(Get-ChildItem ($settingsFile+'.*.tmp'))) 'A failed settings save preserves the previous file and removes its staging file'
    Require ((Widget Status).Text -match 'could not save') 'Report a failed settings save in the dashboard'
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
    [Threading.SynchronizationContext]::SetSynchronizationContext(
        [Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
    function WaitStopTransition {
        $timeout=[Diagnostics.Stopwatch]::StartNew()
        while((IsShown OperationOverlay) -and $timeout.ElapsedMilliseconds -lt 6000) {
            $frame=[Windows.Threading.DispatcherFrame]::new()
            [void]$window.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::Background,[Action]{$frame.Continue=$false})
            [Windows.Threading.Dispatcher]::PushFrame($frame)
            Start-Sleep -Milliseconds 10
        }
        Require (!(IsShown OperationOverlay)) 'The stop operation finished'
    }

    # Exercise the host paths with a local relay stub. Never start a real sharing host here.
    $fixture=Join-Path $tmp 'relay-fixture'
    [void][IO.Directory]::CreateDirectory((Join-Path $fixture 'streaming'))
    $log=Join-Path $fixture 'streaming\calls.txt'
    @'
param([string]$Action,[string]$Address,[string]$DisplayDevice)
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.txt') -Value $Action
if($Action -eq 'InstallHost'){Start-Sleep -Milliseconds 200}
@{ok=$true;message=$Action} | ConvertTo-Json -Compress
'@ | Set-Content -LiteralPath (Join-Path $fixture 'streaming\Relay.ps1') -Encoding UTF8
    $rootField.SetValue($null,$fixture)
    try {
        Click StartHost
        Require ((IsShown OperationOverlay) -and (Widget CancelOperation).IsEnabled) 'Share startup offers cancellation'
        Click CancelOperation; WaitStopTransition
        Click StopHost; Click CancelOperation; WaitStopTransition
        Click StartHost; WaitStopTransition
        Click StopHost; WaitStopTransition
        $calls=@(Get-Content -LiteralPath $log)
        Require (@($calls -eq 'StartHost').Count -eq 1 -and @($calls -eq 'StopHost').Count -eq 1) 'Only confirmed sharing requests reached the relay'
    } finally {$rootField.SetValue($null,$AppRoot)}

    # A startup worker ignores commands until its session has been installed.
    # Return a first-frame snapshot from a ready worker on the following UI tick.
    $stream=[IO.File]::OpenRead((Join-Path $AppRoot 'app\Main.xaml'))
    try {$startupWindow=[Windows.Markup.XamlReader]::Load($stream)} finally {$stream.Dispose()}
    $startupDash=[Activator]::CreateInstance($type,$flags,$null,@($startupWindow,$false),$null)
    $startupWindow.FindName('SourceChoice').SelectedIndex=2
    $engineType=$asm.GetType('Cadrivect.EngineSession',$true)
    $mockEngine=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($engineType)
    $startup=@{ready=$false;applied='';count=0}
    $apply=[Action[string,string]]{param($size,$color) if($startup.ready){$startup.applied="$size/$color";$startup.count++}}
    $engineType.GetField('ConfigureOutput',$flags).SetValue($mockEngine,$apply)
    $engineType.GetField('MarkSettings',$flags).SetValue($mockEngine,[Func[int]]{1})
    $startupMeter=$type.GetField('meter',$flags).GetValue($startupDash)
    $report=@{presented=1;captured=1;sampleId=1;resolution='native';hdrMode='auto';sourceKind='demo'
        mode=$type.GetField('modeIds','Static,NonPublic').GetValue($null)[$type.GetField('mode',$flags).GetValue($startupDash)]
        multiplier=$type.GetField('factor',$flags).GetValue($startupDash)
        frameGenerationProvider=$startupMeter.GetType().GetProperty('FrameGenerationProvider',$flags).GetValue($startupMeter)
        dlssModelHint=$startupMeter.GetType().GetProperty('DlssModelHint',$flags).GetValue($startupMeter)
        dlssQuality=$startupMeter.GetType().GetProperty('DlssQuality',$flags).GetValue($startupMeter)
        comparisonView=$startupMeter.ComparisonView} | ConvertTo-Json -Compress
    $engineType.GetField('Snapshot',$flags).SetValue($mockEngine,[Func[string]]{$report})
    $type.GetField('engine',$flags).SetValue($startupDash,$mockEngine)
    $type.GetField('rendering',$flags).SetValue($startupDash,[Threading.Tasks.Task]::FromResult(0))
    $startupWindow.FindName('RenderResolution').SelectedIndex=1
    $startupWindow.FindName('RenderResolution').SelectedIndex=3
    $startup.ready=$true
    $type.GetMethod('Tick',$flags).Invoke($startupDash,$null)
    $type.GetMethod('Tick',$flags).Invoke($startupDash,$null)
    Require ($startup.count -eq 1 -and $startup.applied -like '1440/*') 'The first frame applies the latest output size chosen during startup exactly once'

    # Closing during an operation must survive both cancellation and committed work.
    $stream=[IO.File]::OpenRead((Join-Path $AppRoot 'app\Main.xaml'))
    try {$pairWindow=[Windows.Markup.XamlReader]::Load($stream)} finally {$stream.Dispose()}
    $pairDash=[Activator]::CreateInstance($type,$flags,$null,@($pairWindow,$false),$null)
    $pairField=$type.GetField('pairingCancellation',$flags)
    $pairCancel=[Threading.CancellationTokenSource]::new()
    $pairField.SetValue($pairDash,$pairCancel)
    $type.GetMethod('SetPage',$flags).Invoke($pairDash,@('Host',$false))
    Require $pairCancel.IsCancellationRequested 'Leaving the pairing form cancels its active registration'
    $type.GetMethod('EndPairing',$flags).Invoke($pairDash,$null)
    $pairCancel=[Threading.CancellationTokenSource]::new()
    $pairField.SetValue($pairDash,$pairCancel)
    $pairClosed=@{value=$false}
    $pairWindow.add_Closed({$pairClosed.value=$true})
    $pairWindow.Close()
    Require ($pairCancel.IsCancellationRequested -and !$pairClosed.value) 'Closing cancels the active registration and waits for its helper'
    $type.GetMethod('EndPairing',$flags).Invoke($pairDash,$null)
    $frame=[Windows.Threading.DispatcherFrame]::new()
    [void]$window.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::Background,[Action]{$frame.Continue=$false})
    [Windows.Threading.Dispatcher]::PushFrame($frame)
    Require $pairClosed.value 'The original close completes after the registration helper exits'

    foreach($committed in @($false,$true)) {
        $stream=[IO.File]::OpenRead((Join-Path $AppRoot 'app\Main.xaml'))
        try {$otherWindow=[Windows.Markup.XamlReader]::Load($stream)} finally {$stream.Dispose()}
        $otherDash=[Activator]::CreateInstance($type,$flags,$null,@($otherWindow,$false),$null)
        $observed=@{closed=$false;cancelled=$false}
        $otherWindow.add_Closed({$observed.closed=$true})
        $cancel=[Action]{$observed.cancelled=$true}
        $operation=$type.GetMethod('BeginOperation',$flags).Invoke($otherDash,@('Checking','Checking',$cancel))
        if($committed){$type.GetMethod('CommitOperation',$flags).Invoke($otherDash,@($operation,'Finishing'))}
        $otherWindow.Close()
        Require (!$observed.closed -and ($observed.cancelled -eq !$committed)) 'Close waits for the current operation and only cancels work that is cancellable'
        $type.GetMethod('EndOperation',$flags).Invoke($otherDash,@($operation))
        $frame=[Windows.Threading.DispatcherFrame]::new()
        [void]$window.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::Background,[Action]{$frame.Continue=$false})
        [Windows.Threading.Dispatcher]::PushFrame($frame)
        Require $observed.closed 'Completing an operation honors the original window close request'
    }

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
    Require ((TryClose) -and (IsShown OperationOverlay)) 'Closing during the disconnect countdown keeps the confirmed operation pending'
    WaitStopTransition
    Require ((IsShown StopPanel) -and (Widget ConfirmStop).Content -eq 'Disconnect and close' -and (Widget StopMessage).Text -like 'Could not send the rest request*' -and (Widget Status).Text -eq (Widget StopMessage).Text -and (Widget RememberStop).IsChecked -and (Widget StopBehavior).SelectedIndex -eq 0) 'Unacknowledged rest keeps its choices, close request, and error inside the modal'
    Click CancelStop
    $null=(Call UserStop).GetAwaiter().GetResult()
    (Widget RestOnStop).IsChecked=$false; (Widget RememberStop).IsChecked=$true; Click ConfirmStop
    WaitStopTransition
    Require ((Widget StopBehavior).SelectedIndex -eq 1 -and !(IsShown StopPanel)) 'Remember leave-on choice'
    (Widget StopBehavior).SelectedIndex=2
    $pendingClose=TryClose
    WaitStopTransition
    Require ($pendingClose -and (IsShown StopPanel) -and (Widget Status).Text -like 'Could not send the rest request*' -and (Widget ConfirmStop).Content -eq 'Disconnect and close') 'Closing with saved rest sends the request and allows recovery on failure'
    Click CancelStop
    $saved=Get-Content (Join-Path $tmp 'LumaRelay\dashboard.json') -Raw | ConvertFrom-Json
    Require ($saved.stopChoices.($row.Mac) -eq 'rest' -and $saved.directMbps -eq 100) 'Saved console preference and quality'
    (Widget StopBehavior).SelectedIndex=0
    $saved=Get-Content (Join-Path $tmp 'LumaRelay\dashboard.json') -Raw | ConvertFrom-Json
    Require (!$saved.stopChoices.($row.Mac)) 'Reset to ask every time'
    (Widget StopBehavior).SelectedIndex=1
    $pendingClose=TryClose
    WaitStopTransition
    Require ($pendingClose -and !(IsShown StopPanel) -and $type.GetField('closing','Instance,NonPublic').GetValue($dash)) 'A saved leave-awake rule closes without a rest request or prompt'
    'Passed startup failure recovery, bounded status history, grouped controls, bitrate defaults, Stop and close prompts, and per-console preferences. No console session opened.'
} finally {
    $env:LOCALAPPDATA=$oldLocal
    $env:P5CAST_OPEN_NR_MODEL=$oldOpenNrModel
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($tmp)) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and (Split-Path $tmp -Leaf) -eq $id -and (Test-Path -LiteralPath $tmp)) {
        Remove-Item -LiteralPath $tmp -Recurse -Force
    }
}
