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
    function IsShown($name){(Widget $name).Visibility -eq 'Visible'}
    (Widget SourceChoice).SelectedIndex=0
    Click StreamGroup
    Require ((IsShown StreamSettings) -and !(IsShown ConsoleSettings)) 'Direct stream group'
    Require ((Widget DirectBitrate).SelectedItem -eq '100 Mbps') 'Maximum Direct request'
    Click DiscordGroup
    Require ((IsShown DiscordSettings) -and !(Widget SharePresence).IsChecked -and !(Widget ShareProject).IsChecked) 'Discord privacy defaults'
    Require (!$type.GetField('discord','Instance,NonPublic').GetValue($dash)) 'No Discord client while idle'
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
    $null=(Call UserStop).GetAwaiter().GetResult()
    Require ((IsShown StopPanel) -and !(Widget RestOnStop).IsChecked -and !(Widget RememberStop).IsChecked) 'Safe initial stop prompt'
    Click CancelStop
    Require (!(IsShown StopPanel) -and (IsShown PlayPage)) 'Keep playing'
    $null=(Call UserStop).GetAwaiter().GetResult()
    (Widget RestOnStop).IsChecked=$true; (Widget RememberStop).IsChecked=$true; Click ConfirmStop
    Require ((IsShown StopPanel) -and (Widget Status).Text -like 'Rest mode could not be confirmed*' -and (Widget StopBehavior).SelectedIndex -eq 0) 'Unacknowledged fixture rest stays cancellable'
    (Widget RememberStop).IsChecked=$true; Click ConfirmStop
    Require ((Widget StopBehavior).SelectedIndex -eq 1 -and !(IsShown StopPanel)) 'Remember leave-on choice'
    (Widget StopBehavior).SelectedIndex=2
    $saved=Get-Content (Join-Path $tmp 'LumaRelay\dashboard.json') -Raw | ConvertFrom-Json
    Require ($saved.stopChoices.($row.Mac) -eq 'rest' -and $saved.directMbps -eq 100) 'Saved console preference and quality'
    (Widget StopBehavior).SelectedIndex=0
    $saved=Get-Content (Join-Path $tmp 'LumaRelay\dashboard.json') -Raw | ConvertFrom-Json
    Require (!$saved.stopChoices.($row.Mac)) 'Reset to ask every time'
    'Passed grouped controls, bitrate defaults, Stop prompt, cancel and per-console preference persistence. No console session opened.'
} finally {
    $env:LOCALAPPDATA=$oldLocal
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($tmp)) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and (Split-Path $tmp -Leaf) -eq $id -and (Test-Path -LiteralPath $tmp)) {
        Remove-Item -LiteralPath $tmp -Recurse -Force
    }
}
