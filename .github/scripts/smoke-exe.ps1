<#
Launches the published GeckoPeeks.exe on a GitHub Windows runner and checks that it opens,
stays open, closes to the tray, reopens from the tray icon and from a second launch, starts
hidden with --tray, repoints a stale Start with Windows value, and logs no crash. Then it denies
writes to Documents\RiotRouteLog the way Controlled folder access does and checks that the log
moves to %LOCALAPPDATA%\GeckoPeeks\RiotRouteLog with its rows, and that the window says Blocked
when neither folder accepts writes. Timings, window text, results and screenshots go to the job
summary and to -OutDir.

CI only: it rewrites the HKCU Run value, adds and removes deny ACLs on the log folders, and
kills every GeckoPeeks process.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$ExpectVersion,
    [string]$OutDir = "smoke"
)

$ErrorActionPreference = "Stop"
if ($env:GITHUB_ACTIONS -ne "true") {
    throw "smoke-exe.ps1 rewrites HKCU Run, changes folder ACLs and kills GeckoPeeks processes; run it on a CI runner only."
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

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
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

    public static List<IntPtr> Descendants(IntPtr parent)
    {
        var found = new List<IntPtr>();
        EnumChildWindows(parent, (hWnd, _) =>
        {
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

    // GetWindowText does not read the controls of another process; WM_GETTEXT does.
    public static string TextOf(IntPtr hWnd)
    {
        const uint WM_GETTEXT = 0x000D;
        const uint WM_GETTEXTLENGTH = 0x000E;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        IntPtr length;
        if (hWnd == IntPtr.Zero
            || SendMessageTimeout(hWnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 3000, out length) == IntPtr.Zero)
            return "";
        var text = new StringBuilder(length.ToInt32() + 2);
        IntPtr copied;
        SendMessageTimeout(hWnd, WM_GETTEXT, (IntPtr)text.Capacity, text, SMTO_ABORTIFHUNG, 3000, out copied);
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
$BM_CLICK = 0x00F5
# WinForms NotifyIcon callback message (WM_USER + 1024); lParam carries the mouse message.
$WM_TRAYMOUSEMESSAGE = 0x0800
$WM_LBUTTONDOWN = 0x0201
$WM_LBUTTONUP = 0x0202
$WM_LBUTTONDBLCLK = 0x0203
$WM_RBUTTONUP = 0x0205
# PrintWindow: render through DWM so windows on top do not matter.
$PW_RENDERFULLCONTENT = 2

# Theme.cs colours: chip text against the Charcoal status panel.
$theme = [ordered]@{ Coral = @(230, 130, 96); Leaf = @(140, 196, 132); Amber = @(240, 200, 110); Cream = @(246, 242, 232) }
$charcoal = @(28, 34, 30)

$results = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[string]]::new()
$denied = [System.Collections.Generic.List[string]]::new()
$readFailures = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$userSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value

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

# Also an annotation on the run, readable without downloading the artifact.
function Notice([string]$Name, [string]$Detail) {
    Info $Name $Detail
    $message = $Detail -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A'
    Write-Host "::notice title=Smoke ${Name}::$message"
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
        $met = try { & $Condition } catch { $false }
        if ($met) { return [int]$clock.ElapsedMilliseconds }
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

function ControlWithText([IntPtr]$Form, [string]$Text, [string]$ClassLike) {
    foreach ($handle in [SmokeWin32]::Descendants($Form)) {
        if ([SmokeWin32]::ClassOf($handle) -like $ClassLike -and [SmokeWin32]::TextOf($handle) -ceq $Text) { return $handle }
    }
    return [IntPtr]::Zero
}

# The label that shares a parent with a caption: the Logging chip value, the LOCAL LOG file path.
function ValueBeside([IntPtr]$Form, [string]$CaptionText) {
    $captionHandle = ControlWithText $Form $CaptionText "*.STATIC.*"
    if ($captionHandle -eq [IntPtr]::Zero) { return [IntPtr]::Zero }
    $parent = [SmokeWin32]::GetParent($captionHandle)
    foreach ($handle in [SmokeWin32]::Descendants($parent)) {
        if ($handle -ne $captionHandle -and [SmokeWin32]::GetParent($handle) -eq $parent -and [SmokeWin32]::ClassOf($handle) -like "*.STATIC.*") { return $handle }
    }
    return [IntPtr]::Zero
}

# The LOCAL LOG status line sits directly in the strip panel, under the File row.
function LogStatusLabel([IntPtr]$Form) {
    $fileCaption = ControlWithText $Form "File" "*.STATIC.*"
    if ($fileCaption -eq [IntPtr]::Zero) { return [IntPtr]::Zero }
    $strip = [SmokeWin32]::GetParent([SmokeWin32]::GetParent($fileCaption))
    foreach ($handle in [SmokeWin32]::Descendants($strip)) {
        if ([SmokeWin32]::GetParent($handle) -eq $strip -and [SmokeWin32]::ClassOf($handle) -like "*.STATIC.*") { return $handle }
    }
    return [IntPtr]::Zero
}

function LogView([IntPtr]$Form) {
    $chip = ValueBeside $Form "LOGGING"
    [pscustomobject]@{
        ChipHandle = $chip
        Chip = [SmokeWin32]::TextOf($chip)
        File = [SmokeWin32]::TextOf((ValueBeside $Form "File"))
        Status = [SmokeWin32]::TextOf((LogStatusLabel $Form))
        Texts = @([SmokeWin32]::Descendants($Form) | ForEach-Object { [SmokeWin32]::TextOf($_) } | Where-Object { $_ })
    }
}

function SaveTexts([string]$Name, $View) {
    ($View.Texts -join "`n") | Out-File -FilePath (Join-Path $OutDir $Name) -Encoding utf8
}

# Nearest theme colour to the strongest text pixels of a label. Empty when no text was found.
function TextColour([IntPtr]$Form, [IntPtr]$Label) {
    $formRect = New-Object SmokeWin32+RECT
    $labelRect = New-Object SmokeWin32+RECT
    if ($Label -eq [IntPtr]::Zero -or -not [SmokeWin32]::GetWindowRect($Form, [ref]$formRect) -or -not [SmokeWin32]::GetWindowRect($Label, [ref]$labelRect)) {
        return ""
    }
    $width = $formRect.Right - $formRect.Left
    $height = $formRect.Bottom - $formRect.Top
    if ($width -le 0 -or $height -le 0) { return "" }

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $hdc = $graphics.GetHdc()
        $printed = [SmokeWin32]::PrintWindow($Form, $hdc, $PW_RENDERFULLCONTENT)
        $graphics.ReleaseHdc($hdc)
        if (-not $printed) {
            $graphics.CopyFromScreen($formRect.Left, $formRect.Top, 0, 0, $bitmap.Size)
        }
        $graphics.Dispose()

        $samples = [System.Collections.Generic.List[object]]::new()
        $strongest = 0
        $top = [Math]::Max(0, $labelRect.Top - $formRect.Top)
        $bottom = [Math]::Min($height, $labelRect.Bottom - $formRect.Top)
        $left = [Math]::Max(0, $labelRect.Left - $formRect.Left)
        $right = [Math]::Min($width, $labelRect.Right - $formRect.Left)
        for ($y = $top; $y -lt $bottom; $y++) {
            for ($x = $left; $x -lt $right; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                $distance = [Math]::Abs($pixel.R - $charcoal[0]) + [Math]::Abs($pixel.G - $charcoal[1]) + [Math]::Abs($pixel.B - $charcoal[2])
                if ($distance -gt 90) {
                    $samples.Add(@($pixel.R, $pixel.G, $pixel.B, $distance))
                    if ($distance -gt $strongest) { $strongest = $distance }
                }
            }
        }

        # Anti-aliased edges blend into the background; the strongest pixels carry the text colour.
        $strong = @($samples | Where-Object { $_[3] -ge 0.7 * $strongest })
        if ($strong.Count -lt 5) { return "" }
        $sum = @(0, 0, 0)
        foreach ($sample in $strong) {
            $sum[0] += $sample[0]
            $sum[1] += $sample[1]
            $sum[2] += $sample[2]
        }
        $average = @([int]($sum[0] / $strong.Count), [int]($sum[1] / $strong.Count), [int]($sum[2] / $strong.Count))

        # A text pixel lies between the background and the text colour, by how much of it the
        # glyph covers. Compare along each background-to-colour line, not by plain distance,
        # which pulls faint text toward the darkest colour.
        $offset = @(($average[0] - $charcoal[0]), ($average[1] - $charcoal[1]), ($average[2] - $charcoal[2]))
        $gaps = [ordered]@{}
        foreach ($name in $theme.Keys) {
            $colour = $theme[$name]
            $toward = @(($colour[0] - $charcoal[0]), ($colour[1] - $charcoal[1]), ($colour[2] - $charcoal[2]))
            $coverage = ($offset[0] * $toward[0] + $offset[1] * $toward[1] + $offset[2] * $toward[2]) / ($toward[0] * $toward[0] + $toward[1] * $toward[1] + $toward[2] * $toward[2])
            $coverage = [Math]::Min(1.0, [Math]::Max(0.0, $coverage))
            $gap = 0.0
            for ($i = 0; $i -lt 3; $i++) { $gap += [Math]::Pow($offset[$i] - $coverage * $toward[$i], 2) }
            $gaps[$name] = [Math]::Sqrt($gap)
        }
        $ranked = @($gaps.GetEnumerator() | Sort-Object Value)
        $nearest = if ($ranked[0].Value -le 0.5 * $ranked[1].Value) { $ranked[0].Key } else { "Unsure" }
        return "$nearest (rgb $($average -join ','), $($strong.Count) px)"
    }
    catch {
        return ""
    }
    finally {
        $bitmap.Dispose()
    }
}

function RectOf([IntPtr]$Handle) {
    $rect = New-Object SmokeWin32+RECT
    if ($Handle -eq [IntPtr]::Zero -or -not [SmokeWin32]::GetWindowRect($Handle, [ref]$rect)) { return $null }
    return $rect
}

function Inside($Inner, $Outer) {
    return ($Inner.Left -ge $Outer.Left) -and ($Inner.Top -ge $Outer.Top) -and ($Inner.Right -le $Outer.Right) -and ($Inner.Bottom -le $Outer.Bottom)
}

# Windows clips a child to its parent, so a caption or button that leaves any ancestor's rectangle
# is drawn cut off (the v1.0.9 LOCAL LOG row hid its caption and the bottom edge of its buttons).
function StripLayout([IntPtr]$Form) {
    $problems = [System.Collections.Generic.List[string]]::new()
    $details = [System.Collections.Generic.List[string]]::new()
    $formRect = RectOf $Form
    $targets = [ordered]@{ "LOCAL LOG (CSV)" = "*.STATIC.*"; "Open log" = "*.BUTTON.*"; "Open folder" = "*.BUTTON.*"; "Copy path" = "*.BUTTON.*" }
    foreach ($text in $targets.Keys) {
        $handle = ControlWithText $Form $text $targets[$text]
        $rect = RectOf $handle
        if ($null -eq $rect) { $problems.Add("'$text' not found"); continue }
        if (-not [SmokeWin32]::IsWindowVisible($handle)) { $problems.Add("'$text' is hidden"); continue }
        $details.Add("$text $($rect.Right - $rect.Left)x$($rect.Bottom - $rect.Top) at $($rect.Left - $formRect.Left),$($rect.Top - $formRect.Top)")

        $parent = [SmokeWin32]::GetParent($handle)
        for ($depth = 0; $depth -lt 10 -and $parent -ne [IntPtr]::Zero; $depth++) {
            $outer = RectOf $parent
            if ($null -ne $outer -and -not (Inside $rect $outer)) {
                $problems.Add("'$text' ($($rect.Left),$($rect.Top))-($($rect.Right),$($rect.Bottom)) leaves its parent ($($outer.Left),$($outer.Top))-($($outer.Right),$($outer.Bottom))")
                break
            }
            if ($parent -eq $Form) { break }
            $parent = [SmokeWin32]::GetParent($parent)
        }
    }
    [pscustomobject]@{ Problems = $problems; Detail = ($details -join " | ") }
}

# UI Automation Invoke runs the button's Click on the app's UI thread. BM_CLICK is the fallback.
function Press([IntPtr]$Form, [string]$Text) {
    $button = ControlWithText $Form $Text "*.BUTTON.*"
    if ($button -eq [IntPtr]::Zero) { return "no '$Text' button" }
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $element = [System.Windows.Automation.AutomationElement]::FromHandle($button)
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        return "UI Automation Invoke"
    }
    catch {
        [void][SmokeWin32]::PostMessage($button, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
        return "BM_CLICK (UI Automation: $($_.Exception.Message))"
    }
}

function ExplorerFolders {
    try {
        $shell = New-Object -ComObject Shell.Application
        return @($shell.Windows() | ForEach-Object { try { $_.Document.Folder.Self.Path } catch { } } | Where-Object { $_ })
    }
    catch {
        return @()
    }
}

function ExplorerTitled([string]$Name) {
    foreach ($process in @(Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        foreach ($handle in [SmokeWin32]::TopWindows($process.Id)) {
            if ([SmokeWin32]::IsWindowVisible($handle) -and [SmokeWin32]::Title($handle) -eq $Name) { return $true }
        }
    }
    return $false
}

function CloseExplorerAt([string]$Folder) {
    try {
        $shell = New-Object -ComObject Shell.Application
        foreach ($window in @($shell.Windows())) {
            try {
                if ($window.Document.Folder.Self.Path -ieq $Folder) { $window.Quit() }
            }
            catch { }
        }
    }
    catch { }
}

# Controlled folder access lets an app read a protected folder but not create, change, rename or
# delete files in it. A deny ACE for this user on those rights is the closest stand-in on a runner.
# DE, not D: icacls's D also carries SYNCHRONIZE, which every file open needs, so it denies reads too.
function DenyWrites([string]$Folder) {
    $null = & icacls $Folder /deny "*${userSid}:(OI)(CI)(WD,AD,WEA,WA,DE,DC)"
    $ok = $LASTEXITCODE -eq 0
    $denied.Add($Folder)
    $ace = @(& icacls $Folder | Where-Object { $_ -match "DENY" } | ForEach-Object { $_.Trim() }) -join " "
    Info "Deny ACE" $ace
    return $ok
}

function RestoreWrites {
    foreach ($folder in $denied) {
        $null = & icacls $folder /remove:d "*$userSid" /T /C /Q
    }
    $denied.Clear()
}

function CanCreateIn([string]$Folder) {
    try {
        $probe = [System.IO.FileStream]::new((Join-Path $Folder "smoke-write-test.tmp"), [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None, 1, [System.IO.FileOptions]::DeleteOnClose)
        $probe.Dispose()
        return $true
    }
    catch {
        return $false
    }
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

function FileLength([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return (Get-Item -LiteralPath $Path).Length }
    return 0
}

# A refused or busy file reads as "" (reported once per path), so one check fails, not the run.
function ReadText([string]$Path) {
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return "" }
        return [string](Get-Content -LiteralPath $Path -Raw -ErrorAction Stop)
    }
    catch {
        if ($readFailures.Add($Path)) { Info "Could not read $Path" $_.Exception.Message }
        return ""
    }
}

function HashOf([string]$Path) {
    try { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash }
    catch { return "unreadable: $($_.Exception.Message)" }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$logFolder = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "RiotRouteLog"
$logCsv = Join-Path $logFolder "riot-route-log.csv"
$crashLog = Join-Path $logFolder "crash.log"
$activityLog = Join-Path $logFolder "activity.log"
$fallbackFolder = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "GeckoPeeks\RiotRouteLog"
$fallbackCsv = Join-Path $fallbackFolder "riot-route-log.csv"
$fallbackCrash = Join-Path $fallbackFolder "crash.log"
$fallbackActivity = Join-Path $fallbackFolder "activity.log"
$crashBefore = FileLength $crashLog
$fallbackCrashBefore = FileLength $fallbackCrash
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$colourCalibrated = $false

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

    $form = MainWindow $app
    $view = LogView $form
    SaveTexts "window-text-normal.txt" $view
    Check "Logging chip says On" ($view.Chip -eq "On") $view.Chip
    $tone = TextColour $form $view.ChipHandle
    $colourCalibrated = $tone -like "Leaf*"
    Info "On chip colour (expected Leaf)" $(if ($tone) { $tone } else { "no text pixels captured" })
    Check "LOCAL LOG shows the Documents CSV" ($view.File -ieq $logCsv) $view.File
    Check "No 'Could not find file' text in the window" (-not ($view.Texts -match "Could not find file"))
    Notice "normal log" "Logging=$($view.Chip) [$tone] | File=$($view.File) | $($view.Status)"
    $layout = StripLayout $form
    Check "LOCAL LOG caption and buttons are drawn whole" ($layout.Problems.Count -eq 0) ($layout.Problems -join "; ")
    Notice "layout" $layout.Detail
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

    $trayForm = MainWindow $tray
    if ($trayForm -ne [IntPtr]::Zero) {
        [void][SmokeWin32]::PostMessage($trayForm, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
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

    $activity = ReadText $activityLog
    Check "Activity log records the first test" ($activity -match "Starting the first test\.")
    $lastLines = ($activity -split "\r?\n" | Where-Object { $_ } | Select-Object -Last 5) -join " / "
    Info "Activity log tail" $lastLines
    StopAll

    # --- Controlled folder access on Documents: reads work, creates and appends are refused.
    Check "No AppData log before Documents is blocked" (-not (Test-Path -LiteralPath $fallbackCsv))
    $header = "Timestamp,LeagueRunning,Upstream,HandoffNetwork,HandoffHost,HandoffIP,HandoffCity,HandoffRttMs,LastHopIP,LastHopHost,LastHopMin,LastHopAvg,LastHopMax,LastHopLossPct,RiotHopAvg,RiotHopLossPct,FullPath"
    $seedTime = (Get-Date).AddMinutes(-30).ToString("yyyy-MM-dd HH:mm:ss", [System.Globalization.CultureInfo]::InvariantCulture)
    $seed = @($seedTime, "N", "SmokeSeed", "", "", "", "", "", "", "", "", "21", "", "", "", "100", "1.1.1.1 > 192.207.0.5") -join ","
    New-Item -ItemType Directory -Force -Path $logFolder | Out-Null
    if ((FileLength $logCsv) -eq 0) {
        [System.IO.File]::WriteAllText($logCsv, "$header`r`n", [System.Text.UTF8Encoding]::new($true))
    }
    [System.IO.File]::AppendAllText($logCsv, "$seed`r`n", [System.Text.UTF8Encoding]::new($false))
    $documentsHash = (Get-FileHash -LiteralPath $logCsv -Algorithm SHA256).Hash
    $documentsRows = @(Get-Content -LiteralPath $logCsv | Where-Object { $_ -and $_ -notmatch "^\uFEFF?Timestamp," }).Count

    Check "Deny ACL set on Documents\RiotRouteLog" (DenyWrites $logFolder)
    $refusesCreate = -not (CanCreateIn $logFolder)
    $stillReads = (ReadText $logCsv) -match "SmokeSeed"
    Check "Documents\RiotRouteLog refuses new files and still reads" ($refusesCreate -and $stillReads) "refuses create: $refusesCreate, reads: $stillReads"

    $cfa = Launch
    $cfaShownMs = WaitUntil { WindowShown $cfa } 30000
    Check "Window opens with Documents blocked" ($cfaShownMs -ge 0) "$cfaShownMs ms"
    $form = MainWindow $cfa
    $movedMs = WaitUntil { (LogView $form).File -ieq $fallbackCsv } 15000
    $view = LogView $form
    SaveTexts "window-text-cfa.txt" $view
    Check "LOCAL LOG shows the AppData CSV" ($movedMs -ge 0) $view.File
    $fallbackText = ReadText $fallbackCsv
    $fallbackRows = @($fallbackText -split "\r?\n" | Where-Object { $_ -and $_ -notmatch "^\uFEFF?Timestamp," }).Count
    Check "Earlier rows copied to AppData" (($fallbackText -match "SmokeSeed") -and ($fallbackRows -ge $documentsRows)) "$documentsRows in Documents, $fallbackRows in AppData"
    $documentsHashAfter = HashOf $logCsv
    Check "Documents CSV left as it was" ($documentsHashAfter -eq $documentsHash) $(if ($documentsHashAfter -like "unreadable:*") { $documentsHashAfter } else { "" })
    Check "Status says Controlled folder access moved the log" ($view.Status -match "Controlled folder access") $view.Status
    Check "Status carries no path or exception" ($view.Status -notmatch ":\\|Could not find file|Exception") $view.Status
    Check "No 'Could not find file' text in the window" (-not ($view.Texts -match "Could not find file"))
    Check "Logging chip stays On while AppData accepts writes" ($view.Chip -eq "On") $view.Chip
    Notice "log moved" "Logging=$($view.Chip) | File=$($view.File) | $($view.Status)"
    Screenshot "smoke-cfa-moved.png"

    $copyHow = Press $form "Copy path"
    Start-Sleep -Milliseconds 500
    $clip = try { "$(Get-Clipboard -Raw)".Trim() } catch { "" }
    if ($clip) {
        Check "Copy path copies the AppData location" (($clip -ieq $fallbackFolder) -or ($clip -ieq $fallbackCsv)) "$clip via $copyHow"
    } else {
        Info "Copy path" "clipboard not readable on this runner ($copyHow)"
    }

    $openHow = Press $form "Open folder"
    $explorerMs = WaitUntil { (ExplorerFolders) -contains $fallbackFolder } 15000
    $explorer = @(ExplorerFolders | Select-Object -Unique)
    if ($explorerMs -ge 0) {
        Check "Open folder opens the AppData folder in Explorer" $true "$explorerMs ms via $openHow"
    } elseif ($explorer.Count -gt 0) {
        Check "Open folder opens the AppData folder in Explorer" $false "Explorer shows $($explorer -join '; ') via $openHow"
    } elseif (ExplorerTitled "RiotRouteLog") {
        Check "Open folder opens an Explorer window on RiotRouteLog" $true "Shell.Application lists no windows; via $openHow"
    } else {
        Check "Open folder opens the AppData folder in Explorer" $false "no Explorer window via $openHow"
    }
    Notice "open folder" "Explorer windows: $($explorer -join '; ')"
    Screenshot "smoke-cfa-open-folder.png"
    CloseExplorerAt $fallbackFolder

    $movedNoted = WaitUntil { (ReadText $fallbackActivity) -match "Log moved to AppData" } 10000
    Check "AppData activity.log records the move" ($movedNoted -ge 0)
    $cfa.Refresh()
    Check "App keeps running after the move" ((-not $cfa.HasExited) -and $cfa.Responding)
    StopAll

    # --- Neither folder accepts writes: the chip says Blocked and the status is one short line.
    Check "Deny ACL set on the AppData log folder" (DenyWrites $fallbackFolder)
    $blocked = Launch
    $blockedShownMs = WaitUntil { WindowShown $blocked } 30000
    Check "Window opens with both folders blocked" ($blockedShownMs -ge 0) "$blockedShownMs ms"
    $form = MainWindow $blocked
    $chipMs = WaitUntil { (LogView $form).Chip -eq "Blocked" } 15000
    $view = LogView $form
    SaveTexts "window-text-blocked.txt" $view
    Check "Logging chip says Blocked" ($chipMs -ge 0) $view.Chip
    $tone = TextColour $form $view.ChipHandle
    if ($colourCalibrated -and $tone -and $tone -notlike "Unsure*") {
        Check "Blocked chip is coral" ($tone -like "Coral*") $tone
    } else {
        Info "Blocked chip colour (expected Coral)" $(if ($tone) { "$tone; not checked (On chip read as Leaf: $colourCalibrated)" } else { "no text pixels captured" })
    }
    Check "Blocked status is one short line" ($view.Status.StartsWith("Blocked:") -and $view.Status.Length -le 200 -and $view.Status -notmatch "[\r\n]") "$($view.Status.Length) chars: $($view.Status)"
    Check "Blocked status names the fix, not a path or exception" (($view.Status -match "Ransomware protection") -and ($view.Status -notmatch ":\\|Could not find file|Exception"))
    Check "No 'Could not find file' text in the window" (-not ($view.Texts -match "Could not find file"))
    Notice "blocked" "Logging=$($view.Chip) [$tone] | $($view.Status)"
    Start-Sleep -Seconds 10
    $blocked.Refresh()
    Check "App keeps running while blocked" ((-not $blocked.HasExited) -and $blocked.Responding)
    Screenshot "smoke-blocked.png"
    StopAll
    RestoreWrites
}
catch {
    Check "Smoke script finished" $false "$($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))"
}
finally {
    StopAll
    RestoreWrites
    CloseExplorerAt $fallbackFolder
    Remove-ItemProperty -Path $runKey -Name "GeckoPeeks" -ErrorAction SilentlyContinue

    $crashAfter = FileLength $crashLog
    Check "No crash.log entries" ($crashAfter -eq $crashBefore) "$crashBefore -> $crashAfter bytes"
    $fallbackCrashAfter = FileLength $fallbackCrash
    Check "No crash.log entries in AppData" ($fallbackCrashAfter -eq $fallbackCrashBefore) "$fallbackCrashBefore -> $fallbackCrashAfter bytes"
    foreach ($file in @($crashLog, $activityLog, $logCsv)) {
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $OutDir -Force }
    }
    foreach ($file in @($fallbackCrash, $fallbackActivity, $fallbackCsv)) {
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $OutDir ("appdata-" + (Split-Path -Leaf $file))) -Force }
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
