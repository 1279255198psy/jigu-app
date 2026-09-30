# FILE: jigu-app/tests/shot-builtin.ps1
# Launch the built-in native UI and capture its window with PrintWindow (no focus stealing),
# so the capture cannot be disturbed by foreground changes.
param(
  [string]$ExePath = "",
  [string]$OutPath = "",
  [int]$WaitSeconds = 9,
  [string]$Query = ""
)

Add-Type -AssemblyName System.Drawing
$sig = @"
using System;
using System.Runtime.InteropServices;
public class PW {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr c, string cls, string title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@
if (-not ("PW" -as [type])) { Add-Type -TypeDefinition $sig }

# Defaults are resolved here rather than in the param block: $PSScriptRoot is only
# guaranteed to be populated in the body. Hardcoding this machine's repo path meant the
# script silently pointed at a directory that no longer exists.
$repoRoot = Split-Path $PSScriptRoot -Parent
$appName  = [string][char]0x7A3D + [string][char]0x53E4
if ($OutPath -eq "") { $OutPath = Join-Path $PSScriptRoot "shot-builtin.png" }
if ($ExePath -eq "") {
  $ExePath = Join-Path (Join-Path $repoRoot "dist") (Join-Path $appName ($appName + ".exe"))
}

$argList = @('--built-in')
if ($Query -ne "") { $argList += $Query }
$p = Start-Process -FilePath $ExePath -ArgumentList $argList -WorkingDirectory (Split-Path $ExePath) -PassThru
Write-Host "pid=$($p.Id) query='$Query'"
Start-Sleep -Seconds $WaitSeconds
$p.Refresh()
if ($p.HasExited) { Write-Host "EXITED before capture code=$($p.ExitCode)"; exit 1 }

$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { Write-Host "no hwnd"; $p.Kill(); exit 2 }
Write-Host "title='$($p.MainWindowTitle)' hwnd=$h"

$r = New-Object PW+RECT
[void][PW]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
Write-Host "rect=$($r.Left),$($r.Top) ${w}x${ht}"

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [PW]::PrintWindow($h, $hdc, 0x00000002)   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()
if (-not $ok) { Write-Host "PrintWindow returned false" }
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved: $OutPath ($([math]::Round((Get-Item $OutPath).Length/1KB,1)) KB)"
$p.Refresh()
if (-not $p.HasExited) { $p.Kill() }
