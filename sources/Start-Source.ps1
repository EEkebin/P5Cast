[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Sony','Direct','Demo')][string]$Source,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SourceTools.ps1')
try {
    if ($Source -eq 'Sony') {
        $path = Find-SonyRemotePlay
        if (-not $path) { throw 'Install PS Remote Play from PlayStation, sign in, and pair your PS5.' }
    } else {
        $path = Install-SourceTool $Source
    }
    if ($PrepareOnly) {
        [pscustomobject]@{source=$Source; executable=$path} | ConvertTo-Json -Compress
        return
    }
    $window = Find-SourceWindow $path
    if (-not $window) {
        $process = Start-Process -FilePath $path -WorkingDirectory (Split-Path $path) -PassThru
        $timeout = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 200
            $window = Find-SourceWindow $path
            if ($process.HasExited -and $process.ExitCode -ne 0 -and -not $window) {
                throw "$Source exited during startup (code $($process.ExitCode))."
            }
        } until ($window -or $timeout.Elapsed.TotalSeconds -gt 30)
        if (-not $window) { throw "$Source did not open a capture window within 30 seconds." }
    }
    if (-not ('LumaRelay.SourceFocus' -as [type])) {
        Add-Type -Namespace LumaRelay -Name SourceFocus -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsIconic(System.IntPtr window);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindowAsync(System.IntPtr window, int command);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr window);
'@
    }
    if ([LumaRelay.SourceFocus]::IsIconic($window.MainWindowHandle)) {
        [void][LumaRelay.SourceFocus]::ShowWindowAsync($window.MainWindowHandle, 9)
    }
    [void][LumaRelay.SourceFocus]::SetForegroundWindow($window.MainWindowHandle)
    [pscustomobject]@{
        source = $Source
        processId = $window.Id
        windowHandle = $window.MainWindowHandle.ToInt64()
        windowTitle = $window.MainWindowTitle
        executable = $path
    } | ConvertTo-Json -Compress
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
