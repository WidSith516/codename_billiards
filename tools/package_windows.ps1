# Package the Windows deliverable into REWIND_demo_win64.zip.
#
# WHY THIS SCRIPT EXISTS: the previous zip was assembled by hand, and when it was rebuilt after the touch
# work the result was a 40 MB archive instead of 75 MB -- it contained only REWIND.exe and REWIND.pck,
# because those are the obvious files and they are NOT sufficient. The exported Mono build keeps its
# runtime and assemblies in a sibling directory (data_RewindKernel_windows_x86_64/, 187 files, ~80 MB).
# An archive without it extracts perfectly and then dies on launch:
#
#     ERROR: .NET: Assemblies not found
#     at: initialize (modules/mono/mono_gd/gd_mono.cpp:650)
#
# Nothing about the file list reveals this -- every file it DOES contain is correct. So the smoke test at
# the end is the point of the script: it runs the extracted build and asserts on the boot markers. A
# package that was never launched is not a verified package.
#
# The alternative to all this is shipping a PCK-only build with the .NET runtime statically linked, which
# this preset does not do; the export is a directory layout, and the whole layout has to travel.
param(
    [string]$Dist = "dist",
    [string]$OutDir = "REWIND_demo_win64",
    [string]$Zip = "REWIND_demo_win64.zip",
    [switch]$NoSmoke
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath ".").Path

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# ---------------------------------------------------------------- collect
$exe = Join-Path $Dist "REWIND.exe"
if (-not (Test-Path $exe)) { throw "no $exe -- run the Windows export first" }

# The data directory is named after the assembly, so it is discovered rather than hard-coded: a project
# rename would otherwise silently produce a package without a runtime again.
$dataDirs = Get-ChildItem $Dist -Directory | Where-Object { $_.Name -like "data_*" }
if ($dataDirs.Count -eq 0) {
    throw "no data_* directory in $Dist. The .NET runtime lives there; a package without it fails at launch with 'Assemblies not found'."
}
Step "data directory: $($dataDirs.Name -join ', ')"
foreach ($d in $dataDirs) {
    $n = (Get-ChildItem $d.FullName -Recurse -File).Count
    Write-Host "    $($d.Name): $n files"
    if ($n -lt 50) { throw "$($d.Name) has only $n files; the runtime looks incomplete" }
}

# ---------------------------------------------------------------- stage
Step "staging into $OutDir"
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

Copy-Item $exe (Join-Path $OutDir "REWIND.exe")
Copy-Item (Join-Path $Dist "REWIND.pck") (Join-Path $OutDir "REWIND.pck")
foreach ($d in $dataDirs) {
    Copy-Item $d.FullName (Join-Path $OutDir $d.Name) -Recurse
}

# The launch scripts and the guide travel with the build: without the d3d12 launcher, a machine whose
# default renderer misbehaves has no documented way in, and the guide is the only place the controls and
# the CLI flags are written down.
#
# MATCHED BY EXTENSION, NOT BY NAME. The files are Chinese-named ("启动游戏.bat", "操作指南.md"), and
# PowerShell reads a .ps1 without a BOM as ANSI on Windows PowerShell -- so a Chinese literal decodes to
# mojibake, Test-Path reports the file missing, and the package ships without its launchers or guide. The
# first run of this script did exactly that AND the wildcard-free .md list was still wrong afterwards: a
# literal "-Filter '操作指南.md'" silently matched nothing. Extensions cannot be mis-decoded.
$travel = @()
foreach ($pat in @("*.bat", "*.md")) {
    $travel += Get-ChildItem $Dist -Filter $pat -File -ErrorAction SilentlyContinue
}
# Only the top-level docs; the wildcard above would otherwise reach into subdirectories.
$travel = $travel | Where-Object { $_.DirectoryName -eq (Resolve-Path $Dist).Path } | Sort-Object Name -Unique
if ($travel.Count -eq 0) {
    throw "found no launcher scripts or guides in $Dist -- the package would ship without them"
}
foreach ($f in $travel) {
    Copy-Item $f.FullName (Join-Path $OutDir $f.Name)
    Write-Host "    + $($f.Name)"
}

