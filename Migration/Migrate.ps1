#Requires -Version 5.1
<#
  迁移个人信息到新账号（Windows 10）
  ---------------------------------------------------------------------------
  默认只搬「个人文件」；AppData 需要 -AppData 或 -Full 才搬，且始终避开危险项。

  用法（双击 迁移到新账号.cmd 即可，脚本会自己请求管理员权限）：
    Migrate.ps1                     # 自动判断源/目标：只搬个人文件 + 常用游戏/输入法数据
    Migrate.ps1 -PersonalOnly       # 只搬个人文件
    Migrate.ps1 -Full               # 尽量把 AppData 也搬（仍排除系统级/缓存/危险项）
    Migrate.ps1 -DryRun             # 只预览，不写任何文件
    Migrate.ps1 -Source $env:USERPROFILE -Dest C:\Users\$env:USERNAME

  安全约定：
    * 绝不使用 /MIR 或 /PURGE —— 不会删除目标里的任何东西
    * 绝不复制 NTUSER.DAT / UsrClass.dat 等注册表文件
    * /XJ 跳过 junction，避免无限递归
    * 不复制文件 ACL（目标自动继承新账号自己的权限，避免所有权混乱）
#>
param(
  [string]$Source = '$env:USERPROFILE',
  [string]$Dest,
  [switch]$PersonalOnly,
  [switch]$Full,
  [switch]$DryRun
)

$ErrorActionPreference = 'Continue'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not (Test-Path $Root)) { $Root = 'C:\Tools\Migration' }
$Log = Join-Path $Root ("migration-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
function Say($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }
function L($m) { $m | Out-File -FilePath $Log -Append -Encoding UTF8 }

Say "==== 迁移个人信息 ====" Cyan
Say "日志: $Log"

# ---------- 自动判断目标 ----------
if (-not $Dest) {
  if ($env:USERNAME -ne '$env:USERNAME') {
    $Dest = $env:USERPROFILE
  } else {
    $cand = Get-ChildItem 'C:\Users' -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notin @('$env:USERNAME','Public','Default','Default User','All Users') -and $_.Name -notmatch '^\.' } |
            Select-Object -First 1
    if ($cand) { $Dest = $cand.FullName }
  }
}
Say "源目录  : $Source" White
Say "目标目录: $Dest" White

if (-not $Dest -or -not (Test-Path -LiteralPath $Dest)) {
  Say "❌ 找不到目标账号的配置文件目录。" Red
  Say "   请先用新账号（$env:USERNAME）登录一次，让它生成 C:\Users\$env:USERNAME，然后再运行本工具。" Yellow
  exit 1
}
if ((Resolve-Path -LiteralPath $Source).Path -eq (Resolve-Path -LiteralPath $Dest).Path) {
  Say "❌ 源目录与目标目录相同，已中止（不会自己搬给自己）。" Red
  exit 1
}
if ($Source -notlike '*\$env:USERNAME') {
  Say "⚠️ 源目录不是默认的 $env:USERNAME，请确认是否正确。" Yellow
}
if ($Dest -like '*\$env:USERNAME') {
  Say "⚠️ 目标目录是 $env:USERNAME；如果你打算迁到新账号，请用 -Dest C:\Users\$env:USERNAME。" Yellow
}

# ---------- 要搬的内容 ----------
$personal = 'Desktop','Documents','Downloads','Pictures','Music','Videos','Favorites','Links','Saved Games','Contacts','Searches','3D Objects'

# 常用应用数据（浏览器/输入法/游戏存档等），不含任何缓存
$appRoaming = 'Rime','Mozilla','WinRAR','Hypergryph','RenPy','Glaiel Games','Goldberg SteamEmu Saves','PROTOTYPE','jellyfin-desktop','あざらしそふと'
$appLocal   = 'Steam','GODSOUL','rmmz-game','jellyfin-desktop','GameViewer','mpv','Moonlight Game Streaming Project','Games'

