# 实验：把 17 种系统光标全部换成空白，保持 10 秒后再完整还原。
# 目的：验证本机 SetSystemCursor 到底有没有"视觉上的"效果。
#   运行期间请用触摸板移动鼠标，看指针是否消失。
param([int]$Seconds = 10)

Add-Type -Namespace TestCursor -Name Api -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr CreateCursor(IntPtr hInst, int x, int y, int w, int h, byte[] andP, byte[] xorP);
[DllImport("user32.dll", SetLastError=true)] public static extern bool SetSystemCursor(IntPtr h, uint id);
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr LoadCursorFromFile(string path);
[DllImport("user32.dll")] public static extern bool DestroyCursor(IntPtr h);
[DllImport("user32.dll", SetLastError=true)] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winini);
'@

$names = 'Arrow','Help','AppStarting','Wait','Crosshair','IBeam','NWPen','No','SizeNS','SizeWE','SizeNWSE','SizeNESW','SizeAll','UpArrow','Hand','Pin','Person'
$ids   = 32512,32651,32650,32514,32515,32513,32631,32648,32645,32644,32642,32643,32646,32516,32649,32671,32672

$cur = Get-ItemProperty 'HKCU:\Control Panel\Cursors'
$and = New-Object byte[] 128
for ($i = 0; $i -lt 128; $i++) { $and[$i] = 0xFF }
$xor = New-Object byte[] 128

Write-Host ''
Write-Host '把全部 17 种系统光标替换为空白 ...'
$ok = 0
for ($i = 0; $i -lt $ids.Count; $i++) {
    $h = [TestCursor.Api]::CreateCursor([IntPtr]::Zero, 0, 0, 32, 32, $and, $xor)
    if ($h -eq [IntPtr]::Zero) { continue }
    if ([TestCursor.Api]::SetSystemCursor($h, [uint32]$ids[$i])) { $ok++ } else { [void][TestCursor.Api]::DestroyCursor($h) }
}
Write-Host ("  已替换 " + $ok + " / " + $ids.Count + " 个")
Write-Host ''
Write-Host ('>>> 现在请用触摸板在这 ' + $Seconds + ' 秒内移动鼠标，看指针还在不在。')
Write-Host ''
Start-Sleep -Seconds $Seconds

Write-Host '还原 ...'
$spi = [TestCursor.Api]::SystemParametersInfo(0x0057, 0, [IntPtr]::Zero, 1)
$byReg = 0
foreach ($i in 0..($ids.Count - 1)) {
    $path = [string]$cur.PSObject.Properties[$names[$i]].Value
    if ([string]::IsNullOrEmpty($path)) { continue }
    $h = [TestCursor.Api]::LoadCursorFromFile($path)
    if ($h -eq [IntPtr]::Zero) { continue }
    if ([TestCursor.Api]::SetSystemCursor($h, [uint32]$ids[$i])) { $byReg++ } else { [void][TestCursor.Api]::DestroyCursor($h) }
}
Write-Host ("  SPI_SETCURSORS=" + $spi + "，按方案回写 " + $byReg + " 个")
Write-Host ''
Write-Host '请回答两个问题：'
Write-Host '  ① 那 10 秒里，触摸板移动时指针消失了没有？'
Write-Host '  ② 如果消失了，指针最后恢复了没有？（没恢复就再跑一次本脚本，或用紧急恢复脚本）'
Write-Host ''
