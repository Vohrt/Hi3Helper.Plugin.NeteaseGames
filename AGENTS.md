# Hi3Helper.Plugin.NeteaseGames — 仓库约定

Collapse Launcher 的网易游戏插件：**一个 Core + 每游戏一个薄 preset**。

协议细节与实测证据见 **`docs/PROTOCOL-VERIFIED.md`**（唯一参考，只写实测结论）。改协议相关代码前必读。

## 结构

| 路径 | 作用 |
|---|---|
| `Hi3Helper.NeteaseGames.Core/` | 协议层：清单解析、URL 签名、下载、落盘、启动、媒体 |
| `Hi3Helper.Plugin.SoR/` | 薄插件：Sea of Remnants（程序集 `SoR.dll`） |
| `Hi3Helper.NeteaseGames.Tests/` | 纯函数单测 |
| `Hi3Helper.Plugin.Core/` | Collapse 上游 ABI 子模块（指针随改动提交） |

## 硬约束

1. **增量包目录 = 远端清单第 0 行**（`NeteaseGamesPatchManifest.ContentVersion`）。
   **不是** `@metadata@.@base_version@` —— 用后者会取到旧包（实测差 9 个包）。
   ⚠ **但 `big_` 基础包目录正好相反**：用 `@base_version@`，不是第 0 行（见 §待完成 1）。
   两个目录别混用。
2. **本地补丁版本 = `patchmd5` 第 3 行**。`pkgversion` 是基础包版本，**另一个维度**，不能比。
   ⚠ **也不要把补丁版本写进 `pkgversion`**（旧实现这么干，会把 6521279 覆盖成 6593596）。
   `NeteaseGamesDownloadPlan` 必须同时持有 `PatchVersion`（写 `patchmd5`/`versiondata.json`）
   与 `PackageVersion`（只在基础层真的变了时才写）。
3. **本地清单不能作为判定依据**。官方若以本地为准就永远发现不了更新。
   判定只能是「远端清单 vs 磁盘实际文件」。
4. **更新必须全成功才写 `pkgversion`**（提交点）。
5. **落盘顺序：分片 → 索引 → 版本号**。游戏靠 `.mpkinfo` 索引读包。
6. **更新后必须重写 `patchmd5`**（官方启动器读它判定，否则重复提示更新）。
7. **同一个 `NeteaseGamesDownloadPlan`** 同时供给 `GetGameSize` / `GetGameDownloadedSize` / 进度回调，
   否则宿主算错剩余空间。
8. **暂存目录只有一个**：`LocalData\Patch\.collapse_download`。
9. **远端路径是不可信输入**：落盘前过 `NeteaseGamesDownloadItem.ResolveTargetPath`。
10. **卸载只删确认是游戏目录的路径**（必须存在 `Engine\Binaries\Win64Release\ywzh.exe`）。

## 每游戏一个插件

- 一游戏 = 一工程 `Hi3Helper.Plugin.<短名>`，`AssemblyName=<短名>`。
- 每游戏独立 zip / `release` 分支目录 / 自更新 URL。
- 新增游戏：加 preset 工程 → `release.yml` 的 `prepare` 加一行 JSON。
- Core **不放**单游戏资源（图标/海报/背景按游戏分）。

## plan 模式

| 模式 | 触发 | 内容 |
|---|---|---|
| `Update` | 宿主更新按钮 | 远端清单中与磁盘大小不符的包 |
| `Repair` | 已装游戏点安装/修复 | 同上（`Repair` 与 `Update` 判定相同） |
| `Install` | 目录里没游戏 | 基础层（`Patch.mpk`+`Patch1..127`）由 `big_` 分块装配 + 着色器层 + 增量层（见下） |

- `plan.IsRemoteListUnavailable` ⇒ 抛异常（空计划意味着"未知"而非"完成"）。
- `plan.IsFreshInstallUnsupported` ⇒ 抛 `NotSupportedException`（仅在 `big_` 清单两份都取不到时设置）。

### 全新安装（Install）—— 2026-10-26 实测定稿

