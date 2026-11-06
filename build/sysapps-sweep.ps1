# System apps sweep: boots the real shell headless once per screen script in tests/nav/sysapps (each opens an app from
# the home screen, walks its menus, PS button, closes it, twice) and reports per script whether it finished (the
# script's own quit), crashed (a guest/process crash in the log) or hung (no quit before the timeout, or a watchdog
# STALL). Shots and logs go to <Out>/<script>/. Needs a data root with the firmware installed (os0, vs0, sa0).
#   build/sysapps-sweep.ps1 -Data <root> [-Exe <VitaSharp.Desktop>] [-Out <dir>] [-Only music,settings] [-TimeoutSec 900]
# Expected (no network, PSN, USB or device): see docs/notes/system-apps.md, "Sweep checklist".
param(
    [Parameter(Mandatory = $true)][string]$Data,
    [string]$Exe = "",
    [string]$Out = "",
    [string[]]$Only = @(),
    [int]$TimeoutSec = 900,
    [int]$Affinity = 0
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $name = if ($IsLinux -or $IsMacOS) { "VitaSharp.Desktop" } else { "VitaSharp.Desktop.exe" }
    $Exe = Join-Path $root "src/VitaSharp.Desktop/bin/Release/net10.0/$name"
}
if (-not (Test-Path $Exe)) { throw "VitaSharp.Desktop not found: $Exe (build it or pass -Exe)" }
if (-not $Out) { $Out = Join-Path $root "tests/output/sysapps-sweep" }
New-Item -ItemType Directory -Force $Out | Out-Null
$scripts = Get-ChildItem (Join-Path $root "tests/nav/sysapps") -Filter *.nav | Sort-Object Name
if ($Only.Count -gt 0) { $scripts = $scripts | Where-Object { $Only -contains $_.BaseName } }
$results = @()
foreach ($s in $scripts) {
    $dir = Join-Path $Out $s.BaseName
    New-Item -ItemType Directory -Force $dir | Out-Null
    $log = Join-Path $dir "log.txt"
    $argList = @("--data=$Data", "boot-shell", "--headless", "--vulkan", "--no-inspect", "--nav=$($s.FullName)", "--nav-out=$dir", "--log=$log")
    $p = Start-Process -FilePath $Exe -ArgumentList $argList -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $dir "stdout.txt") -RedirectStandardError (Join-Path $dir "stderr.txt")
    try { $p.PriorityClass = 'BelowNormal'; if ($Affinity -ne 0) { $p.ProcessorAffinity = $Affinity } } catch {}
    $finished = $p.WaitForExit($TimeoutSec * 1000)
    if (-not $finished) { try { $p.Kill() } catch {} }
    $text = if (Test-Path $log) { Get-Content $log -Raw } else { "" }
    $crash = [regex]::Matches($text, "(crashed in thread|\) crashed:|could not start)").Count
    $stall = [regex]::Matches($text, "\[watchdog\] STALL").Count
    $status = if (-not $finished) { "HANG" } elseif ($crash -gt 0) { "CRASH" } elseif ($stall -gt 0) { "STALL" } else { "OK" }
    $shots = (Get-ChildItem $dir -Filter *.png -ErrorAction SilentlyContinue).Count
    $results += [pscustomobject]@{ Script = $s.BaseName; Status = $status; Crashes = $crash; Stalls = $stall; Shots = $shots }
    "{0,-20} {1,-6} crashes {2} stalls {3} shots {4}" -f $s.BaseName, $status, $crash, $stall, $shots
}
$results | Format-Table -AutoSize | Out-String | Set-Content (Join-Path $Out "summary.txt")
if ($results | Where-Object { $_.Status -ne "OK" }) { exit 1 }
exit 0
