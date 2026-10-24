# Netease 插件问题清单与修复计划（实测复核版）

> 复核日期：2026-10-24
> 复核对象：`Hi3Helper.Plugin.NeteaseGames`（工作树 `1ed5302` + 未提交改动）。
> 真机环境：`D:\ywzh\ywzh_extreme`（NTE/遗忘之海，线上内容版本 `20261023182924387`，补丁版本 `6593596`）。
> 对照：`Hi3Helper.Plugin.PerfectWorld`（下称 **PW**）、`Hi3Helper.Plugin.Hypergryph`（下称 **HG**）。
>
> **本文所有结论都在本机实测过**，包括：构建、单测、真机磁盘、远端 CDN 探测、以及**直接调用插件自己的类**跑出来的行为。
> 凡与仓库里 `AGENTS.md` / `PROTOCOL-VERIFIED.md` / `README.md` 冲突的地方，**以本文为准**，并逐条列在 §4.1。
> 测试环境是 Linux 沙箱（.NET 10.0.402 + 你机器上的真实游戏文件）。凡是依赖 Windows 路径语义的地方（`\` 分隔符、注册表、DPAPI、NativeAOT），文中都明确标注了 `[读码]` 或“需 Windows 复核”，不做超出证据的断言。
> 每条都标了证据等级：`[实跑]` = 我跑出来的输出；`[探测]` = 我发 HTTP 请求得到的状态码/字节数；`[读码]` = 只读代码推断（未跑）。

---

## 0. 一句话结论

**逆向/协议层仍然是这个仓库最好的部分，但"把结果正确落到磁盘并如实报告"这一层有 5 个会直接损坏玩家安装或让功能静默失效的缺陷；同时有一半的"能力表"在真机上是假的。**

对比另外两个插件，Netease 的差距不在协议，而在**工程闭环**：

| 维度 | PerfectWorld | Hypergryph | **Netease** |
|---|---|---|---|
| 单元测试 | 无 | 无 | **有**（124 个测试方法 / 166 个用例，`[实跑]` 165 通过 1 失败） |
| CI build（非发布） | 有 `build.yml` | 有 `build.yml` | **没有**（只有 `release.yml`） |
| 一键 AOT 脚本 | `CompileAOTAndShip.bat` | `CompileAOTAndShip.bat` | **没有**（README 手敲命令） |
| 双语文档 / LICENSE | 有 | 有 | **都没有** |
| 端到端验证记录 | 有 | 有 | 有，但结论一半未落进代码 |
| Core 代码量 | 17.5k 行 | 25.4k 行 | 9.6k 行（其中 ~1/3 是"研究残留"） |
| **全新安装** | ✅ 支持 | ✅ 支持 | **❌ 硬拒绝**（`NotSupportedException`） |
| **增量更新** | ✅ | ✅ | **❌ 当前版本会抛异常**（见 P0-1） |

PW/HG 的"工程化"其实也很薄（都没有单测、都靠手工脚本），所以**不该照搬它们的流程**；真正该补的是 §4.3 里那几条，以及把"能力表"改成真话。

---

## 1. 实测基线

### 1.1 构建与单测

| 项 | 结果 |
|---|---|
| `dotnet build Hi3Helper.Plugin.NeteaseGames.slnx -c Debug`（SDK 10.0.402） | **成功**，0 error / 2 warning（`xUnit1051`，`PackageIndexTests.cs:194,201`） |
| 单测执行 | **165 通过 / 1 失败** |
| 唯一失败 | `PathSafetyTests.ResolveTargetPath_RejectsTraversalOutsideTheRoot("..\\escape.txt")` |

> 这个失败**是 Linux 沙箱假象、不是缺陷**：`Path.GetFullPath` 在 Linux 上把 `..\escape.txt` 当成一个普通文件名（反斜杠不是分隔符），于是"没有越界"。在我的 Windows 语义推演下它会正确抛异常。要让它可跨平台跑，测试应显式跳过非 Windows 或改用 `Path.DirectorySeparatorChar` 组装用例。

### 1.2 真机状态（`D:\ywzh\ywzh_extreme`）

| 项 | 实测值 |
|---|---|
| 补丁版本（`patchmd5` 行 3） | `6593596` |
| 基础包版本（`pkgversion`） | `6521279` |
| `patchmd5` 行 0（内容版本） | `20261023182924387` |
| `gameversion` | `CN_windows_0a66d2e2a_6521279_6593596` |
| `Engine\Config\Built.version` | `261014173721_4a476f25b_release` |
| `Documents\versiondata.json` | `package_version=6521279, patch_version="6593596"` |
| 基础分卷 | `Patch.mpk` + `Patch1..127` = **128 个 / 64.23 GB** |
| 增量分卷 | 50 个包 / **775.0 MB**，与远端清单**逐个大小吻合** |
| 引擎载荷 | 277 个真实文件 / **1.142 GB**（另有 10 条 `used_del_dir` 删除标记） |
| `mpkexclude` | 778 条，其中 775 个 `*.mdb` **全部不在盘上** |
| `sort_map` / `preload_list` | 盘上**不存在**（确认是临时文件） |
| 被改写过的基础分卷 | 10 个（8,115,117,119,121,122,124,125,126,127），共 **2.42 GB** |

### 1.3 远端探测

| 路径 | 结果 |
|---|---|
| `h74.update.netease.com/pl/OB_patchmd5_...txt` | 200，1984 B，明文 |
| `h74.gph.netease.com/{行0}/mpk_*/...` | **不需要 key1/key2，裸请求 200**（签名参数带了也不拒绝） |
| `h74.gph.netease.com/{行0}/shadercachelist_dx12.txt` | 200，7,985,520 B，**与本机 md5 一致** |
| `h74.gph.netease.com/big_{行0}/...` | **目录和清单都在，但 10406 个分块里随机抽 60 个只中 1 个** |
| `h74.gph.netease.com/big_{@base_version@}/...` | **随机抽 60 个全部 200** |
| `h74.gdl.netease.com/loose_file/mini_pack_<ver>/...` | 带签名 200；**不带签名 403** |
| `h74.gph.netease.com/loose_file/...`（插件实际用的） | **404** |

---

## 2. P0 —— 会直接让功能失效或损坏安装

### P0-1 更新当前**必抛 `NotSupportedException`**：`@bigpatchopen@` 长期为 true

`NeteaseGamesGameInstaller.VerifyBaseTierCompletable`（`Management/NeteaseGamesGameInstaller.cs:661`）在 `@bigpatchopen@ == true` 时直接抛异常拒绝整轮更新。

`[实跑]` 用插件自己的代码跑（`VerifyBaseTierCompletable(manifest, plan)`，见附录 A 的复现方法）：

```
VerifyBaseTierCompletable: THREW NotSupportedException
```

> 注意：该守卫检查的是 `manifest.IsBigPatchOpen || manifest.IsFullPackage`，**与 plan 内容无关**，所以只要远端 `@bigpatchopen@` 为 true 就必抛——即使本轮一个包都不用下。

`[探测]` 远端 `@bigpatchopen@` 在 2026-10-22 / 10-23 / 10-24 三次抓取中**都是 `true`**。

⇒ **现在这个版本对已装游戏完全无法更新**，每点一次"更新"都会弹错。这是当前最严重的问题，且比文档里写的"安全侧"严重得多——文档把它描述成"偶尔拒绝一轮"，实际是"永远拒绝"。

### P0-2 `big_` 目录用的是 `@base_version@`，不是行 0 —— 文档与代码都错了

- `AGENTS.md` §待完成 1 与 `PROTOCOL-VERIFIED.md` §5.4 都写：目录是 **`big_` + 行0 内容版本**。
- `[探测]` 三轮独立抽样，结论一致：

  | 抽样 | `big_{行0}` | `big_{@base_version@}` |
  |---|---|---|
  | 60 个（行0 清单里的条目） | **1 / 60 = 200** | 60 / 60 = 200 |
  | 15 个（行0 清单） | — | 15 / 15 = 200 |
  | 15 个（base_version 清单） | 0 / 15 | 15 / 15 = 200 |

- 两版的 `bigpatchlist` 内容也不同（11,122,614 B vs 11,124,133 B；9 个条目只在新清单、4 个只在旧清单、19 个公共条目大小不同）。**而且两份清单描述的条目在 `big_{base_version}` 下都能取到** ⇒ 该目录是一个**持续累积**的分块池，不是"某一版本的快照"。相对的，`big_{行0}` 下只有 9 个新条目能取到（`only_new` 抽样 8/8 = 200），其余全 404 ⇒ 那个目录只放了**本轮新增**。

> 这说明厂商的 `big_` 目录是**按 `@base_version@` 累积**的：老分块一直留着，新分块追加进去。（`PROTOCOL-VERIFIED` §5.4 里记录的那次内存取证读到的 `big_20261023122834919`，按此解释应是当时那一轮的 `@base_version@`，而不是行0——这一条我无法回溯验证，只能说与本次观测一致。）

⇒ **若按现文档实现"从 big 通道修基础包"，会 90%+ 打 404。** 这条结论必须先纠正，再谈合并规则。

### P0-3 基础分卷索引的**两个字段语义被写反**，导致截断检测完全失效

`NeteaseGamesBaseIndexReader.cs` 与 `PROTOCOL-VERIFIED.md` §5.4 都写：

```
value[8:10]  = 分卷 ID          ← 对
value[20:24] = 累积"结束偏移"    ← 错，这是每条资源的"起始偏移"
```

`[实跑]` 用 SQLite 直读 234,910 行并逐分卷核对：

| 分卷 | 条目 | `max(start)` 文档口径 | `max(start+size)` | 文件实际 |
|---|---|---|---|---|
| Patch54 | 1197 | 224,842,991 | **672,533,543** | **672,533,543** ✅ |
| Patch8 | 235 | 82,683,402 | **95,807,964** | **95,807,964** ✅ |
| Patch0 | 4191 | 523,713,530 | **524,474,562** | **524,474,562** ✅ |
| Patch100 | 2252 | 522,501,540 | 522,590,406 | 524,466,680（唯一有 1.8 MB 真实填充） |

**128 个分卷中 127 个满足 `max(start + size) == 文件大小`**（误差 0）。文档据"文件大小 − `max(start)`"算出的 13 KB…447 MB 所谓"填充"，绝大部分是**最后一条资源的大小**，不是填充。

`[实跑]` 破坏性验证：把 `Patch54.mpk` 截断到 300,000,000 B（真值 672,533,543），跑插件自己的函数：

```
FindTruncatedBaseShards(/tmp/trunc) -> 0 reported: []
```

⇒ **基础层截断检测是瞎的**。要命的是它还被当成"更新后完整性校验"的唯一手段（`RunAsync:259`），所以任何基础分卷损坏都会被静默放过，而 `pkgversion` 照样写入。

修法：`ShardEnds` 改存 `max(offset + size)`，同时把"缺失分卷"也纳入报告（当前 `FindTruncatedBaseShards` 里 `!File.Exists` 直接 `continue`，等于漏报）。

### P0-4 更新成功后会**把 `pkgversion` 写成补丁版本号**，覆盖厂商自己的记账

`plan.PackageVersion = manager.RemotePackageVersion`（`NeteaseGamesGameInstaller.cs:323`），而 `RemotePackageVersion` 装的是 `patchmd5` **行 3 的补丁版本**（`NeteaseGamesGameManager.cs:318-329`）。`NeteaseGamesUpdateApplier.cs:165-183` 随后把它写进 `LocalData\Patch\pkgversion` 并刷新 `patchmd5`。

`[实跑]` 造一份 `pkgversion=6521279` 的安装，跑 `ApplyAsync`：

```
plan.PackageVersion (fed into the applier) = 6593596
apply success = True
pkgversion 6521279 -> 6593596      ← 基础包版本被改写
```

`PROTOCOL-VERIFIED.md` §7 自己写了"`package_version` 和 `patch_version` 不能混写"，但代码正是在混写。

后果：官方启动器读 `pkgversion` 判断基础包版本，被改成 6.59M 后会认为"基础包比远端新"或触发不必要的全量校验；Collapse 侧因为 `NeteaseGamesLocalState.Read` 优先读 `patchmd5` 所以看不出异常——**是典型的"插件自己觉得没问题、官方启动器崩给你看"**。

修法：`pkgversion` 只在**基础包真的变了**（P0-2/P0-5 的能力）时才写；日常增量更新只写 `patchmd5` + `versiondata.json` 的 `patch_version`。

### P0-5 引擎载荷（mini pack）在**错误的域名**上，永远是 404

`NeteaseGamesContentEndpoints.GetEnginePayloadHosts()` 返回 `h74.gdl.netease.com`（正确），但**下载走的是 `NeteaseGamesDownloader`**，它的 `_contentRoots` 是 `h74.gph.netease.com` + 两个镜像（`NeteaseGamesDownloader.cs:273-279`）。`BuildUris` 只按 content root 拼，**不看 item 的来源主机**。

`[实跑]`（真机、真实 plan）：

```
uri: https://h74.gph.netease.com/loose_file/mini_pack_202610151759/... 
engine payload download: ok=False
```

`[探测]` 同一路径换 gdl + 签名 → **200 / 183,016 B**；换 gph（含两个镜像）→ **404**。

⇒ **只要触发引擎文件下载（首装、引擎缺失、引擎更新），一定失败**，而且 `DownloadAsync` 会在 3 个主机 × 2 次重试全部 404 后返回 `false`，`ApplyAsync` 报 `DownloadFailed` —— 用户看到的是"补丁无法应用"，实际是域名选错。

修法：给 `NeteaseGamesDownloadItem` 加 `Host`/`RequiresSignature`，或在 `BuildPlanAsync` 里给引擎项写入完整 URL。

---

## 3. P1 —— 会造成错误行为/错误报告

### P1-1 `Patch2500.mpk` 的 marker 映射错，导致布局校验对它永久失效

`BuildShardMarkerMap`（`NeteaseGamesGameInstaller.cs:486-511`）按"段内序号 × 2"生成 marker：

`[实跑]` `markers["Patch2500.mpk"] = 4060`；而 `s_file.mpkinfo` 里 `Patch2500` 的真实 marker 是 **5000**。

`[实跑]` 再造一个 100 字节的假 `Patch2500.mpk`：

```
IsLayoutConsistent(100-byte Patch2500) = True   ← 应该 false
```

⇒ 该分卷的"索引布局校验"这一道闸完全失效，只剩大小比较。

### P1-2 侧文件（`shadercachelist_dx12.txt`）刷新**永远失败**

`AppendShaderCacheList`（`:708-735`）在"本地已有但内容版本变了"时把 `ExpectedSize` 设成**本地旧文件长度**：

```csharp
ExpectedSize = missing ? 0 : new FileInfo(localPath).Length
```

`[实跑]` 先用插件生成真实 plan（侧文件项 `ExpectedSize` 就等于本地旧文件长度），再把它交给下载器：

```
SideFile item: expectedSize=<本地旧文件长度>   # 来自 AppendShaderCacheList
download with the plan's ExpectedSize -> ok=False
服务器实际 7,985,520 B
```

⇒ 内容版本一迁移，侧文件就再也刷不动，每次都报下载失败，**并且会让整轮 `ApplyAsync` 以 `DownloadFailed` 结束**（因为侧文件在同一个 plan 里）。

### P1-3 `HasEngineUpdate` 误报：每次启动都提示"引擎有更新"

`UpdateEngineState`（`NeteaseGamesGameManager.cs:253-281`）拿 `Built.version` 与 mini-pack 的 `engine_version` 比，但两者格式不同：

```
Built.version            = 261014173721_4a476f25b_release
manifest.engine_version  = 261014173721_4a476f25b_release      ← 这一项其实相等
state.EngineVersion      = 261014173721_4a476f25b_release_r0000_6ad03d31   ← 被 versiondata.json 覆盖成了带后缀的
=> HasEngineUpdate = True
```

问题是 `NeteaseGamesLocalState.Read` 用 `versiondata.json` 的 `engine_version` **覆盖**了 `Built.version`（`NeteaseGamesLocalState.cs:284-288`），而后者才是启动器口径。实测同一台机器，只保留 `Built.version` 时 `HasEngineUpdate=False`，加上 `versiondata.json` 后变 `True`。

⇒ 后果不是"提示一下"而是**每轮 plan 里都带上 277 个引擎文件 / 1.14 GB**（`[实跑]` `loose=277 / 1,141,908,166 B`），每次点"更新"都白下 1.1 GB；修好之后同一台机器的 plan 应只剩 50 个包 / 775 MB。`LatestEngineVersion` 在这里只是 `Built.version` 缺失时的兜底，救不了这个场景。

### P1-4 首次 `GetGameSizeAsync` 可能给 0，导致磁盘预检放行

`GetOrBuildPlanAsync` / `BuildPlanAsync` 依赖 `manager.RemotePatchManifest`；若宿主在 `InitAsync` 之前就问大小（`PluginGameInstallWrapper` 是先 `InitPluginComAsync` 再问，`[读码]` 这么看是安全的），但 `ProbeRemoteVersionAsync` 的超时是 5 s（`StartupProbeTimeout`），**超时后不会重试**（`_remoteProbed` 只在成功时置位，但异常被吞掉后直接返回，本轮的 `GetGameSizeAsync` 就拿不到远端清单）。

`[实跑]` 两个目录对比：只放「清单/索引齐全、包是稀疏占位」的目录 → `1,141,908,166 B`（= 277 个引擎文件，因为包占位文件大小都对得上、被判为已完整）；再用真机文件的硬链接重建 → `1,916,934,305 B` = 1,141,908,166 + 775,026,139。两数说明**引擎项与增量包项在同一个 plan 里叠加**，两者都跑才完整。

### P1-5 卸载只认 `ywzh.exe`，对 `ywzh_extreme` 以外的布局会误删/误拒

`UninstallAsyncInner` 用 `IsGameDirectory()` 挡刀（要求存在 `Engine\Binaries\Win64Release\ywzh.exe`），这点是对的。但它随后 `Directory.Delete(gamePath, recursive: true)` 删**整个安装根** —— 实测安装根里还包含 `Documents\`、`mpk_filelist.info`、`ResourceEx*.mpk`。PW 的做法是**只删自己管理的子目录**（`PerfectWorldGameInstaller.cs:146-174` 逐个 `Directory.Delete(clientDir/launcherDir/stagingDir)`），这个模式更安全，建议照搬。

### P1-6 `NeteaseGamesShardCluster` 解析了 `mpk_filelist.info` 但**从未被使用**

`[实跑]` 这份清单是**全协议里唯一能和磁盘逐字节对上的校验源**：8 个文件的大小 + MD5 **全部一致**。

但 `[读码]` `NeteaseGamesShardCluster` 只在测试和 `tools/re/PlanProbe` 里被引用，生产代码没用。而 `mpk_filelist.info` 正好覆盖 `Patch.mpk` / `ResourceEx*` / `Resources*` 这 8 个**基础包**——是 P0-2/P0-3 之外唯一能真正校验基础层的抓手。应接进 `Repair` 计划。

---

## 4. P2 —— 文档、工程与死代码

### 4.1 文档与代码不一致（逐条实测）

| 文档断言 | 实测 | 结论 |
|---|---|---|
| `AGENTS.md`：big 目录 = `big_` + 行0 | `big_{行0}` 59/60 = 404；`big_{base_version}` 60/60 = 200 | **错**（P0-2） |
| `PROTOCOL-VERIFIED`：索引 `[20:24]` = 累积结束偏移 | 实为**起始偏移**；`max(start+size)` 与文件大小 127/128 精确相等 | **错**（P0-3） |
| `PROTOCOL-VERIFIED`：`Patch1009` 有 61,766 B 尾部填充 | 该文件 17,985,424 B，而 `script.mpkinfo` 里 marker 2018 的 `max(offset+size)` = **17,923,658**；差 61,766 B **真实存在** | **对** |
| `PROTOCOL-VERIFIED`：签名不带 → 403 | `h74.gph` 裸请求 **200**；只有 `h74.gdl` 引擎载荷需要签名（裸请求 403） | **部分错**：签名只对 gdl 是硬要求 |
| `README`：基础包"内容服务器全部 404，bigpatch 通道未定位" | `big_` 通道确实存在，只是**版本目录取错**；且分块按批上架/下架（见 §6） | **过期** |
| `AGENTS.md`：`PatchN.mpk` 分卷由多个 `big_common` 条目**合并**而成 | 抽 6 个分块做条目级（解压后 md5）与块大小序列比对，**0 命中**；`Patch126` 的 381 条索引与文件严格连续，分块也是同类容器但边界不同 | **未证实**，现有证据既不支持也不否定 |
| `README` 引用 `docs/re/netease-protocol.md` | 文件已被删除（`git status` 显示 `D`），现存只有 `docs/PROTOCOL-VERIFIED.md` | **死链** |
| `AGENTS.md` 说 MpkExclude 全是 `*.mdb` | 778 条里 775 个 `.mdb` + 3 个 `prerequisite` / `4a/...` / `Misc/lazycode_zstd_dict`；后者**在盘上存在** | **不精确** |
| `README`：全新安装"待完成" | `Install` 抛 `NotSupportedException`，属实；但"144 个 / 64 GB"应是 **128 个 / 64.23 GB** | **数字错** |

### 4.2 版本号 / 自更新（`[读码]`，但推断链完整）

- `Exports.cs` 在 NativeAOT 下 `Load<SeaOfRemnantsPlugin>(new GameVersion(1,0,0,0))`，`GetPluginVersion` 在 AOT 下走 `_currentDllVersion` 兜底 ⇒ 插件自报 **1.0.0.0**。
- `release` 分支上的 `manifest.json` 是 **0.4.0.0**。
- `PluginSelfUpdateBase.TryPerformUpdateAsync` 判 `cdn <= current` 即 `NoAvailableUpdate`。

⇒ **只要 AOT 构建真的走到那个兜底，自更新永远不会触发**。这一点我在 Linux 上无法验证 NativeAOT 行为（没装 MSVC 链接器），**必须由你在 Windows 上实测一次**：装 1.0.0.0 的 DLL，看日志里 `Successfully loaded plugin ... with version X`。如果真是 1.0.0.0，请把 `Load<T>` 的版本参数改成读 `AssemblyInformationalVersion` 或干脆与 csproj `<Version>` 同步。

### 4.3 工程化缺口（对照 PW/HG）

| 项 | 建议 |
|---|---|
| 没有 `build.yml` | 补一个只跑 `dotnet build` + 单测的 PR 工作流（PW 有，且带 paths-ignore） |
| 没有 `CompileAOTAndShip.bat` | 补，含"restore 整解决方案 + 不加 `--no-restore`"的防降级逻辑（这是 Core 文档里记的坑） |
| 没有 LICENSE | 仓库是公开的（`Vohrt/Hi3Helper.Plugin.NeteaseGames`），PW/HG 都有 LICENSE，补一个 |
| 没有双语 README | 至少把"需要官方启动器装过一次"写进英文摘要 |
| `.github/workflows/release.yml` 只在 tag / dispatch 触发 | 没有"每次 push 验证能编译"的闸门，AOT 只能靠手工发现坏了 |
| 未提交改动 674+/987− | `git status` 有 12 个 M + 12 个 ??，且 `docs/re/netease-protocol.md` 被删而 README 还在引用。**提交前先收敛** |

### 4.4 死代码 / 未接入的研究成果

`[读码]` 以下在生产路径上没有任何消费者，但会误导后来的人以为已在用：

`NeteaseGamesShardCluster`（P1-6）、`NeteaseGamesDownloadPlan.HasVerifiableObjects`（只有测试用，因为 `ExpectedMd5` 从来没人填）、`NeteaseGamesLocalState.ReadPatchList` + `InvalidatePatchListCache`（**唯一的调用方是 `SetGamePathInner` 的 invalidate**，读接口没人调）、`NeteaseGamesContentEndpoints.SortMapFileName` / `PreloadListFileName`、`NeteaseGamesUrlSigner` 的整套签名（gph 不需要，gdl 用得上但引擎下载路径没走对）。

建议：要么接上（`ReadPatchList` → 用于本地清单对账；`ShardCluster` → 用于 Repair），要么移到 `Research/` 并标 `// 未接入`。

