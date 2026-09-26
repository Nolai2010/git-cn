# Launch the WPF build, bring it to the foreground, and save a real window screenshot.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
# 不加这句，125% 缩放下 GetWindowRect 返回的是虚拟化坐标，截图会被裁掉右边和下边
[void][Win32]::SetProcessDPIAware()

$exe = 'D:\Agents\Workfile\GitCN\wpf\src\GitCn.App\bin\Debug\net10.0-windows\git-cn-gui.exe'
$out = 'D:\Agents\Workfile\GitCN\docs\screenshot.png'
$dir = 'D:\GitcnDemo'

$p = Start-Process -FilePath $exe -ArgumentList @('--dir', $dir) -PassThru
Start-Sleep -Seconds 6
$hwnd = (Get-Process -Id $p.Id).MainWindowHandle
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Move {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
# 窗口比屏幕可用宽度还宽时，右边会拍到屏幕外，先挪到左上角
[void][Move]::MoveWindow($hwnd, 0, 0, 1150, 760, $true)
[void][Move]::SetForegroundWindow($hwnd)
Start-Sleep -Seconds 2

$rect = New-Object Win32+RECT
[void][Win32]::GetWindowRect($hwnd, [ref]$rect)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Dpi {
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
}
"@
$d = [uint32][Dpi]::GetDpiForWindow($hwnd)
if ($d -lt 96) { $d = 96 }
$scale = $d / 96.0
$w = [int](($rect.Right - $rect.Left) * $scale) - 16
$h = [int](($rect.Bottom - $rect.Top) * $scale) - 16
Write-Host "dpi=$d scale=$scale window=${w}x${h} at $($rect.Left),$($rect.Top)"

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Stop-Process -Id $p.Id -Force
Write-Host "saved $out"
