# 触摸转鼠标类

> 解决的问题：**桌面能点、游戏里点不动。**
> 先判定某款游戏属于哪一类，再选最省事的机制；名单外的程序一律不插手，触摸保持原生。

| 子目录 | 机制 | 需要特权 |
|---|---|---|
| [`TouchToMouse/`](TouchToMouse/README.md) | 鼠标钩子路线：**探针**（只观察）+ 转换（吞掉触摸派生的点击、重注入干净鼠标事件）+ 自检 | **不需要任何特权** |
| [`TouchRedirect/`](TouchRedirect/README.md) | 一个程序内置两套机制：`[convert]` 改写模式（无需特权）+ `[redirect]` 全局触摸重定向（需 UI Access） | `[redirect]` 需签名 + 装 Program Files |
| [游戏触摸不响应-原理与实测.md](游戏触摸不响应-原理与实测.md) | 原理、四类原因、实测数据（Stacklands / 叛逆神魂 GODSOUL）与对策 | — |

## 为什么"桌面能点、游戏点不动"

Windows 把主触点**提升（promotion）**成一套旧式鼠标消息，并打上触摸签名：

```
GetMessageExtraInfo() & 0xFFFFFF00 == 0xFF515700     // 低 7 位 = 触点 id；0x80 位 1=触摸 0=笔
```

断在这条链路的哪一环，就决定了能不能救：

| 类别 | 现象 | 能不能救 |
|---|---|---|
| 1. 程序自己处理 `WM_POINTER*` 且不调 `DefWindowProc` | 系统不再提升，**连鼠标消息都没有** | 任何"鼠标钩子式"方案都救不了 → 只能全局重定向 |
| 2. 收到了带签名的消息但主动忽略 | 桌面能点、游戏不点 | 吞掉签名消息、重注入 `ExtraInfo=0` 的事件 |
| 3. 根本不读鼠标消息（RawInput / DirectInput / 轮询） | 同上 | `SendInput` 注入在系统看来是真实鼠标输入，RawInput 也收得到 |
| 4. 环境 / 权限 | 管理员窗口、全屏独占、低完整性钩子收不到别的进程输入 | 以管理员运行或与目标同权限 |

**判定步骤**：跑 `TouchToMouse\1-探针模式.cmd`（不改动任何输入），在游戏里用手指点几下——

- 日志里有 `TOUCH L-DOWN/L-UP … extra=0xFF5157xx` → 第 2/3 类 → 用 `[convert]` / 转换模式；
- 一条 `TOUCH` 都没有 → 第 1 类 → 必须用 `TouchRedirect` 的 `[redirect]` 全局重定向。

实测样本：Stacklands（Unity）20 秒内 88 条触摸事件 → `[convert]` 生效；
叛逆神魂 GODSOUL（UE5）零鼠标事件 → `[convert]` 无效，`[redirect]` 生效。

## 两个工具怎么选

- **日常用 `TouchRedirect`**：一个程序两套机制，按**前台进程名单**自动选，托盘常驻、配置热加载。
- **`TouchToMouse` 保留的理由是"探针"**：它不需要签名、不需要 UI Access，随时能判断"这个游戏到底属于哪一类"，
  环境干净、结论可靠。转换模式与 `TouchRedirect` 的 `[convert]` 重叠。

## 必须记住的两条

1. **低级钩子回调里绝不能注入输入**（`TouchToMouse/README.md` 有血泪教训）：在 `WH_MOUSE_LL` 回调里直接调
   `SendInput` 会**内核态死锁**——进程卡在"正在终止"、任务管理器与 `taskkill`（含提权）都杀不掉。
   正确做法是回调只做"判断 + 入队 + `return 1`（吞掉）"，真正的注入交给 UI 定时器。
2. **`[redirect]` 必须签名 + 装进 `Program Files`**：`RegisterPointerInputTarget` 要求 UI Access，
   否则 `ERROR_ACCESS_DENIED`；直接运行源码目录编译的 exe 会报 `A referral was returned from the server`。
   实测**自签证书**（装进本机「受信任的根」+「受信任的发布者」）+ Program Files 就够用，不必买商业证书。
   同一桌面同一时刻只能有一个触摸重定向注册者。

## 风险提示

- `TouchRedirect` 的 `suppresscursor` 会**改写目标进程导入表里 `user32!SetCursor`**（让它设光标变成设 NULL）：
  改写前会校验、退出 / 暂停会还原，但**若工具在此状态下被强杀**，目标进程会一直看不到指针，直到它重启。
  不想要就把配置里的 `suppresscursor` 设为 `0`。
- `hidecursor` 那几档（隐藏**系统**光标）慎用：Windows 只在加载光标方案时应用可访问性指针的缩放与颜色，
  工具用原始 `.cur` 回写会丢掉这些设置，指针外观会与设置不一致（附 `修复光标外观.cmd` 一键恢复）。
- **不要在内核反作弊的联机游戏上使用**：注入的是真实鼠标输入，一般不会被判违规，但请自行权衡。

与 `[redirect]` 配套的光标实验 / 紧急恢复脚本都在 `TouchRedirect/` 内，见其 [README](TouchRedirect/README.md)。
