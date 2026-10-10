# 屏幕旋转类

> 系统的 **Simple Device Orientation Sensor / `SensorService` 被删**，而加速度计还能用时，
> 用自研守护进程"看加速度计 → 自己转屏"；同时提供平板 / 笔记本模式与旋转状态的诊断脚本。

| 子目录 | 角色 | 依赖 |
|---|---|---|
| [`AutoRotate/`](AutoRotate/) | **重力自动旋转守护**（PowerShell + WinRT 加速度计 + `ChangeDisplaySettingsEx`） | 系统自带 PowerShell 5.1，无需安装任何东西 |
| [`RotationCheck/`](RotationCheck/RotationCheck.ps1) | 诊断：屏幕方向、`ConvertibleSlateMode`、旋转锁定、加速度计读数 | 同上 |

本机为什么需要它：方向传感器是 **UMDF 1.x** 设备，而 UMDF 1.x 宿主 `WudfSvc` 被删 → 设备起不来（代码 10），
`SimpleOrientationSensor.GetDefault()` 返回 null。详见顶层 [机器环境.md](../机器环境.md)。

## AutoRotate

### 用法

| 命令 | 作用 |
|---|---|
| `AutoRotate.ps1`（无参数） | 常驻轮询自动旋转，推荐由安装脚本注册成登录启动 |
| `AutoRotate.ps1 -Status` | 打印当前状态（重力 / 判定 / 当前方向 / 旋转锁定），**不改画面** |
| `AutoRotate.ps1 -SelfTest` | 能力自检（显示器是否支持 4 个方向 + 加速度计 + 旋转锁定），**不改画面** |
| `AutoRotate.ps1 -Once` | 判定一次并直接应用 |
| `AutoRotate.ps1 -Set N` | 手动旋转：`0`=横向 `1`=纵向 `2`=横向翻转 `3`=纵向翻转 |
| `AutoRotate.ps1 -Calibrate` | 交互校准：记录四个姿势的重力向量，并确认纵向方向 |
| `AutoRotate.ps1 -SwapPortrait` | 临时反转纵向（1/3）的映射关系 |
| `AutoRotate.ps1 -IgnoreLock` | 本次运行忽略系统「旋转锁定」 |

### 一键脚本（`.cmd`，都在 `AutoRotate/` 内）

| 脚本 | 说明 |
|---|---|
| `安装-开机自动旋转.cmd` | 提权 → `Install-AutoStart.ps1`（见下） |
| `卸载-自动旋转.cmd` | 删计划任务 + 停掉实例 + 删全用户启动项 |
| `状态与自检.cmd` | 依次跑 `-SelfTest` 和 `-Status` |
| `立即判定一次.cmd` | `-Once` |
| `校准.cmd` | `-Calibrate`：**四个姿势各保持静止 3 秒** |
| `手动旋转-横向 / 纵向 / 横向翻转 / 纵向翻转.cmd` | 分别对应 `-Set 0 / 1 / 2 / 3` |
| `暂停自动旋转.cmd` | 写 `pause.flag`（手动旋转仍可用） |
| `恢复自动旋转.cmd` | 删 `pause.flag` |

### 「旋转锁定」跟随

默认读取 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AutoRotation\Enable`
（控制中心「旋转锁定」磁贴、设置 → 系统 → 显示 → 旋转锁定，写的是同一个值）：

- `0` = 锁定开 → 守护停止自动旋转，手动旋转仍可用；解锁后自动恢复；
- `1` = 允许自动旋转。

可用 `config.json` 里的 `respectRotationLock: false` 或命令行 `-IgnoreLock` 关掉这一行为。

### `config.json`

首次运行生成，缺字段会自动补默认值（新增 `respectRotationLock` 时就是这么兼容的）：

| 键 | 默认 | 含义 |
|---|---|---|
| `swapPortrait` | `false` | 反转纵向（1/3）映射 |
| `minInPlane` | `0.35` | 判定"设备已放平/立起"的平面内重力阈值 |
| `stableCount` | `4` | 连续判定一致多少次才转屏（防抖） |
| `intervalMs` | `400` | 轮询间隔 |
| `respectRotationLock` | `true` | 是否跟随系统旋转锁定 |
| `poses` | 4 组 | 四个姿势的名称与重力向量 `x`/`y`/`orient`（`-Calibrate` 会改写） |

日志：`AutoRotate.log`（每次旋转结果）；单实例互斥保证只跑一个。

### 自启动为什么有两条路

`安装-开机自动旋转.cmd` / `Install-AutoStart.ps1` 同时铺两条，互为备份：

1. **计划任务 `AutoRotate`**（`Register-ScheduledTask`，登录时启动）：显式设成
   **允许电池供电运行**、**切到电池不停**、**无执行时间上限**、`RunLevel Highest`、多实例忽略新的。
   > 用 `schtasks /create` 建出来的任务默认带「电池供电时不启动 / 切到电池就停止」和 72 小时上限——
   > 平板用电池时会导致"装了自启但没在跑"，这正是本脚本改用 `Register-ScheduledTask` 的原因。
2. **当前用户启动文件夹里的 `AutoRotate.vbs`**（隐藏窗口跑守护），任务计划没能起来时兜底；
   若已存在但指向别的路径会被自动修正。

脚本最后**不只看返回码**，而是等 `AutoRotate.log` 真的增长才算启动成功（前两条路都失败时会直接
`Start-Process` 兜底），并打印任务状态、系统旋转锁定状态与日志尾部。

> ⚠️ `卸载-自动旋转.cmd` 删掉的是**全用户启动夹**（`C:\ProgramData\…\Startup`）与 **Default 用户模板**里的
> `AutoRotate.vbs`，而安装脚本写的是**当前用户**启动夹（`[Environment]::GetFolderPath('Startup')`）——
> 两者不是同一个位置。卸载后请顺手确认当前用户启动夹里没有残留的 `AutoRotate.vbs`。

## RotationCheck

| 脚本 | 说明 |
|---|---|
| `CheckRotation.cmd` → `RotationCheck.ps1` | 实时显示下面这些关键状态（`-Once` 只打印一次不循环） |
| `设置平板-笔记本模式.cmd` → `SetSlateMode.ps1` | 手动写 `ConvertibleSlateMode`（`-Mode 0`/`1`） |

`ConvertibleSlateMode`（`HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl`）的语义：

| 值 | 含义 | 对自动旋转的影响 |
|---|---|---|
| `0` | 平板模式 Slate（物理键盘不在位） | Windows **允许**自动转屏 |
| `1` | 笔记本模式 Desktop/Clamshell（键盘在位） | Windows **不会**自动转屏 |

脚本还会打印 `AutoRotation\Enable` / `SlateEnable`、检测到的键盘类设备
（`VID_12D1` / `CONVERTEDDEVICE`）、方向传感器读数与加速度计 X/Y/Z。
**屏幕不自动转时按这个顺序查**：先看 `ConvertibleSlateMode` 是不是 1（拆下键盘，或强制设 0），
再看 `Enable` 是不是 0（旋转锁定开着），最后才怀疑守护进程。

## 已知限制

- 屏幕旋转依赖显卡驱动与显示器支持，不保证所有机型可用（`-SelfTest` 会验证显示器是否支持 4 个方向）。
- `SetSlateMode.ps1` 写下的值只是**用于验证**：连接 / 拆下键盘时硬件上报可能把它改回去。
- 加速度计本身不可用时（被删 / 驱动缺失）本方案无解。
