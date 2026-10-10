# RotationCheck.ps1 —— 实时显示「系统自带自动旋转」的关键状态
# 语义（依据 Microsoft Learn: GPI Buttons - ConvertibleSlateMode；并经实机核对）：
#   ConvertibleSlateMode = 0 → 平板模式 Slate（物理键盘不在位）→ Windows 允许自动转屏
#   ConvertibleSlateMode = 1 → 笔记本模式 Desktop/Clamshell（物理键盘在位）→ Windows 不自动转屏
param([switch]$Once)
$ErrorActionPreference = 'Continue'

[Windows.Devices.Sensors.SimpleOrientationSensor, Windows.Devices.Sensors, ContentType = WindowsRuntime] | Out-Null
$sdo = [Windows.Devices.Sensors.SimpleOrientationSensor]::GetDefault()
$acc = [Windows.Devices.Sensors.Accelerometer]::GetDefault()

function Get-State {
  $csm = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl' -Name ConvertibleSlateMode -ErrorAction SilentlyContinue).ConvertibleSlateMode
  $en  = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AutoRotation' -Name Enable -ErrorAction SilentlyContinue).Enable
  $sl  = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AutoRotation' -Name SlateEnable -ErrorAction SilentlyContinue).SlateEnable
  $o   = if ($sdo) { $sdo.GetCurrentOrientation().ToString() } else { 'n/a（没有方向传感器）' }
  $r   = if ($acc) { $acc.GetCurrentReading() } else { $null }
  $kbd = (Get-PnpDevice -Class Keyboard -Status OK -ErrorAction SilentlyContinue |
          Where-Object { $_.InstanceId -match 'VID_12D1|CONVERTEDDEVICE' } |
          ForEach-Object { $_.FriendlyName }) -join ' / '
  [pscustomobject]@{ CSM = $csm; Enable = $en; SlateEnable = $sl; Orient = $o; X = $r.AccelerationX; Y = $r.AccelerationY; Z = $r.AccelerationZ; Kbd = $kbd }
}

function Show($s) {
  Clear-Host
  Write-Host "============== 系统自带自动旋转 自检 ==============" -ForegroundColor Cyan
  Write-Host ("  时间: " + (Get-Date -Format 'HH:mm:ss'))
  Write-Host ""
  if ($s.CSM -eq 0) {
    Write-Host "  ConvertibleSlateMode = 0  → 【平板模式 Slate】" -ForegroundColor Green
    Write-Host "  → 物理键盘不在位，Windows 允许自动转屏：竖起来试试" -ForegroundColor Green
  } elseif ($s.CSM -eq 1) {
    Write-Host "  ConvertibleSlateMode = 1  → 【笔记本模式 Desktop/Clamshell】" -ForegroundColor Yellow
    Write-Host "  → Windows 在这种模式下【不会】自动转屏" -ForegroundColor Yellow
    Write-Host "     请拆下可拆卸键盘，或运行「设置平板-笔记本模式」强制设为 0" -ForegroundColor Yellow
  } else {
    Write-Host ("  ConvertibleSlateMode = {0}  (读不到)" -f $s.CSM) -ForegroundColor Red
  }
  Write-Host ""
  Write-Host ("  AutoRotation\Enable      = {0}   (0=旋转锁定开  1=允许)" -f $s.Enable)
  Write-Host ("  AutoRotation\SlateEnable = {0}" -f $s.SlateEnable)
  Write-Host ("  检测到的键盘类设备       = {0}" -f $(if ($s.Kbd) { $s.Kbd } else { '（无 VID_12D1/CONVERTEDDEVICE 键盘）' }))
  Write-Host ""
  Write-Host ("  方向传感器读数 : {0}" -f $s.Orient) -ForegroundColor White
  Write-Host ("  加速度计       : X={0,7:N3}  Y={1,7:N3}  Z={2,7:N3}" -f $s.X, $s.Y, $s.Z)
  Write-Host ""
  Write-Host "  转动设备时上面的读数应跟着变（NotRotated/90/180/270/Faceup）" -ForegroundColor DarkGray
  Write-Host "  按 Ctrl+C 退出" -ForegroundColor DarkGray
  Write-Host "===================================================" -ForegroundColor Cyan
}

if ($Once) { Show (Get-State); exit 0 }
while ($true) { Show (Get-State); Start-Sleep -Milliseconds 800 }