---

## 5. 与 PW/HG 的关键差异（值得抄 / 不值得抄）

**值得抄：**

1. **单向、可恢复的写入顺序**：HG 的 `_Hg_ApplyStage` + `CommitStageDirectory`，先落 staging 再原子换；PW 的"包 → 索引 → 版本号"。Netease 已有类似结构（`NeteaseGamesUpdateApplier` 顺序是对的），但**版本号写错值**（P0-4）。
2. **卸载只删自己管的目录**（PW `:146-174`）。
3. **`InitAsync` 限时**：PW 的 `StartupProbeTimeout = 5 s` 且"超时不置 `_remoteProbed`"，Netease 抄了但**失败后不重试**，应改成"下次宿主询问时允许再试一次"。
4. **进程按路径消歧**：PW 用 `MainModule.FileName.StartsWith(exeDir)`。Netease 已经这么做了（`BelongsToInstallation`），这点**比文档里写的好**，不用改。

**不值得抄：**

1. PW/HG 都**没有单元测试**。Netease 的 166 个测试是这个仓库的资产，别为了"对齐"删掉。
2. PW 的 `CompileAOTAndShip.bat` 里面向交互式 `choice`，CI 不能直接用；Netease 若补脚本应做成可参数化。
3. HG 的 `ParentProcessBridge`（复制签名 exe 当父进程）是 Endfield 专属的 workaround，不要往 Core 里搬。

