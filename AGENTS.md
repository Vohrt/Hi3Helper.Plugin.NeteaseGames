# Hi3Helper.Plugin.NeteaseGames — 仓库约定

Collapse Launcher 的网易游戏插件。**一个发行商 Core + 每游戏一个薄 preset**。

## 结构

| 路径 | 作用 |
|---|---|
| `Hi3Helper.NeteaseGames.Core/` | 协议层：清单解密、URL 签名、下载、应用补丁、启动、媒体 |
| `Hi3Helper.Plugin.SoR/` | 薄插件：Sea of Remnants（程序集 `SoR.dll`，preset + COM 导出 + 启动委托 + 自更新） |
| `Hi3Helper.NeteaseGames.Tests/` | 纯函数单测（解析 / 签名 / 路径安全 / 计划模型） |
| `Hi3Helper.Plugin.Core/` | Collapse 上游 ABI 子模块（**指针要随改动提交**） |
| `docs/re/netease-re.md` | **协议与逆向的唯一参考**；改协议相关代码前先读它 |

## 硬约束（违反会静默损坏玩家安装）

1. **更新必须"全成功才写 `pkgversion`"**。`pkgversion` 是整套更新的**提交点**；任何一个对象失败都要
   抛异常而不是写版本号——否则 Collapse 显示"已最新"，而游戏缺文件。
2. **落盘顺序：分片 → 索引 → 版本号**。游戏通过 `.mpkinfo` 索引读包，包新索引旧 = 读到错位数据。
3. **同一个 `NeteaseGamesDownloadPlan` 必须同时供给** `GetGameSize` / `GetGameDownloadedSize` / 进度回调。
   宿主用 `size - downloaded` 算剩余空间与剩余量（`PluginGameInstallWrapper.cs:637`），三个来源不一致就会
   算错：报小了下到一半满盘，报大了拒绝更新。
4. **暂存目录只有一个**：`LocalData\Patch\.collapse_download`（`StagingDirectoryName`）。改名会让
   `GetGameDownloadedSize` 永远返回 0。
5. **远端清单里的路径是不可信输入**：落盘前必须过 `NeteaseGamesDownloadItem.ResolveTargetPath`。
6. **卸载只删确认是游戏目录的路径**（必须存在 `Engine\Binaries\Win64Release\ywzh.exe`）。

## 每游戏一个插件（**不要**做成一个共享程序集）

- 一个游戏 = 一个工程 `Hi3Helper.Plugin.<短名>`，`AssemblyName=<短名>`（现在只有 `SoR`）。
- 每个游戏**独立 zip、独立 `release` 分支目录、独立自更新 URL**（`.../SoR/`），
  这样用户只下载自己玩的游戏。
- 新增游戏：加 preset 工程 → 在 `release.yml` 的 `prepare` 步骤里加一行 JSON（`short` / `project` / `name`）。
- Core **不放**任何单游戏的东西（图标 / 海报 / 背景按游戏区分）。

## 宿主媒体契约（踩过的坑，别再犯）

- `GetBackgroundEntries` / `GetCarouselEntries` 的 `Path` **必须是 URL 或 base64**，
  **绝不能**是本地文件路径 —— 宿主会自己去"下载"它，本地路径 ⇒ **永久"正在加载背景图片"转圈**。
- 这款游戏没有任何可用的远端主视觉：公告图全是 1026×246 横幅条，notice XML 里连图片 URL 都没有。
  ⇒ 背景用**内嵌**的官方启动画面（`LocalData\Splash.bmp`，1920×814，去黑边后 327 KB）。
- `GetBackgroundFlag` 只报 `TypeIsImage`；`IsSourceFile` 表示"宿主去读本地文件"，**不要**乱用。

## 版本判定（踩过的坑）

- **远端**权威版本 = `@metadata@.@private_res_version@`（= 文本第 4 行），**不是**
  `normal.latest_pkgversion`（后者是别的分支的陈旧值，5282637 vs 真实 6534552 ⇒ 会误报永久有更新）。
- **本地**权威版本 = `LocalData\Patch\pkgversion`，因为它是**插件更新时写入的提交点**。
- **官方启动器的记账是 `patchmd5_windows64_normal.txt` 的第 4 行**（= `@private_res_version@`），
  不是 `pkgversion`。更新成功后**必须同步重写**这个文件（`NeteaseGamesLocalState.WriteLauncherRecord`），
  否则官方启动器不认插件装的更新。
- `gameversion` / `versiondata.json` / `patchmd5` 都是**游戏客户端或官方启动器写的**，插件更新不会自动刷新
  它们 ⇒ 只能作回退来源，且更新后应一并刷新（best effort）。
