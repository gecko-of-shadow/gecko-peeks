<#
Launches the published GeckoPeeks.exe on a GitHub Windows runner and checks that it opens,
stays open, closes to the tray, reopens from the tray icon and from a second launch, starts
hidden with --tray, repoints a stale Start with Windows value, and logs no crash. Timings,
results and screenshots go to the job summary and to -OutDir.

CI only: it rewrites the HKCU Run value and kills every GeckoPeeks process.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$ExpectVersion,
    [string]$OutDir = "smoke"
)

$ErrorActionPreference = "Stop"
if ($env:GITHUB_ACTIONS -ne "true") {
    throw "smoke-exe.ps1 rewrites HKCU Run and kills GeckoPeeks processes; run it on a CI runner only."
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SmokeWin32
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static List<IntPtr> TopWindows(int processId)
    {
        var found = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(hWnd, out owner);
            if (owner == (uint)processId)
                found.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Title(IntPtr hWnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hWnd, text, text.Capacity);
        return text.ToString();
    }

    public static string ClassOf(IntPtr hWnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hWnd, text, text.Capacity);
        return text.ToString();
    }
}
"@

$WM_CLOSE = 0x0010
$WM_SYSCOMMAND = 0x0112
$SC_CLOSE = 0xF060
# WinForms NotifyIcon callback message (WM_USER + 1024); lParam carries the mouse message.
$WM_TRAYMOUSEMESSAGE = 0x0800
$WM_LBUTTONDOWN = 0x0201
$WM_LBUTTONUP = 0x0202
$WM_LBUTTONDBLCLK = 0x0203
$WM_RBUTTONUP = 0x0205

$results = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[string]]::new()

function Check([string]$Name, [bool]$Ok, [string]$Detail = "") {
    $status = if ($Ok) { "pass" } else { "FAIL" }
    $results.Add([pscustomobject]@{ Check = $Name; Result = $status; Detail = $Detail })
    if (-not $Ok) { $failures.Add("$Name $Detail".Trim()) }
    Write-Host "[$status] $Name $Detail"
}

function Info([string]$Name, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Name; Result = "info"; Detail = $Detail })
    Write-Host "[info] $Name $Detail"
}

function MainWindow([System.Diagnostics.Process]$Process) {
    foreach ($handle in [SmokeWin32]::TopWindows($Process.Id)) {
        if ([SmokeWin32]::Title($handle) -like "Gecko Peeks*") { return $handle }
    }
    return [IntPtr]::Zero
}

function WindowShown([System.Diagnostics.Process]$Process) {
    $handle = MainWindow $Process
    return ($handle -ne [IntPtr]::Zero) -and [SmokeWin32]::IsWindowVisible($handle) -and -not [SmokeWin32]::IsIconic($handle)
}

function WaitUntil([scriptblock]$Condition, [int]$TimeoutMs) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Condition) { return [int]$clock.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 200
    }
    return -1
}

function Launch([string[]]$Arguments = @()) {
    $process = if ($Arguments.Count -gt 0) {
        Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru
    } else {
        Start-Process -FilePath $Exe -PassThru
    }
    # Caching the handle keeps ExitCode readable after the process ends.
    $null = $process.Handle
    return $process
}

function CloseWithX([System.Diagnostics.Process]$Process) {
    $handle = MainWindow $Process
    if ($handle -ne [IntPtr]::Zero) {
        [void][SmokeWin32]::PostMessage($handle, $WM_SYSCOMMAND, [IntPtr]$SC_CLOSE, [IntPtr]::Zero)
    }
    return WaitUntil { -not (WindowShown $Process) } 5000
}

# The NotifyIcon listens on a hidden top-level WinForms window. Other hidden WinForms windows
# ignore this message, so every hidden one gets it.
function TrayMouse([System.Diagnostics.Process]$Process, [int[]]$MouseMessages) {
    $targets = @([SmokeWin32]::TopWindows($Process.Id) | Where-Object {
        -not [SmokeWin32]::IsWindowVisible($_) -and [SmokeWin32]::ClassOf($_) -like "WindowsForms10.*"
    })
    foreach ($message in $MouseMessages) {
        foreach ($handle in $targets) {
            [void][SmokeWin32]::PostMessage($handle, $WM_TRAYMOUSEMESSAGE, [IntPtr]::Zero, [IntPtr]$message)
        }
        Start-Sleep -Milliseconds 100
    }
    return $targets.Count
}

