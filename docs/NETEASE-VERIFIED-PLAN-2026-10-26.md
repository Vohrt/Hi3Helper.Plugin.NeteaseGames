# Netease 插件（SoR / 遗忘之海）实测复核与修复计划

> **日期**：2026-10-26
> **对象**：`Hi3Helper.Plugin.NeteaseGames`（工作树 `39a9ebb`，0.4.1，未推送）
> **真机**：`D:\ywzh\ywzh_extreme`（游戏）、`D:\ywzh\ywzh_launcher`（官方启动器）
> **对照**：`Hi3Helper.Plugin.PerfectWorld`（PW）、`Hi3Helper.Plugin.Hypergryph`（HG）
>
> **本文所有结论都标了证据等级**：
> `[实测]` = 本轮在本机跑出来的输出 / HTTP 响应 / 字节比对；`[读码]` = 只读代码；
> `[读码+实测]` = 代码推断被实测证实。
> 仓库内 `AGENTS.md` / `README.md` / `docs/PROTOCOL-VERIFIED.md` / `docs/NETEASE-FIX-PLAN.md` /
> `docs/NETEASE-AUDIT-MEASURED.md` 与本表冲突处，**以本文为准**（纠错见 §6）。

---

## 0. 结论

Netease 插件的**协议逆向是所有三个插件里最强的**（A 级），工程纪律也最好（204 个单测全绿、CI 跑测试、
提交点纪律、单一 plan 供给尺寸/进度）。但**"把下载结果正确落盘"这一层今天仍然是断的**，而且断点比
`NETEASE-AUDIT-MEASURED.md` 记的更靠前：

| 维度 | PerfectWorld | Hypergryph | **Netease（本轮实测）** |
|---|---|---|---|
| 单测 | 无 | 无 | **204/204 通过** `[实测]` |
| CI build（非发布） | `build.yml` | `build.yml` | **无**（只有 `release.yml`） |
| 一键 AOT 脚本 | 有 | 有 | **无** |
| LICENSE / 双语 README | 有 / 有 | 有 / 有 | **无 / 无** |
| 增量更新 | 可用 | 可用 | **被基座层闸门 100% 拒绝（P0-1）** |
| 全新安装 | 支持 | 支持 | **计划器能出计划，但装出来的游戏跑不起来（P0-2）** |
| 卸载 | 只删自己管理的子目录 | 删整个根（有守卫） | **删整个根（P1-4）** |
| 每次安装前固定空等 | 无 | 无 | **10 秒（P1-1，实测 10,012 ms）** |

**一句话**：协议层可以打 A，但"今天在这台机器上点更新会被拒、点全新安装会得到一个缺了 4.7 GB 着色器
和索引的游戏"。下面 §1 是现在就不工作的，§2 是会造成错误行为/数据风险的，§3 是能力缺口，§5 是修复计划。

---

## 1. P0 —— 现在就不工作

### P0-1 增量更新被基座层闸门 100% 拒绝 `[实测]`

```
远端 patch 版本 = 6600116（本地 6593596，确实有更新）
远端 @bigpatchopen@ = true
远端 @bigpatchmd5@  = 16703640110960187218
本地 @bigpatchmd5@  = 6128997482385675719      ← 不同

plan: items=2 pkg=1 idx=0 loose=0 bytes=16,753,304
   Package   Patch1000.mpk              16,753,304
   SideFile  shadercachelist_dx12.txt           0

VerifyBaseTierCompletable: THROWS NotSupportedException
  "This update cannot be applied by the plugin: the base tier changed
   (@bigpatchmd5@ 6128997482385675719 -> 16703640110960187218)…"
```

`VerifyBaseTierCompletable`（`NeteaseGamesGameInstaller.cs:806`）在 `@bigpatchmd5@` 变化时直接抛异常。
**但本轮实测证明这个拒绝是错的**：

1. `@bigpatchmd5@` 变化 ≠ 基座层需要重写。本轮变动的 28 个分块（9 新 + 19 重切）覆盖的 302 个资源里，
   **301 个与磁盘上的基座分卷逐字节相同** `[实测]`（逐资源切片比对，见下）。
   这 28 个分块横跨 18 个基座分卷（8/14/16/29/33/50/55/63/65/93/112/119/121/122/124/125/126/127），
   但**没有任何一个分卷需要改写**。
2. 唯一不同的 `essential`（shard 126, offset 73,993,554）**不在 `Patch.mpkinfo` 索引里**
   （`essential/repository` 在三个 delta 索引里也查不到）`[实测]`，游戏不通过基座索引读它。
3. 真正需要替换的基座内容**已经通过 delta 层下发**：`script.mpkinfo` 里有 **8 条**资源换了位置
   （7 条从 marker 2016/2002/2004/2008 搬进 marker 2000，1 条本来就在 2000 但偏移变了），
   这 8 条的大小之和 = **603,170 B**，**精确等于** `Patch1000.mpk` 的增长量
   （16,150,134 → 16,753,304）`[实测]`。⇒ 基座里被替换的那 8 个资源，delta 层一个不少地重发了。
4. 最硬的证据：本地 `Patch1000.mpk` 是远端那份的**逐字节前缀**——
   前 16,150,134 B **0 个字节不同**，远端只是在末尾追加了 603,170 B `[实测]`。
   远端索引 marker 2000 的 `max(offset+size)` = 16,753,304 = 远端文件长度；
   另外 3,560 条未被搬动的 marker-2000 资源偏移**一个没变** `[实测]`。
   ⇒ 这一轮对 `Patch1000` 是"追加 + 换索引"，不需要碰基座层。

