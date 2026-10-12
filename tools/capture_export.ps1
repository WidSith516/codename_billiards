# Capture the EXPORTED build's window, to verify the warp reaches the shipped game.
#
# The dev-build captures prove the rendering code; they cannot prove the exported artifact, because
# inside a PCK `res://` is read-only so `--out=` cannot work there. A window grab is the only way to see
# what the shipped exe actually puts on screen.
param(
    [string]$Extra = "",
    [string]$Out = "godot\evidence\export_check.png"
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

$args = @("--rendering-driver", "d3d12", "--")
if ($Extra -ne "") { $args += $Extra -split ' ' }

$p = Start-Process "dist\REWIND.exe" -ArgumentList $args -PassThru
Start-Sleep -Seconds 9
$p.Refresh()

if ($p.HasExited) {
    Write-Output "EXITED early: code=$($p.ExitCode)"
    exit 1
}

[Win]::ShowWindow($p.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
[Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 900

$r = New-Object Win+RECT
[Win]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.R - $r.L
$h = $r.B - $r.T
Write-Output "window ${w}x${h} at ($($r.L),$($r.T))"

if ($w -le 0 -or $h -le 0) { Write-Output "BAD RECT"; Stop-Process -Id $p.Id -Force; exit 1 }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
$bmp.Save((Resolve-Path -LiteralPath ".").Path + "\" + $Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Output "saved $Out"
Stop-Process -Id $p.Id -Force