**分块字节 = 分卷字节，无需合并。** 分块是其资源按清单顺序的**无缝拼接**（分块内部资源之间没有间隙）。
实测：`Char_13`（57 资源，厂商布局里有 10,223 B 空洞）按顺序切片 **57/57 逐字节等于厂商分卷**；
`Char_730` 行 0 版 6/6 正确、`@base_version@` 版只 3/6；`CGs_13`/`CGs_125` 整块 MD5 == 厂商切片 MD5。

**目录分工（不是"整份清单用一个目录"）：**

| 分块 | 取哪个目录 |
|---|---|
| 本轮变动的 28 个（9 个新增 + 19 个重切） | `big_{行0}` —— 实测 28/28 返回字节数 == 清单声明 |
| 其余全部 | `big_{@base_version@}` —— 实测 30/30 命中；行 0 目录对其 **404** |

判定「本轮变动」= 该分块在行 0 清单里的 `(size, paths)` 与 `@base_version@` 清单不同（或只存在于行 0）。
**不能用「哪份清单列了它」来选目录**：`@base_version@` 清单也列了那 19 个重切分块，且目录里放的是**旧的**字节。

**规划器按行 0 清单为准。** 行 0 的 10,424 个分块与 `Patch.mpkinfo` **10,421/10,421 一致**；
`@base_version@` 的 10,419 个里有 **20 个大小不符**（旧一代）。`@base_version@` 只用于补 4 个行 0 没列的分块名
（其中 3 个大小不符会被跳过，1 个 `essential_0` 与 `essential_1` 重名 —— 行 0 版胜出）。

**分卷数不必与厂商相同。** 插件按 524,288,000 B 装填得到 **123** 个分卷（厂商 128），
资源→分卷映射完全不同（实测 `w_0` 在厂商是分卷 106，插件布局里落到别处），
但**逐资源字节一致**（跨分卷抽样 60/60 相同）。索引由插件自己写（`NeteaseGamesBaseIndexWriter`），
描述的就是插件产出的布局。

**松散文件只有 3 个**（`NeteaseGamesBaseLooseFiles`）：`prerequisite`、`Misc/lazycode_zstd_dict`、
`4a/4a3101465e82fbbdc7e08f834fc82419`。它们不在 `Patch.mpkinfo` 里，各自的分块字节 == 磁盘文件 MD5。
⚠ **不要**把「单资源分块」都当松散文件 —— 池里有 **672 个**单资源分块（`Fonts_1`/`CGs_0`/`Char_14`…）是分卷内资源；
`essential`（分卷 126，偏移 73,993,554）与 `patch.repository` 也在索引里，必须留在分卷内。

**同路径冲突**：`resource.repository_0`（旧）与 `resource.repository_1`（新）都列 `resource.repository`。
规划器保留先放置者，`BuildSteps` 只给**真正放置了资源的那个分块**发 step（`Placement.ChunkName` 判定）；
否则落败分块的字节会覆盖胜者的。`AGENTS.md` 旧文提到的「9 个包差」就是这个。

**根目录资源包来自引擎载荷**：`md5_engine.txt` 里 10 条 `used_del_dir\mpk_res\*` **不是删除标记**，
是真实文件 → 游戏根目录（`NeteaseGamesEnginePatcher.ResolveTargetPath`）。
实测下载 `ResourceExA.mpkinfo.1`/`ResourceExB.mpkinfo.1`/`Resources.mpkinfo.1` 均 200 且 MD5 与磁盘一致。
- 包判定有两道闸：① 磁盘大小 == 远端清单 `entry[-1]`；② `*.mpkinfo` 索引布局校验
  **磁盘大小 ≥ 载荷长度**（不是相等！`Patch1009.mpk` 有 61,766 B 尾部填充）。
  两者都过才算完好。见 PROTOCOL-VERIFIED §4。
- ⚠ **marker 不能按序号推算**：`Patch2500.mpk` 的真实 marker 是 **5000**（`s_file.mpkinfo` 实测），
  按"段内序号 × 2"会算成 4060，于是该分卷的布局校验**永远放行**。
  `BuildShardMarkerMap` 必须优先用索引里真实出现的 marker，推不出来时**跳过校验（返回 false）而不是放过**。

