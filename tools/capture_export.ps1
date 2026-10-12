# Capture the EXPORTED build by letting IT write the PNG, not by grabbing the screen.
#
# WHY THIS REPLACED THE WINDOW-GRAB VERSION: a desktop grab proved nothing. Two captures of the SAME
# state differed on 95.2% of pixels, because the compositor placed the window at (441,163) one run and
# (100,100) the next and SetWindowPos did not take. The noise floor swamped the effect entirely.
#
# The export CAN screenshot itself: --out= is passed straight to Image.SavePng, which takes an OS path,
# so an absolute path works from inside the packed build. (Only a res:// path fails, because a PCK is
# read-only -- an earlier round generalised that one failure into "the export cannot be captured", which
# was wrong and cost real time.)
#
# It also asserts on the play-scene boot markers. The export always boots menu.tscn, so without
# --level=<id> it sits on the title screen, and an earlier "the export shows the rail impact" conclusion
# was in fact a photograph of the menu.
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Extra = "",
    [string]$Exe = "dist\REWIND.exe",
    [int]$WaitSeconds = 14
)

$root = (Resolve-Path -LiteralPath ".").Path
$outPosix = ($root -replace '\\', '/') + "/$Name.png"
$outWin = Join-Path $root "$Name.png"
Remove-Item $outWin -ErrorAction SilentlyContinue

$stdout = "cap_$Name.out.txt"
$stderr = "cap_$Name.err.txt"

$argList = @("--rendering-driver", "d3d12", "--", "--out=$outPosix", "--capture-frames=30")
if ($Extra -ne "") { $argList += $Extra -split ' ' }

$p = Start-Process $Exe -ArgumentList $argList -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr

$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline -and -not (Test-Path $outWin)) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Milliseconds 700
$p.Refresh()
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

$log = Get-Content $stdout -ErrorAction SilentlyContinue

if (-not ($log | Where-Object { $_ -match 'KERNEL_VIEW_READY' })) {
    Write-Output "FAIL [$Name]: never reached the play scene (would have been a menu shot)"
    $log | Select-Object -First 12
    exit 2
}

if (-not (Test-Path $outWin)) {
    Write-Output "FAIL [$Name]: no PNG written; the capture never ran"
    $log | Select-Object -Last 12
    Get-Content $stderr -ErrorAction SilentlyContinue | Select-Object -First 8
    exit 3
}

# Echo the kernel's own view of the frame: these are the numbers a claim about this capture rests on.
$state = $log | Where-Object { $_ -match 'rail state|\[play\] level=' }
Write-Output "OK [$Name] -> $Name.png"
$state | ForEach-Object { Write-Output "    $_" }