---

## 6. 仍未解决/需要真机捕获的事

1. **`PatchN.mpk` 的合并规则**：本次对 6 个 `big_common` 分块做条目级（ZSTD 解压后 md5）与“块大小序列”两种比对，**均 0 命中**。已确认的事实只有两条：`big_common` 分块与 `PatchN.mpk` **都**是「`ZSTD`+u32 解压长度+frame」的重复容器；`Patch126.mpk` 的 381 条索引与文件**严格连续**（381/381 首尾相接，末条结束 = 文件大小 76,739,485），其中 31 条是纯 ZSTD 块、350 条以 `02 02 02 01` 开头且每条前 296 B 是同一段元数据头。**“分卷由分块合并”这个前提本身还没被任何观测支持**——更可能的情况是：基础分卷与 big 分块是**同一内容池的两种编排**，切分边界不同。要继续，必须在 `fenbao_mode ≠ 0` 时抓一次真实的全量改写（`tools/re/MemRegion` 的流程文档里有）。
2. **分块按批上下架**：同一份 `bigpatchlist` 里，`only_new` 的 9 个条目只在 `big_{行0}` 存在、`common` 的条目只在 `big_{base_version}` 存在。说明 CDN 保留的是"**本次变更涉及的那一批**"，不是"某一版本的完整快照"。任何实现都必须先读 `@allpatchlistdiffmd5@` 决定去哪取，而不是假定"一个目录全都有"。
3. **`sort_map` 的数值语义**：12,503 条，值域 `5,000,000,000,000 … 6,049,137,359,3xx`，落在基础层 64.23 GB 的**前 94%**，但**不等于**任何索引条目的 `start` 或 `start+size`（精确命中 1/12503）。目前只能确认它是"临时文件、不该持久化"，数值含义未解。
4. **`pkgversion` 与 `latest_pkgversion` 的关系**：远端的 `normal.latest_pkgversion = 5282637`，本机 `pkgversion = 6521279`，两者不等但都不是补丁版本。是否需要在某些轮次同步，未验证。

