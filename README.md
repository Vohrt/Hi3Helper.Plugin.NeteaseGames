# Hi3Helper.Plugin.NeteaseGames

Collapse Launcher 的**网易游戏**插件仓库：**一个发行商 Core + 每个游戏一个独立插件**。

> 仓库名是 `NeteaseGames`（发行商级），但**发布出来的每个游戏都是独立的插件**（独立程序集 + 独立 zip +
> 独立自更新目录），所以用户只下载自己玩的那款游戏。

## 已支持的游戏

| 游戏 | 程序集 / 短名 | 工程 | 状态 |
|---|---|---|---|
| 《遗忘之海》/ Sea of Remnants | **`SoR.dll`** | `Hi3Helper.Plugin.SoR` | 启动 / 版本 / 公告 / 更新 / 修复 / 全新安装 |

新增一款网易游戏 = 新增一个 preset 工程（`Hi3Helper.Plugin.<短名>`，`AssemblyName=<短名>`）+ 在
`release.yml` 的 `prepare` 步骤里加一行。Core 不动。

## 仓库结构

```
Hi3Helper.NeteaseGames.Core/   发行商 Core（程序集 NeteaseGames.Core，协议/下载/启动/媒体）
Hi3Helper.Plugin.SoR/          薄插件：Sea of Remnants（程序集 SoR.dll，导出 TryGetApiExport）
Hi3Helper.NeteaseGames.Tests/  单元测试（解析器 / 签名器 / 路径安全 / 计划模型 / 背景资源）
Hi3Helper.Plugin.Core/         Collapse 上游 ABI（子模块）
tools/Indexer.exe              生成插件 manifest 与发布包
docs/re/netease-re.md          逆向与协议笔记（唯一参考）
docs/FIX-LOG-2026-10-20.md     上一轮全量整改记录
```

## 当前状态（诚实版）

| 能力 | 状态 | 说明 |
|---|---|---|
| 检测安装 | ✅ 已验证 | 注册表 `OB_ywzh_launcher_PC` 的 `AdditionalPaths`（`InstPath` 含零宽空格，需清洗） |
| 本地版本 | ✅ 已验证 | `pkgversion` / `patchmd5` / `versiondata.json` |
| 远端版本（有无更新） | ✅ 已修 | 权威字段是 `@metadata@.@private_res_version@`，**不是** `normal.latest_pkgversion` |
| 直接启动（不经官方启动器） | ✅ **真机验证** | 见 `docs/re/netease-re.md` §1 |
| 背景 / 图标 | ✅ 已修 | 主视觉与图标**内嵌**；以 base64 交给宿主（本地路径会导致无限转圈） |
| 公告 / 轮播 | ✅ 已修 | 逐条抓取公告 JSON 取真实标题与 1920×1080 轮播图（6 小时磁盘缓存） |
| 插件自更新 | ✅ | `cl-plugins.sakurakoi.top/SoR/` |
| **增量更新** | ✅ 已实现 | 分片/索引粒度，与官方一致（`Char/…` 等是虚拟路径，**不是**磁盘文件） |
| **修复（Repair）** | ✅ 已实现 | 对账磁盘：分片/索引按大小校验，缺失或截断才补 |
| **社交侧栏** | ✅ 已实现 | 8 个官方渠道（微信/哔哩哔哩/抖音/小红书/QQ 群/大神…），带二维码图与跳转 |
| 视频背景 | ⛔ 不适用 | 游戏与启动器都**没有任何视频资源**（仅 Splash.bmp 静态图） |
| **全新安装（0 → 全量）** | ✅ 已实现 | 引擎载荷 + 全部分片 + 索引 |
| 下载并发 / 重试 | ✅ | 4 路并行、`ArrayPool` 缓冲、每主机 2 次指数退避重试 |

> **更新/修复/安装三种模式**（`NeteaseGamesPlanMode`）：`Update` 只下变更分片；`Install` 下全量；
> `Repair` 只补磁盘上缺失或截断的部分（不追新版本，追新是 `Update` 的职责）。

## 构建

```bat
dotnet build Hi3Helper.Plugin.NeteaseGames.slnx -c Debug
dotnet test  Hi3Helper.NeteaseGames.Tests\Hi3Helper.NeteaseGames.Tests.csproj
```

单游戏原生发布：

```bat
dotnet publish Hi3Helper.Plugin.SoR\Hi3Helper.Plugin.SoR.csproj ^
  -c Release -r win-x64 /p:PublishProfile=ReleasePublish-O2
tools\Indexer.exe Hi3Helper.Plugin.SoR\publish\Release
```

- `ReleasePublish-O2` = 体积优先，`ReleasePublish-O6` = 速度优先。
- Release 配置**不产出 PDB**（PDB 会让发布包涨 3 倍并泄漏本机路径）。
- ⚠️ 不要用 `--no-restore`：会让 NativeAOT **静默降级**成 IL 程序集。CI 有体积与 PDB 的显式检查。

## 发版

打 `v*` 标签，或在 Actions 里手动 dispatch（可选 `version` / `game` / `publish_profile`）：

1. `prepare` 解析要构建的游戏列表；
2. `release` 按游戏矩阵分别 AOT 构建 + Indexer 打包（**每个游戏一个 zip**）；
3. `publish-release` 把**所有游戏的 zip** 挂到同一个 GitHub Release；
4. `publish-cdn` 把每个游戏的 `manifest.json` + `<短名>.dll` 推到 `release` 分支的 `<短名>/` 目录，
   供插件自更新使用（`SoR/manifest.json`、`SoR/SoR.dll`）。

安装时在 Collapse 里**按游戏分别安装**（`_plugins\Hi3Helper.Plugin.SoR\`）。

## 安全相关的实现约定

- **从不关闭 TLS 证书校验**：所有请求走 `NeteaseGamesHttp.CreateClient`。
- 登录态（若保存）用 **DPAPI（CurrentUser）** 加密存 `session.dat`，且只在启动游戏前后短暂注入环境变量。
- 远端清单里的路径视为**不可信输入**：落盘前校验不逃出安装目录。
- 卸载只删"确认是游戏安装目录"的路径。
