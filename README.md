# Windows Tablet Kit —— 精简版 Windows 平板的输入与显示能力替代方案

当系统缺失 `TabTip.exe` / `osk.exe`（触摸键盘叫不出来），或缺失 `SensorService` / 传感器组件
（屏幕不自动旋转）时，本仓库用**自研实现 + 诊断脚本**替代这两个能力，并附带"让桌面游戏响应手指"
与系统维护工具。

> 背景：部分 OEM / 精简版 / 嵌入式 Windows 镜像会移除平板相关的可选组件，
> 典型症状是"点输入框没反应""平板转了但屏幕不转"。本仓库给出可直接使用的替代实现，以及定位这类问题的诊断脚本。
> **本仓库的结论全部来自一台实机**，硬件与系统细节见 [机器环境.md](机器环境.md)。

## 目录结构（按大项目分类）

```
windows-tablet-kit/
├─ README.md                        ← 本文件：项目说明
├─ 机器环境.md                       ← 机器环境信息（症状归因与实测平台事实）
├─ 机器环境-详细诊断记录.md           ← 原始诊断记录（含回滚命令与联网修复全过程）
├─ 触控键盘/                         ← 触摸键盘类
│  ├─ README.md
│  ├─ TouchKeyboard/                 自研触控键盘本体（C# / WinForms，单文件 exe）
│  ├─ OskRedirect/                   把系统触摸键盘入口重定向到自研键盘
│  ├─ OskCheck/                      系统屏幕键盘可用性检查
│  ├─ NB_TouchKeyboard_资料/          第三方同类方案说明（不含其二进制）
│  └─ 第三方屏幕键盘方案调研.md
├─ 屏幕旋转/                         ← 屏幕旋转类
│  ├─ README.md
│  ├─ AutoRotate/                    重力自动旋转守护（加速度计 + ChangeDisplaySettingsEx）
│  └─ RotationCheck/                 屏幕方向 / 平板模式 / 旋转锁定诊断
├─ 触摸转鼠标/                       ← 触摸输入类
│  ├─ README.md
│  ├─ TouchRedirect/                 改写 + 全局重定向（两套机制自动切换）
│  ├─ TouchToMouse/                  诊断探针 + 改写（鼠标钩子路线）
│  └─ 游戏触摸不响应-原理与实测.md
├─ 系统维护/                         ← 系统维护类
│  ├─ README.md
│  ├─ UpdateControl/                 Windows 更新的分层禁用 / 恢复
│  └─ Migration/                     用户配置迁移到新账号
└─ LICENSE
```

## 四大类速览

| 大类 | 解决什么 | 关键实现 | 需要权限 |
|---|---|---|---|
| [触控键盘](触控键盘/README.md) | 触摸键盘叫不出来；任务栏键盘按钮失效 | 自研键盘（自动呼出 / AppBar 贴底 / 布局热加载）+ 注册表与 IFEO 接管系统入口 | 键盘本体普通用户；接管入口需管理员 |
| [屏幕旋转](屏幕旋转/README.md) | 平板转了屏幕不转 | 读加速度计姿态 + 自研守护转屏，绕开被删的方向传感器；跟随系统「旋转锁定」 | 普通用户（自启需管理员） |
| [触摸转鼠标](触摸转鼠标/README.md) | 桌面能点、游戏里点不动 | 鼠标消息改写（无需特权）/ 全局触摸重定向（需 UI Access，自签证书够用） | 视机制而定 |
| [系统维护](系统维护/README.md) | 更新乱重启；换账号搬数据 | 更新的分层禁用与备份还原；个人文件迁移 | 管理员 |

## 按症状选入口

| 症状 | 先做什么 |
|---|---|
| 点输入框没反应 / 触摸键盘叫不出来 | 跑 [`触控键盘\OskCheck\检查屏幕键盘.cmd`](触控键盘/OskCheck/CheckOsk.ps1) 确认系统键盘状态，再部署 [`TouchKeyboard`](触控键盘/TouchKeyboard/) |
| 任务栏键盘按钮 / `Win+Ctrl+O` 点了没反应 | [`触控键盘\OskRedirect`](触控键盘/OskRedirect/README.md)（先读它的第三节） |
| 平板转了，屏幕不转 | `屏幕旋转\AutoRotate\状态与自检.cmd`，再按 [屏幕旋转/README.md](屏幕旋转/README.md) 的顺序查 |
| 桌面能点、游戏里点不动 | [`触摸转鼠标\TouchToMouse\1-探针模式.cmd`](触摸转鼠标/TouchToMouse/README.md) 判定类别 |
| 更新总在乱重启 | [`系统维护\UpdateControl\禁用系统更新.cmd`](系统维护/UpdateControl/DisableWindowsUpdate.ps1)（先 `-DryRun` 预览） |
| 换账号要搬数据 | [`系统维护\Migration\迁移到新账号.cmd`](系统维护/Migration/Migrate.ps1)（先 `-DryRun`） |

## 构建

`TouchKeyboard` 用 PowerShell 内联编译，**不需要 Visual Studio**：

```powershell
$code = Get-Content '.\触控键盘\TouchKeyboard\TouchKeyboard.v2.cs' -Raw -Encoding UTF8
Add-Type -TypeDefinition $code -Language CSharp `
  -OutputAssembly "$env:TEMP\TouchKeyboard.exe" -OutputType WindowsApplication `
  -ReferencedAssemblies System.Windows.Forms,System.Drawing,UIAutomationClient,UIAutomationTypes
```

- ⚠️ **必须用 Windows PowerShell 5.1**（`powershell.exe`）：PowerShell 7 的 `Add-Type` 不支持
  `-OutputType WindowsApplication`。本仓库已在 5.1 下实测编译通过（57 344 字节，零警告）。
