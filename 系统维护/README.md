# 系统维护类

> 这台平板是被"精简 / 优化"过的镜像：更新源无法修复被删组件，所以更新需要能**分层关掉**；
> 换账号时又要把个人数据搬过去。这一大类是配套的维护脚本。

| 子目录 | 用途 | 需要权限 |
|---|---|---|
| [`UpdateControl/`](UpdateControl/) | Windows 更新的分层禁用 / 恢复（策略 + 服务 + 计划任务 + 可选 hosts 网络层） | 管理员 |
| [`Migration/`](Migration/) | 用户配置迁移到新账号 | 管理员 |

## UpdateControl

| 脚本 | 说明 |
|---|---|
| `禁用系统更新.cmd` | 一键入口，自动提权 → 跑下面那个脚本 |
| `DisableWindowsUpdate.ps1` | 分层禁用；**所有改动前先备份**到 `backup-<时间戳>/`（策略 `.reg`、服务启动类型 `service-starts.json`、计划任务 `tasks.json`、`hosts.bak`） |
| `恢复系统更新.cmd` / `RestoreWindowsUpdate.ps1` | 按备份逐项还原 |

- `DisableWindowsUpdate.ps1 -DryRun`：只预览、不改任何东西，**不需要管理员**。建议先跑这个看它要动什么。
- `-BlockNetwork`：额外用 hosts 封锁更新域名（可选，默认不做）。
- 运行日志：脚本同目录 `disable-wu-<时间戳>.log`。
- 脚本默认按 `C:\Tools\UpdateControl\` 工作；放在别处会以脚本自身目录为准。

> 为什么要禁更新：见 [机器环境.md](../机器环境.md) —— 本机更新源给不出被删组件的载荷，
> `DISM /RestoreHealth` 两次都以 `0x800f081f` 失败，所以更新留着也修不好系统，
> 反而会在不合时宜的时候重启。**要修复请走官方 ISO**（覆盖安装或作为 `DISM /Source:`）。

## Migration

| 脚本 | 说明 |
|---|---|
| `迁移到新账号.cmd` | 一键入口，自动提权 → 跑 `Migrate.ps1` |
| `Migrate.ps1` | 把个人信息迁到新账号 |

用法（`Migrate.ps1`）：

```powershell
Migrate.ps1                     # 自动判断源/目标：只搬个人文件 + 常用游戏/输入法数据
Migrate.ps1 -PersonalOnly       # 只搬个人文件
Migrate.ps1 -Full               # 尽量把 AppData 也搬（仍排除系统级/缓存/危险项）
Migrate.ps1 -DryRun             # 只预览，不写任何文件
Migrate.ps1 -Source $env:USERPROFILE -Dest C:\Users\<新账号>
```

安全约定（脚本头部写明的，值得记住）：

- **绝不使用 `/MIR` 或 `/PURGE`** —— 不会删除目标里的任何东西；
- **绝不复制** `NTUSER.DAT` / `UsrClass.dat` 等注册表文件；
- `/XJ` 跳过 junction，避免无限递归；
- **不复制文件 ACL**，让目标自动继承新账号自己的权限，避免所有权混乱。

日志：脚本同目录 `migration-<时间戳>.log`。默认只搬个人文件，`-AppData` / `-Full` 才碰 AppData。

## 免责

这两个工具都会**修改系统设置**（注册表、服务、计划任务、hosts）或**移动大量个人文件**。
请先读脚本、先用 `-DryRun` 预览。总免责声明见顶层 [README.md](../README.md)。
