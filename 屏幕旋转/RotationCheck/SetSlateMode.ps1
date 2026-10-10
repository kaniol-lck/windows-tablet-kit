# SetSlateMode.ps1 —— 手动设置 ConvertibleSlateMode（0=平板 Slate，1=笔记本 Desktop）
# 用途：当 GPIO/ACPI 没有正确上报键盘状态时，强制切换模式来验证系统自动旋转
param([int]$Mode = -1)
$ErrorActionPreference = 'Continue'
$key = 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl'
$cur = (Get-ItemProperty $key -Name ConvertibleSlateMode -ErrorAction SilentlyContinue).ConvertibleSlateMode
Write-Host ("当前 ConvertibleSlateMode = {0}" -f $cur) -ForegroundColor Cyan
Write-Host "  0 = 平板模式 Slate（键盘不在位，系统允许自动转屏）"
Write-Host "  1 = 笔记本模式 Desktop（键盘在位，系统不自动转屏）"
if ($Mode -lt 0 -or $Mode -gt 1) {
  $ans = Read-Host "请输入要设置的值 (0 或 1)"
  $Mode = [int]$ans
}
if ($Mode -ne 0 -and $Mode -ne 1) { Write-Host "输入无效，退出。" -ForegroundColor Red; exit 1 }
try {
  Set-ItemProperty -Path $key -Name ConvertibleSlateMode -Value $Mode -Type DWord -Force -ErrorAction Stop
  $now = (Get-ItemProperty $key -Name ConvertibleSlateMode).ConvertibleSlateMode
  Write-Host ("已设置为 {0}（回读 = {1}）" -f $Mode, $now) -ForegroundColor Green
  Write-Host "提示：连接/拆下键盘时，硬件上报可能会把它改回去；本设置主要用于验证。" -ForegroundColor DarkGray
} catch {
  Write-Host ("设置失败： " + $_.Exception.Message) -ForegroundColor Red
}
Write-Host ""
Write-Host "接下来：运行「检查系统旋转」观察状态，然后把设备竖起来看是否自动转屏。" -ForegroundColor Yellow
