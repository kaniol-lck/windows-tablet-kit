# SetAutostart.ps1 —— 触控键盘开机自启 安装/卸载/状态
# 安装：在"当前用户"和"所有用户"启动文件夹各写一个隐藏启动器（-hidden → 只驻留托盘）
param([switch]$Install, [switch]$Uninstall, [switch]$Status)
$ErrorActionPreference = 'Continue'
$exeDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not (Test-Path $exeDir)) { $exeDir = 'C:\Tools\TouchKeyboard' }
$exe = Join-Path $exeDir 'TouchKeyboard.exe'
$common = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup'
$user   = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
$name   = 'TouchKeyboard.vbs'

function MakeVbs() {
    $run = 'sh.Run "' + $exe + ' -hidden", 0, False'
    return "' TouchKeyboard autostart (hidden to tray)" + "`r`n" +
           'Set sh = CreateObject("WScript.Shell")' + "`r`n" +
           $run + "`r`n"
}

function Report() {
    Write-Host "触控键盘开机自启状态：" -ForegroundColor Cyan
    foreach ($p in @((Join-Path $user $name), (Join-Path $common $name))) {
        $ok = Test-Path $p
        Write-Host ("  {0,-95} {1}" -f $p, $(if ($ok) { '已安装' } else { '未安装' }))
    }
    Write-Host ("  程序: " + $exe + "  存在=" + (Test-Path $exe))
}

if ($Uninstall) {
    foreach ($p in @((Join-Path $user $name), (Join-Path $common $name))) {
        if (Test-Path $p) { Remove-Item $p -Force; Write-Host ("  已删除 " + $p) -ForegroundColor Green }
        else { Write-Host ("  跳过（不存在） " + $p) -ForegroundColor DarkGray }
    }
    Report
    exit 0
}

if ($Status) { Report; exit 0 }

# 默认执行安装
if (-not (Test-Path $exe)) { Write-Host "找不到 TouchKeyboard.exe，请把它放在本脚本同目录。" -ForegroundColor Red; exit 1 }
$vbs = MakeVbs
foreach ($p in @((Join-Path $user $name), (Join-Path $common $name))) {
    try {
        [System.IO.File]::WriteAllText($p, $vbs, (New-Object System.Text.ASCIIEncoding))
        Write-Host ("  已写入 " + $p) -ForegroundColor Green
    } catch {
        Write-Host ("  写入失败（多半需要管理员）: " + $p + "  -> " + $_.Exception.GetType().Name) -ForegroundColor Yellow
    }
}
Report
Write-Host ""
Write-Host "提示：开机后键盘只驻留托盘；开启“自动呼出”后点击输入框会自动弹出。" -ForegroundColor DarkGray
