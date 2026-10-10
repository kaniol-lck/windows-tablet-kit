<#
  Install-OskRedirect.ps1 —— 把 Windows 的「触摸键盘」入口重定向到自研 TouchKeyboard（**需要管理员**）

  本机为什么需要它：
    精简版 Windows 删掉了 TabTip.exe 之后，任务栏那个「触摸键盘」按钮点了没反应。
    原因不只是文件缺失——TabletInputService（TabSvc.dll）是**从注册表**读触摸键盘进程路径的：
        HKLM\SOFTWARE\Classes\CLSID\{054AAE20-4BEA-4347-8A35-64A533254A9D}\LocalServer32
    （对应的函数是 TabSvc.dll 里的 PENSERVICE_CTabTipProcessInfo::_GetPath）。
    这个键在精简系统上通常也被删了，所以按钮连"该启动哪个 exe"都查不到。

  本脚本做三件事：
    ① 建上面的 CLSID 注册项，指向自研键盘 exe → 按钮/热键触发的服务拉起会拉起自研键盘
    ② 给 osk.exe / TabTip.exe 挂 IFEO Debugger 重定向 → Win+Ctrl+O、任何按名字拉起 osk/TabTip 的入口也走自研键盘
    ③ 建一个开始菜单快捷方式（TouchKeyboard 单实例运行，**再次启动 = 切换显示/隐藏**，所以这个快捷方式本身就是个开关）

  用法（管理员 PowerShell）：
    powershell -ExecutionPolicy Bypass -File Install-OskRedirect.ps1 -KeyboardPath "C:\Tools\TouchKeyboard\TouchKeyboard.exe"

  撤销：Uninstall-OskRedirect.ps1
#>
[CmdletBinding()]
param(
  [string]$KeyboardPath = 'C:\Tools\TouchKeyboard\TouchKeyboard.exe',
  [switch]$NoIfeo,
  [switch]$NoShortcut,
  [string]$ShortcutName = '触摸键盘'
)

$ErrorActionPreference = 'Continue'
$clsidTabTip    = '{054AAE20-4BEA-4347-8A35-64A533254A9D}'   # TabSvc 读 LocalServer32 的那个 CLSID
$ifeoRoot       = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
$ifeoTargets    = @('osk.exe', 'TabTip.exe')

function Info($m) { Write-Host $m }

Info '============================================'
Info '  Redirect Windows touch-keyboard entry to TouchKeyboard'
Info '============================================'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Info '[X] 需要管理员权限，请用「以管理员身份运行」'; exit 1 }

$KeyboardPath = [IO.Path]::GetFullPath($KeyboardPath)
if (-not (Test-Path $KeyboardPath)) { Info "[X] 找不到键盘 exe：$KeyboardPath"; exit 1 }
Info "[1/3] 键盘 exe：$KeyboardPath"

# ① CLSID LocalServer32
$key = "HKLM:\SOFTWARE\Classes\CLSID\$clsidTabTip"
try {
  New-Item -Path "$key\LocalServer32" -Force | Out-Null
  Set-ItemProperty -Path $key -Name '(Default)' -Value 'TouchKeyboard (自研触控键盘)'
  Set-ItemProperty -Path "$key\LocalServer32" -Name '(Default)' -Value ('"' + $KeyboardPath + '"')
  Info "      OK  $clsidTabTip\LocalServer32 = `"$KeyboardPath`""
} catch { Info "[X] CLSID 写入失败：$($_.Exception.Message)"; exit 1 }

# ② IFEO
if ($NoIfeo) {
  Info '[2/3] 跳过 IFEO（-NoIfeo）'
} else {
  foreach ($name in $ifeoTargets) {
    $k = Join-Path $ifeoRoot $name
    try {
      New-Item -Path $k -Force | Out-Null
      Set-ItemProperty -Path $k -Name 'Debugger' -Value $KeyboardPath -Type String
      Info "      OK  IFEO $name  Debugger = $KeyboardPath"
    } catch { Info "[!] IFEO $name 写入失败：$($_.Exception.Message)" }
  }
}

# ③ 开始菜单快捷方式
if ($NoShortcut) {
  Info '[3/3] 跳过快捷方式（-NoShortcut）'
} else {
  try {
    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$ShortcutName.lnk"
    $sh = New-Object -ComObject WScript.Shell
    $sc = $sh.CreateShortcut($lnk)
    $sc.TargetPath = $KeyboardPath
    $sc.WorkingDirectory = Split-Path $KeyboardPath -Parent
    $sc.Description = '自研触控键盘（再次启动 = 显示/隐藏）'
    $sc.IconLocation = "$KeyboardPath,0"
    $sc.Save()
    Info "      OK  快捷方式 $lnk"
  } catch { Info "[!] 快捷方式创建失败：$($_.Exception.Message)" }
}

Info ''
Info '完成。触摸键盘按钮现在应该会拉起自研键盘。'
Info '注意：'
Info '  * 键盘**已在运行**时点按钮 = 把开关请求转给运行中的实例（显示/隐藏），不会起第二个进程；'
Info '  * 键盘**没在运行**时点按钮 = 由系统服务（SYSTEM 身份）拉起。SYSTEM 身份下键盘能显示、能输入，'
Info '    但托盘图标的右键菜单可能点不动（UIPI 反向限制）；用键盘上的 ✕ 关掉后，从开始菜单快捷方式'
Info '    重新启动即可回到用户身份。'
Info ''
Info '撤销：powershell -ExecutionPolicy Bypass -File Uninstall-OskRedirect.ps1'
