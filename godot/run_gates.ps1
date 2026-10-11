#!/usr/bin/env pwsh
<#
  run_gates.ps1 - the no-human verification loop for the REWIND kernel (milestone M1).

  One command, real process exit codes:
    1. headless import                          (rc must be 0, no ERROR/WARNING)
    2. dotnet build of the C# assembly          (rc must be 0, warning-free)
    3. headless main-scene boot                 (rc must be 0)
    4. headless T01-T11 + kernel invariant gate (rc must be 0)
    5. NEGATIVE CONTROL: the gate must be able to fail (rc must be non-zero)
    6. CROSS-PROCESS DETERMINISM: two separate processes must print the same hash

  Usage:
    pwsh godot/run_gates.ps1              # steps 1-6
    pwsh godot/run_gates.ps1 -SkipImport # reuse the existing .godot import cache

  WHY every path is spelled out instead of relying on PATH: see
  tools/godotprobe/REPORT.md section 0. Bare `godot` from PATH is a trap (Low integrity
  level: writes are silently dropped), and godot.exe is GUI-subsystem, so PowerShell
  neither waits for it nor sets $LASTEXITCODE.

  NOTE: ASCII-only on purpose. Windows PowerShell reads a BOM-less .ps1 as ANSI, so
  non-ASCII punctuation in this file would be corrupted on load.
#>
[CmdletBinding()]
param(
    [switch]$SkipImport
)

$ErrorActionPreference = 'Stop'

$godotDir = $PSScriptRoot
$wsroot   = (Resolve-Path (Join-Path $godotDir '..')).Path
$godot    = Join-Path $wsroot 'tools\godot\godot_console.exe'
$dotnet   = 'C:\Program Files\dotnet\dotnet.exe'
$tmp      = Join-Path $godotDir '.tmp'
$logs     = Join-Path $tmp 'gate'
$scene    = 'res://tests/test_main.tscn'

New-Item -ItemType Directory -Force -Path $logs, "$tmp\tmp", "$tmp\clihome" | Out-Null

# dotnet cannot use the default Temp / CLI home in this environment (see probe REPORT 0.3).
$env:TMP = "$tmp\tmp"; $env:TEMP = "$tmp\tmp"; $env:TMPDIR = "$tmp\tmp"
$env:DOTNET_CLI_HOME = "$tmp\clihome"
Remove-Item Env:\DOTNET_ROOT -ErrorAction SilentlyContinue
$env:PATH = "C:\Program Files\dotnet;" + (($env:PATH -split ';' | Where-Object { $_ -notmatch 'saint_ruin\\tools\\dotnet' }) -join ';')

$failed = @()
function Step($name, $ok, $detail) {
    if ($ok) { Write-Host "  PASS  $name  $detail" -ForegroundColor Green }
    else     { Write-Host "  FAIL  $name  $detail" -ForegroundColor Red; $script:failed += $name }
}

Write-Host "=== REWIND kernel gates ===" -ForegroundColor Cyan
Write-Host "godot  = $godot"
Write-Host "dotnet = $dotnet"
if (-not (Test-Path $godot))  { throw "missing godot_console.exe at $godot" }
if (-not (Test-Path $dotnet)) { throw "missing dotnet at $dotnet" }

# --- 1. import -----------------------------------------------------------------
if (-not $SkipImport) {
    Write-Host ""
    Write-Host "[1/6] headless import"
    & $godot --headless --path $godotDir --import *> "$logs\01_import.log"
    $importRc = $LASTEXITCODE
    $importDiag = @(Select-String -Path "$logs\01_import.log" -Pattern 'ERROR|WARNING' -Encoding UTF8)
    Step 'headless import exits 0' ($importRc -eq 0) "rc=$importRc"
    Step 'headless import prints no ERROR/WARNING' ($importDiag.Count -eq 0) "matches=$($importDiag.Count)"
} else {
    Write-Host "[1/6] headless import SKIPPED"
}

