# OskRedirect —— 把系统「触摸键盘」入口接管到自研 TouchKeyboard

面向**被精简过的 Windows**：任务栏那个「触摸键盘」按钮点了没反应、`Win+Ctrl+O` 也叫不出键盘时，
用这个目录里的脚本把系统入口重定向到本仓库的 `TouchKeyboard.exe`。

> 已在 Windows 10 专业版 22H2（19045，TabTip.exe 被物理删除的精简系统）上真机验证：
> 点任务栏「触摸键盘」按钮 → 自研键盘弹出/收起。

## 一、为什么"补一个 TabTip.exe 文件"不够

触摸键盘进程的路径**不是写死在代码里的**，是服务从注册表读的。在 `C:\Windows\System32\TabSvc.dll`
（TabletInputService 的实现）里能扫到这段字符串：

```
PENSERVICE_CTabTipProcessInfo::_GetPath
CLSID\{054AAE20-4BEA-4347-8A35-64A533254A9D}\LocalServer32
```

也就是说：**服务从 `HKLM\SOFTWARE\Classes\CLSID\{054AAE20-…}\LocalServer32` 取「触摸键盘 exe 在哪」**。
精简系统上这个键通常和 `TabTip.exe` 一起被删掉，于是按钮连"要启动哪个 exe"都查不到 → 静默失败。

