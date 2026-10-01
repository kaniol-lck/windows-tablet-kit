#Requires -Version 5.1
<#
  AutoRotate.ps1  —— 用本机加速度计实现屏幕自动旋转
  适用：系统自带"简单设备方向传感器/SensorService"被删除、但加速度计仍可用的 Windows 10
  依赖：系统自带 Windows PowerShell 5.1 + WinRT 传感器 API + user32 显示 API（无需安装任何东西）

  用法：
    无参数          常驻轮询，自动旋转（推荐由"安装-开机自动旋转.cmd"注册成登录启动）
    -Status         打印当前状态（重力/判定/当前方向），不改画面
    -SelfTest       能力自检（显示器是否支持 4 个方向 + 加速度计是否可用），不改画面
    -Once           判定一次并直接应用
    -Set N          手动旋转到指定方向：0=横向 1=纵向 2=横向翻转 3=纵向翻转
    -Calibrate      交互校准：记录四个姿势的重力向量，并确认纵向方向
    -SwapPortrait   临时反转纵向(1/3)的映射关系
#>
param(
  [switch]$Calibrate,
  [switch]$Once,
  [switch]$SelfTest,
  [switch]$Status,
  [switch]$SwapPortrait,
  [int]$Set = -1
)

$ErrorActionPreference = 'Stop'
$Root    = Split-Path -Parent $MyInvocation.MyCommand.Definition
$CfgPath = Join-Path $Root 'config.json'
$LogPath = Join-Path $Root 'AutoRotate.log'
$PauseFlag = Join-Path $Root 'pause.flag'

function Say($msg, $color = 'Gray') { Write-Host $msg -ForegroundColor $color }
function Log($msg) {
  try { Add-Content -LiteralPath $LogPath -Value ("{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg) -Encoding UTF8 } catch {}
}

# ---------------------------------------------------------------- 显示旋转 API
$cs = @'
using System;
using System.Runtime.InteropServices;
public class DisplayApi {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
    public int dmFields;
    public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet=CharSet.Ansi)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
  [DllImport("user32.dll", CharSet=CharSet.Ansi)] static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr wnd, uint flags, IntPtr param);
  const int ENUM_CURRENT_SETTINGS = -1; const uint CDS_TEST = 2;
  const int DM_DISPLAYORIENTATION = 0x80, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;

  static DEVMODE Get() {
    DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
    if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm)) throw new Exception("EnumDisplaySettings failed");
    return dm;
  }
  public static int CurrentOrientation() { return Get().dmDisplayOrientation; }
  public static string Describe() {
    DEVMODE d = Get();
    return string.Format("{0}x{1}  orient={2}  {3}Hz  {4}bpp", d.dmPelsWidth, d.dmPelsHeight, d.dmDisplayOrientation, d.dmDisplayFrequency, d.dmBitsPerPel);
  }
  static DEVMODE Build(int o) {
    DEVMODE d = Get();
    bool cur = (d.dmDisplayOrientation == 1 || d.dmDisplayOrientation == 3);
    bool tgt = (o == 1 || o == 3);
    if (cur != tgt) { int t = d.dmPelsWidth; d.dmPelsWidth = d.dmPelsHeight; d.dmPelsHeight = t; }
    d.dmDisplayOrientation = o; d.dmFields = DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT; return d;
  }
  public static int Test(int o) { DEVMODE d = Build(o); return ChangeDisplaySettingsEx(null, ref d, IntPtr.Zero, CDS_TEST, IntPtr.Zero); }
  public static int Apply(int o) { DEVMODE d = Build(o); return ChangeDisplaySettingsEx(null, ref d, IntPtr.Zero, 0, IntPtr.Zero); }
}
'@
if (-not ('DisplayApi' -as [type])) { Add-Type -TypeDefinition $cs -Language CSharp | Out-Null }

