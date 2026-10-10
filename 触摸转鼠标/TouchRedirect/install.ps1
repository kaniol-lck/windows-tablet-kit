# TouchRedirect 安装（需要管理员，脚本会自己请求提权）
#
#  ① 备好自签代码签名证书（CN=TouchRedirect Local Test），并装进本机「受信任的根」与「受信任的发布者」
#  ② 用 csc.exe 编译 TouchRedirect.cs（带 app.manifest：uiAccess=true + asInvoker）
#  ③ 用该证书签名（UI Access 只认受信任的签名）
#  ④ 复制到 C:\Program Files\TouchRedirect\（UI Access 要求安全目录）
#  ⑤ 建开始菜单快捷方式，并以**普通权限**启动（explorer 代启，避免继承提权令牌）
#
# 配置与日志在 %LOCALAPPDATA%\TouchRedirect\（games.txt 白名单 / TouchRedirect.log），
# 程序本体在 Program Files 只读，所以配置不放在程序目录里。

$ErrorActionPreference = 'Stop'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host '需要管理员权限，正在请求提权（会弹 UAC）...'
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
        )
        exit
    }
}

Assert-Admin

$here = $PSScriptRoot
$src = Join-Path $here 'TouchRedirect.cs'
$manifest = Join-Path $here 'app.manifest'
$build = Join-Path $here '_build'
$buildExe = Join-Path $build 'TouchRedirect.exe'
$destDir = Join-Path $env:ProgramFiles 'TouchRedirect'
$destExe = Join-Path $destDir 'TouchRedirect.exe'
$subject = 'CN=TouchRedirect Local Test'
$dataDir = Join-Path $env:LOCALAPPDATA 'TouchRedirect'

Write-Host ''
Write-Host '=== 1/5 证书 ==='
$cert = Get-ChildItem 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $subject } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject `
        -KeyUsage DigitalSignature -FriendlyName 'TouchRedirect Local Test' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
    Write-Host ("新建证书 " + $cert.Thumbprint + "，有效期至 " + $cert.NotAfter)
} else {
    Write-Host ("复用证书 " + $cert.Thumbprint + "，有效期至 " + $cert.NotAfter)
}
if ($cert.NotAfter -lt (Get-Date).AddDays(30)) { Write-Host '⚠ 证书 30 天内到期：删掉它再跑一次本脚本即可重建' }

Write-Host ''
Write-Host '=== 2/5 装进本机受信任存储 ==='
foreach ($storeName in @('Root', 'TrustedPublisher')) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')
    $store.Open('ReadWrite')
    $exists = $false
    foreach ($c in $store.Certificates) { if ($c.Thumbprint -eq $cert.Thumbprint) { $exists = $true; break } }
    if (-not $exists) {
        # 注意：必须 -ArgumentList (,$bytes)，否则 byte[] 会被展开成 N 个参数
        $pub = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (,$cert.RawData)
        $store.Add($pub)
        Write-Host ("已加入本机 " + $storeName)
    } else {
        Write-Host ("本机 " + $storeName + " 已存在")
    }
    $store.Close()
}

Write-Host ''
Write-Host '=== 3/5 编译 + 签名 ==='
New-Item -ItemType Directory -Force -Path $build | Out-Null
$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw '找不到 csc.exe（.NET Framework 4.x 编译器）' }
& $csc /nologo /target:winexe /optimize+ /win32manifest:"$manifest" /out:"$buildExe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "$src"
if (-not (Test-Path -LiteralPath $buildExe)) { throw '编译失败' }
$sig = Set-AuthenticodeSignature -FilePath $buildExe -Certificate $cert -HashAlgorithm SHA256
Write-Host ("签名状态：" + $sig.Status)
if ($sig.Status -ne 'Valid') { Write-Host ("⚠ 签名不是 Valid：" + $sig.StatusMessage + "（UI Access 会拿不到）") }

Write-Host ''
Write-Host '=== 4/5 安装到 Program Files ==='
Get-Process TouchRedirect -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
Copy-Item -LiteralPath $buildExe -Destination $destExe -Force
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Write-Host ("已安装 " + $destExe)
Write-Host ("名单 / 日志：" + $dataDir)

Write-Host ''
Write-Host '=== 5/5 开始菜单快捷方式 + 以普通权限启动 ==='
try {
    $shell = New-Object -ComObject WScript.Shell
    $lnkPath = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs\TouchRedirect.lnk'
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = $destExe
    $lnk.WorkingDirectory = $destDir
    $lnk.Description = '全局触摸重定向：名单内程序前台时把触摸改写成真实鼠标点击'
    $lnk.Save()
    Write-Host ("已建快捷方式 " + $lnkPath)
} catch { Write-Host ("建快捷方式失败（不影响使用）：" + $_.Exception.Message) }

Get-Process TouchRedirect -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$destExe`""
Start-Sleep -Seconds 2

Write-Host ''
Write-Host '--------------------------------------------------------------'
Write-Host 'TouchRedirect 已在托盘运行（右键图标：暂停 / 重新加载名单 / 打开名单 / 打开日志 / 退出）。'
Write-Host '确认日志：'
$log = Join-Path $dataDir 'TouchRedirect.log'
if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 12 | ForEach-Object { Write-Host ('  ' + $_) } }
else { Write-Host ('  （还没生成 ' + $log + '）') }
Write-Host ''
Write-Host '接着把游戏名填进 games.txt（已预置 stacklands 与 godsoul*），启动游戏后日志里应出现'
Write-Host '「已注册触摸重定向」；这时用手指在游戏里点，就是真正的鼠标左键。'
Write-Host '卸载：跑 uninstall.ps1（或 2-卸载-需管理员.cmd）。'
Write-Host '--------------------------------------------------------------'
