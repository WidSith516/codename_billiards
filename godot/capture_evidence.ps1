#!/usr/bin/env pwsh
<#
  M2 evidence capture. Renders each UI state of the presentation layer with the verified toolchain
  recipe (godot_console.exe + --rendering-driver d3d12, required because Vulkan is broken here) and
  then proves every PNG is non-blank with res://src/ui/verify_png.gd.

  Every claim in godot/M2_REPORT.md about a screenshot comes from this script's output.
#>
[CmdletBinding()]
param(
    [string]$OutDir = "godot/evidence",
    [switch]$SkipCapture
)

$ErrorActionPreference = 'Stop'
$root  = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$godot = Join-Path $root 'tools\godot\godot_console.exe'
$abs   = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $abs | Out-Null

$states = @(
    @{ Name = 'm2_1_aim';             Args = @('--state=aim');            Frames = 8 },
    @{ Name = 'm2_2_strike_trail';    Args = @('--state=strike');         Frames = 8 },
    @{ Name = 'm2_3_rewind';          Args = @('--state=rewind');         Frames = 8 },
    @{ Name = 'm2_4_rewind_contact';  Args = @('--state=rewind-contact'); Frames = 8 },
    @{ Name = 'm2_5_scrub';           Args = @('--state=scrub');          Frames = 8 },
    @{ Name = 'm2_6_debug_overlay';   Args = @('--state=debug');          Frames = 8 },
    @{ Name = 'm2_7_win';             Args = @('--state=win','--win-mode=forced'); Frames = 8 }
)

$results = @()
foreach ($s in $states) {
    $png = Join-Path $abs ("$($s.Name).png")
    if (Test-Path $png) { Remove-Item $png -Force }
    $log = Join-Path $root ("godot/.tmp/capture_$($s.Name).log")
    $args = @('--path', (Join-Path $root 'godot'), '--resolution', '1040x560', '--rendering-driver', 'd3d12',
              'res://scenes/play.tscn', '--') + $s.Args + @("--out=$($png -replace '\\','/')",
              "--min-frames=$($s.Frames)", "--capture-frames=$($s.Frames)")
    & $godot @args *> $log
    $rc = $LASTEXITCODE
    $cap = (Select-String -Path $log -Pattern 'CAPTURE file=' -Encoding UTF8).Line
    $st  = (Select-String -Path $log -Pattern '^\[play\] state=' -Encoding UTF8).Line
    Write-Host "=== $($s.Name) rc=$rc ===" -ForegroundColor Cyan
    Write-Host "  $st"
    Write-Host "  $cap"
    $results += [pscustomobject]@{ State = $s.Name; Rc = $rc; Png = $png }
}

Write-Host ""
Write-Host "=== non-blank verification ===" -ForegroundColor Cyan
$fail = 0
foreach ($r in $results) {
    if (-not (Test-Path $r.Png)) { Write-Host "  MISSING $($r.Png)" -ForegroundColor Red; $fail++; continue }
    $out = & $godot --headless --path (Join-Path $root 'godot') --script res://src/ui/verify_png.gd -- ($r.Png -replace '\\','/') 2>&1
    $vrc = $LASTEXITCODE
    $verdict = ($out | Select-String -Pattern 'VERDICT=').Line
    $distinct = ($out | Select-String -Pattern 'distinct_rgb=').Line
    $content = ($out | Select-String -Pattern 'content_px=').Line
    $quad = ($out | Select-String -Pattern 'content_by_quadrant').Line
    $bg = ($out | Select-String -Pattern 'background_mode_color=').Line
    if ($vrc -ne 0) { $fail++ }
    Write-Host "  $($r.State)  rc=$vrc"
    Write-Host "     $distinct"
    Write-Host "     $bg"
    Write-Host "     $content"
    Write-Host "     $quad"
    Write-Host "     $verdict"
}

Write-Host ""
if ($fail -eq 0) { Write-Host "ALL CAPTURES NON-BLANK ($($results.Count)/$($results.Count))" -ForegroundColor Green; exit 0 }
Write-Host "CAPTURE VERIFICATION FAILED ($fail)" -ForegroundColor Red
exit 1
