# Gecko Peeks

Windows 11 tray app that traceroutes to Riot (`192.207.0.5`) and appends each result to a CSV. It starts in the tray. Closing or minimizing the window hides it. Only **Exit** quits.

## Build

From this folder, in PowerShell:

```powershell
Set-Location GeckoPeeks
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The executable is `GeckoPeeks\bin\Release\net8.0-windows\win-x64\publish\GeckoPeeks.exe`. It is a single-file self-contained win-x64 app and does not need administrator rights. The publish step embeds `Assets\gecko-peeks.ico` (multi-size 16/32/48/256) so Explorer, shortcuts, and the taskbar show the gecko.

Run it:

```powershell
Start-Process .\GeckoPeeks\bin\Release\net8.0-windows\win-x64\publish\GeckoPeeks.exe
```

Logic tests, from the repo root:

```powershell
dotnet test .\GeckoPeeks.Tests\GeckoPeeks.Tests.csproj
```

## Log

```powershell
Join-Path $env:USERPROFILE "Documents\RiotRouteLog\riot-route-log.csv"
```

That folder also holds `riot-route-log.pending.csv` (used when Excel has the main file open), `riot-route-log.backup.csv`, `gecko-peeks-settings.json`, and a capped `activity.log`. The main CSV is append-only.

If Windows Controlled folder access blocks Documents, allow `GeckoPeeks.exe` under ransomware protection or the log stays empty.

## Start with Windows

The tray command **Start with Windows** writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `GeckoPeeks` as the quoted exe path. Remove it with:

```powershell
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "GeckoPeeks"
```

## Upstream and handoff

Each responding hop is checked, case-insensitive, for `zayo.com` (Zayo), `cogentco.com` (Cogent), `telus` (TELUS), `wolfpaw` (Wolfpaw), and `he.net` (HE). **Upstream** is the provider on the last hop that matches one of those and does not also name a different network.

**Handoff** is the first hop after that upstream hop whose hostname belongs to a different recognized network. `twelve99.net` or `telia` means Telia. A hostname such as `zayo-telia...` names both, so it is the handoff onto Telia, not another Zayo hop. The city code is the first hostname token like `sea1`, `den3`, `chi`, `bil1`, or `yyc2`.

If that choice is wrong for your path, the rule lives in `GeckoPeeks/Routing/RouteClassifier.cs`.

The tray tooltip looks like `Zayo → Telia sea1 | 61 ms | 2 min ago`. `61 ms` is the average ping to `104.160.141.51`, or the handoff hop RTT when that average is blank. A balloon fires only when the upstream or the handoff city changes after the first logged test. City names in the balloon (`den3` → Denver, `sea1` → Seattle) are display-only; the CSV stores the code.
