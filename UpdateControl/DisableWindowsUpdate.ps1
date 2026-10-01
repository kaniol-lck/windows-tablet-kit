# DisableWindowsUpdate.ps1 —— 彻底禁用 Windows 更新（可恢复）
# 分层：策略层 + 服务层 + 计划任务层 +（可选）hosts 网络层
# 所有改动前会先备份到 backup-<时间戳> 目录，可用 RestoreWindowsUpdate.ps1 还原
param(
  [switch]$DryRun,        # 只预览，不改任何东西（也不需要管理员）
  [switch]$BlockNetwork   # 额外用 hosts 封锁更新域名
)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not (Test-Path $root)) { $root = 'C:\Tools\UpdateControl' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupDir = Join-Path $root "backup-$stamp"
$log = Join-Path $root "disable-wu-$stamp.log"
function Say($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c; try { Add-Content -LiteralPath $log -Value $m -Encoding UTF8 } catch {} }

Say "==================== 彻底禁用 Windows 更新 ====================" Cyan
if ($DryRun) { Say "【预览模式】不会修改任何设置" Yellow }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::$env:USERNAME)
Say ("管理员权限: " + $admin)
if (-not $admin -and -not $DryRun) { Say "❌ 需要管理员权限：请通过「禁用系统更新.cmd」运行（会自动提权）" Red; exit 1 }

# ---------------- 0. 备份 ----------------
if (-not $DryRun) {
  New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
  foreach ($pair in @(
      @('HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate', 'WindowsUpdate-Policies.reg'),
      @('HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings', 'WindowsUpdate-UX-Settings.reg'),
      @('HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization', 'DeliveryOptimization.reg'))) {
    $out = reg.exe export $pair[0] (Join-Path $backupDir $pair[1]) /y 2>&1
  }
  $svc = @{}
  foreach ($n in 'wuauserv', 'UsoSvc', 'WaaSMedicSvc', 'DoSvc', 'BITS') {
    $p = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$n" -ErrorAction SilentlyContinue
    if ($p) { $svc[$n] = $p.Start }
  }
  $svc | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupDir 'service-starts.json') -Encoding UTF8
  Get-ScheduledTask -ErrorAction SilentlyContinue |
    Where-Object { $_.TaskPath -match 'UpdateOrchestrator|WindowsUpdate|WaaSMedic|InstallService|DeliveryOptimization' } |
    Select-Object TaskPath, TaskName, State |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupDir 'tasks.json') -Encoding UTF8
  Copy-Item 'C:\Windows\System32\drivers\etc\hosts' (Join-Path $backupDir 'hosts.bak') -Force -ErrorAction SilentlyContinue
  Say ("备份目录: " + $backupDir) Cyan
}

# ---------------- 1. 策略层（真正的锁） ----------------
Say ""
Say "---- 1. 策略层 ----" White
$wu = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$au = "$wu\AU"
$pol = @(
  @($au, 'NoAutoUpdate',            1,        'DWord'),
  @($au, 'AUOptions',               1,        'DWord'),
  @($au, 'NoAutoRebootWithLoggedOnUsers', 1,  'DWord'),
  @($wu, 'SetDisableUXWUAccess',    1,        'DWord'),
  @($wu, 'DisableWindowsUpdateAccess', 1,     'DWord'),
  @($wu, 'DoNotConnectToWindowsUpdateInternetLocations', 1, 'DWord'),
  @($wu, 'ExcludeWUDriversInQualityUpdate', 1, 'DWord'),
  @($wu, 'TargetReleaseVersion',    1,        'DWord'),
  @($wu, 'TargetReleaseVersionInfo', '22H2',  'String')
)
foreach ($p in $pol) {
  if ($DryRun) { Say ("  [预览] {0}  {1} = {2}" -f $p[0], $p[1], $p[2]); continue }
  if (-not (Test-Path $p[0])) { New-Item -Path $p[0] -Force | Out-Null }
  try { Set-ItemProperty -Path $p[0] -Name $p[1] -Value $p[2] -Type $p[3] -Force -ErrorAction Stop; Say ("  ✅ {0} = {1}" -f $p[1], $p[2]) Green }
  catch { Say ("  ❌ {0}: {1}" -f $p[1], $_.Exception.Message) Red }
}
# 用户界面的「暂停更新」也钉死到 2066
$ux = 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'
$pauseVals = @{
  PauseFeatureUpdatesStartTime = '2022-07-01T20:00:00Z'
  PauseQualityUpdatesStartTime = '2022-07-01T20:00:00Z'
  PauseFeatureUpdatesEndTime   = '2066-01-01T20:00:00Z'
  PauseQualityUpdatesEndTime   = '2066-01-01T20:00:00Z'
  PauseUpdatesExpiryTime       = '2066-01-01T20:00:00Z'
}
foreach ($k in $pauseVals.Keys) {
  if ($DryRun) { Say ("  [预览] {0} = {1}" -f $k, $pauseVals[$k]); continue }
  try { Set-ItemProperty -Path $ux -Name $k -Value $pauseVals[$k] -Type String -Force -ErrorAction Stop; Say ("  ✅ 暂停相关 {0} 已钉死" -f $k) Green }
  catch { Say ("  ❌ {0}: {1}" -f $k, $_.Exception.Message) Red }
}

