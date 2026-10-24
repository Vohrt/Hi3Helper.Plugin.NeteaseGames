# Netease 插件实测审计（2026-10-24）

> 对象：`Hi3Helper.Plugin.NeteaseGames`（工作树 `7660532` + 未提交改动）
> 真机：`D:\ywzh\ywzh_extreme`（《遗忘之海》），官方启动器 `D:\ywzh\ywzh_launcher`
> 对照：`Hi3Helper.Plugin.PerfectWorld`（PW）、`Hi3Helper.Plugin.Hypergryph`（HG）
>
> **本文每一条都标了来源**：`[实跑]` = 本轮在本机跑出的输出；`[探测]` = 本轮发出的 HTTP 请求；
> `[读码]` = 只读代码推断。仓库里 `AGENTS.md` / `README.md` / `docs/PROTOCOL-VERIFIED.md` /
> `docs/NETEASE-FIX-PLAN.md` 与本表冲突处，以本表为准（纠错见 §5）。
>
> 与 `NETEASE-FIX-PLAN.md` 的关系：那份文档写于同一天早些时候，**协议层结论仍然成立**，
> 但它的"实施进度"表已经过期（它把"已修"当成了"已生效"，而实际部署的二进制里没有这些修复），
> 且它没有发现本轮的 P0-1、P0-2。以本文为准。

---

## 0. 结论

协议逆向是这个仓库最强的一块，**但"用户实际在跑的那份插件"和"源码里的那份"不是同一个东西**，
而且源码里还有一个让"更新"永远不收敛的缺陷。这两条加起来，等于**今天这台机器上的增量更新完全不可用**。

| 维度 | PerfectWorld | Hypergryph | **Netease（实测）** |
|---|---|---|---|
| 单测 | 无 | 无 | **174/174 通过**（0 失败）`[实跑]` |
| CI build（非发布） | `build.yml` | `build.yml` | **无**（只有 `release.yml`） |
| 一键 AOT 脚本 | `CompileAOTAndShip.bat` | `CompileAOTAndShip.bat` | **无** |
| LICENSE / 双语 README | 有 / 有 | 有 / 有 | **无 / 无** |
| 代码量（Core+薄插件，不含子模块） | 17,533 行 | 25,449 行 | 8,231 行 `[实跑]` |
| 全新安装 | 支持 | 支持 | **硬拒（`NotSupportedException`）** |
| 增量更新 | 可用 | 可用 | **源码不可收敛（P0-2）；部署版直接抛异常（P0-1）** |

---

## 1. P0 —— 现在就不工作

### P0-1 部署的插件是旧版，而且**任何一次更新都会被它拒绝** `[实跑]`