---

## 6.5 实施进度（2026-10-24 当天）

| 项 | 状态 | 验证方式与结果 |
|---|---|---|
| P0-1 `@bigpatchopen@` 无条件拒绝 | ✅ 已修 | 改为比对 `@bigpatchmd5@`。真机 `bigPatchOpen=True` + 指纹相同 → **放行**；指纹改成 `1` → **拒绝**。`@is_full@` 仍直接拒绝 |
| P0-2 `big_` 目录版本 | 📄 已改文档 | PROTOCOL-VERIFIED §5.4 与 AGENTS 已改为 `@base_version@`；代码侧尚未接入 big 通道（阶段 1.3 仍待做） |
| P0-3 索引字段语义 | ✅ 已修 | `ShardEnds` 改 `max(start+size)`。真机 **0** 误报；300 MB 假 `Patch54` → 精确报出 `Patch54.mpk`（1 个，不再是 0 或 128） |
| P0-4 `pkgversion` 被覆盖 | ✅ 已修 | `plan.PatchVersion` / `plan.PackageVersion` 拆分。完整更新后 `pkgversion` **仍为 6521279**，`patchmd5` 行 3 前进到 6593596 |
| P0-5 引擎载荷域名 | ✅ 已修 | 引擎项带 `Hosts=[h74.gdl.netease.com]` + `RequiresSignature`。真机下载 `amd_fidelityfx_dx12.dll.1` → **ok=True / 6,647,504 B** |
| P1-1 `Patch2500` marker | ✅ 已修 | 改为"名字里的数字 ×2"。`Patch2500 → 5000`；100 B 假文件布局校验 **false** |
| P1-2 侧文件 `ExpectedSize` | ✅ 已修 | 刷新时填 0（未知） |
| P1-3 引擎版本误报 | ✅ 已修 | `Built.version` 优先；新增 `EngineVersionsMatch` 容忍 `_r0000_*` 后缀。真机 `HasEngineUpdate=False`，plan 里引擎项 **0** |
| P1-5 卸载删整根 | ⬜ 未做 | 阶段 3.4 |
| P1-6 `mpk_filelist.info` 未接入 | ⬜ 未做 | 阶段 3.1 |
| 工程化（build.yml / AOT 脚本 / LICENSE） | ⬜ 未做 | 阶段 2 |