function Screenshot([string]$Name) {
    try {
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bitmap.Save((Join-Path $OutDir $Name), [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
        Info "Screenshot" "$Name ($($bounds.Width)x$($bounds.Height))"
    }
    catch {
        Info "Screenshot" "$Name not captured: $($_.Exception.Message)"
    }
}

function StopAll {
    Get-Process -Name GeckoPeeks -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$logFolder = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "RiotRouteLog"
$crashLog = Join-Path $logFolder "crash.log"
$activityLog = Join-Path $logFolder "activity.log"
$crashBefore = if (Test-Path -LiteralPath $crashLog) { (Get-Item -LiteralPath $crashLog).Length } else { 0 }
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

try {
    StopAll
    Info "Exe" ("{0} ({1:N1} MB)" -f $Exe, ((Get-Item -LiteralPath $Exe).Length / 1MB))

    # How v1.0.7 wrote it, for an older download: no --tray, different path. Fresh runner
    # profiles have no Run key at all.
    if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    Set-ItemProperty -Path $runKey -Name "GeckoPeeks" -Value '"C:\Users\Public\Downloads\GeckoPeeks (old).exe"'

    # --- Manual launch: window opens, stays open, closes to the tray, reopens from the tray icon.
    $app = Launch
    $shownMs = WaitUntil { WindowShown $app } 30000
    Check "Window opens on launch (load time)" ($shownMs -ge 0) "$shownMs ms"
    if ($shownMs -lt 0) { throw "The window never appeared." }

    $title = [SmokeWin32]::Title((MainWindow $app))
    Check "Title shows v$ExpectVersion" ($title -eq "Gecko Peeks v$ExpectVersion") $title

    $runValue = (Get-ItemProperty -Path $runKey -Name "GeckoPeeks" -ErrorAction SilentlyContinue).GeckoPeeks
    Check "Start with Windows value repointed at this exe with --tray" ($runValue -eq "`"$Exe`" --tray") $runValue

    $steady = 0
    for ($second = 0; $second -lt 20; $second++) {
        Start-Sleep -Seconds 1
        if ($app.HasExited -or -not (WindowShown $app)) { break }
        $steady++
    }
    Check "Window stays open for 20 s while the first test runs" ($steady -eq 20) "$steady s"
    $app.Refresh()
    Check "Window responds to messages" ($app.Responding)
    Info "Memory and CPU after 20 s" ("{0:N0} MB working set, {1:N1} s CPU" -f ($app.WorkingSet64 / 1MB), $app.TotalProcessorTime.TotalSeconds)
    Screenshot "smoke-window.png"

    $handoff = [System.Diagnostics.Stopwatch]::StartNew()
    $duplicate = Launch
    $duplicateExited = $duplicate.WaitForExit(15000)
    Check "Second launch hands off and exits" ($duplicateExited -and $duplicate.ExitCode -eq 0) "$($handoff.ElapsedMilliseconds) ms"
    Check "First instance keeps running with its window open" ((-not $app.HasExited) -and (WindowShown $app))

    $hiddenMs = CloseWithX $app
    Start-Sleep -Seconds 1
    Check "Close (X) hides to the tray and keeps logging" (($hiddenMs -ge 0) -and -not $app.HasExited) "$hiddenMs ms"

    $targets = TrayMouse $app @($WM_LBUTTONDBLCLK, $WM_LBUTTONUP)
    $doubleMs = WaitUntil { WindowShown $app } 10000
    Check "Tray double-click opens the window" ($doubleMs -ge 0) "$doubleMs ms, $targets hidden windows signalled"
    Start-Sleep -Seconds 5
    Check "Window stays open 5 s after the tray double-click" (WindowShown $app)

    $null = CloseWithX $app
    $null = TrayMouse $app @($WM_LBUTTONDOWN, $WM_LBUTTONUP)
    $clickMs = WaitUntil { WindowShown $app } 10000
    Check "Tray single click opens the window" ($clickMs -ge 0) "$clickMs ms"

    try { [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(900, 700) } catch { }
    $null = TrayMouse $app @($WM_RBUTTONUP)
    Start-Sleep -Milliseconds 1500
    Screenshot "smoke-tray-menu.png"
    StopAll

    # --- Start with Windows launch: hidden in the tray until opened.
    $tray = Launch @("--tray")
    Start-Sleep -Seconds 8
    Check "--tray start stays hidden and keeps running" ((-not $tray.HasExited) -and -not (WindowShown $tray))

    $form = MainWindow $tray
    if ($form -ne [IntPtr]::Zero) {
        [void][SmokeWin32]::PostMessage($form, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
    }
    Start-Sleep -Seconds 2
    Check "Outside WM_CLOSE keeps the window alive in the tray" ((-not $tray.HasExited) -and ((MainWindow $tray) -ne [IntPtr]::Zero))

    $reopen = [System.Diagnostics.Stopwatch]::StartNew()
    $opener = Launch
    $openMs = WaitUntil { WindowShown $tray } 15000
    Check "Relaunch opens the running app's window" ($openMs -ge 0) "$($reopen.ElapsedMilliseconds) ms"
    Check "Relaunch process exits" ($opener.WaitForExit(15000))
    Start-Sleep -Seconds 5
    Check "Window stays open 5 s after the relaunch" (WindowShown $tray)
    Screenshot "smoke-after-relaunch.png"

    $activity = if (Test-Path -LiteralPath $activityLog) { Get-Content -LiteralPath $activityLog -Raw } else { "" }
    Check "Activity log records the first test" ($activity -match "Starting the first test\.")
    $lastLines = ($activity -split "\r?\n" | Where-Object { $_ } | Select-Object -Last 5) -join " / "
    Info "Activity log tail" $lastLines
}
catch {
    Check "Smoke script finished" $false $_.Exception.Message
}
finally {
    StopAll
    Remove-ItemProperty -Path $runKey -Name "GeckoPeeks" -ErrorAction SilentlyContinue

    $crashAfter = if (Test-Path -LiteralPath $crashLog) { (Get-Item -LiteralPath $crashLog).Length } else { 0 }
    Check "No crash.log entries" ($crashAfter -eq $crashBefore) "$crashBefore -> $crashAfter bytes"
    foreach ($file in @($crashLog, $activityLog, (Join-Path $logFolder "riot-route-log.csv"))) {
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $OutDir -Force }
    }

    $lines = @("### Gecko Peeks $ExpectVersion smoke on windows-latest", "", "| Check | Result | Detail |", "| --- | --- | --- |")
    foreach ($result in $results) {
        $lines += "| $($result.Check) | $($result.Result) | $(($result.Detail -replace '\|', '/')) |"
    }
    $markdown = $lines -join "`n"
    $markdown | Out-File -FilePath (Join-Path $OutDir "smoke-summary.md") -Encoding utf8
    if ($env:GITHUB_STEP_SUMMARY) { $markdown | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}

if ($failures.Count -gt 0) {
    Write-Host "::error::Smoke failed: $($failures -join '; ')"
    exit 1
}
Write-Host "Smoke passed: $($results.Count) checks."