```
resources compared: 302   byte-identical: 301   differing: 1
  DIFF ('essential_1', 'essential', 126, 73993554, 41157)
```

**后果**：用户点"更新"永远失败，只能开官方启动器。而官方启动器跑完后 `@bigpatchmd5@` 会被刷新，
插件才"恢复正常"——这正是文档里那句"Open the official launcher once"的真实含义。

**另外**：`essential` 的新旧三代都不等于磁盘（`essential_0`/`essential_1`@base/`essential_1`@line0 三个 MD5
互不相同，磁盘是第四种）`[实测]`。所以这条资源**不能用"重放 chunk"来校验**，只能信索引。

**⚠ 顺带发现（同一处代码，必须一起修）**：闸门只在 `mode == Update` 时调用
（`NeteaseGamesGameInstaller.cs:228-232`），但 `Repair` 的判定与 `Update` 完全相同 `[实测]`：
两者都得到 2 项 / 16,753,304 B。⇒ **"修复"按钮会绕过闸门，照样只下 delta 层就写版本号**，
正是这个闸门本来要防的"半更新被报告成已完成"。修复时把闸门提到 mode 判断之外。

**修复方向**（详见 §5 P0-1）：把"基座层真的变了"的判据从 `@bigpatchmd5@` 换成
**"远端 delta 索引是否描述了磁盘上的基座分卷"**：
读远端 `script/s_file/indep.mpkinfo`，对每个 marker 取 `max(offset+size)`，与磁盘 `PatchN.mpk` 长度比。
本轮实测：47 个 marker 里只有 `Patch1000` 一个不满足（因为磁盘还是旧版），更新完就满足 `[实测]`。
这个判据还顺带修好了 P1-2（同尺寸索引不刷新）。

**这条判据的盲区（必须知道）**：它只能证明"delta 分卷没有变短"。理论上存在一种情况——delta 分卷在
**同样长度内改了字节**，而对应资源又只被基座索引描述。本轮真机没有这种情况（`Patch1000` 前缀 0 字节差异，
其余 46 个分卷的 hash 列表与本地完全一致 `[实测]`），但判据本身应加一道兜底：
`NeedsPackageDownload` 用 hash 列表比对（P1-2）后，任何 hash 变化的 delta 分卷都会被下载，
两者合起来才闭环。

### P0-2 全新安装缺 4.7 GB 着色器层 + 两个索引，装完跑不起来 `[实测]`

在空目录上构建 `Install` 计划：

```
plan items=341 bytes=66,013,125,492
   LooseFile   290 items  1,396,830,145 B
   Package      47 items    765,050,189 B
   Index         3 items     10,579,120 B
   SideFile      1 items              0 B
   base: 10,421 steps / 123 shards / 63,840,666,038 B
```

**没有的东西**：

| 缺什么 | 实测值 | 证据 |
|---|---|---|
| `ResourceExA.mpk` + `ResourceExA1..16.mpk`（着色器分卷） | **4,989,773,170 B / 17 个文件** | 磁盘 `[实测]`；计划里只有引擎载荷里那个 62 MB 的 `ResourceExA.mpk` |
| `ResourceExA.patchinfo`（着色器索引，173,861 行） | **52,342,784 B** | 磁盘 `[实测]`；计划里没有 |
| `Patch.mpkinfo` 的 **2,460 条目录行** | 2,460 行（size=0） | `WriteBaseIndex` 里 `if (placement.Size <= 0) continue;` `[读码]` |
| `Patch.mpkinfo` 的 `extra_mpkinfo_families` | 50 项（47 个 delta 分卷 + 3 个索引） | `WriteBaseIndex` 硬编码 `"[]"` `[读码]` |
| `Documents\`（`versiondata.json` 等） | 141 MB | 磁盘 `[实测]`；计划里 `planned=False` |

**着色器层是可下载的，而且规则已经测出来了** `[实测]`：

- 清单：`https://h74.gph.netease.com/big_{version}/bigshadercachelist_dx12.txt`
  （line0 版 8,906,674 B / 920 个 chunk；`@base_version@` 版 8,906,483 B / 919 个 chunk）
- 分块：`big_{version}/big_shader/{dx12_N}`
- 重建方式与基座层完全一样（chunk = 其资源按清单顺序的无缝拼接）：
  `dx12_916` 137/137 逐字节相同、`dx12_920` 4/4、`dx12_1` 518/518、`dx12_500` 150/150 `[实测]`
- 目录分工也和基座层一样：**本轮重切的 3 个（`dx12_916/917/918`）+ 新增的 `dx12_920` 只在 line0 目录**，
  其余 916 个只在 `@base_version@` 目录（`dx12_919` 只有 base 目录有）`[实测]`
- 17 个着色器分卷的 `max(start+size)` 与磁盘**17/17 精确相等**（不像基座层有 padding）`[实测]`

**`Patch.mpkinfo` 的两个缺口也是实测的** `[实测]`：

```
live index rows = 234,910
  232,450  size > 0   ← 插件会写这些
    2,460  size == 0  ← 全部是目录条目（无扩展名，如 '5e'/'8f'/'UIScript'/'Char/player/linsegui'），插件跳过
meta: max_index=127, extra_mpkinfo_families=[50 项]
插件写入: 232,450 行, max_index=122（123 个分卷）, extra_mpkinfo_families="[]"
```