- 源码把**警告当错误**（未使用的字段 / 变量都会导致编译失败）。
- 目标 exe 被占用时无法覆盖：编译前先退出正在运行的键盘。
- 仓库内的一键脚本默认按 `C:\Tools\TouchKeyboard\` 部署路径工作；放到别处需自行改脚本里的路径。

## 使用要点

- **首次运行**会在 exe 同级生成 `layouts.ini`（键盘布局）与 `TouchKeyboard.ini`（设置）。
- **命令行参数**：`-show` / `-hidden` / `-dock` / `-nosnap` / `-auto` / `-debug` / `-comserve` / `-embedding`；
  上游文档提到的 `-noauto` 在源码里**没有实现**（详见 [触控键盘/README.md](触控键盘/README.md)）。
- **贴底状态下请通过键盘上的「贴底」开关或托盘菜单退出**：强杀进程可能让 Windows 残留 AppBar 占位
  （桌面工作区变小 / 最大化窗口变矮），重开再正常关闭、或重启资源管理器即可恢复。
- 退出请走**托盘菜单**，避免留下"幽灵托盘图标"（鼠标划过托盘区即可清除残留）。
- **AutoRotate 的自启动有两个入口**：计划任务 `AutoRotate` 与启动文件夹里的 `AutoRotate.vbs`，互为备份
  （单实例互斥保证只跑一个）。屏幕不自动转时：先跑 `状态与自检.cmd`，再跑 `安装-开机自动旋转.cmd` 重装自启。
- 所有会改系统的脚本**都先备份**（更新控制会写 `backup-<时间戳>/`），但请仍然**先读脚本、先用 `-DryRun`**。

## 已知限制

- **UIPI 完整性级别**：非 UIAccess 键盘无法向"以管理员身份运行"的窗口输入；登录界面 / UAC 安全桌面不可用。
- **多指同时操作**在部分环境不可用：Windows 只把首个触点模拟为鼠标事件；`WM_TOUCH` 多点通路在真机未充分验证，
  自研键盘里默认关闭（源码 `touchRoutingEnabled = false`）。
- **贴底占位**依赖 Windows AppBar 机制，不同系统版本表现可能略有差异。
- **被系统触摸键盘服务拉起时可能是 SYSTEM 身份**：键盘能显示、能输入，但托盘右键菜单可能点不动。
- **`TouchRedirect` 的重定向模式需要 UI Access**：必须由受信任证书签名（自签即可）**并装在 `Program Files`**；
  同一桌面同一时刻只能有一个触摸重定向注册者。
- **`TouchRedirect` 会改目标进程的内存**（`user32!SetCursor` 导入表项）；若工具在此状态下被强杀，
  目标进程会保持"看不到指针"直到它重启。不想要就把 `suppresscursor` 设为 `0`。
- **不要在内核反作弊的联机游戏上使用触摸改写**：注入的是真实鼠标输入，一般不会被判违规，但请自行权衡。
- `hidecursor` 那几档（隐藏**系统**光标）慎用：回写原始 `.cur` 会丢掉可访问性指针的缩放与颜色
  （附 `修复光标外观.cmd` 一键恢复）。
- 屏幕旋转依赖显卡驱动与显示器支持，不保证所有机型可用。

## 与上游的关系

本仓库克隆自 <https://github.com/kaniol-lck/windows-tablet-kit>，并按大项目重整了目录结构、
补充了顶层文档（[机器环境.md](机器环境.md)、各类 `README.md`）：

- **只做移动与文档增补，未改动任何源码或脚本逻辑**；
- 原三份散在顶层的文档分别归入对应大类，机器的诊断记录留在顶层并改名为
  [机器环境-详细诊断记录.md](机器环境-详细诊断记录.md)；
- git 历史完整保留（目录移动被识别为 rename）。

## 编码约定

仓库里所有文本文件都是 **UTF-8**，但**是否带 BOM 取决于谁来读它**——这是实测出来的规则，别改：

| 文件类型 | 编码 | 原因 |
|---|---|---|
| `.ps1` / `.cs` | **UTF-8 with BOM** | 受支持的 PowerShell 是 Windows PowerShell 5.1，它按 **ANSI 代码页**解码无 BOM 的 `.ps1`：中文变乱码，严重时注释里的字节会让整个脚本**解析失败**。`.cs` 与仓库既有文件（`TouchKeyboard.v2.cs`、`TouchRedirect.cs`）保持一致 |
| `.cmd` | **UTF-8 无 BOM** + 首行 `chcp 65001 >nul` | 实测：给 `.cmd` 加 BOM 后，在 936 / 437 代码页下 cmd 解析首行报错、命令被拼坏；无 BOM + `chcp 65001` 则在 936 / 437 / 65001 下都能正确解析。**凡是 `.cmd` 里出现中文路径，必须有这一行** |
| `.md` / `.txt` | UTF-8 无 BOM | Markdown 与 GitHub 的通行约定；`games.txt` 是被 `for /f "usebackq tokens=* eol=;"` 读的，加 BOM 会让首行 `;` 注释不再以 `;` 开头而被当成进程名 |

新增脚本时请按上表保存；在无 BOM 的 `.ps1` 里写中文路径，实测会得到"文件不存在"这类莫名其妙的报错。

## 免责声明

- 本仓库脚本会**修改系统设置**（注册表、服务、计划任务、启动项）与**显示方向**，并会**移动大量个人文件**。
  请先看清脚本内容再执行，建议逐项确认；由此产生的后果由使用者自行承担。
- 屏幕旋转依赖显卡驱动与显示器支持，不保证所有机型可用。
- 本项目仅供学习与自用，作者不对任何数据丢失或系统异常负责。

## 许可

MIT License，见 [LICENSE](LICENSE)。
