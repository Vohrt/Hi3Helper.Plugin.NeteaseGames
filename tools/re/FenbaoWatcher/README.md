# FenbaoWatcher — 捕获基础分卷合并规则的现场

## 为什么需要它

`PatchN.mpk`（基础分卷）的**合并顺序**是所有公开数据里都推不出来的：

- 远端清单 `bigpatchlist_windows64_normal.txt` 只描述分块，不含排序；
- 本地索引 `Patch.mpkinfo` 只记录**已安装**的分卷布局，不含重建顺序；
- 合并顺序依赖运行时状态（`fenbao_migration_map` / `_pending_batches` / `_current_fenbao_name`），
  只在**正在改写基础层的那一轮更新过程中**存在于官方启动器的内存里。

已逐一证伪的排序假设（6 种）见 `docs/PROTOCOL-VERIFIED.md` §5.4。
当前安装 `fenbao_mode = 0`，无法在本地复现。

**结论：只能等网易发一次真正改写基础分卷的更新，并在那一刻抓取现场。**
这个工具干的就是这件事。

## 用法

**必须用管理员权限运行**（官方启动器是提权进程，不提权连它的内存都看不见）：

```powershell
cd <repo>\tools\re\FenbaoWatcher
dotnet build -c Release

# 提权 PowerShell 中：
.\bin\Release\net10.0\FenbaoWatcher.exe "D:\ywzh\ywzh_extreme" 5 "D:\fbw-evidence"
```

参数：`<游戏目录> [轮询秒数] [证据目录]`，默认 `D:\ywzh\ywzh_extreme`、5 秒、`%TEMP%\NeteaseFenbaoEvidence`。

启动时会做**预检**：确认能打开 `PCLauncher` 进程并读内存；失败会明确提示需要提权。

## 触发条件

满足任一即触发一次现场捕获：

1. `LocalData\Patch\fenbao_mode` 的值发生变化（`0` → 非 `0` 就是基础层改写开始）
2. 补丁目录的文件数增长超过 2 个
3. `LocalData\Patch\tmp_file` 目录出现（基础层暂存开始）

## 捕获内容

每次触发建立一个 `triggerNN_<时间戳>\` 目录：

| 内容 | 说明 |
|---|---|
| `trigger.txt` | 触发原因与时间 |
| `listing.txt` | 补丁目录完整清单 |
| `fenbao_mode` / `pkgversion` / `gameversion` / `patchmd5_*.txt` 等 | 状态文件原样副本 |
| `tmp_file_listing.txt` | 基础层暂存文件清单（只记路径，不复制，可能很大） |
| `proclist_<pid>.txt` | 启动器 pid / 启动时间 / 工作集 |
| `mem_*.bin` | **包含以下标记的内存区域**：`fenbao_migration_map`、`_current_fenbao_name`、`FenbaoTmpPatchMpk`、`bigpatchlist_windows64`、`big_common/` |

实测（2026-10-23 模拟触发）：单次捕获 **31 个文件 / 50 MB**，其中
`mem_0506_*_bigpatchlist_windows64.bin` 含 **34 条真实下载 URL**。

## 拿到证据之后

1. 在 `mem_*` 里搜 `fenbao_migration_map` / `_pending_batches`，还原「分块 → 分卷」的映射
2. 与 `bigpatchlist_windows64_normal.txt` 的 `big_common` 条目对照
3. 用 `Patch.mpkinfo`（已破解，见 `read_mpkinfo.py`）校验还原出的布局
4. 把结论补进 `docs/PROTOCOL-VERIFIED.md` §5.4，然后才可以把
   `VerifyBaseTierCompletable` 的拒绝逻辑替换为真正的实现

## 注意

- 证据目录**不要**放在 `%TEMP%`（本会话的沙箱对它有写限制，实测会失败），放普通磁盘目录。
- 官方启动器关闭后，运行时状态即消失；所以工具必须在**更新开始前**就已在运行。
- 建议开机后常驻，或至少在每次版本更新前启动。