目录条目在 `Patch.mpkinfo` 里是真实存在的（游戏可能靠它做目录列举）。插件一个都不写。

**还有两个 P0-2 的附带问题**：

- **安装完成不写版本记录**：`Install` 计划的 `PatchVersion = 0`、`PackageVersion = 0`，
  而 `NeteaseGamesUpdateApplier` 只在 `> 0` 时写 `patchmd5` / `pkgversion` `[读码+实测]`。
  模拟"刚装完"的状态：`installed=True local=0.0.0 remote=0.0.6600116 hasUpdate=True` `[实测]`
  ⇒ 装完立刻显示"有更新"，下一次更新还会走 P0-1 的闸门。
- **尺寸重复计算**：3 个松散 chunk（`prerequisite` / `Misc/lazycode_zstd_dict` / `4a/…`，共 119,967 B）
  同时算进 `BaseAssembly.TotalSize` 和 `plan.Items` ⇒ 上报的下载总量偏大 119,967 B `[实测]`。

### P0-3 部署的二进制比源码旧，且自更新升不上去 `[实测]`

| 项 | 值 |
|---|---|
| 部署的 `SoR.dll` | 7,730,688 B，2026-10-24 23:46，`PluginVersion 0.4.1.0` |
| 工作树 `publish/Release/SoR.dll` | 7,730,688 B，MD5 `2aeb12bb…` **与部署一致** |
| `release` 分支 `SoR/SoR.dll` | 7,730,688 B，MD5 `9bd46c4b…`（不同） |
| 工作树最新提交 `39a9ebb`（基座层装配，10-26 01:13） | **没有部署、也没有发到 release 分支** |

字符串指纹 `[实测]`：部署版与 release 分支版**都**含 `A fresh installation is not supported yet`、
`the base tier changed (@bigpatchmd5@`，**都**不含 `Assembling the base tier` / `bigpatchlist_windows64_normal.txt`。
⇒ 用户机器上跑的是"0.4.1 旧逻辑"：既没有基座层装配，也会被 P0-1 的闸门拒绝。

`git rev-list --left-right --count origin/master...master` = `0 1` ⇒ `39a9ebb` 还没推送 `[实测]`。

---

## 2. P1 —— 错误行为 / 数据风险

### P1-1 每次安装/更新/卸载前固定空等 10 秒 `[实测]`

`NeteaseGamesGameInstaller` 没有覆写 `InitializableTask.InitAsync`（只有 `GameManager` 覆写了）。
基类实现是"延迟 10 秒然后返回 69420"，而宿主在**每一次**安装/更新/卸载/预载之前都 `await InitPluginComAsync`
（`PluginGameInstallWrapper.cs:398 / 482 / 695 / 720`）。实测：

```
InitAsync (protected) found on Hi3Helper.Plugin.Core.InitializableTask
NeteaseGamesGameInstaller overrides it: Hi3Helper.Plugin.Core.InitializableTask
base InitAsync returned 69420 after 10012 ms
```

**PW 和 HG 的 installer 都覆写了**（`PerfectWorldGameInstaller.cs:50`、`HgGameInstaller.cs:35`），只有 Netease 漏了。
返回值 69420 在 `InitPluginComAsync` 里会命中 `throw new NotImplementedException("InitAsync isn't overriden!")`
——只是因为宿主先判了 `initTask.IsCompletedSuccessfully` 才没炸 `[读码]`。

### P1-2 同尺寸的索引变更永远检测不到 `[实测]`

`NeedsPackageDownload`（`NeteaseGamesGameInstaller.cs:769`）对 `*.mpkinfo` 只比大小（这是 0.4.1 的修复，
本身是对的）。但本轮真机上正好有一个反例：

```
script.mpkinfo  size 1,870,332 -> 1,870,332   sameSize=True  sameHashes=False  pluginSaysDownload=False
```

本地与远端的 `script.mpkinfo` 大小完全相同、8 条资源换了分卷/偏移，插件仍然认为"最新"。
**它现在没造成伤害**，只因为同一轮的 `Patch1000.mpk` 也变了、被一起下载了 `[实测]`。
一旦某轮只动索引（换分卷不改大小），插件会留下旧索引 + 新分卷 ⇒ 游戏读到错位数据。

**修法**：本地 `patchlist_windows64_normal.txt` 里的**每包 64 位 hash 列表**是免费的权威比对源
（远端清单第 0 行目录下的 `patchlist_windows64_normal_mpk.txt` 与本地那份**逐字节相同** `[实测]`，
MD5 都是 `aab2ba8d…`）。用 `NeteaseGamesPatchList.HashesEqual`（已存在、**零调用者**）即可。
实测：49/50 个包的 hash 列表完全一致，只有 `Patch1000.mpk`（大小变了）与 `script.mpkinfo`（大小没变）不同 `[实测]`。

⚠ 前提：插件必须在更新成功后**把新清单写回本地**（`patchlist_windows64_normal.txt`）。
目前插件**从不写这个文件** `[读码]`，只有官方启动器会写——现在能用是因为官方启动器同步过一次。

### P1-3 `Patch.mpkinfo` 的 WAL 被忽略 `[实测]`