# ---------------- 2. 服务层 ----------------
Say ""
Say "---- 2. 服务层（wuauserv/DoSvc -> 禁用） ----" White
foreach ($n in 'wuauserv', 'UsoSvc', 'WaaSMedicSvc', 'DoSvc') {
  $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$n"
  if (-not (Test-Path $key)) { Say ("  {0}: 服务键不存在（已被精简，无需处理）" -f $n) DarkGray; continue }
  if ($DryRun) { Say ("  [预览] {0}: Start -> 4(禁用)" -f $n); continue }
  try {
    Stop-Service $n -Force -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $key -Name Start -Value 4 -Type DWord -Force -ErrorAction Stop
    Say ("  ✅ {0} -> Start=4（已禁用）" -f $n) Green
  } catch { Say ("  ❌ {0}: {1}" -f $n, $_.Exception.Message) Red }
}

# ---------------- 3. 计划任务层 ----------------
Say ""
Say "---- 3. 计划任务层 ----" White
$tasks = Get-ScheduledTask -ErrorAction SilentlyContinue |
  Where-Object { $_.TaskPath -match 'UpdateOrchestrator|WindowsUpdate|WaaSMedic|InstallService|DeliveryOptimization' -and $_.State -ne 'Disabled' }
if (-not $tasks) { Say "  （没有需要禁用的更新任务）" DarkGray }
foreach ($t in $tasks) {
  if ($DryRun) { Say ("  [预览] 禁用任务 {0}{1}" -f $t.TaskPath, $t.TaskName); continue }
  try { Disable-ScheduledTask -TaskPath $t.TaskPath -TaskName $t.TaskName -ErrorAction Stop | Out-Null; Say ("  ✅ 已禁用 {0}{1}" -f $t.TaskPath, $t.TaskName) Green }
  catch { Say ("  ⚠️ 无法禁用 {0}{1}: {2}" -f $t.TaskPath, $t.TaskName, $_.Exception.Message) Yellow }
}

# ---------------- 4. hosts 网络层（可选） ----------------
Say ""
Say "---- 4. hosts 网络层 ----" White
$hostsPath = 'C:\Windows\System32\drivers\etc\hosts'
$marker = '# --- block Windows Update (added by devtool tool) ---'
$domains = 'windowsupdate.com', 'www.windowsupdate.com', 'update.microsoft.com', 'download.windowsupdate.com',
'delivery.mp.microsoft.com', 'dl.delivery.mp.microsoft.com', 'tlu.dl.delivery.mp.microsoft.com',
'fe2.update.microsoft.com', 'fe3.delivery.mp.microsoft.com', 'catalog.update.microsoft.com', 'sls.update.microsoft.com'
if ($BlockNetwork) {
  if ($DryRun) { Say "  [预览] 将在 hosts 追加屏蔽 $($domains.Count) 个更新域名" }
  else {
    $content = Get-Content -LiteralPath $hostsPath -Raw -ErrorAction SilentlyContinue
    if ($content -match [regex]::Escape($marker)) { Say "  已存在屏蔽标记，跳过" DarkGray }
    else {
      $lines = @("", $marker)
      foreach ($d in $domains) { $lines += ("0.0.0.0 " + $d) }
      Add-Content -LiteralPath $hostsPath -Value $lines -Encoding ASCII
      Say ("  ✅ 已在 hosts 屏蔽 {0} 个更新域名" -f $domains.Count) Green
    }
  }
} else { Say "  （未启用；如需网络层封锁请用「禁用系统更新.cmd」的第 3 项）" DarkGray }

# ---------------- 5. 结果核对 ----------------
Say ""
Say "---- 5. 结果核对 ----" White
if (-not $DryRun) {
  foreach ($n in 'wuauserv', 'UsoSvc', 'WaaSMedicSvc', 'DoSvc') {
    $p = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$n" -ErrorAction SilentlyContinue
    if ($p) { Say ("  {0,-16} Start={1}  状态={2}" -f $n, $p.Start, (Get-Service $n -ErrorAction SilentlyContinue).Status) }
    else { Say ("  {0,-16} 服务键不存在" -f $n) }
  }
  Say ("  NoAutoUpdate = " + (Get-ItemProperty $au -Name NoAutoUpdate -ErrorAction SilentlyContinue).NoAutoUpdate)
  Say ("  PauseQualityUpdatesEndTime = " + (Get-ItemProperty $ux -Name PauseQualityUpdatesEndTime -ErrorAction SilentlyContinue).PauseQualityUpdatesEndTime)
  Say ("  hosts 屏蔽标记 = " + [bool]((Get-Content -LiteralPath $hostsPath -Raw -ErrorAction SilentlyContinue) -match [regex]::Escape($marker)))
  Say ("  更新缓存目录 C:\Windows\SoftwareDistribution 存在 = " + (Test-Path 'C:\Windows\SoftwareDistribution'))
}
Say ""
Say "完成。还原请运行 「恢复系统更新.cmd」（使用 $backupDir）" Cyan
