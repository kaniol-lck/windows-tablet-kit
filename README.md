# Windows Tablet Kit —— 触屏键盘 + 重力自动旋转

面向**被精简 / 定制过的 Windows 平板**的输入能力替代方案。

当系统缺失 `TabTip.exe` / `osk.exe`（触摸键盘无法启动），或缺失 `SensorService` / `WudfSvc` 等
传感器相关组件（屏幕自动旋转失效）时，本仓库用自研实现与诊断脚本替代这两个能力。

> 背景：部分 OEM / 精简版 / 嵌入式 Windows 镜像会移除平板相关的可选组件，
> 典型症状是"点输入框没反应、触摸键盘叫不出来""平板转了但屏幕不转"。
> 本仓库给出可直接使用的替代实现，以及定位这类问题的诊断脚本。

## 功能

### TouchKeyboard —— 自研触控屏幕键盘（C# / WinForms，编译为单文件 exe）

- **自动呼出**：焦点进入输入框时自动弹出（Win32 Caret 探测 + UI Automation 兜底）；
  可手动关闭，关闭后需再次点击输入框才恢复
- **贴底占位**：向系统登记为 AppBar，其他窗口与最大化窗口自动让位；
  键盘顶部可拖拽调整贴底高度；**关闭键盘即归还桌面工作区**；屏幕旋转 / 分辨率变化后自动重算工作区
- **可视化调整**：顶栏「◢ 拖拽缩放」手柄拖动改变键盘尺寸（拖拽期间只显示预览框，松手才应用一次）；
  拖动 / 缩放结束后自动吸附到屏幕边缘
- **多套布局**：外部 `layouts.ini` 热加载（点键盘上的「布局」即生效）；
  内置四套布局（系统 / 默认 / 带Fn行 / 简洁），均以 Windows 自带触摸键盘的 26 键排布为蓝本，再按需增加额外键
- **实体键盘实时高亮**：按下物理键盘的键，屏幕键盘上对应的键实时点亮（全局键盘钩子）
- **粘滞键**：`关`（`⇧` 锁定）/ `单次`（修饰键只对下一个按键生效）
- 托盘常驻、可选开机自启；不抢焦点（`WS_EX_NOACTIVATE`）；
  不进入任务栏 / Alt+Tab / 任务视图（`WS_EX_TOOLWINDOW`）
- **单实例运行 = 开关语义**：键盘已在运行时再次启动它，不弹「已在运行」提示框，而是让运行中的实例
  **显示 ↔ 隐藏**（命名事件 `TouchKeyboard_ToggleRequest_v2`），后启动的实例静默退出。
  正因如此，它可以被系统的触摸键盘入口（任务栏按钮 / `Win+Ctrl+O`）直接当目标拉起；
  每次启动还会在 exe 同级 `kb-launch.log` 记一行「命令行 + 账户 + 会话号」，便于排查是谁拉起的
- 窗口位置、尺寸、各开关状态持久化到同目录 `TouchKeyboard.ini`
- **接管系统触摸键盘入口**：见 [`OskRedirect/`](OskRedirect/README.md) ——
  把任务栏「触摸键盘」按钮、`osk.exe`、`TabTip.exe` 都重定向到本键盘（适合 TabTip 被删掉的精简系统）

### AutoRotate —— 重力自动旋转守护（PowerShell）

- WinRT 加速度计读取姿态 + Win32 `ChangeDisplaySettingsEx` 旋转屏幕
- **跟随系统「旋转锁定」**：默认读取
  `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AutoRotation\Enable`
  （控制中心 / 操作中心里的「旋转锁定」磁贴、设置 → 系统 → 显示 → 旋转锁定，写的是同一个值；
  `0` = 锁定开，`1` = 允许自动旋转）。锁定开着时自动旋转暂停、手动旋转仍可用，解锁后自动恢复；
  可用 `config.json` 的 `respectRotationLock: false` 或命令行 `-IgnoreLock` 关掉这一行为
- 附带手动旋转 / 暂停 / 校准 / 自检的一键脚本（`.cmd`）
- 单实例互斥，支持暂停标记文件，日志记录每次旋转结果

### TouchRedirect —— 让桌面游戏响应手指（两种机制，按前台程序自动选）

针对"桌面能点、游戏里点不动"：程序内置两套机制，按**前台进程名单**自动选最省事的那套。

- **`[convert]` 改写模式**（不需要任何特权）：吞掉带 `0xFF515700` 触摸签名的鼠标消息，
  用 `SendInput` 重注入干净的鼠标事件 —— 适用于"走鼠标消息通路、只是忽略触摸签名或不读鼠标消息"的游戏
