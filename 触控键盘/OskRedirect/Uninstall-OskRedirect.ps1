<#
  Uninstall-OskRedirect.ps1 —— 撤销 Install-OskRedirect.ps1 的改动（**需要管理员**）

  删掉：CLSID {054AAE20-…}\LocalServer32、osk.exe / TabTip.exe 的 IFEO Debugger、开始菜单快捷方式。
  不动：自研键盘 exe 本身、键盘的 ini/layouts、桌面文件。
#>
[CmdletBinding()]
param(
  [string]$ShortcutName = '触摸键盘',
  [switch]$KeepIfeo
)

$ErrorActionPreference = 'Continue'
$clsidTabTip = '{054AAE20-4BEA-4347-8A35-64A533254A9D}'
$ifeoRoot    = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'

function Info($m) { Write-Host $m }

Info '============================================'
Info '  Undo touch-keyboard redirect'
Info '============================================'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Info '[X] 需要管理员权限'; exit 1 }

$key = "HKLM:\SOFTWARE\Classes\CLSID\$clsidTabTip"
try { Remove-Item $key -Recurse -Force -ErrorAction Stop; Info "已删除 CLSID $clsidTabTip" }
catch { Info "删除 CLSID 失败（可能本来就没有）：$($_.Exception.Message)" }

if ($KeepIfeo) {
  Info '保留 IFEO（-KeepIfeo）'
} else {
  foreach ($name in @('osk.exe', 'TabTip.exe')) {
    $k = Join-Path $ifeoRoot $name
    if (-not (Test-Path $k)) { Info "IFEO $name 不存在"; continue }
    try { Remove-ItemProperty -Path $k -Name 'Debugger' -ErrorAction Stop; Info "已删除 IFEO $name 的 Debugger" }
    catch { Info "删除 IFEO $name 失败：$($_.Exception.Message)" }
    try {
      $sub = @(Get-ChildItem $k -ErrorAction SilentlyContinue)
      $props = @((Get-Item $k).Property)
      if ($sub.Count -eq 0 -and $props.Count -eq 0) { Remove-Item $k -Force; Info "已删除空键 $name" }
    } catch { }
  }
}

try {
  $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$ShortcutName.lnk"
  if (Test-Path $lnk) { Remove-Item $lnk -Force; Info "已删除快捷方式 $lnk" } else { Info '快捷方式不存在' }
} catch { Info "删除快捷方式失败：$($_.Exception.Message)" }

Info ''
Info '完成。系统触摸键盘入口已恢复原状（自研键盘本身未动）。'