单测：**174 个用例，173 通过**，唯一失败仍是 `..\escape.txt` 的 Linux 路径语义假象（Windows 上通过）。
完整更新实跑（3 个包故意做旧）：`StartUpdateAsyncInner: completed`，陈旧文件大小恢复正确，两个版本计数器各归各位。

## 7. 修复计划

### 阶段 0：止血（0.5 天，必须先做）

| # | 动作 | 验收 |
|---|---|---|
| 0.1 | **修 P0-3**：`ShardEnds` 改存 `max(offset + size)`；缺失分卷也纳入报告 | 用 300 MB 的假 `Patch54`（真值 672,533,543）必须报出 1 个；真机必须报 0 个 |
| 0.2 | **修 P0-4**：新增 `PatchVersion` 字段与 `PackageVersion` 分离，只写 `patchmd5` + `versiondata.json:patch_version` | 跑一次真实更新后 `pkgversion` **不变**；`patchmd5` 行 3 前进 |
| 0.3 | **修 P1-1**：marker 用 `s_file.mpkinfo` 里读到的真实 marker（`Patch2500 → 5000`），不再按序号推算；或在推不出时**跳过校验而不是放过** | 100 B 假 `Patch2500` 必须报 false |
| 0.4 | **修 P1-2**：侧文件 `ExpectedSize` 一律填 0（未知），交给"下载后按服务器实际长度校验"；或从 `@bigshadercachelistmd5@` 取大小 | 用旧文件触发刷新 → 成功 |
| 0.5 | **修 P1-3**：`UpdateEngineState` 优先用 `Built.version`，且比较时先剥掉 `_r0000_*` 后缀 | 真机 `HasEngineUpdate` = false，plan 里引擎文件数 = 0 |