- **`[redirect]` 重定向模式**（需要 **UI Access**）：`RegisterPointerInputTarget(PT_TOUCH)` 把整个桌面的触摸
  先收到自己窗口，再转成真实鼠标事件 —— 连"自己处理 `WM_POINTER*`、系统不再做提升"的引擎（如 Unreal）也能救
- 名单外的程序一律不插手，触摸保持原生（双指缩放、长按右键都在）
- **UI Access 用自签证书就能拿到**（实测：自签证书装进本机受信任根 + 放 Program Files 即可），
  安装脚本一条龙包办：建证书 → 装信任 → 编译嵌清单 → 签名 → 装 `C:\Program Files\` → 建快捷方式 → 启动
- 托盘常驻：状态 / 暂停 / 重新加载配置 / 打开配置 / 打开日志 / 退出
- 顺带处理**目标程序自己的鼠标指针**：类光标置空（每 500 ms 复查）+ 改写该进程 `user32!SetCursor` 的导入表项，
  让它"设置自己的光标"变成设置 NULL（并完整还原）

### TouchToMouse —— 触摸→鼠标的诊断与改写（鼠标钩子路线，无需特权）

- **探针模式**：只观察、不改动，判定某款游戏属于哪一类问题（有没有触摸派生的鼠标消息）
- **转换模式**：吞掉触摸派生的点击、重注入干净鼠标事件；可按前台进程名单生效
- **自检**：用"假触摸"事件验证"钩子看到几次 → 目标窗口就只能收到几次"
- 附带环境诊断（窗口站 / 桌面 / 完整性级别）与两组"钩子自检"，用来先排除权限问题

> 原理、实测数据与四类原因见 [`游戏触摸不响应-原理与实测.md`](游戏触摸不响应-原理与实测.md)。

### 诊断与维护脚本

| 目录 | 用途 |
|---|---|
| `RotationCheck/` | 屏幕方向与 `ConvertibleSlateMode`（平板 / 笔记本模式）诊断 |
| `OskCheck/` | 系统屏幕键盘（TabTip / osk）可用性检查 |
| `OskRedirect/` | 把系统「触摸键盘」入口（任务栏按钮 / `osk.exe` / `TabTip.exe`）重定向到自研键盘 |
| `UpdateControl/` | Windows 更新的分层禁用 / 恢复（策略 + 服务 + 计划任务） |
| `Migration/` | 用户配置迁移到新账号 |

## 目录结构

```
windows-tablet-kit/
├─ TouchKeyboard/          # 自研触控键盘
│  ├─ TouchKeyboard.v2.cs  # 当前源码（唯一真源）
│  ├─ TouchKeyboard.cs     # 早期版本源码（保留作参考）
│  ├─ SetAutostart.ps1     # 开机自启 安装 / 卸载
│  └─ *.cmd                # 一键脚本（自启安装 / 卸载等）
├─ AutoRotate/             # 重力自动旋转守护 + 一键脚本
├─ TouchRedirect/          # 触摸→鼠标：改写 + 全局重定向（UI Access），安装 / 卸载脚本齐全
├─ TouchToMouse/           # 触摸→鼠标：诊断探针 + 改写（鼠标钩子路线，无需特权）
├─ RotationCheck/          # 屏幕方向 / 平板模式诊断
├─ OskCheck/               # 屏幕键盘诊断
├─ OskRedirect/            # 把系统触摸键盘入口重定向到本键盘（安装 / 卸载脚本）
├─ UpdateControl/          # 系统更新禁用 / 恢复
├─ Migration/              # 配置迁移
├─ NB_TouchKeyboard_资料/  # 第三方同类方案的文字说明（不含其二进制）
└─ *.md                    # 诊断记录与方案调研
```

## 构建

`TouchKeyboard` 用 PowerShell 内联编译，**不需要 Visual Studio**：

```powershell
$src  = '.\TouchKeyboard\TouchKeyboard.v2.cs'
$code = Get-Content $src -Raw -Encoding UTF8
Add-Type -TypeDefinition $code -Language CSharp `
  -OutputAssembly "$env:TEMP\TouchKeyboard.exe" -OutputType WindowsApplication `
  -ReferencedAssemblies System.Windows.Forms,System.Drawing,UIAutomationClient,UIAutomationTypes
