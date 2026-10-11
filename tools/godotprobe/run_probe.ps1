#!/usr/bin/env pwsh
<#
  run_probe.ps1 - the no-human verification loop for the Godot toolchain in THIS workspace.

  One command, real process exit codes, no human at the keyboard:
    1. import the project headlessly
    2. build the C# assembly
    3. run the headless smoke gate (PASS/FAIL, exit code)
    4. run a NON-headless render + screenshot, then PROVE the PNG is not blank
    5. optionally export a Windows release build and self-test the exported binary

  Usage:
    powershell -File tools/godotprobe/run_probe.ps1              # steps 1-4
    powershell -File tools/godotprobe/run_probe.ps1 -Export      # steps 1-5
    powershell -File tools/godotprobe/run_probe.ps1 -Negative    # also assert the gate CAN fail

  WHY every path here is spelled out instead of relying on PATH: see REPORT.md section 0.
  Two environment traps make the "obvious" invocation silently wrong:
    * godot.exe / dotnet.exe under D:\saint_ruin\tools\ run at Low Mandatory Integrity
      Level, so every write they attempt is silently dropped (mkdir returns OK, creates
      nothing). Only the workspace-local copies run at High IL.
    * godot.exe is a GUI-subsystem binary, so PowerShell will not wait for it and never
      sets $LASTEXITCODE. godot_console.exe (CONSOLE subsystem) is the scriptable one.

  NOTE: ASCII-only on purpose. Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so
  non-ASCII authors' punctuation in this file would be corrupted on load.
#>
[CmdletBinding()]
param(
    [switch]$Export,
    [switch]$Negative
)

$ErrorActionPreference = 'Stop'
$probe   = $PSScriptRoot
$wsroot  = (Resolve-Path (Join-Path $probe '..\..')).Path
$godot   = Join-Path $wsroot 'tools\godot\godot_console.exe'
$dotnet  = 'C:\Program Files\dotnet\dotnet.exe'      # High IL; the saint_ruin one is Low IL
$evid    = Join-Path $probe 'evidence'
$tmp     = Join-Path $probe '.tmp'
$png     = Join-Path $evid 'render_probe.png'

New-Item -ItemType Directory -Force -Path $evid, "$tmp\tmp", "$tmp\clihome", "$probe\build" | Out-Null

# --- environment: MSBuild and the dotnet CLI cannot use the default Temp/CLI home here ---
$env:TMP = "$tmp\tmp"; $env:TEMP = "$tmp\tmp"; $env:TMPDIR = "$tmp\tmp"
$env:DOTNET_CLI_HOME = "$tmp\clihome"
Remove-Item Env:\DOTNET_ROOT -ErrorAction SilentlyContinue
$env:PATH = "C:\Program Files\dotnet;" + (($env:PATH -split ';' | Where-Object { $_ -notmatch 'saint_ruin\\tools\\dotnet' }) -join ';')

$failed = @()
function Step($name, $ok, $detail) {
    if ($ok) { Write-Host "  PASS  $name  $detail" -ForegroundColor Green }
    else     { Write-Host "  FAIL  $name  $detail" -ForegroundColor Red; $script:failed += $name }
}

Write-Host "=== godotprobe loop ===" -ForegroundColor Cyan
Write-Host "godot  = $godot"
Write-Host "dotnet = $dotnet"
if (-not (Test-Path $godot))  { throw "missing godot_console.exe at $godot" }
if (-not (Test-Path $dotnet)) { throw "missing dotnet at $dotnet" }

# --- 1. import -----------------------------------------------------------------
Write-Host ""
Write-Host "[1/5] headless import"
& $godot --headless --path $probe --import *> "$evid\01_import.log"
$importRc = $LASTEXITCODE
$importErrors = @(Select-String -Path "$evid\01_import.log" -Pattern 'ERROR|WARNING' -Encoding UTF8)
Step 'headless import exits 0' ($importRc -eq 0) "rc=$importRc"
Step 'headless import prints no ERROR/WARNING' ($importErrors.Count -eq 0) "matches=$($importErrors.Count)"