顺带一提：`osk.exe`（经典屏幕键盘）在精简系统上常常是**文件还在但起不来**（UIAccess 链断了，
报「无法启动屏幕键盘。」），所以也不能指望它。用 `..\OskCheck\` 里的脚本可以先确认。

## 二、做法

`Install-OskRedirect.ps1`（需要管理员）：

1. 建 `HKLM\SOFTWARE\Classes\CLSID\{054AAE20-4BEA-4347-8A35-64A533254A9D}\LocalServer32`
   = 自研键盘 exe 的完整路径（带引号）。
2. 给 `osk.exe`、`TabTip.exe` 挂 IFEO：`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<名字>\Debugger`
   = 同一个 exe → 任何按名字拉起 `osk`/`TabTip` 的入口（`Win+Ctrl+O`、第三方快捷键、开始菜单快捷方式）也走自研键盘。
3. 建一个开始菜单快捷方式。**再次启动 = 显示/隐藏**（见第四节），所以它本身就是个可钉到任务栏的开关。

不碰任何系统文件（不往 `Program Files` / `System32` 写东西），全部改动可用 `Uninstall-OskRedirect.ps1` 撤销。

## 三、不要走 COM 那条路（已踩过，记下来）

第三方程序（Chromium、各种 AutoHotkey 脚本）调用触摸键盘走的是
`CLSID_UIHostNoLaunch {4CE576FA-83DC-4F88-951C-9D0782B4E376}` + `ITipInvocation::Toggle`。

**在精简系统上这条路补不起来**，原因是跨进程封送：

- 该接口是自定义 `IUnknown` 基接口，跨进程必须注册 ProxyStub；
  本机 `HKLM\SOFTWARE\Classes\Interface\{37C994E7-432B-4834-A2F7-DCE1F13B834B}` **不存在**
  （在完整系统上是系统自带注册的）。
- 实测：自己实现 `IClassFactory` 并 `CoRegisterClassObject` 后，客户端 `CoCreateInstance` 能成功，
  但随后 `QueryInterface(IID_ITipInvocation)` **必然 `E_NOINTERFACE`** —— 即使同用户同会话、同机。
- 更糟的副作用：外壳会**每 2 分钟重试一次**这次注定失败的激活，而 SCM 每次都会把"注册不上类对象的
  LocalServer32 进程"结束掉（表现为日志里每隔 2 分钟一条新的 `-Embedding` 启动、进程活不过 2 分钟）。
  实测期间该机 explorer.exe 崩过一次（该机本身也有高负载下外壳重启的历史），
  出于稳定性考虑，`{4CE576FA-…}` 注册项已被删除，键盘也默认不再注册类工厂（见 `-comserve`）。

结论：**只保留"服务读路径 + IFEO"这条 CreateProcess 路线**，它不需要任何跨进程封送。

## 四、配套的键盘行为

`TouchKeyboard.exe` 为这个用途加了三件事：

| 行为 | 说明 |
|---|---|
| **单实例 = 开关** | 键盘已在运行时再次启动它，不会弹「已在运行」对话框，而是通过命名事件 `TouchKeyboard_ToggleRequest_v2` 让运行中的实例**显示↔隐藏**；第二实例静默退出。按钮每次都是把 exe 当第二实例拉起，所以这项是按钮能用的关键。 |
| **`-Embedding` 兜底** | 若被以 COM LocalServer32 方式拉起（命令行带 `-Embedding`），先保持隐藏等 `Toggle()`；1.2s 内没等到就自己显示。 |
| **`kb-launch.log`** | 每次启动（第一实例）追加一行：时间、命令行、账户、会话号 —— 用来确认"是谁、以什么身份把键盘拉起来的"。 |

## 五、实测记录（Windows 10 22H2 / 19045，TabTip.exe 已删）

`C:\Tools\TouchKeyboard\kb-launch.log`：

| 时间 | 命令行 | 身份 | 说明 |
|---|---|---|---|
| 16:47–16:53（每 2 分钟） | `-Embedding` | 用户 / 会话 1 | COM 索取（SCM 拉起）——见第三节，已停用 |
| 19:54:49 | `/QuitInfo:0000000000000910;0000000000000880;` | **SYSTEM** / 会话 1 | 服务直接 CreateProcess（开机 14 秒） |
| 19:55:01 | `-hidden` | 用户 / 会话 1 | 用户的开机自启 VBS |
| 20:05:38 | `/QuitInfo:…` | SYSTEM / 会话 1 | 键盘被关闭 10 秒后，服务又拉了一次 |
| 20:13:02 | 无参数 | 用户 / 会话 1 | 用户点开始菜单快捷方式 |

**用户点任务栏「触摸键盘」按钮 → 键盘弹出**（键盘当时已在托盘运行，按钮触发的其实是第二实例→转发开关，
所以日志不新增行，这是正确行为）。

## 六、已知副作用

1. **服务是以 SYSTEM 身份拉起键盘的**。此时键盘能显示、能输入（SYSTEM 甚至可以往"以管理员身份运行"的
   窗口输入），但**托盘图标的右键菜单可能点不动**（UIPI 不允许低完整性进程给高完整性窗口发消息）。
   日常不受影响：键盘由用户身份自启常驻托盘，点按钮走"第二实例转发"。
   万一真的遇到 SYSTEM 实例占着单实例互斥体（自己的快捷方式只能切换、起不来用户身份实例），
   用键盘上的 `✕` 关掉它，再从快捷方式启动即可。
2. **键盘进程消失后服务会再拉一次**（带 `/QuitInfo:…`，自研键盘不认这个参数）→ 表现为键盘"自己冒出来"。
   点一次 `✕` 或快捷方式即可。
3. 触摸键盘按钮只在**平板模式**下自动显示；关掉平板模式时该按钮宽 0（系统行为）。
   打开方式：平板模式，或任务栏空白处右键 →「显示触摸键盘按钮」。

## 七、用法

```powershell
# 管理员 PowerShell（默认键盘路径 C:\Tools\TouchKeyboard\TouchKeyboard.exe）
powershell -ExecutionPolicy Bypass -File .\Install-OskRedirect.ps1
# 键盘在别处：
powershell -ExecutionPolicy Bypass -File .\Install-OskRedirect.ps1 -KeyboardPath "D:\Tools\TouchKeyboard\TouchKeyboard.exe"
# 只改 CLSID、不挂 IFEO / 不建快捷方式：
powershell -ExecutionPolicy Bypass -File .\Install-OskRedirect.ps1 -NoIfeo -NoShortcut

# 撤销
powershell -ExecutionPolicy Bypass -File .\Uninstall-OskRedirect.ps1
```

也可以直接双击同目录下的 `安装-接管触摸键盘入口.cmd` / `卸载-恢复系统触摸键盘入口.cmd`（会弹 UAC）。