- **完整逆向记录见 `docs/re/launcher-version-logic.md`**（四行结构、`MLauncher.check`、历史 bug 三连）。

## 安全约定

- **不要**关闭 TLS 校验。所有客户端统一由 `NeteaseGamesHttp.CreateClient` 创建。
  （历史教训：曾有 5 处 `AllowUntrustedCert`，其中一处把**远端下发的 IP** 交给"信任任意证书"的客户端。）
- 登录态是凭据：DPAPI(`CurrentUser`) 加密存 `session.dat`；注入进程环境变量后**必须**在 `finally` 恢复。
- 不要在日志里打印 token / 签名 secret。

## 启动契约（易错，详见 `docs/re/netease-re.md` §1）

```
Engine\Binaries\Win64Release\ywzh.exe --start=Python --use-pso-precache=1 --python-args=innerdesktop;script_mode#RELEASE_MODE --dx12
```
- **工作目录必须是 exe 所在目录**（用安装根 ⇒ 331 MB 处崩溃）
- 传 `--fs-*` / `--takeover` ⇒ 起不来
- exe 是 `requireAdministrator` ⇒ 必须 `UseShellExecute + runas`
- 游戏会另起 `ywzh_.exe` ⇒ 跟踪两个名字；进程匹配**必须按路径**，否则会误判/误杀另一份安装

## 构建

```bat
dotnet build Hi3Helper.Plugin.NeteaseGames.slnx -c Debug
dotnet test  Hi3Helper.NeteaseGames.Tests\Hi3Helper.NeteaseGames.Tests.csproj
dotnet publish Hi3Helper.Plugin.SoR\Hi3Helper.Plugin.SoR.csproj -c Release -r win-x64 /p:PublishProfile=ReleasePublish-O2
tools\Indexer.exe Hi3Helper.Plugin.SoR\publish\Release
```

- **不要**用 `--no-restore`（AOT 会静默降级成 IL 程序集）。
- Release 配置已关符号：publish 目录**不应**出现 `*.pdb`（出现即是陈旧中间产物，先 `dotnet clean`）。
- 发布包应有且仅有一个 zip；CI 会强制检查（数量、DLL 体积、无 PDB）。

## 三种计划模式（`NeteaseGamesPlanMode`）

`BuildPlanAsync` 按用途构建计划，语义**不要混淆**：

| 模式 | 触发 | 计划内容 |
|---|---|---|
| `Update` | 宿主“更新”按钮 | 与本地 patchlist 比对，只下**变更的分片/索引** |
| `Install` | 目录里没有游戏 | 全部补丁分片 + 索引 + 引擎载荷（1.8 GB），但**缺基础包 ⇒ 拒绝执行** |
| `Repair` | 已装游戏点“安装”/修复 | 对账**磁盘**：本地清单认为当前的分片，只校验存在与大小，缺/截断才补 |

- `Repair` **不追新版本**：追新是 `Update` 的职责。两者在“本机已落后”时会给出相同结果，属正常。
- **全新安装不可行**（已实测确认）：基础包 `Patch.mpk` / `Patch1-123.mpk` / `ResourceExA*.mpk`
  = **123 个 / 59.10 GB**，占完整安装的 92%。内容服务器对它们**所有 section 都返回 404**，
  任何清单也不描述它们，mini-pack 只有 1.3 GB（装不下）。⇒ `Install` 检测到缺失即抛
  `NotSupportedException`，绝不能让宿主报告“安装完成”而游戏起不来。
  详见 `docs/re/launcher-install-logic.md`。
- 三者的 `TotalSize` 必须与 `GetGameDownloadedSizeAsync` 同源，否则宿主算错剩余空间。

## 尚未完成（改动前必读）

1. **引擎载荷只按大小校验**（网易 64 位 hash 算法未定位）。
2. Logo 叠加（`GetLogoOverlayEntries`）未做；视频背景**不适用**——游戏与启动器都没有视频资源。
3. 进度粒度只到“文件数”，未细到当前阶段/当前文件。

> ⚠️ **曾经的错误结论（已纠正，别再犯）**：`patchlist` 的 `common`/`windows64_common` 段有 231,619 条
> “松散文件”，一度被当成需要逐个下载的文件，导致计划里塞进 23 万项、Update 误报 1 GB、Repair 误报 60 GB。
> **它们不是磁盘文件**，而是引擎内部虚拟资源路径（`Char/…`、`Sounds/…`、`VA/…`），内容全部存在
> 186 个 `.mpk` 分片里。实测抽样 2000 条，磁盘上 **0 条存在**。因此：
> - 不需要、也不可能“下载松散文件”；
> - 真正的增量只有 **mpk 分片 + 索引**；
> - `mpkexclude`（778 条）同样是虚拟路径，不是待下载清单。