真机有 `Patch.mpkinfo-shm`（32,768 B）与 `Patch.mpkinfo-wal`（当前 **0 B**）。
`NeteaseGamesBaseIndex.Read` 是零依赖 B-tree walker，只读主文件。`-wal` 为空时无影响；
一旦官方启动器写了未 checkpoint 的页，插件读到旧布局 ⇒ `FindTruncatedBaseShards` 会误报/漏报。
`ResourceExA.patchinfo` 同样是 WAL 模式（`-shm` 32,768 B）`[实测]`。

**修法**：`-wal` 非空时跳过基座层校验并记日志（"不可判定"优于"误判"）。

### P1-4 卸载删掉整个安装根 `[读码]`

```csharp
// NeteaseGamesGameInstaller.cs:164-178
if (!NeteaseGamesLocalState.IsGameDirectory(gamePath)) throw …;
Directory.Delete(gamePath, recursive: true);
```

唯一守卫是"根目录下有 `Engine\Binaries\Win64Release\ywzh.exe`"。本机布局安全（启动器在**同级**
`D:\ywzh\ywzh_launcher`），但只要用户把游戏内容铺在共享目录里，卸载就是整目录删除。
而且它会删掉 `Documents\`（141 MB 用户数据：`Userdata\userdata_r7267775.json` 等）`[实测]`。
PW 的做法是**只删自己管理的子目录**（`PerfectWorldGameInstaller.cs:146-174`），Netease 应对齐。

### P1-5 尺寸查询的缓存会被"未探测"状态毒化 `[实测]`

`GetOrBuildPlanAsync` 会缓存计划（键只有 `gamePath`）。如果宿主在远端探测完成前调用
`GetGameSizeAsync`/`GetGameDownloadedSizeAsync`，会缓存一个 `IsRemoteListUnavailable=True` 的空计划，
并且**永不失效**：

```
-- host order: GetGameSize -> GetGameDownloadedSize, BEFORE any remote probe --
   size = 0   downloaded = 0
-- now the probe succeeds --
   remotePatchVersion = 6600116
-- host calls the size getters again --
   size = 0   <- 应为 16,753,304
-- 直接 BuildPlanAsync（不走缓存）--
   items=2 bytes=16,753,304 unavailable=False
