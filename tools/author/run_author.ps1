#!/usr/bin/env pwsh
<#
  run_author.ps1 - one command to build and run the tolerance-aware level authoring tool.

  Same environment traps as godot/run_gates.ps1 (see tools/godotprobe/REPORT.md):
  MSBuild cannot use the machine temp directory here, so TMP/TEMP/TMPDIR/DOTNET_CLI_HOME are
  redirected into tools/author/.tmp, and the `saint_ruin` dotnet copy is kept off PATH.

  Usage (the whole command line goes into ONE -Command string):
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\author\run_author.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\author\run_author.ps1 -Command "verify"
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\author\run_author.ps1 -Command "probe --pocket=5 --approach=45 --dball=110 --lback=35"
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\author\run_author.ps1 -Command "scan"
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\author\run_author.ps1 -Command "full"
#>
[CmdletBinding()]
param(
    [string] $Command = 'verify',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$here   = $PSScriptRoot
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$tmp    = Join-Path $here '.tmp'

New-Item -ItemType Directory -Force -Path $tmp, "$tmp\tmp", "$tmp\clihome" | Out-Null
$env:TMP = "$tmp\tmp"; $env:TEMP = "$tmp\tmp"; $env:TMPDIR = "$tmp\tmp"
$env:DOTNET_CLI_HOME = "$tmp\clihome"
Remove-Item Env:\DOTNET_ROOT -ErrorAction SilentlyContinue
$env:PATH = "C:\Program Files\dotnet;" + (($env:PATH -split ';' | Where-Object { $_ -notmatch 'saint_ruin\\tools\\dotnet' }) -join ';')
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (-not (Test-Path $dotnet)) { throw "missing dotnet at $dotnet" }

if (-not $NoBuild) {
    & $dotnet build (Join-Path $here 'AuthorTool.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "author tool build failed (rc=$LASTEXITCODE)" }
}

$argv = @('run', '--project', (Join-Path $here 'AuthorTool.csproj'), '-c', 'Release', '--no-build', '--')
$argv += ($Command -split '\s+' | Where-Object { $_ -ne '' })
& $dotnet @argv
exit $LASTEXITCODE
