# Decodes Assets/gecko-peeks.ico.b64 into Assets/gecko-peeks.ico for ApplicationIcon.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$b64Path = Join-Path $here "gecko-peeks.ico.b64"
$icoPath = Join-Path $here "gecko-peeks.ico"
$b64 = (Get-Content -LiteralPath $b64Path -Raw).Trim()
[System.IO.File]::WriteAllBytes($icoPath, [Convert]::FromBase64String($b64))
