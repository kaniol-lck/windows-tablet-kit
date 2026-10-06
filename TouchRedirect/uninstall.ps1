# TouchRedirect 卸载（需要管理员，脚本会自己请求提权）
# 删除：进程、C:\Program Files\TouchRedirect\、开始菜单快捷方式、本机受信任存储与 CurrentUser\My 里的自签证书。
# 保留：%LOCALAPPDATA%\TouchRedirect\（名单与日志），要一并删就加 -PurgeData。
param([switch]$PurgeData)

$ErrorActionPreference = 'Stop'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host '需要管理员权限，正在请求提权（会弹 UAC）...'
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
        if ($PurgeData) { $argList += '-PurgeData' }
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList
        exit
    }
}

Assert-Admin

$subject = 'CN=TouchRedirect Local Test'
$destDir = Join-Path $env:ProgramFiles 'TouchRedirect'
$dataDir = Join-Path $env:LOCALAPPDATA 'TouchRedirect'

Get-Process TouchRedirect -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if (Test-Path -LiteralPath $destDir) {
    Remove-Item -LiteralPath $destDir -Recurse -Force
    Write-Host ("已删除 " + $destDir)
}

try {
    $lnkPath = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs\TouchRedirect.lnk'
    if (Test-Path -LiteralPath $lnkPath) { Remove-Item -LiteralPath $lnkPath -Force; Write-Host ("已删除快捷方式 " + $lnkPath) }
} catch { }

foreach ($storeName in @('Root', 'TrustedPublisher')) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')
    $store.Open('ReadWrite')
    $targets = @()
    foreach ($c in $store.Certificates) { if ($c.Subject -eq $subject) { $targets += $c } }
    foreach ($c in $targets) { $store.Remove($c); Write-Host ("已从本机 " + $storeName + " 移除 " + $c.Thumbprint) }
    $store.Close()
}

foreach ($c in (Get-ChildItem 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $subject })) {
    Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $c.Thumbprint) -Force
    Write-Host ("已从 CurrentUser\My 移除 " + $c.Thumbprint)
}

if ($PurgeData -and (Test-Path -LiteralPath $dataDir)) {
    Remove-Item -LiteralPath $dataDir -Recurse -Force
    Write-Host ("已删除 " + $dataDir)
} else {
    Write-Host ("保留 " + $dataDir + "（名单与日志；要删就加 -PurgeData）")
}
Write-Host '卸载完成。'
