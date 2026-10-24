# Hi3Helper.Plugin.NeteaseGames

Collapse Launcher 的**网易游戏**插件仓库：**一个发行商 Core + 每个游戏一个独立插件**。

> 仓库名是 `NeteaseGames`（发行商级），但**发布出来的每个游戏都是独立的插件**（独立程序集 + 独立 zip +
> 独立自更新目录），所以用户只下载自己玩的那款游戏。

## 已支持的游戏

| 游戏 | 程序集 / 短名 | 工程 | 状态 |
|---|---|---|---|
| 《遗忘之海》/ Sea of Remnants | **`SoR.dll`** | `Hi3Helper.Plugin.SoR` | 启动 / 版本 / 更新 / 修复 / 公告 |

新增一款网易游戏 = 新增一个 preset 工程（`Hi3Helper.Plugin.<短名>`，`AssemblyName=<短名>`）+ 在
`release.yml` 的 `prepare` 步骤里加一行。Core 不动。

## 仓库结构

```
Hi3Helper.NeteaseGames.Core/   发行商 Core（程序集 NeteaseGames.Core，协议/下载/启动/媒体）
Hi3Helper.Plugin.SoR/          薄插件：Sea of Remnants（程序集 SoR.dll，导出 TryGetApiExport）
Hi3Helper.NeteaseGames.Tests/  单元测试（解析器 / 签名器 / 路径安全 / 计划模型）
Hi3Helper.Plugin.Core/         Collapse 上游 ABI（子模块）
tools/Indexer.exe              生成插件 manifest 与发布包
docs/PROTOCOL-VERIFIED.md      协议（唯一参考，只写实测结论）
docs/NETEASE-FIX-PLAN.md       2026-10-24 实测复核 + 修复计划
```

## 当前状态

| 能力 | 状态 | 说明 |
|---|---|---|
| 检测安装 | ✅ 已验证 | 注册表 `OB_ywzh_launcher_PC` 的 `AdditionalPaths` |
| 本地版本 | ✅ 已验证 | `patchmd5` **第 3 行**（补丁版本）；`pkgversion` 是基础包版本，另一维度 |
| 远端版本 / 更新判定 | ✅ 已验证 | 远端清单**第 3 行**；判定 =「远端包大小 vs 磁盘大小」 |
| 增量包目录 | ✅ 已验证 | 远端清单**第 0 行**；**不是** `@metadata@.@base_version@`（实测差 9 个包） |
| **基础包（`big_`）目录** | ✅ 已验证 | **`@base_version@`**，不是第 0 行（两者用途相反，见 PROTOCOL-VERIFIED §5.4） |
| 直接启动（不经官方启动器） | ✅ 真机验证 | 见 `docs/PROTOCOL-VERIFIED.md` |
| 背景 / 图标 | ✅ 已修 | 内嵌 base64（本地路径会让宿主无限转圈） |
| 公告 / 轮播 | ✅ 已修 | 逐条抓取公告 JSON 取真实标题与轮播图 |
| 插件自更新 | ⚠️ 待实测 | 指向 `release` 分支；NativeAOT 下自报版本号需在 Windows 上确认（见 FIX-PLAN §4.2） |
| **增量更新** | ✅ 已修（2026-10-24） | 按 `@bigpatchmd5@` 指纹判断基础层是否真的变化；`@bigpatchopen@` 本身不再阻断 |
| **修复（Repair）** | ✅ 已修（2026-10-24） | 基础层截断检测按 `max(start+size)` 判定；真机 0 误报，300 MB 假分卷能准确报出 |
| **社交侧栏** | ✅ 已实现 | 8 个官方渠道，带二维码图与跳转 |
| 视频背景 | ⛔ 不适用 | 游戏与启动器都没有视频资源（仅 `Splash.bmp` 静态图） |
| **全新安装（0 → 全量）** | 🚧 **待完成** | 基础层 = **128 个分卷 / 64.23 GB**（`Patch.mpk` + `Patch1..127`）；`big_` 通道已定位（`@base_version@`），但分卷重建规则未还原 ⇒ `Install` 仍直接拒绝 |
| 下载并发 / 重试 | ✅ | 4 路并行、`ArrayPool` 缓冲、每主机 2 次指数退避 |

> **计划模式**（`NeteaseGamesPlanMode`）：`Update` 与 `Repair` 判定相同（远端大小 vs 磁盘大小）；
> `Install` **不支持**，抛 `NotSupportedException`。
> 远端清单取不到时抛异常——空计划意味着"未知"，不能当成"已完成"。

> ⚠ **2026-10-24 实测复核见 `docs/NETEASE-FIX-PLAN.md`**：本表在此之前有多处与真机不符
> （基础包数量写成 144、`big_` 通道写成"未定位"、增量更新写成"已验证"），已按实测更正。

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
