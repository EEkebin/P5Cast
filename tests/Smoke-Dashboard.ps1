# Run from a signed-in Windows desktop, after Build.ps1.
[CmdletBinding()]
param([string]$AppRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('Cadrivect.DashboardSnapshot' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace Cadrivect {
    public static class DashboardSnapshot {
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int left, top, right, bottom; }
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr mode);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        public static IntPtr PlayerWindow(int processId) {
            IntPtr cursor = IntPtr.Zero;
            while ((cursor = FindWindowEx(IntPtr.Zero,cursor,null,"Cadrivect Player")) != IntPtr.Zero) {
                uint pid;
                GetWindowThreadProcessId(cursor,out pid);
                if (pid == processId) return cursor;
            }
            return IntPtr.Zero;
        }
        public static void Save(IntPtr window, string path) {
            var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try {
                Rect r;
                if (!GetClientRect(window, out r) || r.right < 320 || r.bottom < 280)
                    throw new Exception("The dashboard client rectangle is unavailable.");
                using (var bitmap = new Bitmap(r.right, r.bottom)) {
                    using (var graphics = Graphics.FromImage(bitmap)) {
                        var dc = graphics.GetHdc();
                        try {
                            if (!PrintWindow(window, dc, 3)) throw new Exception("PrintWindow failed.");
                        } finally { graphics.ReleaseHdc(dc); }
                    }
                    int mint = 0, dark = 0;
                    for (int y = 0; y < r.bottom; y += 5)
                        for (int x = 0; x < r.right; x += 5) {
                            var pixel = bitmap.GetPixel(x, y);
                            if (pixel.R == 143 && pixel.G == 230 && pixel.B == 208) ++mint;
                            if (pixel.R == 16 && pixel.G == 23 && pixel.B == 28) ++dark;
                        }
                    if (mint < 10 || dark < 200)
                        throw new Exception("The snapshot lacks the dashboard background or active controls.");
                    bitmap.Save(path, ImageFormat.Png);
                }
            } finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }
    }
}
'@
}

function Wait-For([scriptblock]$Check, [string]$Description) {
    $timeout = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Check
        if ($value) { return $value }
        Start-Sleep -Milliseconds 120
    } until ($timeout.Elapsed.TotalSeconds -ge 25)
    throw "Timed out waiting for $Description."
}
function Get-Control([string]$Id) {
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $ui.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Click-Control([string]$Id) {
    $element = Wait-For { $control = Get-Control $Id; if ($control -and $control.Current.IsEnabled) { $control } } $Id
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Wait-Text([string]$Id, [string]$Match) {
    Wait-For {
        $element = Get-Control $Id
        if ($element -and $element.Current.Name -match $Match) { $element.Current.Name }
    } "$Id to match $Match"
}

$app = Get-Item -LiteralPath (Join-Path $AppRoot 'Cadrivect.exe')
$runId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path ([IO.Path]::GetTempPath()) "Cadrivect-smoke-$runId"
$snapshot = Join-Path ([IO.Path]::GetTempPath()) "Cadrivect-dashboard-$runId.png"
[IO.Directory]::CreateDirectory($stage) | Out-Null
$process = $null
try {
    $start = New-Object Diagnostics.ProcessStartInfo $app.FullName
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $app.DirectoryName
    $start.EnvironmentVariables['LOCALAPPDATA'] = $stage
    $process = [Diagnostics.Process]::Start($start)
    $handle = Wait-For { $process.Refresh(); if ($process.MainWindowHandle -ne 0) { $process.MainWindowHandle } } 'dashboard window'
    $ui = [Windows.Automation.AutomationElement]::FromHandle($handle)
    Click-Control 'PlayNav'
    Click-Control 'Demo'
    $null = Wait-Text 'HeroTitle' '^Arcade demo$'
    [Cadrivect.DashboardSnapshot]::Save($handle, $snapshot)
    Click-Control 'OpenSource'
    $demoStatus = Wait-Text 'Status' '^Opened Arcade demo'
    $demo = Wait-For {
        Get-Process -Name SignalRun -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -and $_.Path.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase) -and $_.MainWindowHandle -ne 0
        } | Select-Object -First 1
    } 'a playable demo window under the isolated profile'
    Click-Control 'Enhance'
    $viewStatus = Wait-Text 'Status' '^Enhanced view opened'
    $playerWindow = Wait-For {
        Get-CimInstance Win32_Process -Filter "Name='CadrivectPlayer.exe' AND ParentProcessId=$($process.Id)" |
            ForEach-Object { [Cadrivect.DashboardSnapshot]::PlayerWindow($_.ProcessId) } |
            Where-Object { $_ -ne [IntPtr]::Zero } | Select-Object -First 1
    } 'the GPU player launched by this dashboard'
    Start-Sleep -Seconds 2
    [void][Cadrivect.DashboardSnapshot]::PostMessage($playerWindow,0x0312,[IntPtr]1,[IntPtr]::Zero)
    $viewSummary = Wait-Text 'Status' '^Session ended'
    $reportFile = Get-ChildItem (Join-Path $stage 'LumaRelay\sessions') -Filter '*.json' | Select-Object -First 1
    $report = Get-Content $reportFile.FullName -Raw | ConvertFrom-Json
    if ($report.captured -lt 5 -or $report.presented -lt 5) { throw 'Demo capture or presentation did not advance.' }
    Click-Control 'HostNav'
    Click-Control 'HostStatus'
    $hostStatus = Wait-Text 'HostState' '^Host is stopped'
    Click-Control 'ClientNav'
    (Get-Control 'HostAddress').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('invalid host!')
    Click-Control 'ClientPlay'
    $clientStatus = Wait-Text 'Status' '^Enter the host PC'
    [pscustomobject]@{demo=$demoStatus; view=$viewSummary; frames=$report.presented; host=$hostStatus; client=$clientStatus; screenshot=$snapshot} | ConvertTo-Json
} finally {
    # Only processes whose executable was built in this run's random directory belong to the test.
    Get-Process -Name SignalRun -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object { [void]$_.CloseMainWindow(); if (-not $_.WaitForExit(2500)) { $_.Kill(); $_.WaitForExit() } }
    if ($process) {
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(2500)) { $process.Kill(); $process.WaitForExit() }
        }
        $process.Dispose()
    }
    $resolved = [IO.Path]::GetFullPath($stage)
    if ([IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($resolved) -eq "Cadrivect-smoke-$runId") {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