# --- 2. build ------------------------------------------------------------------
Write-Host ""
Write-Host "[2/6] dotnet build"
& $dotnet build (Join-Path $godotDir 'RewindKernel.csproj') -c Debug --nologo *> "$logs\02_build.log"
$buildRc = $LASTEXITCODE
$buildDiag = @(Select-String -Path "$logs\02_build.log" -Pattern 'error|warning' -Encoding UTF8)
Step 'C# build exits 0' ($buildRc -eq 0) "rc=$buildRc"
Step 'C# build is warning-free' ($buildDiag.Count -eq 0) "matches=$($buildDiag.Count)"

# --- 3. boot -------------------------------------------------------------------
Write-Host ""
Write-Host "[3/6] headless boot of the main scene"
& $godot --headless --path $godotDir --quit *> "$logs\03_boot.log"
$bootRc = $LASTEXITCODE
$bootDiag = @(Select-String -Path "$logs\03_boot.log" -Pattern 'ERROR|WARNING' -Encoding UTF8)
Step 'boot exits 0' ($bootRc -eq 0) "rc=$bootRc"
Step 'boot prints no ERROR/WARNING' ($bootDiag.Count -eq 0) "matches=$($bootDiag.Count)"
# The main scene is now the FRONT END (scenes/menu.tscn), which hands off to the play scene.
# The boot contract is therefore "the front end constructed and found its levels", asserted on the
# menu's own marker. Play's KERNEL_VIEW_READY is still accepted so a direct play-scene boot passes.
Step 'boot loaded the front end' (
    ($null -ne (Select-String -Path "$logs\03_boot.log" -Pattern 'MENU_READY' -Encoding UTF8)) -or
    ($null -ne (Select-String -Path "$logs\03_boot.log" -Pattern 'KERNEL_VIEW_READY' -Encoding UTF8))
) ''

# --- 4. gate -------------------------------------------------------------------
Write-Host ""
Write-Host "[4/6] headless test gate (T01-T11 + kernel invariants)"
& $godot --headless --path $godotDir $scene *> "$logs\04_gate.log"
$gateRc = $LASTEXITCODE
$summary = (Select-String -Path "$logs\04_gate.log" -Pattern '=== kernel gate summary' -Encoding UTF8).Line
$failCount = @(Select-String -Path "$logs\04_gate.log" -Pattern '^\s+FAIL' -Encoding UTF8).Count
Step 'test gate exits 0' ($gateRc -eq 0) "rc=$gateRc"
Step 'test gate reports GATE PASS' ($null -ne (Select-String -Path "$logs\04_gate.log" -Pattern 'GATE PASS' -Encoding UTF8)) "$summary"
Step 'no FAIL lines in the gate log' ($failCount -eq 0) "failLines=$failCount"

# --- 5. negative control -------------------------------------------------------
Write-Host ""
Write-Host "[5/6] negative control - the gate MUST be able to fail"
& $godot --headless --path $godotDir $scene -- --negative *> "$logs\05_negative.log"
$negRc = $LASTEXITCODE
Step 'forced failure exits non-zero' ($negRc -ne 0) "rc=$negRc"

# --- 6. cross-process determinism ----------------------------------------------
Write-Host ""
Write-Host "[6/6] cross-process determinism (two separate processes)"
& $godot --headless --path $godotDir $scene -- --suite=gdd *> "$logs\06_det_a.log"
$detRcA = $LASTEXITCODE
& $godot --headless --path $godotDir $scene -- --suite=gdd *> "$logs\06_det_b.log"
$detRcB = $LASTEXITCODE
$hashA = (Select-String -Path "$logs\06_det_a.log" -Pattern '^DETERMINISM_HASH=' -Encoding UTF8).Line
$hashB = (Select-String -Path "$logs\06_det_b.log" -Pattern '^DETERMINISM_HASH=' -Encoding UTF8).Line
Step 'determinism run A exits 0' ($detRcA -eq 0) "rc=$detRcA"
Step 'determinism run B exits 0' ($detRcB -eq 0) "rc=$detRcB"
Step 'two processes print the same final-state hash' ($hashA -eq $hashB -and $hashA) "$hashA / $hashB"
if ($hashA) { Write-Host "  hash $hashA" -ForegroundColor DarkGray }

# --- summary -------------------------------------------------------------------
Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "GATES PASS" -ForegroundColor Green
    exit 0
}
Write-Host "GATES FAIL: $($failed -join ', ')" -ForegroundColor Red
exit 1
