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
2. **落盘顺序：包 → 索引 → 松散文件 → 版本号**。游戏通过 `.mpkinfo` 索引读包，包新索引旧 = 读到错位数据。
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

- 权威包版本 = `@metadata@.@private_res_version@`（= 文本第 4 行），**不是** `normal.latest_pkgversion`
  （后者是别的分支的陈旧值，5282637 vs 真实 6534552 ⇒ 会误报永久有更新）。

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

## 尚未完成（改动前必读）

1. **松散文件增量下载未实现**：`patchlist` 的 `common`/`windows64_common` 变更项（真实更新里就有 9 个）
   与 `mpkexclude`（778 条）目前无法下载 → 实现里**主动拒绝**把更新标记为完成。
2. **全新安装（0 → 78 GB）不可用**：需要官方启动器装过一次本体。
3. 引擎载荷只按**大小**校验（网易 64 位 hash 算法未定位）。
