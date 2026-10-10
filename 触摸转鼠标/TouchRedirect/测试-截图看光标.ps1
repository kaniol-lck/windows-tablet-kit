# 判据：GDI 的 CopyFromScreen **不会**包含硬件光标，只包含"画进画面里"的东西。
#   截图里出现箭头  → 是游戏自己把指针画进画面的（软件光标），任何外部手段都改不到；
#   截图里没有箭头  → 是 Windows 的硬件光标（只是用了游戏自己的图片），还有办法。
#
# 用法：先打开 GodSoul 并让指针显示出来，然后运行本脚本，
#       在 20 张截图过程中**反复用手指点游戏画面**（让指针尽量一直可见）。
#       截图存在同目录的 截图\ 里。

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Cap -Name Api -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
'@
[void][Cap.Api]::SetProcessDPIAware()

$dir = Join-Path $PSScriptRoot '截图'
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
Get-ChildItem -LiteralPath $dir -Filter '*.png' | Remove-Item -Force -ErrorAction SilentlyContinue

$vx = [Cap.Api]::GetSystemMetrics(76)
$vy = [Cap.Api]::GetSystemMetrics(77)
$vw = [Cap.Api]::GetSystemMetrics(78)
$vh = [Cap.Api]::GetSystemMetrics(79)
Write-Host ("屏幕区域 " + $vw + " x " + $vh + "（物理像素）")
Write-Host ''
Write-Host '>>> 现在请在 GodSoul 里反复用手指点画面，尽量让指针一直可见。'
Write-Host ''

for ($i = 1; $i -le 20; $i++) {
    $bmp = New-Object System.Drawing.Bitmap($vw, $vh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($vx, $vy, 0, 0, (New-Object System.Drawing.Size($vw, $vh)))
    $g.Dispose()
    $file = Join-Path $dir ('shot-{0:D2}.png' -f $i)
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("  截图 " + $i + ' / 20')
    Start-Sleep -Milliseconds 400
}

Write-Host ''
Write-Host ('完成，截图在：' + $dir)
Write-Host '把这句话回给助手即可：截图已生成（或说明哪几张里能看到指针）。'
Write-Host ''
