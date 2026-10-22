# Hi3Helper.Plugin.NeteaseGames — 仓库约定

Collapse Launcher 的网易游戏插件：**一个 Core + 每游戏一个薄 preset**。

协议细节见 **`docs/re/netease-protocol.md`**（唯一参考，只写实测结论）。改协议相关代码前必读。

## 结构

| 路径 | 作用 |
|---|---|
| `Hi3Helper.NeteaseGames.Core/` | 协议层：清单解析、URL 签名、下载、落盘、启动、媒体 |
| `Hi3Helper.Plugin.SoR/` | 薄插件：Sea of Remnants（程序集 `SoR.dll`） |
| `Hi3Helper.NeteaseGames.Tests/` | 纯函数单测 |
| `Hi3Helper.Plugin.Core/` | Collapse 上游 ABI 子模块（指针随改动提交） |

## 硬约束

1. **下载目录 = 远端清单第 0 行**（`NeteaseGamesPatchManifest.ContentVersion`）。
   **不是** `@metadata@.@base_version@` —— 用后者会取到旧包（实测差 9 个包）。
2. **本地补丁版本 = `patchmd5` 第 3 行**。`pkgversion` 是基础包版本，**另一个维度**，不能比。
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
| `Install` | 目录里没游戏 | **不支持**，抛 `NotSupportedException` |

- `plan.IsRemoteListUnavailable` ⇒ 抛异常（空计划意味着"未知"而非"完成"）。
- `plan.IsFreshInstallUnsupported` ⇒ 抛 `NotSupportedException`。

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

## 待完成

见 `docs/re/netease-protocol.md` §8。最要紧的两项：

1. **全新安装**：基础包（144 个 / 64 GB）在内容服务器全 404，但官方确实下载了它们
   （`big_common_*` → `@bigpatchopen@` 的 bigpatch 通道），**通道未定位**。当前 `Install` 直接拒绝。
2. **引擎载荷只按大小校验**（网易 64 位哈希算法未定位）。