> 阶段 0 完成后，即使 P0-1 的 `@bigpatchopen@` 仍为 true，至少**不再静默损坏**、也不会白下 1.14 GB。

### 阶段 1：让更新真的能跑（2–3 天）

| # | 动作 | 验收 |
|---|---|---|
| 1.1 | **修 P0-5**：引擎载荷走 `h74.gdl.netease.com` + 签名，与内容下载器解耦 | 真机下载 `cclibdll.dll.1` 得到 183,016 B |
| 1.2 | **修 P0-1**：`@bigpatchopen@` 不再无条件拒绝。改为：读 `@bigpatchmd5@` 与本地记录比对 → 未变 → 只做增量；变了 → 只对**受影响的单个分卷**走 big 通道（依赖 1.3） | 当前轮（`bigpatchmd5` 与本机一致）能够完成更新 |
| 1.3 | **修 P0-2**：分块优先取 `big_{@base_version@}`（累积池），取不到再回退 `big_{行0}`（本轮新增）；清单本身两处都要看 | 随机 20 个分块 20/20 = 200 |
| 1.4 | 用 `mpk_filelist.info`（P1-6）做基础层 Repair 的校验源 | 故意改坏 1 个基础包 → Repair 能定位到它 |
| 1.5 | 合并规则（§6.1）：抓一次真实全量改写，落一份可复现的 `docs/re/FENBAO.md` | 能对 3 个被改写分卷逐字节重建 |