## 媒体

- `GetBackgroundEntries` / `GetCarouselEntries` 的 `Path` **必须是 URL 或 base64**，
  本地路径会让宿主永久转圈。
- 背景用**内嵌**的官方启动画面（`LocalData\Splash.bmp`）。

## 安全

- **不要**关闭 TLS 校验。所有客户端由 `NeteaseGamesHttp.CreateClient` 创建。
- 登录态是凭据：DPAPI(`CurrentUser`) 存 `session.dat`；注入环境变量后在 `finally` 恢复。
- 日志里不打印 token / 签名 secret。

## 启动契约

```
Engine\Binaries\Win64Release\ywzh.exe --start=Python --use-pso-precache=1 --python-args=innerdesktop;script_mode#RELEASE_MODE --dx12
```
- **工作目录必须是 exe 所在目录**
- 传 `--fs-*` / `--takeover` ⇒ 起不来
- exe 是 `requireAdministrator` ⇒ 必须 `UseShellExecute + runas`
- 游戏会另起 `ywzh_.exe` ⇒ 跟踪两个名字

## 构建

```bat
dotnet build Hi3Helper.Plugin.NeteaseGames.slnx -c Debug
dotnet publish Hi3Helper.Plugin.SoR\Hi3Helper.Plugin.SoR.csproj -c Release -r win-x64 /p:PublishProfile=ReleasePublish-O2
tools\Indexer.exe Hi3Helper.Plugin.SoR\publish\Release
```

- **不要**加 `--no-restore`（AOT 会静默降级成 IL）。
- publish 目录不应有 `*.pdb`。

## 待完成 / 已知边界

协议细节与实测证据见 **`docs/PROTOCOL-VERIFIED.md`**（唯一参考）。

1. **基础包通道已定位（2026-10-23 内存取证；2026-10-24 实测修正）**：
   清单在 `/{big_<version>}/bigpatchlist_windows64_normal.txt`，分块在
   `/{big_<version>}/{big_common|big_shader}/{条目}`。

   ⚠ **`<version>` 要按分块分别选目录**（2026-10-26 实测定稿，见「plan 模式」一节的表）：
   行 0 目录只服务**本轮变动的 28 个分块**（28/28 命中），其余分块在那里 **404**；
   `@base_version@` 目录服务其余全部分块（30/30 命中），但其中 19 个重切分块放的是**旧的**字节。
   ⇒ 只用一个目录会失败（行 0 → 90%+ 404；base → 19 个分块字节过期）。
   规划以**行 0 清单**为准（与 `Patch.mpkinfo` 10,421/10,421 一致）；`@base_version@` 只补 4 个行 0 未列的分块名。

   清单实测 11,122,614 B（base_version 版）/ 11,124,133 B（行0 版）；`big_common` 10,406 块 /
   **59.37 GB**。辅助函数：`NeteaseGamesContentEndpoints.GetBigPatchUrl` / `GetBigPatchContentPath`
   （**调用方必须自己传对 version**）。⚠ 更早的"基础包不可达"结论也是错的（当时少写了 `big_` 前缀）。
2. **基础分卷的本地索引已破解**：`LocalData\Patch\Patch.mpkinfo` 是**真正的 SQLite 数据库**
   （`info0(filepath, value)`，实测 234,910 行）。`value` 的 `[8:10]` 是**分卷 ID**（0..127）、
   **`[0:4]` 是资源大小、`[20:24]` 是分卷内起始偏移**。
   ⇒ 每条资源的结束位置是 `[20:24] + [0:4]`，按 ID 分组取 `max(start + size)` 即得该分卷的**真实载荷长度**。

   ⚠ **2026-10-24 实测修正**：旧结论说 `[20:24]` 是"累积结束偏移"、差值（文件大小 − 索引末端）是
   "分卷填充，可达 447 MB"——**全错**。用 `max(start + size)` 核对真机 128 个分卷，**127 个与文件大小
   精确相等**（唯一不符的 `Patch100` 差 1,876,274 B，是真实填充）。按旧口径算出的"447 MB 填充"
   其实是 `Patch54` 最后一条资源自身的大小。

   实现 `NeteaseGamesBaseIndex`（零依赖 B-tree 遍历，**不要**引入 SQLitePCLRaw，它有高危漏洞公告）
   + `NeteaseGamesGameInstaller.FindTruncatedBaseShards`。
   被误判的 "填充陷阱" 直接后果：截断检测完全失效（把 `Patch54` 截到 300 MB 仍报 0 个）。