```

当前宿主顺序是先 `InitPluginComAsync`（→ 本插件 10 秒空等）再取尺寸，所以**暂时**碰不到；
但 P1-1 修好后空等消失，这个竞态就会暴露。**修 P1-1 必须同时修 P1-5**。

### P1-6 增量更新不写本地清单 `[读码]`

见 P1-2。插件更新完只写 `patchmd5` / `pkgversion` / `versiondata.json` / `gameversion`，
**不写** `patchlist_windows64_normal.txt`。官方启动器下次打开时会用自己的清单覆盖，
但插件自己下一次更新比对的就是这份可能过期的本地清单。

### P1-7 `@engine_version@` 从不读取 `[实测]`

```
mini pack  engine_version       = 261014173721_4a476f25b_release   ← 插件只读这个
patch manifest @engine_version@ = 261021114347_cd5d7a8d5_release   ← 插件从不读
本地 Built.version              = 261014173721_4a476f25b_release
⇒ HasEngineUpdate = False
```

`NeteaseGamesPatchManifest.EngineVersion` 在代码里**没有任何读取方** `[读码]`。
mini pack 的 `current_version` 是 `202610151759`（10-15），比 10-21 的补丁轮次旧，
所以"引擎有没有更新"目前只有一半证据。**先验证语义再改**，不要直接改成读 `@engine_version@`。

### P1-8 安装/更新期间不检查游戏是否在跑 `[读码]`

`NeteaseGamesGameInstaller` / `NeteaseGamesUpdateApplier` 里没有任何 `IsRunning` 调用
（`NeteaseGamesGameLauncher.IsRunning` 存在，只被启动路径使用）。
游戏运行时替换 `Patch*.mpk` 会被文件锁挡住（表现为 `ApplyFailed`），但基座层装配是**直接写**
`Patch.mpk` 的，可能把正在被读的分卷写坏。

---

## 3. P2 —— 能力缺口 / 死代码 / 工程化

| 项 | 实测 | 说明 |
|---|---|---|
| `big_windows64_common` 段 | **计划器不区分段** `[读码]` | 18 个 chunk（`r_0..r_17`，2026 个资源）与 `big_common` 一起排进基座分卷。实测能重建（抽样 100% 一致），但语义上它属于另一层，建议显式处理 |
| 着色器层 | **完全未实现** `[读码+实测]` | 见 P0-2；`GetShaderShardFileName` 零调用者 |
| 引擎载荷目录映射 | **实测正确** `[实测]` | 10 条 `used_del_dir\mpk_res\*` 落地到游戏根，10/10 大小与磁盘一致 |
| 根目录 `ResourceExA.mpk`（62 MB） | 由引擎载荷提供 `[实测]` | 与 `LocalData\Patch\ResourceExA.mpk`（524 MB）是**两个不同文件**，别混 |
| `NeteaseGamesSession` | **部分死代码** `[读码]` | `ApplyStoredSession` 有调用方，但 `NeteaseGamesLoginState.Save` **零调用者** ⇒ `session.dat` 永不创建；341 行 + `System.Security.Cryptography.ProtectedData` 原生依赖 |
| `NeteaseGamesShardCluster` | 零生产调用 `[读码]` | 只在测试与 `tools/re/PlanProbe` 用；实测 8 个资源包 size+MD5 **全部与磁盘一致**，能力现成 |
| `NeteaseGamesBigPatchList.Merge` | 零调用者 `[读码]` | 注释里还写着"Merge 合并两份清单"（已被 0.4.1 推翻） |
| `NeteaseGamesPatchList.HashesEqual` | 零调用者 `[读码]` | 见 P1-2，正好可用 |
| `NeteaseGamesGameManager.MeasureInstallSize` | 零调用者 `[读码]` | — |
| `NeteaseGamesBaseIndexWriter` 的 `extra_mpkinfo_families` | 写死 `"[]"` `[读码]` | 真机是 50 项 |
| 公告语言 | 硬编码 `zh_CN` `[读码]` | `NeteaseGamesLauncherApiNews.cs:287` 忽略 `SharedStatic.PluginLocaleCode`（HG 用了） |
| 媒体构造参数 | 未使用 `[读码]` | `NeteaseGamesLauncherApiMedia` / `…ApiNews` 的 `gamePathProvider` 都不使用 |
| 背景 | 内嵌 base64 `[实测]` | `background.jpg` 327,549 B 已嵌入 Core；不依赖安装路径，**不会转圈**（与 PW 的坑不同） |
| 轮播 | 可用 `[实测]` | 公告清单 29 条；抽查前 12 条里有 7 条带图，图片 URL 均为 200 |
| 图标 / 海报 / 区域 Logo | 均 200 `[实测]` | `095541248827.png` 213,933 B、`270811864061.png` 205,079 B |
| 自更新 URL | 可用 `[实测]` | `release/SoR/manifest.json` 200，`PluginVersion 0.4.1.0` |
| 构建 | 0 error / 2 warning `[实测]` | xUnit1051 ×2 |
| CI | 只 `release.yml`（含单测）`[读码]` | 缺 push/PR 的 `build.yml`；缺 `CompileAOTAndShip.bat`；缺 `LICENSE` |
| 子模块指针 | `55d0570` `[实测]` | 与 HG 一致；PW 是 `0e9d9ad` |

---

## 4. 实测确认"是对的"的部分（不要动）

| 结论 | 证据 |
|---|---|
| 增量包目录 = 远端清单**第 0 行** | `20261025174155378`，包/索引实测 200 |
| 本地补丁版本 = `patchmd5` **第 3 行** | 本地 6593596 / 远端 6600116，`HasUpdate=True` 正确 |
| `pkgversion` 是另一个计数器 | 本地 6521279 未被覆盖 |
| 基座索引 `value` 布局 `[0:4]=size, [8:10]=shard, [20:24]=offset` | 234,910 行；`max(start+size)` 与磁盘 **127/128 精确相等**，唯一不符的 `Patch100` 差 1,876,274 B 是真实尾部填充 |
| 分块字节 = 分卷字节（无缝拼接） | 基座：28 个重切分块 302 个资源 **301 个逐字节相同**；着色器：6 个分块 1,032 个资源 **1,031 个逐字节相同**（唯一例外是 `dx12_917` 里一个已删除的资源） |
| 分块目录分工（line0 只服务本轮变动） | 基座：28/28 命中 line0、30/30 命中 base；着色器：4 个变动块只在 line0、916 个只在 base |
| 引擎载荷只在 `h74.gdl` 且**必须签名** | 裸请求 403；`h74.gph` 404 |
| 引擎清单 10 条 `used_del_dir` 是真实文件 | 10/10 大小与磁盘一致 |
| 着色器缓存清单 | `shadercachelist_dx12.txt` 7,985,520 B，远端与磁盘 **MD5 相同** |
| `@shadercachelistmd5@` 本地 = 远端 | 三个字段完全相同 ⇒ 本轮无需刷新（但 `AppendShaderCacheList` 只看 contentVersion，会多下一次） |
| 注册表检测 | HKCU/HKLM `Software\OB_ywzh_launcher_PC` 都有 `AdditionalPaths` |
| 单一 plan 供给尺寸/进度 | `_cachedPlan` 同时喂 `GetGameSize`/`GetGameDownloadedSize`/`RunAsync`（比 PW/HG 都好） |
| 提交点纪律 | 先下载到 `.collapse_download` → 包 → 索引 → 松散文件 → 版本记录 |
| TLS | `NeteaseGamesHttp` 从不关闭校验（比 PW/HG 都好） |
| 单测 | **204/204 通过** |

---

## 5. 修复计划

> **实施状态（2026-10-26 晚）**：阶段 0 的 0.1–0.3、阶段 1 的 1.5（闸门覆盖 Repair）、
> 阶段 2 的 2.1–2.3 与 1.7（尺寸重复计算）**已在工作树实现**，213 个单测全绿（204 → 213）。
> 2.4 与 `pkgversion` 仍未解决（见下方说明）。所有改动**未提交**。

### 阶段 0 —— 止血（半天）

| # | 动作 | 验收 | 状态 |
|---|---|---|---|
| 0.1 | 覆写 `NeteaseGamesGameInstaller.InitAsync`，`return 0`（照 PW `:50` / HG `:35`） | 安装/更新/卸载前不再有 10 秒空等 | ✅ 实测 `returned 0 after 0 ms` |
| 0.2 | 计划缓存加"远端未就绪"保护：`IsRemoteListUnavailable` 的缓存不生效 | 先取尺寸再探测，第二次调用返回 16,753,304 | ✅ 实测 `0 → 16,753,304` |
| 0.3 | 补回归测试 | 测试数 204 → 213 且全绿 | ✅ 213/213 |
| 0.4 | 重新 AOT 构建 → **重跑 `tools\Indexer.exe`** → 部署 → 推送 `master` + 发 `release` 分支 | `manifest.json` 的 `Size/FileHash` 与 DLL 一致；版本号提到 0.4.2 | ✅ 构建/Indexer/部署已完成，**未推送** |

> **0.4 实测记录（2026-10-26）**
> - AOT：`dotnet publish … -p:PublishProfile=ReleasePublish-O2`，产物 7,940,096 B，`BSJB` 不存在（确认原生）。
>   ⚠ 必须先 `export PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH"`，否则 `vswhere.exe`
>   找不到、`link.exe` 退出码 123（PW 笔记里记过同一个坑）。
> - Indexer：`0.4.2.0`，`Assets[0].Size = 7,940,096`、`FileHash` 解码后 = DLL 的 MD5 `c5dd3797…`，自校验通过。
> - 部署：先备份到 `_plugins\_backup_20261026_v0.4.1\`，再覆盖 `SoR.dll` + `manifest.json`（Collapse 未运行）。
> - **功能验证**：用 `LoadLibraryW` + `TryGetApiExport` 按宿主的方式加载部署后的二进制，
>   `GetPluginVersion` 自报 **0.4.2.0**，`GetPlugin` 等导出可解析。
>   （注意：AOT 会折叠常量字符串，用 `strings` 搜 `bigshadercachelist_dx12.txt` 之类会**误判为缺失**；
>   判断部署是否生效请看文件 MD5 与版本号，不要用字符串指纹。）
> - 构建过程中 AOT restore 会改子模块的 `packages.lock.json`（ILLink 10.0.12 → 10.0.13），已还原，子模块保持干净。

> 0.4 不能省：`39a9ebb` 没推送、release 分支也没有它，用户机器上还是旧逻辑（P0-3）。

### 阶段 1 —— 让"更新"真的能用（2–3 天）

| # | 动作 | 验收 |
|---|---|---|
| 1.1 | **P0-1**：`VerifyBaseTierCompletable` 改用"远端 delta 索引是否描述磁盘基座分卷"判据（读远端 `script/s_file/indep.mpkinfo`，逐 marker 比 `max(offset+size)` 与文件长度）；**并把闸门提到 `mode == Update` 判断之外**，让 Repair 也受管 | 真机 `Update` 与 `Repair` 的闸门都 **PASS**；`Patch1000` 更新完后 47/47 满足 |
| 1.2 | **P1-2**：`NeedsPackageDownload` 增加 hash 列表比对（用本地 `patchlist_windows64_normal.txt` + `HashesEqual`） | `script.mpkinfo`（同尺寸、hash 变）被列入计划 |
| 1.3 | **P1-6**：更新成功后把远端 `patchlist_windows64_normal_mpk.txt` 写回本地（HEX_ 包络，保留 `.collapse-bak`） | 连续两次更新，第二次计划为空 |
| 1.4 | **P1-3**：`Patch.mpkinfo-wal` 非空时跳过基座校验并记日志 | 造一个非空 WAL，返回"不可判定" |
| 1.5 | **P1-4**：卸载改为只删插件管理的对象 + 版本记录（照 PW `:146-174`），`Documents\` 不动 | 造一个"根目录里还有别的东西"的布局，卸载后别的东西还在 |
| 1.6 | **P1-8**：安装/更新前 `IsRunning` 检查，正在跑就抛明确异常 | 游戏运行时点更新，得到可读错误而不是 `ApplyFailed` |
| 1.7 | 修尺寸重复计算（3 个松散 chunk 只算一次） | 计划总量 = `BaseAssembly.TotalSize + ΣItems`（不重复） |

### 阶段 2 —— 让"全新安装"真的能装出一个能跑的游戏（1–2 周）

| # | 动作 | 阻塞点 | 状态 |
|---|---|---|---|
| 2.1 | **着色器层**：解析 `bigshadercachelist_dx12.txt`，按与基座层相同的目录分工规则装配 `ResourceExA*.mpk`，并用 `NeteaseGamesBaseIndexWriter`（`info1` 表）写 `ResourceExA.patchinfo` | 无 | ✅ 见下方实测 |
| 2.2 | **P0-2 索引补全**：`WriteBaseIndex` 补目录行（size=0）+ 真实 `extra_mpkinfo_families` | 无 | ✅ `max_index`/families 与真机一致 |
| 2.3 | **P0-2 版本记录**：`Install` 计划写 `patchmd5`（远端清单逐字）+ 本地 `patchlist` | `pkgversion` 无公开来源 | ✅ 部分（见下） |
| 2.4 | **`Documents\` 与 `LocalData\Patch` 的小状态文件** | 需要一次干净机器验证 | ⬜ 未做 |
| 2.5 | 端到端验收：空目录 → 插件安装 → 游戏能启动进主界面 | 需要一次完整下载（约 71 GB） | ⬜ 未做 |

#### 2.1 着色器层（已实现）

- `NeteaseGamesShaderCacheList`：解析 `HEX_` → `{"file_list": {path: [hash, size]}}`
- `NeteaseGamesGameManager.FetchShaderPoolAsync` / `FetchShaderCacheListAsync`
- `NeteaseGamesGameInstaller.BuildShaderAssemblyPlanAsync` / `WriteShaderIndex`
- `NeteaseGamesBaseAssembler.AssembleAsync` 增加 `shardFileName` 参数（基座 `Patch{N}.mpk` / 着色器 `ResourceExA{N}.mpk`）

**实测验收** `[实测]`：

| 检查 | 结果 |
|---|---|
| 计划里的着色器分块 | 920 / 4,900,910,673 B，10 个分卷（厂商 17 个，数量不必相同） |
| 目录分工 | 4 个本轮变动块走 line0、916 个走 base |
| 分块字节重建（对真机分卷逐资源切片） | `dx12_1` 518/518、`dx12_500` 150/150、`dx12_916` 137/137、`dx12_920` 4/4、`dx12_919` 8/8 —— **817/817 逐字节相同** |
| `ResourceExA.patchinfo` 行数 | 173,861（173,859 资源 + `Shader`、`Shader/dx12` 两行目录）与真机一致 |
| 每行 size / hash | **173,859 / 173,859 与真机完全相同** |
| `meta` | `max_index`、`status` 与真机逐字相同 |
| SQLite 完整性 | `pragma integrity_check` = `ok` |

> ⚠ 实现中发现并修正了一个只有真机才会暴露的坑：着色器清单里的段名是 **`big_file_list`**，
> 但内容主机把分块放在 **`big_shader`** 目录下。直接拿解析出的段名拼 URL 会得到
> `big_{v}/big_file_list/dx12_920` → **404**（实测）。现已统一改写为 `big_shader`，并有单测钉住。

#### 2.2 / 2.3 索引与版本记录（已实现，`pkgversion` 例外）

- `Patch.mpkinfo`：目录占位行 + `extra_mpkinfo_families`
  - 实测：产出 234,909 行 / 真机 234,910 行（差 1 行，见下）；`extra_mpkinfo_families` **50 项，集合与真机完全相同**
  - `max_index` = 122（123 个分卷）vs 真机 127 —— 因为插件自己决定分卷数，索引描述的就是它写出的布局，这是设计如此
  - 那 1 行的差来自 `Char/Prop/sk_prop_zhayaotong/ani/sp`：真机索引里有一条目录行，但池里没有任何资源落在这个目录
    （该目录下唯一的资源 `base.skeleton` 在上一级）。这条无法从池推导，属于厂商历史残留，**不影响读取**
- `Install` 现在写 `patchmd5`（远端清单逐字）：模拟装完后 `installed=True current=0.0.6600116 hasUpdate=False` ✅
- `Install` 现在把 `patchlist_windows64_normal_mpk.txt` 落到本地 `patchlist_windows64_normal.txt`
  （实测该文件与真机本地副本逐字节相同）✅
- **`pkgversion` 仍不写**：它的值（真机 `6521279`）**不在任何公开清单里**——
  远端 `@private_res_version@` 是补丁版本（`6600116`），mini pack 的 `latest_pkgversion` 是 `5282637`（另一个计数器），
  `pkgcheck` 是 64 位哈希。写错会污染官方启动器的记账（旧实现就是这么把 6,521,279 覆盖成 6,593,596 的），
  所以**宁可不写**。后果：官方启动器可能认为基础包版本未知；Collapse 不受影响（它读 `patchmd5`）。

#### 2.4 仍然缺的东西（未做）

一次全新安装现在会产出 342 项 / **70,913,916,198 B（70.91 GB）**：基座 123 分卷、着色器 10 分卷、
delta 47 分卷 + 3 索引、引擎载荷 287 文件、根目录资源包 10 个、两个索引、`patchmd5`、本地 `patchlist`。

**仍然不会产生**（真机上有，但来源未定位）：

| 缺什么 | 真机值 | 说明 |
|---|---|---|
| `Documents\`（`versiondata.json`、`devicedata.json`、`Userdata\`、`notice\`、`hotfix_cache`、`perf_config`） | 141 MB | 极可能由游戏首次启动自建；**必须在一台干净机器上验证**，不能靠猜 |
| `LocalData\Patch\pkgversion` | `6521279` | 见上，无公开来源 |
| `LocalData\Patch\gameversion` | `CN_windows_0a66d2e2a_6521279_6589318` | 由客户端写；中间那段的包版本同样无来源 |
| `pkgcheck` / `patch_info` / `dxmode` / `cur_driver` / `fenbao_mode` / `latest_engine_ver` / `devicelimit.json` / `env_check.json` | 0 B–63 KB | 全部是启动器/客户端自建的小状态文件，插件**一个都不读也不写**（`grep` 确认）；`patch_info` 实测内容是 `{"base": [0, 0], "common": [0, 0]}` |

结论：**能不能装出一个"能跑"的游戏，现在只差 2.5 的端到端验收**。上面这些文件里，插件自己不依赖任何一个，
真正的风险只有"游戏首次启动是否自建 `Documents\`"这一条——它决定 2.4 要不要做。

### 阶段 3 —— 工程闭环（1 天，可与阶段 1 并行）

| # | 动作 |
|---|---|
| 3.1 | 加 `.github/workflows/build.yml`（push/PR 跑 build + 单测，`paths-ignore: **.md`） |
| 3.2 | 加 `CompileAOTAndShip.bat`（参数化、非交互，与 PW/HG 对齐） |
| 3.3 | 补 `LICENSE`（MIT）+ `README.zh-CN.md` |
| 3.4 | 清理死代码：`NeteaseGamesLoginState.Save`（接通或删）、`BigPatchList.Merge`、`MeasureInstallSize`、`ShardCluster`（接上或删）、`gamePathProvider` 参数 |
| 3.5 | 公告语言改用 `SharedStatic.PluginLocaleCode` |
| 3.6 | 修 `AGENTS.md` / `README.md` / `PROTOCOL-VERIFIED.md` 里已被推翻的段落（见 §6） |

---

## 6. 文档纠错（逐条）

| 文档 | 原文 | 实测 |
|---|---|---|
| `README.md` 能力表 | "**全新安装** 🚧 待完成 …… `big_` 通道已定位" | 计划器**已能**出 Install 计划（341 项 / 66 GB），但缺着色器层与索引行 ⇒ 状态应为"计划可用、结果不可用" |
| `README.md` | "**增量更新** ✅ 已修（2026-10-24）" | 源码成立；**真机被 `@bigpatchmd5@` 闸门拒绝**（P0-1） |
| `README.md` | "**修复（Repair）** ✅ 已修" | 判定与 Update 相同；但 Repair **不走** `VerifyBaseTierCompletable` ⇒ 它会绕过闸门直接下 delta 层并写版本号 |
| `AGENTS.md` §待完成 1 | "行 0 目录只服务本轮变动的 28 个分块，其余 404" | **正确**（本轮复测 28/28、30/30） |
| `AGENTS.md` §plan 模式 | "分卷数不必与厂商相同（123 vs 128）" | **正确**，但 `max_index` 写 122 与真机 127 不同；目录行缺失 |
| `AGENTS.md` §待完成 4 | "引擎载荷只按大小校验" | **正确**；10 条 `used_del_dir` 实测 10/10 大小一致 |
| `AGENTS.md` §媒体 | "背景用内嵌的官方启动画面（`LocalData\Splash.bmp`）" | 文件是 `background.jpg`（327,549 B，在 Core 里）；`Splash.bmp` 确实存在但插件不读 |
| `NETEASE-AUDIT-MEASURED.md` §1 P0-1 | "部署的是旧版，任何一次更新都会被拒绝" | 该文写于 10-24；现在部署的是 0.4.1（MD5 与 publish 一致），但**仍会被拒绝**（原因变成了 P0-1 的指纹闸门） |
| `NETEASE-AUDIT-MEASURED.md` §2 P1-1 | "卸载会删掉整个安装根" | **正确**（且会连 `Documents\` 一起删） |
| `NETEASE-AUDIT-MEASURED.md` §2 P1-4 | "`NeteaseGamesSession` 是死代码" | **部分正确**：`ApplyStoredSession` 有调用方，只有 `Save` 是死的 |
| `NETEASE-AUDIT-MEASURED.md` §3 | "松文件层不在磁盘也不在索引里" | **正确**（232,453 条，磁盘命中 0） |
| `PROTOCOL-VERIFIED.md` | 未记载 | `Patch.mpkinfo` 有 **2,460 条 size=0 的目录行**；`extra_mpkinfo_families` 是 50 项；`ResourceExA.patchinfo` 用 `info1` 表 + `status` meta |
| 本文档之前的 `REVIEW-NETEASEGAMES.md`（2026-10-20） | "5 处关闭 TLS 校验" | 当前代码 **0 处**（`NeteaseGamesHttp` 从不关闭） |

---

## 7. 复现方法（本轮实际用的）

```bash
# 1) 真机状态 + 更新计划 + 基座闸门（直接调插件自己的类）
#    探针工程在 %TEMP%\ntprobe\InstallProbe（引用 Core + Plugin.Core）
dotnet build -c Debug && ./bin/Debug/net10.0/InstallProbe.exe update "D:\ywzh\ywzh_extreme"