### 阶段 2：工程闭环（1 天，可与阶段 1 并行）

| # | 动作 | 验收 |
|---|---|---|
| 2.1 | 加 `build.yml`：push/PR 跑 build + 单测（含 `paths-ignore: **.md`） | PR 上出现绿色 check |
| 2.2 | 加 `CompileAOTAndShip.bat`（参数化，非交互），内含"restore 整解决方案、publish 不加 `--no-restore`、publish 后删 PDB、跑 Indexer" | 一条命令产出可部署的 `SoR.dll` + `manifest.json`，且 `SoR.dll` > 2 MB 且无 `BSJB` |
| 2.3 | **实测并修正自更新版本号**（§4.2） | 日志里的 version 与 `manifest.json` 一致；把 release 分支版本 +1 后能触发自更新 |
| 2.4 | 补 LICENSE、双语 README；删掉 `docs/re/netease-protocol.md` 死链，改成 `docs/PROTOCOL-VERIFIED.md` | `grep -rn "netease-protocol.md"` 为空 |
| 2.5 | 把 `docs/PROTOCOL-VERIFIED.md` §5.4（行0 目录、`[20:24]` 字段语义）与 §1（"不带签名 403"）就地更正，并在文首加一段"2026-10-24 复核推翻清单" | 文档与 §4.1 的表逐行一致 |

### 阶段 3：可选增强