# ---------------------------------------------------------------- 加速度计
function Initialize-Sensor {
  if (-not $script:Acc) {
    [Windows.Devices.Sensors.Accelerometer, Windows.Devices.Sensors, ContentType = WindowsRuntime] | Out-Null
    $script:Acc = [Windows.Devices.Sensors.Accelerometer]::GetDefault()
  }
  return $script:Acc
}
function Get-Gravity {
  $acc = Initialize-Sensor
  if (-not $acc) { return $null }
  $r = $acc.GetCurrentReading()
  if (-not $r) { return $null }
  [pscustomobject]@{ X = [double]$r.AccelerationX; Y = [double]$r.AccelerationY; Z = [double]$r.AccelerationZ }
}

# ---------------------------------------------------------------- 配置
function Get-DefaultConfig {
  [pscustomobject]@{
    swapPortrait = $false
    minInPlane   = 0.35
    stableCount  = 4
    intervalMs   = 400
    poses        = @(
      [pscustomobject]@{ name = '横向';                 x =  0.0; y = -1.0; orient = 0 }
      [pscustomobject]@{ name = '纵向(上边缘朝左)';      x = -1.0; y =  0.0; orient = 1 }
      [pscustomobject]@{ name = '横向翻转';             x =  0.0; y =  1.0; orient = 2 }
      [pscustomobject]@{ name = '纵向(上边缘朝右)';      x =  1.0; y =  0.0; orient = 3 }
    )
  }
}
function Load-Config {
  if (Test-Path $CfgPath) {
    try { return (Get-Content -LiteralPath $CfgPath -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { Say "config.json 读取失败，改用默认配置：$_" 'Yellow' }
  }
  $cfg = Get-DefaultConfig
  Save-Config $cfg
  return $cfg
}
function Save-Config($cfg) {
  $cfg | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $CfgPath -Encoding UTF8
}

function Classify($g, $cfg) {
  if (-not $g) { return $null }
  $mag = [Math]::Sqrt($g.X * $g.X + $g.Y * $g.Y)
  if ($mag -lt [double]$cfg.minInPlane) { return $null }   # 设备接近水平，无法判定
  $best = $null; $bestDot = -2.0
  foreach ($p in $cfg.poses) {
    $rm = [Math]::Sqrt($p.x * $p.x + $p.y * $p.y)
    if ($rm -le 0) { continue }
    $dot = ($g.X * $p.x + $g.Y * $p.y) / ($mag * $rm)
    if ($dot -gt $bestDot) { $bestDot = $dot; $best = $p }
  }
  return $best
}
function Resolve-Orient($pose, $cfg, $swap) {
  $o = [int]$pose.orient
  $sw = if ($swap) { $true } else { [bool]$cfg.swapPortrait }
  if ($sw) { if ($o -eq 1) { $o = 3 } elseif ($o -eq 3) { $o = 1 } }
  return $o
}
$OrientName = @{ 0 = '横向'; 1 = '纵向'; 2 = '横向翻转'; 3 = '纵向翻转' }

# ---------------------------------------------------------------- 自检 / 状态
if ($SelfTest) {
  Say "==== AutoRotate 自检 ====" 'Cyan'
  Say ("显示器当前状态 : " + [DisplayApi]::Describe())
  foreach ($o in 0, 1, 2, 3) {
    $r = [DisplayApi]::Test($o)
    Say ("  切到 {0,-10} -> {1} {2}" -f $OrientName[$o], $r, $(if ($r -eq 0) { '(支持)' } else { '(不支持)' }))
  }
  $acc = Initialize-Sensor
  if ($acc) {
    $g = Get-Gravity
    Say ("加速度计       : 可用   读数 X={0:N3} Y={1:N3} Z={2:N3}" -f $g.X, $g.Y, $g.Z)
    Say ("  平面内重力   : {0:N3}  (小于 minInPlane 时视为水平放置，不旋转)" -f ([Math]::Sqrt($g.X * $g.X + $g.Y * $g.Y)))
  }
  else { Say "加速度计       : 不可用" 'Red' }
  $cfg = Load-Config
  $p = Classify $g $cfg
  Say ("当前判定姿势   : " + $(if ($p) { "$($p.name) -> 应转 $((Resolve-Orient $p $cfg $SwapPortrait)) 度档位" } else { '无法判定（水平放置）' }))
  Say ("配置文件       : $CfgPath")
  return
}

if ($Status) {
  $g = Get-Gravity; $cfg = Load-Config; $p = Classify $g $cfg
  $gx = if ($g) { "{0:N3}" -f $g.X } else { 'n/a' }
  $gy = if ($g) { "{0:N3}" -f $g.Y } else { 'n/a' }
  $gz = if ($g) { "{0:N3}" -f $g.Z } else { 'n/a' }
  Say "==== AutoRotate 状态 ====" 'Cyan'
  Say "显示器  : $([DisplayApi]::Describe())"
  Say "重力    : X=$gx  Y=$gy  Z=$gz"
  Say "判定    : $(if ($p) { $p.name } else { '无法判定（水平放置）' })"
  Say "配置    : swapPortrait=$($cfg.swapPortrait)  minInPlane=$($cfg.minInPlane)  stableCount=$($cfg.stableCount)  intervalMs=$($cfg.intervalMs)"
  Say "暂停标记: $(if (Test-Path $PauseFlag) { '存在（自动旋转已暂停）' } else { '不存在' })"
  exit 0
}

if ($Set -ge 0) {
  if ($Set -gt 3) { Say "-Set 只能是 0/1/2/3" 'Red'; exit 1 }
  $r = [DisplayApi]::Apply($Set)
  Say ("旋转到 {0} -> 返回 {1}；现在 {2}" -f $OrientName[$Set], $r, [DisplayApi]::Describe()) 'Green'
  Log "手动旋转到 $Set ($($OrientName[$Set])) -> $r"
  exit 0
}

# ---------------------------------------------------------------- 校准
if ($Calibrate) {
  Say "==== 姿势校准 ====" 'Cyan'
  Say "接下来会依次让你把设备摆成 4 个姿势，每个姿势保持不动 3 秒。"
  $cfg = Get-DefaultConfig
  $poses = @(
    @{ name = '横向（正常使用，像笔记本一样放好）'; x = 0.0; y = -1.0; orient = 0 },
    @{ name = '纵向：把设备逆时针转 90°（原来的上边缘朝左）'; x = -1.0; y = 0.0; orient = 1 },
    @{ name = '横向翻转：把设备上下颠倒 180°'; x = 0.0; y = 1.0; orient = 2 },
    @{ name = '纵向：把设备顺时针转 90°（原来的上边缘朝右）'; x = 1.0; y = 0.0; orient = 3 }
  )
  $out = @()
  foreach ($pd in $poses) {
    while ($true) {
      Say ""
      Say ("→ " + $pd.name) 'Yellow'
      Say "  摆好并保持不动，3 秒后自动记录…"
      Start-Sleep -Milliseconds 800
      $sx = 0.0; $sy = 0.0; $n = 0
      $until = (Get-Date).AddSeconds(3)
      while ((Get-Date) -lt $until) {
        $g = Get-Gravity
        if ($g) { $sx += $g.X; $sy += $g.Y; $n++ }
        Start-Sleep -Milliseconds 100
      }
      if ($n -eq 0) { Say "  读不到加速度计，重试" 'Red'; continue }
      $sx = $sx / $n; $sy = $sy / $n
      $mag = [Math]::Sqrt($sx * $sx + $sy * $sy)
      Say ("  记录：X={0:N3}  Y={1:N3}  平面内={2:N3}" -f $sx, $sy, $mag)
      if ($mag -lt 0.5) { Say "  这个姿势太平（接近水平），请把设备立起来一点再试" 'Red'; continue }
      $out += [pscustomobject]@{ name = $pd.name; x = [Math]::Round($sx, 4); y = [Math]::Round($sy, 4); orient = $pd.orient }
      break
    }
  }

  Say ""
  Say "==== 确认纵向方向 ====" 'Cyan'
  Say "先按默认映射试一下：把设备摆成上边缘朝左的纵向姿势，观察屏幕内容是否正立。"
  [DisplayApi]::Apply(1) | Out-Null
  Start-Sleep -Seconds 2
  $ans = Read-Host "现在屏幕内容正立吗？(Y=正立 / N=倒的，需要反转纵向)"
  $swap = $false
  if ($ans -match '^[Nn]') { $swap = $true }
  [DisplayApi]::Apply(0) | Out-Null

  $cfg.poses = $out
  $cfg.swapPortrait = $swap
  Save-Config $cfg
  Say ""
  Say ("校准完成：swapPortrait=$swap，已写入 $CfgPath") 'Green'
  Say "可以运行 安装-开机自动旋转.cmd 让它开机自动工作。"
  exit 0
}

# ---------------------------------------------------------------- 单次判定
if ($Once) {
  $cfg = Load-Config; $g = Get-Gravity; $p = Classify $g $cfg
  if (-not $p) { Say "无法判定（设备可能水平放置）"; exit 0 }
  $o = Resolve-Orient $p $cfg $SwapPortrait
  $cur = [DisplayApi]::CurrentOrientation()
  Say ("判定：{0} -> 目标 {1}({2})，当前 {3}" -f $p.name, $o, $OrientName[$o], $cur)
  if ($o -ne $cur) { $r = [DisplayApi]::Apply($o); Say ("已旋转，返回 $r") 'Green'; Log "单次旋转 $cur -> $o ($($p.name))" }
  else { Say "方向已正确，无需旋转" }
  exit 0
}

# ---------------------------------------------------------------- 常驻自动旋转
# 单实例保护：计划任务 / 启动文件夹 等多个入口同时存在时，只保留一个实例
$script:Mtx = New-Object System.Threading.Mutex($false, 'Local\AutoRotate_SingleInstance')
if (-not $script:Mtx.WaitOne(0)) { Say "已有 AutoRotate 实例在运行，本实例退出。" 'Yellow'; exit 0 }
$cfg = Load-Config
Say "AutoRotate 已启动（配置文件 $CfgPath，日志 $LogPath）" 'Green'
Say "若需暂停：在本目录新建空文件 pause.flag；恢复：删掉它。" 'DarkGray'
Log "==== 启动 ===="
$candidate = $null; $stable = 0; $fail = 0
while ($true) {
  try {
    if (Test-Path $PauseFlag) { Start-Sleep -Milliseconds 1000; continue }
    $g = Get-Gravity
    $p = Classify $g $cfg
    if ($p) {
      $target = Resolve-Orient $p $cfg $SwapPortrait
      if ($target -eq $candidate) { $stable++ } else { $candidate = $target; $stable = 1 }
      if ($stable -ge [int]$cfg.stableCount) {
        $cur = [DisplayApi]::CurrentOrientation()
        if ($target -ne $cur) {
          $r = [DisplayApi]::Apply($target)
          if ($r -ne 0) { $script:failN = [int]$script:failN + 1; Log "旋转 $cur -> $target ($($p.name)) 失败 返回=$r（连续第 $script:failN 次；若前台有全屏程序/串流占用显示模式，退出全屏即可）" } else { $script:failN = 0; Log "旋转 $cur -> $target ($($p.name)) 返回=0" }
        }
        $stable = 0
      }
    } else { $candidate = $null; $stable = 0 }
    $fail = 0
  } catch {
    $fail++
    Log ("异常({0}): {1}" -f $fail, $_.Exception.Message)
    if ($fail -gt 50) { Log "连续异常过多，退出"; exit 1 }
  }
  Start-Sleep -Milliseconds ([int]$cfg.intervalMs)
}
