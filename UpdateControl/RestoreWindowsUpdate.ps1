# RestoreWindowsUpdate.ps1 —— 还原「彻底禁用 Windows 更新」所做的改动
param([switch]$DryRun)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not (Test-Path $root)) { $root = 'C:\Tools\UpdateControl' }
function Say($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::$env:USERNAME)
Say ("==================== 恢复 Windows 更新设置 ====================") Cyan
Say ("管理员权限: " + $admin)
if (-not $admin -and -not $DryRun) { Say "需要管理员权限：请通过「恢复系统更新.cmd」运行" Red; exit 1 }

$backups = Get-ChildItem -Path $root -Directory -Filter 'backup-*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending
if (-not $backups) { Say "❌ 找不到备份目录，无法自动还原。" Red; exit 1 }
$b = $backups[0].FullName
Say ("使用备份: " + $b) Cyan
if ($DryRun) { Say "【预览模式】" Yellow }

# 1) 注册表策略
foreach ($f in 'WindowsUpdate-Policies.reg', 'WindowsUpdate-UX-Settings.reg', 'DeliveryOptimization.reg') {
  $p = Join-Path $b $f
  if (Test-Path $p) {
    if ($DryRun) { Say ("  [预览] 导入 " + $f) }
    else { $r = reg.exe import $p 2>&1; Say ("  导入 {0}: {1}" -f $f, ($r -join ' ')) Green }
  } else { Say ("  （无 " + $f + "，跳过）") DarkGray }
}

# 2) 服务启动类型
$svcFile = Join-Path $b 'service-starts.json'
if (Test-Path $svcFile) {
  $svc = Get-Content -LiteralPath $svcFile -Raw | ConvertFrom-Json
  foreach ($n in $svc.PSObject.Properties.Name) {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$n"
    if (-not (Test-Path $key)) { continue }
    if ($DryRun) { Say ("  [预览] {0}: Start -> {1}" -f $n, $svc.$n) }
    else { try { Set-ItemProperty -Path $key -Name Start -Value ([int]$svc.$n) -Type DWord -Force -ErrorAction Stop; Say ("  ✅ {0} -> Start={1}" -f $n, $svc.$n) Green } catch { Say ("  ❌ {0}: {1}" -f $n, $_.Exception.Message) Red } }
  }
}

# 3) 计划任务
$taskFile = Join-Path $b 'tasks.json'
if (Test-Path $taskFile) {
  foreach ($t in (Get-Content -LiteralPath $taskFile -Raw | ConvertFrom-Json)) {
    if ($DryRun) { Say ("  [预览] 启用任务 {0}{1}" -f $t.TaskPath, $t.TaskName) ; continue }
    try { Enable-ScheduledTask -TaskPath $t.TaskPath -TaskName $t.TaskName -ErrorAction Stop | Out-Null; Say ("  ✅ 已启用 {0}{1}" -f $t.TaskPath, $t.TaskName) Green }
    catch { Say ("  ⚠️ 无法启用 {0}{1}: {2}" -f $t.TaskPath, $t.TaskName, $_.Exception.Message) Yellow }
  }
}

# 4) hosts
$hostsBak = Join-Path $b 'hosts.bak'
if (Test-Path $hostsBak) {
  if ($DryRun) { Say "  [预览] 从备份恢复 hosts" }
  else { Copy-Item $hostsBak 'C:\Windows\System32\drivers\etc\hosts' -Force; Say "  ✅ hosts 已从备份恢复" Green }
}

Say ""
Say "完成。若要让更新策略完全回到系统默认，还可手动删除 HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate 下的相关值。" Cyan
