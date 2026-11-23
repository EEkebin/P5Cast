# Run from a signed-in Windows desktop, after Build.ps1.
[CmdletBinding()]
param([string]$AppRoot)
if(!$AppRoot) {$AppRoot=Split-Path $PSScriptRoot -Parent}
if($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -AppRoot $AppRoot
    if($LASTEXITCODE) {throw "Dashboard smoke failed ($LASTEXITCODE)."}
    return
}
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
        delegate bool EnumProc(IntPtr hwnd, IntPtr param);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hwnd, EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd,System.Text.StringBuilder text,int count);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc,int x,int y);
        public static IntPtr Surface(IntPtr window) {
            IntPtr found=IntPtr.Zero;
            EnumChildWindows(window,delegate(IntPtr hwnd,IntPtr param) {
                var label=new System.Text.StringBuilder(64); GetWindowText(hwnd,label,64);
                if(label.ToString()=="P5Cast video") found=hwnd;
                return found==IntPtr.Zero;
            },IntPtr.Zero);
            return found;
        }
        public static int Windows(int id) {
            int count=0;
            EnumWindows(delegate(IntPtr hwnd,IntPtr param) {
                uint pid; GetWindowThreadProcessId(hwnd,out pid);
                if(pid==id && IsWindowVisible(hwnd)) count++;
                return true;
            },IntPtr.Zero);
            return count;
        }
        public static string Bounds(IntPtr hwnd) {
            Rect r; GetWindowRect(hwnd,out r); return string.Format("{0},{1} {2}x{3}",r.left,r.top,r.right-r.left,r.bottom-r.top);
        }
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
        [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from,IntPtr to,ref Rect rect,uint count);
        public static bool Black(IntPtr hwnd) {
            var root=GetAncestor(hwnd,2); Rect r,video;
            if(!GetClientRect(root,out r)||!GetClientRect(hwnd,out video)||video.right<100) return false;
            MapWindowPoints(hwnd,root,ref video,2);
            using(var bmp=new Bitmap(r.right,r.bottom)) using(var g=Graphics.FromImage(bmp)) {
                var dc=g.GetHdc();
                try {if(!PrintWindow(root,dc,3)) throw new Exception("Could not capture dashboard.");}
                finally {g.ReleaseHdc(dc);}
                for(int y=1;y<=3;y++) for(int x=1;x<=3;x++) {
                    var pixel=bmp.GetPixel(video.left+(video.right-video.left)*x/4,video.top+(video.bottom-video.top)*y/4);
                    if(pixel.R>3 || pixel.G>3 || pixel.B>3) return false;
                }
                return true;
            }
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

$app = Get-Item -LiteralPath (Join-Path $AppRoot 'P5Cast.exe')
$runId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path ([IO.Path]::GetTempPath()) "P5Cast-smoke-$runId"
$snapshot = Join-Path ([IO.Path]::GetTempPath()) "P5Cast-dashboard-$runId.png"
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
    if ($ui.Current.Name -ne 'P5Cast') {throw 'Unexpected dashboard title.'}
    $video=Wait-For { $v=[Cadrivect.DashboardSnapshot]::Surface($handle); if($v -ne [IntPtr]::Zero){$v} } 'permanent video surface'
    $geometry=[Cadrivect.DashboardSnapshot]::Bounds($video)
    $dial=Wait-For {Get-Control 'FrameRateDial'} 'frame-rate dial'
    $dialGeometry=$dial.Current.BoundingRectangle
    function Assert-Layout {
        if ([Cadrivect.DashboardSnapshot]::Surface($handle) -ne $video -or [Cadrivect.DashboardSnapshot]::Bounds($video) -ne $geometry) {throw 'The playback surface moved or was replaced.'}
        if ((Get-Control 'FrameRateDial').Current.BoundingRectangle -ne $dialGeometry) {throw 'The frame-rate dial moved.'}
        if ([Cadrivect.DashboardSnapshot]::Windows($process.Id) -ne 1) {throw 'The dashboard opened another visible window.'}
    }
    $null=Wait-For { [Cadrivect.DashboardSnapshot]::Black($video) } 'black idle area'
    Click-Control 'AddConsole'
    Assert-Layout
    Click-Control 'PairConsole'
    $null=Wait-Text 'Status' 'address|Account ID'
    Click-Control 'CancelPair'
    $choice=Get-Control 'SourceChoice'
    $choice.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $condition=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,'Offline demo')
    $choice.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $choice.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Click-Control 'OpenSource'
    $demoStatus=Wait-Text 'Status' '^Offline demo$'
    $null=Wait-For { (Get-Control 'FrameRateDial').Current.HelpText -match 'Presented\s+[1-9]' } 'real frame rates on the dial'
    $factorCondition=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,'2×')
    $factor=$ui.FindFirst([Windows.Automation.TreeScope]::Descendants,$factorCondition)
    $factor.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $null=Wait-For { (Get-Control 'FrameRateDial').Current.HelpText -match 'Generated\s+[1-9]' } 'generated frames on the dial'
    Assert-Layout
    $null=Wait-For { -not [Cadrivect.DashboardSnapshot]::Black($video) } 'rendered demo pixels'
    [Cadrivect.DashboardSnapshot]::Save($handle,$snapshot)
    Click-Control 'Fullscreen'
    $null=Wait-For { [Cadrivect.DashboardSnapshot]::Bounds($video) -ne $geometry } 'edge-to-edge video'
    [void][Cadrivect.DashboardSnapshot]::PostMessage($video,0x203,[IntPtr]::Zero,[IntPtr]::Zero)
    $null=Wait-For { [Cadrivect.DashboardSnapshot]::Bounds($video) -eq $geometry } 'restored dashboard'
    Click-Control 'OpenSource'
    $null=Wait-Text 'Status' '^Disconnected$'
    $null=Wait-For { [Cadrivect.DashboardSnapshot]::Black($video) } 'black disconnected area'
    Assert-Layout
    Click-Control 'HostNav'
    Assert-Layout
    Click-Control 'HostStatus'
    $hostStatus=Wait-Text 'HostState' '^Host is stopped'
    Click-Control 'ClientNav'
    (Get-Control 'HostAddress').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('invalid host!')
    Click-Control 'ClientPlay'
    $clientStatus=Wait-Text 'Status' '^Enter the host PC'
    [pscustomobject]@{demo=$demoStatus;geometry=$geometry;surface=$video.ToInt64();windows=1;pairing='Inline';host=$hostStatus;client=$clientStatus;screenshot=$snapshot} | ConvertTo-Json
} finally {
    if ($process) {
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(2500)) { $process.Kill(); $process.WaitForExit() }
        }
        $process.Dispose()
    }
    $resolved = [IO.Path]::GetFullPath($stage)
    if ([IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($resolved) -eq "P5Cast-smoke-$runId") {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