# --- 2. build ------------------------------------------------------------------
Write-Host ""
Write-Host "[2/5] dotnet build"
& $dotnet build "$probe\GodotProbe.csproj" -c Debug --nologo *> "$evid\03_dotnet_build.log"
$buildRc = $LASTEXITCODE
$buildErrors = @(Select-String -Path "$evid\03_dotnet_build.log" -Pattern 'error|warning' -Encoding UTF8)
Step 'C# build exits 0' ($buildRc -eq 0) "rc=$buildRc"
Step 'C# build is warning-free' ($buildErrors.Count -eq 0) "matches=$($buildErrors.Count)"

# --- 3. headless smoke gate ----------------------------------------------------
Write-Host ""
Write-Host "[3/5] headless C# smoke gate"
& $godot --headless --path $probe res://tests/probe_smoke.tscn *> "$evid\22_smoke_pass.log"
$smokeRc = $LASTEXITCODE
$passLine = (Select-String -Path "$evid\22_smoke_pass.log" -Pattern '=== smoke summary' -Encoding UTF8).Line
Step 'smoke gate exits 0' ($smokeRc -eq 0) "rc=$smokeRc"
Step 'smoke gate reports PASS' ($null -ne (Select-String -Path "$evid\22_smoke_pass.log" -Pattern 'SMOKE PASS' -Encoding UTF8)) "$passLine"

if ($Negative) {
    Write-Host ""
    Write-Host "[3b] negative control - the gate MUST be able to fail"
    & $godot --headless --path $probe res://tests/probe_smoke.tscn -- --selftest-fail *> "$evid\23_smoke_fail_negative_control.log"
    $negRc = $LASTEXITCODE
    Step 'forced failure exits non-zero' ($negRc -ne 0) "rc=$negRc"
}

# --- 4. render + screenshot + prove non-blank ---------------------------------
Write-Host ""
Write-Host "[4/5] non-headless render + screenshot"
Remove-Item $png -Force -ErrorAction SilentlyContinue
& $godot --path $probe --resolution 1040x560 --rendering-driver d3d12 res://scenes/render_probe.tscn -- "--out=$png" *> "$evid\05_render_d3d12.log"
$renderRc = $LASTEXITCODE
Step 'render probe exits 0' ($renderRc -eq 0) "rc=$renderRc"

& $godot --headless --path $probe --script res://diag/verify_png.gd -- $png *> "$evid\06_verify_png.log"
$verifyRc = $LASTEXITCODE
$verdict = (Select-String -Path "$evid\06_verify_png.log" -Pattern 'VERDICT' -Encoding UTF8).Line
Step 'PNG exists and is non-blank' ($verifyRc -eq 0 -and (Test-Path $png)) $verdict

# --- 5. export + run the exported binary (optional) ---------------------------
if ($Export) {
    Write-Host ""
    Write-Host "[5/5] export Windows release + self-test the exported binary"
    & $godot --headless --path $probe --export-release 'Windows Desktop' "$probe\build\GodotProbe.exe" *> "$evid\31_export_windows.log"
    $exportRc = $LASTEXITCODE
    Step 'export exits 0' ($exportRc -eq 0) "rc=$exportRc"
    Step 'export ran dotnet publish' ($null -ne (Select-String -Path "$evid\31_export_windows.log" -Pattern 'dotnet_publish_project' -Encoding UTF8)) ''
    $exeOk = (Test-Path "$probe\build\GodotProbe.exe") -and (Test-Path "$probe\build\data_GodotProbe_windows_x86_64\GodotProbe.dll")
    Step 'exported build contains the C# assembly' $exeOk ''

    # An exported build cannot take a scene path, so drive it through user args.
    $pr = Start-Process -FilePath "$probe\build\GodotProbe.exe" -ArgumentList '--headless','--','--selftest' -WorkingDirectory "$probe\build" -Wait -PassThru -NoNewWindow -RedirectStandardOutput "$evid\32_exported_exe_smoke.log" -RedirectStandardError "$evid\32_exported_exe_smoke.err"
    Step 'exported binary smoke gate exits 0' ($pr.ExitCode -eq 0) "rc=$($pr.ExitCode)"
}

# --- summary -------------------------------------------------------------------
Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "PROBE PASS" -ForegroundColor Green
    exit 0
}
Write-Host "PROBE FAIL: $($failed -join ', ')" -ForegroundColor Red
exit 1
