# Gecko Peeks

Windows 11 tray app that traceroutes to Riot (`192.207.0.5`) and appends each result to a CSV. Launching the exe opens the window; **Start with Windows** starts it hidden in the tray. Close (X) hides the window and logging continues in the tray (a notification says so the first time). Minimize keeps it on the taskbar like any window. Only **Exit** quits.

## Getting the window back

- Click or double-click the gecko tray icon. Windows 11 may tuck it under the **^** overflow arrow.
- Or right-click the gecko and choose **Open window** (bold, first item).
- Or launch `GeckoPeeks.exe` again (Start menu, taskbar pin): the running copy brings its window forward instead of starting a second monitor.

The tray menu: **Open window**, Run test now, Open log, Open log folder, Pause/Resume logging, Start with Windows (cream checkmark when on), Exit.

## Build

From this folder, in PowerShell:

```powershell
# Fresh clone: materialize the application icon (also done automatically by MSBuild).
if (-not (Test-Path -LiteralPath "GeckoPeeks\Assets\gecko-peeks.ico")) {
  certutil -decode "GeckoPeeks\Assets\gecko-peeks.ico.b64" "GeckoPeeks\Assets\gecko-peeks.ico"
}

Set-Location GeckoPeeks
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The executable is `GeckoPeeks\bin\Release\net8.0-windows\win-x64\publish\GeckoPeeks.exe`. It is a compressed single-file self-contained win-x64 app and does not need administrator rights. The publish step embeds `Assets\gecko-peeks.ico` (multi-size 16/32/48) so Explorer, shortcuts, and the taskbar show the gecko. The binary `.ico` is generated from the committed `gecko-peeks.ico.b64` when missing.

Run it:

```powershell
Start-Process .\GeckoPeeks\bin\Release\net8.0-windows\win-x64\publish\GeckoPeeks.exe
```

Logic tests, from the repo root:

```powershell
dotnet test -c Release
```

## Log

```powershell
Join-Path $env:USERPROFILE "Documents\RiotRouteLog\riot-route-log.csv"
```

That folder also holds `riot-route-log.pending.csv` (used when Excel has the main file open), `riot-route-log.backup.csv`, `gecko-peeks-settings.json`, and a capped `activity.log`. The main CSV is append-only.

If Windows Controlled folder access blocks Documents, allow `GeckoPeeks.exe` under ransomware protection or the log stays empty.

The window's **LOCAL LOG** strip shows the CSV file path and one status line (last write, or the current problem), with **Open log** / **Open folder** / **Copy path**. The header shows `v{Version}` so you can confirm the running build.

## Latency: last hop vs Riot ICMP

Each test pings two addresses ten times:

- **Last hop ms** (`LastHopAvg`) is the average ping to the last router that answered the traceroute. It is path latency, not in-game ping.
- **Riot ICMP** (`RiotHopAvg`) is the average ping to `104.160.141.51`. Riot drops ping on many paths. When every ping is lost, the window shows **no reply** (the CSV keeps `RiotHopAvg` blank and `RiotHopLossPct` at 100), and the series stops after 3 unanswered pings so the row is written sooner.

The upstream summary shows both columns side by side and never fills one from the other. The CSV column names are unchanged.

## Start with Windows

The tray command **Start with Windows** writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `GeckoPeeks` as `"<path to GeckoPeeks.exe>" --tray`, so Windows starts it hidden in the tray. On each launch, an existing value that points at another copy of the exe, or lacks `--tray` (as v1.0.7 wrote it), is repointed at the running exe. Remove it with:

```powershell
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "GeckoPeeks"
```

## Upstream and handoff

Each responding hop is checked for official domains (`zayo.com`, `cogentco.com`, `telus.net` / `telus.com`, `wolfpaw.net`, `he.net`) and label-anchored stems for mixed names. **Upstream** is the provider on the last hop that matches one of those and does not also name a different network.

**Handoff** is the first hop after that upstream hop whose hostname belongs to a different recognized network. `twelve99.net` or a `telia` label means Telia. A hostname such as `zayo-telia...` names both, so it is the handoff onto Telia, not another Zayo hop. The city code prefers known city tokens and skips interface names (`ae5`, `ccr31`, `core1`).

If that choice is wrong for your path, the rule lives in `GeckoPeeks/Routing/RouteClassifier.cs`.

The tray tooltip looks like `Zayo → Telia sea1 | last hop 21 ms | 2 min ago` and names where its number came from: `Riot 61 ms` when Riot answered ping, else `last hop`, else `handoff` (the handoff hop's traceroute RTT). A balloon fires only when the upstream or the handoff city changes after the first logged test, and not when either side is blank/`Unknown`. City names in the balloon (`den3` → Denver, `sea1` → Seattle) are display-only; the CSV stores the code.

## Release

GitHub Actions publishes `GeckoPeeks.exe` only on `v*` tag pushes or `workflow_dispatch` (with a tag input). Ordinary pushes to `main` do not overwrite a release.

Before uploading, the release job runs `dotnet test` and `.github/scripts/smoke-exe.ps1` on windows-latest. The smoke launches the published exe and checks that the window opens with the expected version, stays open through the first test, hides to the tray on Close, reopens from a tray click, a tray double-click, and a second launch, starts hidden with `--tray`, repoints a stale Start with Windows value, and leaves no `crash.log`. Load time, memory, and screenshots land in the job summary and the `smoke-v*` artifact.

Still checked by hand on a real Windows 11 desktop: clicking the actual tray icon (including from the ^ overflow), the closed-to-tray notification, the Start with Windows checkmark at 125%/150% scaling, and the window coming to the front over other apps.