$sum = (Get-ChildItem $OutDir -Recurse -File | Measure-Object -Property Length -Sum)
Step "staged $($sum.Count) files, $('{0:N0}' -f $sum.Sum) bytes"

# ---------------------------------------------------------------- smoke test
# Run the STAGED build, from the staging directory, before zipping. Whatever is verified here is exactly
# what goes into the archive.
$smokeOk = $true
if (-not $NoSmoke) {
    Step "smoke test (launch the staged build and capture)"
    $png = Join-Path $root ".tmp_pkg_smoke.png"
    $outLog = Join-Path $root ".tmp_pkg_smoke.out.txt"
    $errLog = Join-Path $root ".tmp_pkg_smoke.err.txt"
    Remove-Item $png, $outLog, $errLog -ErrorAction SilentlyContinue

    $pngPosix = ($png -replace '\\', '/')
    $argList = @("--rendering-driver", "d3d12", "--", "--out=$pngPosix", "--capture-frames=30",
        "--level=corridor", "--state=strike", "--touch-controls")
    $p = Start-Process (Join-Path $OutDir "REWIND.exe") -ArgumentList $argList -PassThru `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog -WorkingDirectory (Resolve-Path $OutDir).Path
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $png)) { Start-Sleep -Milliseconds 250 }
    Start-Sleep -Milliseconds 700
    $p.Refresh()
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

    $log = Get-Content $outLog -ErrorAction SilentlyContinue
    $errs = Get-Content $errLog -ErrorAction SilentlyContinue

    if ($errs | Where-Object { $_ -match 'Assemblies not found' }) {
        Write-Host "  FAIL: the staged build cannot find its assemblies -- the package is broken" -ForegroundColor Red
        $smokeOk = $false
    } elseif (-not ($log | Where-Object { $_ -match 'KERNEL_VIEW_READY|MENU_READY' })) {
        Write-Host "  FAIL: the staged build never reached a scene" -ForegroundColor Red
        $log | Select-Object -First 10 | ForEach-Object { Write-Host "    $_" }
        $smokeOk = $false
    } elseif (-not (Test-Path $png)) {
        Write-Host "  FAIL: the staged build wrote no capture" -ForegroundColor Red
        $smokeOk = $false
    } else {
        Write-Host "  OK: launched, reached a scene, wrote $((Get-Item $png).Length) bytes" -ForegroundColor Green
        $log | Where-Object { $_ -match 'touch controls|\[play\] level=' } | ForEach-Object { Write-Host "    $_" }
    }

    Remove-Item $png, $outLog, $errLog -ErrorAction SilentlyContinue
}

if (-not $smokeOk) {
    throw "the staged package failed its smoke test; not producing a zip that would ship broken"
}

# ---------------------------------------------------------------- zip
Step "zipping to $Zip"
Remove-Item $Zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $OutDir -DestinationPath $Zip -Force

# Assert the archive actually contains the runtime, by reading it back rather than trusting the copy.
#
# Separators are NORMALISED first: Compress-Archive writes entry names with BACKSLASHES
# ("REWIND_demo_win64\data_...\clrjit.dll"), so a "*/..." pattern matches nothing and the first version
# of this check reported an empty, broken archive that was in fact complete. The zip format itself
# mandates forward slashes, so normalising is the correct reading of either form.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $Zip).Path)
try {
    $names = $z.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }
    $hasExe = [bool]($names | Where-Object { $_ -like "*/REWIND.exe" })
    $hasPck = [bool]($names | Where-Object { $_ -like "*/REWIND.pck" })
    $runtime = ($names | Where-Object { $_ -like "*/data_*/System.Private.CoreLib.dll" }).Count
    $dlls = ($names | Where-Object { $_ -like "*/data_*/*.dll" }).Count
    Write-Host "  entries=$($names.Count) exe=$hasExe pck=$hasPck runtime_corelib=$runtime dlls=$dlls"
    if (-not ($hasExe -and $hasPck) -or $runtime -lt 1 -or $dlls -lt 100) {
        throw "the zip is incomplete (exe=$hasExe pck=$hasPck corelib=$runtime dlls=$dlls) -- this is the 'Assemblies not found' failure"
    }
} finally {
    $z.Dispose()
}

Step "DONE  $Zip  ($('{0:N0}' -f (Get-Item $Zip).Length) bytes)"