3. ~~**仍缺 `PatchN.mpk` 的合并规则**~~ —— **2026-10-26 已解决：不存在"合并"，分块字节就是分卷字节。**
   分块 = 其资源按清单顺序的无缝拼接；按分卷内偏移切片即得资源字节（实测 57/57、6/6、整块 MD5 全等）。
   插件只需决定**每个分块放在哪个分卷的哪个偏移**，然后顺序追加；分卷数/排布不必与厂商一致。

   历史（保留供追溯）：旧笔记说单个分卷由多个 `big_common` 条目合并（`fenbao_name` / `PendingBatch` /
   `merge_fenbao_tmp`），顺序依赖 `fenbao_migration_map`；已逐一证伪 6 种排序假设。

   ~~⚠ 2026-10-24 复核：这个前提本身还没有任何观测支持。~~（2026-10-26 推翻：见本条开头）
   - `big_common` 分块与 `PatchN.mpk` **都是**「`ZSTD`+u32 解压长度+frame」的重复容器；
   - `Patch126.mpk` 的 381 条索引与文件**严格连续**（381/381 首尾相接，末条结束 = 文件大小）。

   ~~当前 `fenbao_mode = 0`，无法复现真实改写。~~ 不需要复现了：全新安装按插件自己的布局写索引即可。

   ✅ **已修（2026-10-24）**：`@bigpatchopen@` 是**分支能力位**（自 10-22 起一直为 true），不是"本轮要改基础层"
   的意思。`VerifyBaseTierCompletable` 现在改为比对 **`@bigpatchmd5@` 指纹**：本地记录（`patchmd5` 里）与远端
   一致 ⇒ 纯增量，放行；不一致 ⇒ 基础层真的变了，才拒绝。`@is_full@` 仍然直接拒绝。
4. **引擎载荷只按大小校验**（网易 64 位哈希未定位，已排除 MD5/SHA 截断）。
   `*.mpkinfo` 索引可用于"是否截断"校验（见 PROTOCOL-VERIFIED §4）。
   ⚠ 引擎载荷**只在 `h74.gdl.netease.com`**（且**必须**带 key1/key2 签名，裸请求 403）；
   `h74.gph.*` 全 404。下载器默认用 gph，所以引擎项必须显式指定主机 + 签名。

   ⚠ **`used_del_dir\mpk_res\*` 不是删除标记**（2026-10-26 实测）：10 条都是真实文件，落地到**游戏根目录**
   （`Resources*.mpk` / `ResourceEx*.mpk` / `mpk_filelist.info` / `package_resource.info`）。
   下载三个最小的验证：200 且 MD5 与磁盘完全一致。跳过它们 → 全新安装缺根目录资源包。
   映射见 `NeteaseGamesEnginePatcher.ResolveTargetPath`。
5. **`sort_map` / `preload_list` 是临时文件**，真实安装里不存在 ⇒ 插件不应持久化（见 §9.3）。
6. **`InstanceLogger` 在宿主中可能不挂载**（`SharedLogger.Log` 在 `InstanceLoggerCallback == null` 时静默返回），
   排查运行时行为要看宿主 Console 窗口或日志里的 `[Scheme]`/`[PluginInfo]` 行，不要指望 `[NeteaseGames]` 前缀。
7. **逆向方法**：PCLauncher 的协议逻辑在 zstd 压缩+加密的 Python 模块里，**静态搜字符串拿不到 URL**。
   有效路径是让它跑一次真实更新，再用 `tools/re/MemRegion` dump 整段内存——官方会把请求 URL 与
   Python 调用链（含文件名:行号）写进内存 report。详见 PROTOCOL-VERIFIED §9 的"逆向方法备忘"。
