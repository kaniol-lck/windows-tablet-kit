# CheckOsk.ps1 —— 检测系统自带屏幕键盘 osk.exe 能否真正启动
# 用法：双击同目录下的「检查屏幕键盘.cmd」
$ErrorActionPreference = 'Continue'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class OskChk {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static List<string> Dump(uint target) {
    List<string> res = new List<string>();
    EnumWindows((h,l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target) {
        StringBuilder t = new StringBuilder(1024); GetWindowText(h, t, 1024);
        StringBuilder c = new StringBuilder(512); GetClassName(h, c, 512);
        res.Add("TOP|" + c + "|" + t);
        EnumChildWindows(h, (ch,l2) => {
          StringBuilder t2 = new StringBuilder(1024); GetWindowText(ch, t2, 1024);
          StringBuilder c2 = new StringBuilder(512); GetClassName(ch, c2, 512);
          if (t2.Length > 0) res.Add("CHILD|" + c2 + "|" + t2);
          return true;
        }, IntPtr.Zero);
      }
      return true;
    }, IntPtr.Zero);
    return res;
  }
}
'@ -Language CSharp | Out-Null

Write-Host "当前账号: $env:USERDOMAIN\$env:USERNAME" -ForegroundColor Cyan
$lua = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name EnableLUA -ErrorAction SilentlyContinue).EnableLUA
Write-Host "UAC(EnableLUA) = $lua    AppInfo 服务 = $((Get-Service AppInfo -ErrorAction SilentlyContinue).Status)" -ForegroundColor Cyan
Write-Host "正在启动 osk.exe 并检测窗口…" -ForegroundColor Cyan
$p = Start-Process 'C:\Windows\System32\osk.exe' -PassThru -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4
$dump = @()
if ($p) { $dump = [OskChk]::Dump([uint32]$p.Id) }
if ($dump.Count -eq 0) {
  Write-Host "❌ 没有检测到任何窗口（进程未起来或被拦截）" -ForegroundColor Red
  if ($p) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  exit 1
}
$dump | ForEach-Object { $f = $_ -split '\|'; Write-Host ("    [{0}] class={1}  text={2}" -f $f[0], $f[1], $f[2]) -ForegroundColor DarkGray }
if (($dump -join ' ') -match '无法启动屏幕键盘') {
  Write-Host "❌ 仍然失败：出现了「无法启动屏幕键盘。」错误框" -ForegroundColor Red
  Write-Host "   → 若已经重启过，说明该账号下仍不具备 UIAccess 条件；可改用新建的 Tablet 账号登录，或使用第三方键盘。" -ForegroundColor Yellow
  if ($p) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  exit 1
} else {
  Write-Host "✅ 成功！系统屏幕键盘已启动，窗口已留在屏幕上，可直接使用。" -ForegroundColor Green
  Write-Host "   （若没看到窗口，检查是否被其它窗口挡住，或按 Win 键后在任务栏找「屏幕键盘」）" -ForegroundColor DarkGray
  exit 0
}
