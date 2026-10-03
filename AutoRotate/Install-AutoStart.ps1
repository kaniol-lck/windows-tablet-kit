#Requires -Version 5.1
<#
  Install-AutoStart.ps1 —— 安装 / 更新「登录后自动启动 AutoRotate」，并把守护进程立刻跑起来
  一般由 安装-开机自动旋转.cmd 调用（需要管理员权限：注册计划任务）。

  踩过的坑，本脚本逐个处理：
    1) 任务计划默认「电池供电时不启动 / 一切到电池就停止」（schtasks /create 的默认值）：
       平板用电池时 schtasks /run 会失败，而旧脚本没检查返回码，照样打印 OK —— 这里显式允许电池运行，
       并且启动后必须看到日志增长才算成功。
    2) 任务计划默认 72 小时执行上限（ExecutionTimeLimit=PT72H）：长跑的守护会被杀掉 —— 这里设为无限制。
    3) 更新 AutoRotate.ps1 后旧实例仍在跑（单实例互斥会挡住新实例）—— 这里先杀旧实例，再启动。
    4) 启动文件夹里的 AutoRotate.vbs 不受电池策略影响，作为兜底一并检查 / 修正。
#>
param(
  [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$TaskName = 'AutoRotate'
$Root     = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Script   = Join-Path $Root 'AutoRotate.ps1'
$LogPath  = Join-Path $Root 'AutoRotate.log'
$VbsPath  = Join-Path ([Environment]::GetFolderPath('Startup')) 'AutoRotate.vbs'

function Say($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }

if (-not (Test-Path -LiteralPath $Script)) { Say "找不到 AutoRotate.ps1：$Script" 'Red'; exit 1 }

# ---------------------------------------------------------------- 1. 停掉正在运行的实例
Say '1) 停止正在运行的 AutoRotate 实例…'
$killed = 0
try {
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction Stop |
    Where-Object { $_.CommandLine -and $_.CommandLine -like '*AutoRotate.ps1*' -and $_.ProcessId -ne $PID } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; $script:killed++ }
} catch { Say "   （无法枚举进程：$($_.Exception.Message)）" 'Yellow' }
Say ("   已停止 {0} 个实例" -f $killed)
Start-Sleep -Milliseconds 800

# ---------------------------------------------------------------- 2. 注册计划任务（电池友好、无时间上限）
Say ("2) 注册计划任务 {0}（登录时启动 / 允许电池供电 / 无 72 小时上限）…" -f $TaskName)
$taskOk = $false
try {
  $action  = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}"' -f $Script)
  $trigger = New-ScheduledTaskTrigger -AtLogOn -User ("{0}\{1}" -f $env:USERDOMAIN, $env:USERNAME)
  $set     = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds 0)
  $princ   = New-ScheduledTaskPrincipal -UserId ("{0}\{1}" -f $env:USERDOMAIN, $env:USERNAME) -RunLevel Highest -LogonType Interactive
  Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $set -Principal $princ -Force | Out-Null
  $taskOk = $true
  Say '   任务已注册' 'Green'
} catch {
  Say "   注册失败：$($_.Exception.Message)" 'Red'
  Say '   （本次仍会启动；下次开机靠下面的启动文件夹兜底）' 'Yellow'
}

# ---------------------------------------------------------------- 3. 启动文件夹兜底
Say '3) 检查启动文件夹兜底项…'
$vbs = @"
' AutoRotate autostart (hidden daemon)
Set sh = CreateObject("WScript.Shell")
sh.Run "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""$Script""", 0, False
"@
try {
  if (Test-Path -LiteralPath $VbsPath) {
    $cur = Get-Content -LiteralPath $VbsPath -Raw -Encoding Default
    if ($cur -notlike "*$Script*") {
      Set-Content -LiteralPath $VbsPath -Value $vbs -Encoding Default
      Say "   已修正指向：$VbsPath" 'Yellow'
    } else { Say "   已存在且指向正确：$VbsPath" }
  } else {
    Set-Content -LiteralPath $VbsPath -Value $vbs -Encoding Default
    Say "   已创建：$VbsPath" 'Green'
  }
} catch { Say "   写入启动文件夹失败：$($_.Exception.Message)" 'Yellow' }

# ---------------------------------------------------------------- 4. 立刻启动并用日志验证
if ($NoStart) { Say '4) 指定了 -NoStart，跳过启动'; exit 0 }
Say '4) 立即启动守护进程并验证（看日志是否增长）…'
$before = if (Test-Path -LiteralPath $LogPath) { (Get-Item -LiteralPath $LogPath).Length } else { 0 }
function Test-Grew { (Test-Path -LiteralPath $LogPath) -and ((Get-Item -LiteralPath $LogPath).Length -gt $script:before) }

$started = $false
if ($taskOk) {
  try { Start-ScheduledTask -TaskName $TaskName } catch { Say "   Start-ScheduledTask 失败：$($_.Exception.Message)" 'Yellow' }
  for ($i = 0; $i -lt 12; $i++) { Start-Sleep -Milliseconds 500; if (Test-Grew) { $started = $true; break } }
}
if (-not $started) {
  Say '   任务计划没能起来（电池策略 / 任务被禁用都可能导致），改为直接启动…' 'Yellow'
  Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass', '-File', "`"$Script`"" -WindowStyle Hidden
  for ($i = 0; $i -lt 12; $i++) { Start-Sleep -Milliseconds 500; if (Test-Grew) { $started = $true; break } }
}

# ---------------------------------------------------------------- 5. 报告
Say ''
Say '==== 结果 ====' 'Cyan'
if ($taskOk) {
  $t = Get-ScheduledTask -TaskName $TaskName
  Say ("计划任务      : {0}" -f $t.State)
  Say ("  允许电池运行: {0}    切到电池不停止: {1}    执行上限: {2}" -f $t.Settings.AllowStartIfOnBatteries, $t.Settings.DontStopIfGoingOnBatteries, $t.Settings.ExecutionTimeLimit)
} else { Say '计划任务      : 未注册成功（靠启动文件夹 .vbs 兜底）' 'Yellow' }
Say ("守护进程      : {0}" -f $(if ($started) { '已启动' } else { '未确认，请看下面日志' }))
$en = $null
try { $en = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AutoRotation' -Name Enable -ErrorAction Stop).Enable } catch { }
if ($null -eq $en) { Say '系统旋转锁定  : 读不到 Enable 值' 'Yellow' }
elseif ([int]$en -eq 0) { Say '系统旋转锁定  : 锁定中（Enable=0）→ 不会自动旋转；把控制中心的「旋转锁定」关掉即可' 'Yellow' }
else { Say '系统旋转锁定  : 未锁定（Enable=1）→ 正常自动旋转' 'Green' }
Say ''
if (Test-Path -LiteralPath $LogPath) {
  Say '---- 日志最后 8 行 ----'
  Get-Content -LiteralPath $LogPath -Tail 8 -Encoding UTF8
}