# 2) 全新安装计划（空目录）
./bin/Debug/net10.0/InstallProbe.exe install "<空目录>"

# 3) 单测（必须直接跑 exe：dotnet test 会走 MSBuild 命名管道）
./Hi3Helper.NeteaseGames.Tests/bin/Debug/net10.0/win-x64/Hi3Helper.NeteaseGames.Tests.exe

# 4) 协议探测（python -I，避免 cwd 注入）
#    HEX_ 解密 = base64 → XOR "netease_joker_h74_encrypt_key" → zlib
#    基座索引 = SQLite；filepath/value 用 XOR 混淆：head=len%17, i<head ? 0x2B : 0x35
#    value 布局：u32 size@0, u16 shard@8, u32 offset@20, u64 hash@10
```

关键数字（可复算）：

| 量 | 值 |
|---|---|
| 远端内容版本（行 0） | `20261025174155378` |
| 远端 `@base_version@` | `20261021113421287` |
| 补丁版本（行 3） | `6600116` |
| 本地补丁版本 | `6593596` |
| 远端 / 本地 `@bigpatchmd5@` | `16703640110960187218` / `6128997482385675719` |
| 基座层 | 128 分卷 / 64,232,426,422 B |
| delta 层 | 47 分卷 / 764,447,019 B |
| 着色器层 | 17 分卷 / 4,989,773,170 B + 索引 52,342,784 B |
| 根目录资源包 | 5 个 / 254,775,136 B |
| 基座 chunk 池（line0） | 10,424 块 / 63,840,666,038 B |
| 着色器 chunk 池（line0） | 920 块 / 4,900,910,673 B |
| `Patch.mpkinfo` | 234,910 行（232,450 有效 + 2,460 目录） |
| `ResourceExA.patchinfo` | 173,861 行 / 17 个 marker |
| 重切分块 | 9 新 + 19 改，覆盖 302 资源 / 18 个基座分卷 |
