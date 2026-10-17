# Gecko Peeks

Windows 11 tray app that traceroutes to Riot (`192.207.0.5`) and appends each result to a CSV. Launching the exe opens the window; **Start with Windows** starts it hidden in the tray. Close (X) hides the window and logging continues in the tray (a notification says so the first time). Minimize keeps it on the taskbar like any window. Only **Exit** quits.

## Getting the window back

- Click or double-click the gecko tray icon. Windows 11 may tuck it under the **^** overflow arrow.
- Or right-click the gecko and choose **Open window** (bold, first item).
- Or launch `GeckoPeeks.exe` again (Start menu, taskbar pin): the running copy brings its window forward instead of starting a second monitor.

The tray menu: **Open window**, Run test now, Copy diagnosis, Open log, Open log folder, Pause/Resume logging, Start with Windows (cream checkmark when on), Check for updates / Install update, Exit. **Copy diagnosis** puts a short plain-text report on the clipboard (route, last hop, Riot ICMP, League, adapter, and the hop list).

## Updates

A few seconds after launch the app asks GitHub for the latest release. When a newer `GeckoPeeks.exe` is up, it downloads in the background and the tray item becomes **Install update (v…)**. One click swaps the file and restarts with the same arguments (including `--tray` when Start with Windows launched it). **Check for updates** does the same check on demand; Cancel on the prompt skips that version so it does not nag again. Needs network access to `api.github.com` and `github.com` (or `objects.githubusercontent.com` for the asset).

## Window

The window is one surface. The route is the title, with **Last hop** and **Riot ICMP** as two separate figures beside **Copy diagnosis** and **Run test now**. One line under that names League, the adapter (and Wi-Fi, when there is one), a VPN when the default route is a tunnel, and how long ago the test ran. A hop whose address changed is marked in the path and called out on its own line (`Hop 8 changed`).

The path list uses the full width. Upstream totals sit on that heading as one line (`Zayo 80% · 21 ms · Riot no reply`), not a second grid. **Recent** is a short list of time, route, last hop, and Riot ICMP. Logging, the next test, and the phase are a single line in the top bar. The CSV path and **Open log** / **Open folder** / **Copy path** stay in a quiet **Log** row at the bottom. The full CSV stays behind **Open log**. The window scales with the display, so captions and buttons are drawn whole. Replacing an older build by hand: exit the old tray app first (right-click the gecko > **Exit**), then run the new exe, and check the title bar shows the new version. From v1.0.12 on, prefer **Install update** in the tray instead.

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

That folder also holds `riot-route-log.pending.csv` (only while Excel has the main file open; normally it does not exist), `riot-route-log.backup.csv`, `gecko-peeks-settings.json`, and a capped `activity.log`. The main CSV is append-only.

If Windows Controlled folder access (ransomware protection) refuses writes in Documents, the log moves here, with the earlier rows copied first:

```powershell
Join-Path $env:LOCALAPPDATA "GeckoPeeks\RiotRouteLog\riot-route-log.csv"
```

The Documents copy is left as it was and stays readable. Later runs keep logging in AppData, even after `GeckoPeeks.exe` is allowed, so the rows stay in one file.

The window's **Log** row shows the CSV in use and one status line (last write, why the log moved, or the current problem), with **Open log** / **Open folder** / **Copy path**, which follow the folder in use. **Logging** says **Blocked** in coral only when neither folder accepts writes. Rows from blocked tests wait in memory (up to 1,000, while the app runs) and are saved by the first test after a folder accepts writes again. If Controlled folder access is the cause, allow `GeckoPeeks.exe` under Ransomware protection. The header shows `v{Version}` so you can confirm the running build.

## Latency: last hop vs Riot ICMP

Each test pings two addresses ten times:

- **Last hop ms** (`LastHopAvg`) is measured mtr-style at the last router before the target that answered the traceroute: echoes go to the trace target with the TTL set to that hop's number, and only that router's Time Exceeded replies count (anything else is loss). It is path latency, not in-game ping. When only the target answered the traceroute, the last-hop fields are blank.
- **Riot ICMP** (`RiotHopAvg`) is the average ping to `192.207.0.5`, the same address the traceroute targets. Riot drops ping on many paths. When every ping is lost, the window shows **no reply** (the CSV keeps `RiotHopAvg` blank and `RiotHopLossPct` at 100), and the series stops after 3 unanswered pings so the row is written sooner.