# 永不复制（系统级用户状态 / 缓存 / 体积巨大且应重装的目录）
$neverCopy = @(
  'NTUSER.DAT','NTUSER.DAT.LOG1','NTUSER.DAT.LOG2','UsrClass.dat','UsrClass.dat.LOG1','UsrClass.dat.LOG2',
  'AppData\Local\Temp','AppData\Local\Microsoft','AppData\Local\Packages','AppData\Local\Programs',
  'AppData\Local\Kingsoft','AppData\Roaming\kingsoft','AppData\Roaming\Microsoft',
  'AppData\Local\cache','AppData\Local\CEF','AppData\Local\D3DSCache','AppData\Local\ElevatedDiagnostics',
  'AppData\Local\ConnectedDevicesPlatform','AppData\Local\PlaceholderTileLogoFolder','AppData\Local\PlatformProcess',
  'AppData\Local\Upgrade','AppData\Local\node-addon-native-custom-loader','AppData\Local\pnpm','AppData\Local\pnpm-cache','AppData\Local\pnpm-state',
  'AppData\Local\@example-vendordsh-desktop-updater','AppData\Roaming\@example-vendor'
)

$jobs = New-Object System.Collections.Generic.List[string]
foreach ($p in $personal) { $jobs.Add($p) }
if (-not $PersonalOnly) {
  foreach ($p in $appRoaming) { $jobs.Add("AppData\Roaming\$p") }
  foreach ($p in $appLocal)   { $jobs.Add("AppData\Local\$p") }
}
if ($Full) {
  foreach ($base in 'AppData\Roaming','AppData\Local') {
    $srcBase = Join-Path $Source $base
    Get-ChildItem $srcBase -Directory -Force -ErrorAction SilentlyContinue | ForEach-Object {
      $rel = "$base\$($_.Name)"
      if ($neverCopy -notcontains $rel) { $jobs.Add($rel) }
    }
  }
}
$jobs = $jobs | Select-Object -Unique | Where-Object { $neverCopy -notcontains $_ }

# ---------- 开始复制 ----------
$stats = @()
$totalBytes = 0
foreach ($rel in $jobs) {
  $s = Join-Path $Source $rel
  $d = Join-Path $Dest   $rel
  if (-not (Test-Path -LiteralPath $s)) { L "跳过（源不存在）: $rel"; continue }
  $size = (Get-ChildItem -LiteralPath $s -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
  $totalBytes += $size

  $rcArgs = @($s, $d, '/E', '/COPY:DAT', '/DCOPY:DAT', '/R:1', '/W:1', '/XJ', '/NP', '/NFL', '/NDL', '/MT:8')
  if ($rel -eq 'Desktop') { $rcArgs += @('/XF', 'Example App.lnk') }   # 该快捷方式在新账号里无效
  if ($DryRun) { $rcArgs += '/L' }

  Say ("  {0,-42} {1,9:N1} MB" -f $rel, ($size / 1MB)) DarkGray
  $out = & robocopy @rcArgs 2>&1
  $code = $LASTEXITCODE
  $ok = ($code -lt 8)
  L ("{0,-42} rc={1} {2}  {3:N1} MB" -f $rel, $code, $(if ($ok) { 'OK' } else { 'FAILED' }), ($size / 1MB))
  if (-not $ok) { $out | Out-File -FilePath $Log -Append -Encoding UTF8 }
  $stats += [pscustomobject]@{ 项目 = $rel; MB = [Math]::Round($size / 1MB, 1); 结果 = $(if ($ok) { '成功' } else { "失败(rc=$code)" }) }
}

Say ""
Say ("==== 汇总 {0} ====" -f $(if ($DryRun) { '(预览模式，未写入任何文件)' } else { '' })) Cyan
$stats | Format-Table -AutoSize | Out-String -Width 120 | ForEach-Object { Write-Host $_ }
Say ("合计约 {0:N1} MB" -f ($totalBytes / 1MB)) White
L ("总计 {0:N1} MB" -f ($totalBytes / 1MB))

Say ""
Say "提醒：" Yellow
Say "  1) 已刻意不搬：WPS(Kingsoft) 数据、AppData\Local\Programs（含旧 devtool 本体）、Microsoft/Packages 等系统用户状态。" Yellow
Say "     新账号里请重新安装 WPS、Example App、UU远程 等「只装了当前用户」的软件。" Yellow
Say "  2) Steam 自启等 HKCU 设置不会自动迁移：在「运行」里输入 shell:startup / regedit 自行添加即可。" Yellow
Say "  3) 部分应用首次在新账号打开时可能需要重新登录（浏览器、通讯类）。" Yellow
Say "  4) 建议迁移完成后重启一次。" Yellow