| 项 | 值 |
|---|---|
| 部署位置 | `%LOCALAPPDATA%Low\CollapseLauncher\GameFolder\_plugins\Hi3Helper.Plugin.SoR\` |
| `SoR.dll` | **7,721,984 B**，2026-10-23 16:45 |
| `manifest.json` | `PluginVersion 0.4.0.0`，`ManifestDate 2026-10-23T08:45`，`Size/FileHash` 与磁盘 DLL **一致**（自校验通过） |

对部署的 DLL 做 UTF-16 字符串扫描，命中的是**旧逻辑**：

```
HAS   this round may rewrite the base shard tier (@bigpatchopen@
HAS   this round ships a full package (@is_full@
MISS  @bigpatchmd5@
MISS  fingerprint is unchanged
MISS  The index on disk does not describe the shard
```

对工作树 AOT 产物（`publish/Release/SoR.dll`，7,730,688 B）做同样扫描，结果正好相反：
含 `@bigpatchmd5@` / `fingerprint is unchanged` / `The index on disk does not describe the shard`，
**不含**旧文案。

而真机当前状态（`[实跑]`，直接调插件自己的 `ProbeRemoteVersionAsync`）：

```
远端 @bigpatchopen@ = true     本地记录 @bigpatchopen@ = true
远端 @bigpatchmd5@  = 6128997482385675719
本地 @bigpatchmd5@  = 6128997482385675719      ← 指纹相同，本轮是纯增量
远端 @is_full@      = false
```

⇒ **`@bigpatchopen@` 为 true，而部署版没有指纹比较逻辑 ⇒ 每次点"更新"都会抛
`NotSupportedException("This update cannot be applied by the plugin: …")`。**

> ⚠ 额外发现：部署的这个二进制**不对应任何可重建的源码状态**。工作树里那段旧文案已经不存在
> （`grep` 只命中 `NeteaseGamesManifests.cs:392` 的一行注释）；我遍历了仓库里全部 git blob
> （`git cat-file --batch-all-objects`）也 0 命中。说明它是某个已丢弃的中间构建。
> **不能靠"重新 build 一下就一样"来糊弄，必须重新构建 + 重新部署。**

**修复**：重新 AOT 构建 → 跑 Indexer → 覆盖部署。注意 `publish/Release/manifest.json`
现在还是 10-23 的（`Size 7,721,984`），而 DLL 是 10-24 的（7,730,688）——
**必须重跑 `tools\Indexer.exe`，否则新 manifest 校验不过**。

### P0-2 `.mpkinfo` 永久进 plan —— "更新"永远不收敛 `[实跑]`

在**完全最新**的安装上构建计划（`BuildPlanAsync(Update)`）：

```
PLAN: items=3 pkg=0 idx=3 size=10579120 empty=False remoteUnavailable=False
   Index    indep.mpkinfo                312652
   Index    s_file.mpkinfo               8396136
   Index    script.mpkinfo               1870332
```

（`Repair` 模式同样 3 项 / 10,579,120 B。）

根因是两处"各自正确、合起来错误"的设计：

1. `BuildShardMarkerMap` **有意**把 `*.mpkinfo` 排除在外（索引不是分片）；
2. `IsLayoutConsistent` 在"索引存在、但没有描述这个分片"时**返回 `false`**（注释明说这是为了不让
   `Patch2500` 的错 marker 静默放行）。

合起来 ⇒ 对索引文件本身，第 2 条永远成立。实测：

```
markers: 47 entries; contains indep.mpkinfo=False s_file=False script=False
indep.mpkinfo : sizeOk=True  IsLayoutConsistent=False
s_file.mpkinfo: sizeOk=True  IsLayoutConsistent=False
script.mpkinfo: sizeOk=True  IsLayoutConsistent=False
47 个真实分片对同一个函数：0 failing
```

**后果**（都不是崩溃，但都是"看起来在干活、其实没活"）：

- `plan.IsEmpty` 永远为 `false` ⇒ `RunAsync` 里"无需下载"的短路分支**永不执行**；
- `GetGameSizeAsync` / `GetGameDownloadedSizeAsync` 永远报 ~10.5 MB 待下载，宿主永远画进度；
- 每次"更新"都白下 10.5 MB，然后重写一遍版本记录；
- `VerifyBaseTierCompletable` 的 `if (plan.IsEmpty) return;` 早退也**永不触发**
  （实测 `VerifyBaseTierCompletable: PASS` 是靠指纹比较过的，不是靠早退）。

**修复**：`Kind == Index` 时跳过布局闸（索引只按远端大小校验）。三部分实测拼起来，修完后
同一台机器应当得到 `plan.IsEmpty == true`：47 个分片 size+layout 全过、3 个索引 size 全过、
着色器清单的 `ContentVersion` 与远端相同且文件在盘（`[实跑]`：本地/远端都是 `20261023182924387`）。

**必须补的回归测试**：构造一个"最新安装"的临时目录，断言 `plan.IsEmpty`。当前 174 个测试里
**没有任何一条**覆盖"plan 应当为空"。

---

## 2. P1 —— 会造成错误行为 / 数据风险

### P1-1 卸载会删掉整个安装根 `[读码]`

```csharp
// NeteaseGamesGameInstaller.cs:154-168
if (!NeteaseGamesLocalState.IsGameDirectory(gamePath)) throw …;
Directory.Delete(gamePath, recursive: true);
```

唯一守卫是"根目录下有 `Engine\Binaries\Win64Release\ywzh.exe`"。本机布局下这是安全的
（游戏在 `D:\ywzh\ywzh_extreme`，官方启动器在**同级** `D:\ywzh\ywzh_launcher`，不会被波及），
但只要用户把游戏内容直接铺在某个共享目录里，卸载就是整目录删除。

PW 的做法（`PerfectWorldGameInstaller.cs:146-174`）是**只删自己管理的子目录**
（`clientDir` / `launcherDir` / pak 暂存）+ 状态文件。Netease 应当照此改成
"只删 `LocalData\Patch` 下由计划写入的对象 + 版本记录"，把 `Patch*.mpk` / `Resource*` 的删除
留给官方卸载器，或至少要求二次确认。

### P1-2 引擎版本判定可能漏报 `[实跑]`

```
mini pack  engine_version  = 261014173721_4a476f25b_release     ← 插件只读这个
patch manifest @engine_version@ = 261021114347_cd5d7a8d5_release  ← 插件从不读
本地 Built.version         = 261014173721_4a476f25b_release
⇒ HasEngineUpdate = False
```

`UpdateEngineState`（`NeteaseGamesGameManager.cs:257-287`）只比 mini pack 的 `EngineVersion`，
而 `NeteaseGamesPatchManifest.EngineVersion`（`@metadata@.@engine_version@`）**在代码里没有任何读取方**
（`grep` 确认）。mini pack 的 `current_version` 是 `202610151759`（10-15），比 10-23 的补丁轮次旧，
所以"引擎有没有更新"这件事目前只有一半的证据。

**待验证**（不要直接改）：先确认两个字段的语义。若 `@engine_version@` 才是"本轮要求的引擎构建"，
则这是一个真实的漏报 —— 引擎落后但插件说"最新"。

### P1-3 `Patch.mpkinfo` 的 WAL 没有被处理 `[实跑]`+`[读码]`

真机目录里存在 `Patch.mpkinfo-shm`（32,768 B）与 `Patch.mpkinfo-wal`（**0 B**）。
`NeteaseGamesBaseIndex.Read` 是零依赖 B-tree walker，**只读主文件、不读 WAL**。
当前 `-wal` 为空 ⇒ 无影响；一旦官方启动器写了未 checkpoint 的页，插件读到的是旧布局，
`FindTruncatedBaseShards` 可能误报或漏报。

**修复**：`-wal` 非空时直接跳过基础层校验（"不可判定"优于"误判"），并在日志里说明。

### P1-4 登录态模块是死代码 `[读码]`

`NeteaseGamesSession`（341 行，含 DPAPI 依赖 `System.Security.Cryptography.ProtectedData`）
唯一的写入入口 `NeteaseGamesLoginState.Save()` **零调用者**（`grep` 确认）：

- `session.dat` 永远不会被创建 ⇒ `ApplyStoredSession()` 永远返回 `null`；
- 启动路径上的 `using IDisposable? session = …` 永远走 null 分支。

游戏自己会登录，所以这不是功能缺陷，但它是 **341 行 + 一个 native 依赖的死代码**，
并且给人一种"插件支持免登录启动"的错觉。要么接通（从官方启动器的凭据存储导入），要么删掉。

### P1-5 自更新永远不会修好 P0-1 `[探测]`

```
release 分支 SoR/manifest.json : 200, PluginVersion 0.4.0.0, ManifestDate 2026-10-22T11:04
release 分支 SoR/SoR.dll       : 200, 7,662,080 B
部署的 manifest                : PluginVersion 0.4.0.0
```

部署版（10-23 构建）与 release 分支（10-22 构建）**版本号相同**，自更新判定"已最新"，不会拉。
⇒ 即使 release 分支换成正确代码，只要版本号不变，用户机器也永远升不上去。

**修复**：本次部署必须把 `Hi3Helper.Plugin.SoR.csproj` 的 `<Version>` 从 `0.4.0` 提到 `0.4.1`。

---

## 3. P2 —— 能力缺口 / 死代码

| 项 | 实测 | 说明 |
|---|---|---|
| **全新安装** | `NotSupportedException` `[读码]` | 基础层实测 **128 个文件 / 64,232,426,422 B**（= 64.23 GB 十进制 = 59.82 GiB）。README 的"64.23 GB"没错，注意别和 GiB 混。 |
| `big_` 通道 | **可达** `[探测]` | `big_20261021113421287`（= `@base_version@`）清单 11,122,614 B，抽 25 块 **25/25 = 200**；`big_20261023182924387`（= 行 0）清单 11,124,133 B，抽 25 块 **0/25**。⇒ `AGENTS.md` 的修正**正确**。 |
| `big_` 辅助函数 | **零生产调用** `[读码]` | `GetBigPatchUrl` / `GetBigPatchContentPath` / `BigPatchListFileName` / `BigCommonSection` / `BigShaderSection` 只被自身引用。通道打通了，但没接。 |
| 分卷重建规则 | **未还原** | `fenbao_mode = 0`，无法复现真实改写；这是全新安装的唯一阻塞点。 |
| 松文件层（loose） | **不是磁盘文件，也不在 delta 索引里** `[实跑]` | `common` 230,427 + `windows64_common` 2,026 = 232,453 条；磁盘命中 **0/40**，三个 delta `.mpkinfo` 命中 **0/40**。 |
| ↳ 但它们确实是基础层资源 | **新证据** `[实跑]` | 把这 232,453 条的大小多重集与 `Patch.mpkinfo` 的 234,910 行 `value[0:4]` 比较：**232,450 个大小精确对上**。⇒ "虚拟路径存在 mpk 里"的说法成立，且**可以用来做基础层的交叉校验**（现在完全没用）。 |
| `Patch.mpkinfo` 的 `filepath` | **混淆，未破解** | 插件只用 `value`（分卷 ID / 起始偏移 / 大小），所以不影响功能；但"按路径查基础层资源"这条路还没打通。 |
| `NeteaseGamesShardCluster` | **零生产调用** `[读码]` | 解析 `mpk_filelist.info`；实测该文件 8 个资源包的 size+MD5 **全部与磁盘一致** ⇒ 校验能力是现成的，只是没接。 |
| 根目录资源包 | **无任何校验** | `ResourceExA.mpk` 等 8 个文件在**安装根**（不在 `LocalData\Patch`），`FindTruncatedBaseShards` 只扫 `Patch*.mpk`。 |
| 公告语言 | 硬编码 `zh_CN` `[读码]` | `NeteaseGamesLauncherApiNews.cs:287` 忽略 `SharedStatic.PluginLocaleCode`（HG 用了）。 |

---

## 4. 实测确认"是对的"的部分（不要动）

| 结论 | 证据 |
|---|---|
| 增量包目录 = 远端清单**第 0 行** | `20261023182924387`，索引/分块实测 200 |
| `big_` 目录 = **`@base_version@`** | 25/25 vs 0/25（见上） |
| 本地补丁版本 = `patchmd5` **第 3 行** | 本地/远端都是 `6593596`，`HasUpdate=False` |
| `pkgversion` 是另一个计数器 | 本地 `6521279` 未被补丁版本覆盖 |
| `[20:24]` = **起始**偏移 | `max(start+size)` 与 127/128 分卷精确相等；`FindTruncatedBaseShards` 真机 **0** 误报 |
| marker = 名字里的数字 ×2 | 47 个分片对索引 **0 failing**（`Patch2500 → 5000` 已对上） |
| 引擎载荷只在 `h74.gdl` 且**必须签名** | 裸请求 403；签名后 `md5_engine.txt` **200 / 21,811 B** |
| 引擎清单本地=远端 | 两边 21,811 B，MD5 都是 `c40bac19…` **逐字节相同** |
| 着色器清单 URL | `/{contentVersion}/shadercachelist_dx12.txt` **200 / 7,985,520 B**，MD5 与磁盘一致 |
| `big_` 以外的主机签名 | 内容主机接受裸请求；只有 gdl 强制签名 |
| 注册表检测 | HKCU `Software\OB_ywzh_launcher_PC` 命中 |
| 媒体走 base64 | 背景内嵌，不碰磁盘/网络 |
| 构建与单测 | `dotnet build` **0 error / 2 warning**（xUnit1051 ×2）；测试 **174/174 通过** |

---

## 5. 文档纠错（逐条）

| 文档 | 原文 | 实测 |
|---|---|---|
| `NETEASE-FIX-PLAN.md` §1.1 | "165 通过 / 1 失败"，失败是 Linux 沙箱路径假象 | **174/174 全通过，0 失败**。那是另一个环境的产物，本机 Windows 上不复现。 |
| `NETEASE-FIX-PLAN.md` §0 | Netease Core "9.6k 行" | **8,231 行**（Core + SoR，不含子模块与测试） |
| `NETEASE-FIX-PLAN.md` §6.5 | P0-1…P1-3 全标"✅ 已修" | **只在工作树里修了；部署的二进制里一条都没有**（P0-1）。"已修"≠"已生效"。 |
| `NETEASE-FIX-PLAN.md` §4.3 | `README` 引用已删的 `docs/re/netease-protocol.md` | README **已经改成**引用 `PROTOCOL-VERIFIED.md`，死链不存在。但该文件仍被 git 跟踪（删除未提交），提交时要 `git rm`。 |
| `README.md` 能力表 | "增量更新 ✅ 已修（2026-10-24）" | 工作树成立；**部署版不成立**。 |
| `AGENTS.md` 硬约束 1 | 增量目录=行 0，`big_`=`@base_version@` | **正确** |
| `AGENTS.md` §待完成 2 | `[20:24]` 是起始偏移 | **正确** |
| `AGENTS.md` §plan 模式 | 包判定两道闸：大小 + 索引布局 | **正确，但漏了"索引文件自己也会过这道闸"**（P0-2） |
| `PROTOCOL-VERIFIED.md` §记账 | 着色器清单与远端 MD5 相同 | **正确** |
| `PROTOCOL-VERIFIED.md` §5.4 | `big_` 通道已定位 | **正确** |
| 未记载 | — | `Patch.mpkinfo` 的 `filepath` 是混淆的（P2）；`-wal/-shm` 存在（P1-3） |

---

## 6. 修复计划

### 阶段 0 —— 止血（半天，必须先做）

| # | 动作 | 验收 |
|---|---|---|
| 0.1 | 把 `Hi3Helper.Plugin.SoR.csproj` 的 `<Version>` 提到 **0.4.1** | `manifest.json` 里 `PluginVersion 0.4.1.0` |
| 0.2 | 修 P0-2：`Kind == Index` 跳过 `IsLayoutConsistent`；加回归测试"最新安装 ⇒ `plan.IsEmpty`" | 真机 `BuildPlanAsync(Update)` 返回 **0 项**；测试从 174 → 175+ 且全绿 |
| 0.3 | 重新 AOT 构建（`ReleasePublish-O2`）→ **重跑 `tools\Indexer.exe`** → 覆盖部署 | `manifest.json` 的 `Size/FileHash` 与 DLL 一致；字符串扫描含 `@bigpatchmd5@`、不含旧文案 |
| 0.4 | 提交工作树：12 个 M + 3 个未跟踪测试 + `docs/PROTOCOL-VERIFIED.md` + `git rm docs/re/netease-protocol.md` 与两个废弃 ps1 + 子模块指针 | `git status` 干净 |

> 0.1 不能省：不涨版本号，自更新永远不会把 0.4.0 换成正确实现（P1-5）。
> 0.3 里"重跑 Indexer"也不能省：现在 `publish/Release/` 的 DLL 与 manifest 是**不一致**的
> （7,730,688 vs 7,721,984），直接拿去部署会自校验失败。

### 阶段 1 —— 让"更新"真的对（2–3 天）

| # | 动作 | 验收 |
|---|---|---|
| 1.1 | P1-3：`Patch.mpkinfo-wal` 非空时跳过基础层校验 | 造一个非空 WAL，`FindTruncatedBaseShards` 返回"不可判定"而不是乱报 |
| 1.2 | P1-2：先验证 `@engine_version@` 与 mini pack `engine_version` 的语义，再决定是否改判定 | 用真机数据能解释 `261021114347…` vs `261014173721…` 的差异；结论写进 PROTOCOL-VERIFIED |
| 1.3 | P1-1：卸载改为"只删计划写入的对象 + 版本记录"（照 PW `:146-174`） | 造一个"游戏根目录里还放了别的东西"的布局，卸载后别的东西还在 |
| 1.4 | P1-4：`NeteaseGamesSession` 要么接通、要么删掉（含 `ProtectedData` 包引用） | `grep` 无死入口；若删，`Core.csproj` 少一个 native 依赖 |

### 阶段 2 —— 补齐产品级能力（1–2 周，有外部阻塞）

| # | 动作 | 阻塞点 |
|---|---|---|
| 2.1 | 接上 `big_` 通道（`@base_version@` 清单 + `big_common` 分块 + 分卷重建） | **分卷重建规则未还原**；必须在 `fenbao_mode ≠ 0` 时抓一次官方全量改写 |
| 2.2 | 全新安装（0 → 全量） | 依赖 2.1 |
| 2.3 | 用 `Patch.mpkinfo` 的 size 多重集 + `mpk_filelist.info` 的 MD5 做基础层/资源包交叉校验 | 无（能力已具备，只是没接） |

### 阶段 3 —— 工程闭环（1 天，可与阶段 1 并行）

| # | 动作 | 验收 |
|---|---|---|
| 3.1 | 加 `.github/workflows/build.yml`（push/PR 跑 build + 单测，`paths-ignore: **.md`） | PR 上有绿色 check |
| 3.2 | 加 `CompileAOTAndShip.bat`（参数化、非交互） | 一条命令产出可部署的 `SoR.dll` + `manifest.json`，DLL > 2 MB 且无 `BSJB` |
| 3.3 | 补 `LICENSE` + `README.zh-CN.md` | 与 PW/HG 对齐 |
| 3.4 | 清理死代码：`big_` 辅助函数（接上或删）、`NeteaseGamesShardCluster`（接上或删） | `grep` 每个 public 成员至少一个非测试调用方 |

---

## 7. 复现方法（本轮实际用的）

```bash
# 1) 真机状态 + 计划（直接调插件自己的类）
#    探针工程在 %TEMP%\nprobe，引用 Core + Plugin.Core
dotnet build -c Debug && ./bin/Debug/net10.0/nprobe.exe

# 2) 部署版 vs 工作树产物的差异
#    对两个 DLL 做 UTF-16 字符串扫描（@bigpatchmd5@ / fingerprint is unchanged / …）

# 3) 单测（必须直接跑 exe：dotnet test 会走 MSBuild 命名管道）
./Hi3Helper.NeteaseGames.Tests/bin/Debug/net10.0/win-x64/Hi3Helper.NeteaseGames.Tests.exe

# 4) 协议探测（python -I，避免 cwd 注入）
#    HEX_ 解密 = base64 → XOR "netease_joker_h74_encrypt_key" → zlib
#    URL 签名 = key1 = md5(SECRET + path + key2), key2 = hex(unix+43200)&0xffffffff
```

关键数字（可复算）：

| 量 | 值 |
|---|---|
| 远端内容版本（行 0） | `20261023182924387` |
| 远端 `@base_version@` | `20261021113421287` |
| 补丁版本（行 3） | `6593596` |
| 基础包版本（`pkgversion`） | `6521279` |
| 基础层分卷 | 128 个 / 64,232,426,422 B |
| delta 包 | 47 个分片 + 3 个索引 |
| 基础层索引行数 | 234,910（`Patch.mpkinfo`，`info0(filepath,value)`） |
| 松文件条目 | 232,453（`common` 230,427 + `windows64_common` 2,026） |
| `big_common` 分块 | 10,401（`@base_version@` 版清单） |