```

- 源码把**警告当作错误**（未使用的字段 / 变量都会导致编译失败）
- 目标 exe 被占用时无法覆盖：编译前请先退出正在运行的键盘
- 仓库内的一键脚本默认按 `C:\Tools\TouchKeyboard\` 部署路径工作；放到别处需自行改脚本里的路径

## 使用要点

- **AutoRotate 的自启动有两个入口**：计划任务 `AutoRotate` 与启动文件夹里的 `AutoRotate.vbs`，
  两者互为备份（单实例互斥保证只跑一个）。`schtasks /create` 建出来的任务默认带
  「电池供电时不启动 / 切到电池就停止」和 72 小时执行上限 —— 平板用电池时会导致
  「装了自启但没在跑」；`AutoRotate\安装-开机自动旋转.cmd` 已改为用 `Register-ScheduledTask`
  注册电池友好、无时限的任务，并在启动后**用日志增长校验**、最后打印结果。
  屏幕不自动转时：先跑 `AutoRotate\状态与自检.cmd`（看守护进程是否在跑、旋转锁定是否开着），
  再跑 `AutoRotate\安装-开机自动旋转.cmd` 重装自启。
- 首次运行会在 exe 同级生成 `layouts.ini`（键盘布局）与 `TouchKeyboard.ini`（设置）
- 命令行参数：`-show` 启动即显示、`-hidden` 启动即驻留托盘、`-dock` 启动即贴底、
  `-nosnap` 关闭吸附、`-auto` / `-noauto` 自动呼出开关、`-debug` 写 `kb-debug.log` 便于排查、
  `-comserve` 额外注册 `CLSID_UIHostNoLaunch` 的 COM 类工厂（**默认关闭**，原因见
  [`OskRedirect/README.md`](OskRedirect/README.md) 第三节：精简系统上没有该接口的 ProxyStub，
  跨进程必然 `E_NOINTERFACE`，注册只会招来每 2 分钟一次的失败重试）
- 布局文件格式见 `layouts.ini` 头部注释：`Row: 键名[*宽度] ...`，支持
  单字符、`esc tab caps enter bk del ins home end pgup pgdn shift ctrl alt win space zh close left up down right f1..f12`
- 贴底状态下请通过键盘上的「贴底」开关或托盘菜单退出；**强杀进程可能让 Windows 残留 AppBar 占位**
  （表现为桌面工作区变小 / 最大化窗口变矮），重新打开再正常关闭、或重启资源管理器即可恢复
- 退出请走托盘菜单，避免留下"幽灵托盘图标"（鼠标划过托盘区即可清除单个残留）

## 已知限制

- 无法向"以管理员身份运行"的窗口输入（Windows UIPI 完整性级别限制，所有非 UIAccess 键盘同理）
- 登录界面 / UAC 安全桌面不可用
- 极少数全自绘输入框（例如网页富文本编辑区）可能不触发自动呼出，此时可从托盘手动呼出
- **多指同时操作**（一根手指按住修饰键、另一根指点字母）在部分环境不可用：
  Windows 只把首个触点模拟为鼠标事件；`WM_TOUCH` 多点通路在真机上未充分验证，故默认关闭
- 贴底占位依赖 Windows AppBar 机制，不同系统版本表现可能略有差异
- **被系统触摸键盘服务拉起时可能是 SYSTEM 身份**（`OskRedirect/` 的做法）：键盘能显示、能输入，
  但托盘图标的右键菜单可能点不动（UIPI 不允许低完整性进程给高完整性窗口发消息）。
  用键盘上的 `✕` 关掉后，从开始菜单快捷方式重新启动即可回到用户身份
- **`TouchRedirect` 的重定向模式需要 UI Access**：必须由受信任证书签名（自签即可）**并装在 `Program Files`**，
  否则 `RegisterPointerInputTarget` 返回 `ERROR_ACCESS_DENIED`；直接运行源码目录编译出的 exe 会报
  `A referral was returned from the server`。同一桌面同一时刻只能有一个触摸重定向注册者
- **`TouchRedirect` 会改目标进程的内存**（把 `user32!SetCursor` 的导入表项指向一个小桩，用于隐藏游戏自己的指针）：
  改写前会校验、退出/暂停会还原；但**若工具在此状态下被强杀**，目标进程会保持"看不到指针"直到它重启。
  不想要这一行为就把配置里的 `suppresscursor` 设为 `0`
- **不要在内核反作弊的联机游戏上使用**：注入的是真实鼠标输入，一般不会被判违规，但请自行权衡
- `hidecursor` 那几档（隐藏**系统**光标）慎用：Windows 只在加载光标方案时应用可访问性指针的缩放与颜色，
  工具用原始 `.cur` 回写会丢掉这些设置，指针外观会与设置不一致（附 `修复光标外观.cmd` 一键恢复）

## 免责声明

- 本仓库脚本会**修改系统设置**（注册表、服务、计划任务、启动项）与**显示方向**，
  请先看清脚本内容再执行，建议逐项确认；由此产生的后果由使用者自行承担
- 屏幕旋转依赖显卡驱动与显示器支持，不保证所有机型可用
- 本项目仅供学习与自用，作者不对任何数据丢失或系统异常负责

## 许可

MIT License，见 [LICENSE](LICENSE)。