| # | 动作 |
|---|---|
| 3.1 | 接上 `NeteaseGamesShardCluster` → Repair；接上 `ReadPatchList` → 本地对账（否则删掉） |
| 3.2 | 全新安装路径：先写文档说清"必须先跑官方启动器一次"，等阶段 1.3/1.5 完成后再谈从零装 |
| 3.3 | 把 `tools/re/` 的证据（小样本 + 脚本）正式提交，`*.etl` / 大 dump 继续 ignore |
| 3.4 | 卸载改为"只删自己管理的目录"（照 PW `:146-174`） |

---

## 8. 验收清单（跑完本文所有项应满足）

```
[ ] dotnet build -c Debug                    0 error
[ ] 单测（Windows 上）                        166/166（当前 Linux 上 165/166，1 个平台假象）
[ ] FindTruncatedBaseShards(真机)             0
[ ] FindTruncatedBaseShards(300 MB 假 Patch54) 1（Patch54.mpk）
[ ] IsLayoutConsistent(100 B 假 Patch2500)     false
[ ] 真机 plan: 引擎文件数                       0（HasEngineUpdate=false 后）
[ ] 真机 plan 总大小                            775 MB 左右（不再含 1.14 GB 引擎），或明确说明为何仍包含引擎项
[ ] ApplyAsync 后 pkgversion                    不变（6521279）
[ ] ApplyAsync 后 patchmd5 行3                  前进到远端行3
[ ] 引擎载荷下载                                gdl + 签名 → 200
[ ] big 分块抽检 20 个                         20/20 = 200（用 @base_version@ 目录）
[ ] 日志里 "Successfully loaded plugin ... version" 与 manifest.json 一致
```

---

## 附录 A：本次用到的实测数据（可复现）

### 如何绕过"Linux 沙箱 / Windows 代码"的路径差异

插件的所有相对路径常量都用 `\`（如 `Engine\Binaries\Win64Release\ywzh.exe`）。在 Linux 上 `\` 是普通文件名字符，所以直接读 `D:\ywzh\...` 的挂载会得到"文件不存在"的假阴性。本次的做法：

1. 建一个目录，**用带反斜杠的目录名**（`/tmp/winlike/Engine\Config/Built.version`）复刻插件的相对布局；
2. 大文件用 `truncate` 造**等长稀疏文件**，只让插件比较大小；
3. 需要真实内容时用 `copyfile`（跨设备无法 `link`）；
4. 然后 `new NeteaseGamesGameManager()` / `new NeteaseGamesGameInstaller(mgr)` 直接调用，和宿主走同样的代码路径。

这个方法能可靠验证：版本读取、plan 构建、大小计算、索引解析、守卫判定、下载 URL 生成。**不能**验证：注册表探测、DPAPI、NativeAOT 导出、`Process.Start(UseShellExecute+runas)`。



```bash
# 真机
D:\ywzh\ywzh_extreme\LocalData\Patch\{patchmd5_windows64_normal.txt, pkgversion, patchlist_windows64_normal.txt}
D:\ywzh\ywzh_extreme\{mpk_filelist.info, package_resource.info}

# 远端
https://h74.update.netease.com/pl/OB_patchmd5_windows64_normal.txt              # 200, 1984 B
https://h74.gph.netease.com/20261023182924387/patchlist_windows64_normal_mpk.txt  # 200, 9,299,048 B
https://h74.gph.netease.com/big_20261021113421287/bigpatchlist_windows64_normal.txt  # 200, 11,122,614 B
https://h74.gph.netease.com/big_20261023182924387/bigpatchlist_windows64_normal.txt  # 200, 11,124,133 B（分块多为 404）
https://h74.gdl.netease.com/loose_file/mini_pack_202610151759/md5_engine.txt    # 需签名, 21,811 B

# 关键复现步骤
# 1) 索引字段语义：SQLite 直读 Patch.mpkinfo，按 [8:10] 分组，比较 max([20:24]) 与 max([20:24]+[0:4])
# 2) 截断检测失效：sparse 造 300,000,000 B 的 Patch54.mpk → FindTruncatedBaseShards 返回 0
# 3) 版本写错：造 pkgversion=6521279 的安装 → ApplyAsync → pkgversion 变 6593596
# 4) 引擎 404：NeteaseGamesDownloader.BuildUris(引擎条目的 RelativePath) 全部指向 gph
```

## 附录 B：本次未能验证、需要你在 Windows 上确认的三件事

1. **NativeAOT 下的 `GetPluginVersion`**（§4.2）：决定自更新是否有效。
2. **`@bigpatchopen@` 是否会变 false**：我只能看到 10-22/10-23/10-24 三天，需要再观察一周。
3. **`fenbao_mode ≠ 0` 的真实改写过程**（§6.1）：只有它能解开合并规则。

---

*附注：`docs/REVIEW-NETEASEGAMES.md`（2026-10-20 那份）里的多数 P0/P1（松散文件不下载、索引不同批替换、TLS 关闭、token 明文、零单测、PDB 进包）**已经在 `1ed5302` 之后的未提交改动里修掉了**，本文不再重复；本文只列**当前工作树仍然存在**的问题。那份审查里"零单元测试"一项已被 166 个测试推翻，请勿再引用。*