The upstream summary shows both columns side by side and never fills one from the other. Those CSV column names are unchanged. v1.0.13 appends `Adapter`, `Wifi`, `Gateway`, and `Vpn` after `FullPath`. An older log keeps its rows; the header is widened on the next save, and the older rows leave the new fields blank. `Vpn` is `Y` when the default-route adapter is a known tunnel (WireGuard, NordLynx, Tailscale, OpenVPN, and the like). A hop-only path change is written to `activity.log` and shown on the card. Only an address that became a different address, or a path that got longer or shorter, counts; a hop that went to `*` or came back from `*` does not. A balloon still fires only when the upstream or the handoff city changes (see below).

## Start with Windows

The tray command **Start with Windows** writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `GeckoPeeks` as `"<path to GeckoPeeks.exe>" --tray`, so Windows starts it hidden in the tray. On each launch, an existing value that points at another copy of the exe, or lacks `--tray` (as v1.0.7 wrote it), is repointed at the running exe. Remove it with:

```powershell
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "GeckoPeeks"
```

## Upstream and handoff

Each responding hop is checked for official domains (`zayo.com`, `cogentco.com`, `telus.net` / `telus.com`, `wolfpaw.net`, `he.net`) and label-anchored stems for mixed names. **Upstream** is the provider on the last hop that matches one of those and does not also name a different network.

**Handoff** is the first hop after that upstream hop whose hostname belongs to a different recognized network. `twelve99.net` or a `telia` label means Telia. A hostname such as `zayo-telia...` names both, so it is the handoff onto Telia, not another Zayo hop. The city code prefers known city tokens and skips interface names (`ae5`, `ccr31`, `core1`).

If that choice is wrong for your path, the rule lives in `GeckoPeeks/Routing/RouteClassifier.cs`.

The tray tooltip looks like `Zayo → Telia sea1 | last hop 21 ms | 2 min ago` and names where its number came from: `Riot 61 ms` when Riot answered ping, else `last hop`, else `handoff` (the handoff hop's traceroute RTT). A balloon fires only when the upstream or the handoff city changes after the first logged test, and only once two logged tests in a row agree on the new upstream and city. Cities compare by name, so `sea1` → `sea2` is not a change. A test that is blank/`Unknown`, or whose city went blank after a known one, neither counts toward a change nor cancels one waiting for its second test. The pending change is kept in memory only. City names in the balloon (`den3` → Denver, `sea1` → Seattle) are display-only; the CSV stores the code.

## Release

GitHub Actions publishes `GeckoPeeks.exe` only on `v*` tag pushes or `workflow_dispatch` (with a tag input). Ordinary pushes to `main` do not overwrite a release.

Before uploading, the release job runs `dotnet test` and `.github/scripts/smoke-exe.ps1` on windows-latest. The CI workflow runs the same smoke on every push to `main`, so a regression shows up on the push that caused it. The smoke launches the published exe and checks that the window opens with the expected version, stays open through the first test, hides to the tray on Close, reopens from a tray click, a tray double-click, and a second launch, starts hidden with `--tray`, repoints a stale Start with Windows value, draws every label and button whole (inside its panel, no text cut at the bottom edge, no overlaps; checked on a render of the window), and leaves no `crash.log`.

It then denies writes to `Documents\RiotRouteLog` with an ACL, the way Controlled folder access refuses them, and checks that the log moves to `%LOCALAPPDATA%\GeckoPeeks\RiotRouteLog` with its rows, that the Documents CSV is unchanged, that the **Log** row shows the new path with no "Could not find file" text, and that **Open folder** opens it. With AppData denied too, the **Logging** chip must read **Blocked** (in coral) and the status must be one short line. Window text, load time, memory, and screenshots land in the job summary, run annotations, and the `smoke-v*` artifact (`smoke-ci-<sha>` on CI).

Still checked by hand on a real Windows 11 desktop: clicking the actual tray icon (including from the ^ overflow), the closed-to-tray notification, the Start with Windows checkmark at 125%/150% scaling, the window coming to the front over other apps, and real Controlled folder access (the smoke stands in for it with a deny ACL).
