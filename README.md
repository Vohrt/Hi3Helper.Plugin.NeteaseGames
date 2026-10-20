# Hi3Helper.Plugin.NeteaseGames

Collapse Launcher 的 **网易游戏** 插件（发行商 Core + 每游戏一个 preset）。
首个支持对象：**《遗忘之海》/ Sea of Remnants**（网易自研 **Messiah** 引擎，UE4 派生）。

> 目标（**已达成**）：**完全绕过官方 PCLauncher** —— 用户只需装游戏本体，
> 下载 / 更新 / 启动全部由 Collapse 负责，**不需要装网易官方启动器**。

## 仓库结构

```
Hi3Helper.NeteaseGames.Core/        发行商 Core（程序集 NeteaseGames.Core）
Hi3Helper.Plugin.NeteaseGames/       薄插件（程序集 NeteaseGames，导出 TryGetApiExport）
Hi3Helper.Plugin.Core/               Collapse 上游 ABI（子模块）
tools/Indexer.exe                    生成插件 manifest 与发布包
.github/workflows/release.yml        GitHub Actions：NativeAOT 构建 + 发 pre-release
```

## 已实现能力

| 能力 | 状态 | 依据 |
|---|---|---|
| **直启游戏（不走官方启动器）** | ✅ **已真机验证** | `NeteaseGamesGameLauncher`：原始 exe + 官方 4 个参数 + **工作目录=二进制目录** ⇒ 窗口标题「遗忘之海」、标题界面「开始游戏」 |
| 检测安装（注册表 `OB_ywzh_launcher_PC` → `InstPath`） | ✅ | `InstPath` 是**启动器**目录，游戏目录 = `<启动器目录>\ywzh_extreme`（`LauncherGameDirectoryName`） |
| 本地版本（包 / 补丁 / 引擎 / shader） | ✅ | `LocalData\Patch\pkgversion`、`patchmd5_windows64_normal.txt`、`Documents\versiondata.json` |
| 远端版本（有无更新） | ✅ | `https://h74.update.netease.com/pl/release_patchmd5_windows64_normal.txt`（HEX_ 加密） |
| 远端 mini-pack 清单（引擎差分块） | ✅ | `/pl/release_release_mini_pack_version.json`（20 块 / 1.39 GB） |
| 全量文件清单（23 万文件 + 50 个 mpk 分片） | ✅ | 本地 `patchlist_windows64_normal.txt`（HEX_ 加密，17.4 MB JSON） |
| 差分计划（文件 / 分片粒度增量，与官方一致） | ✅ | `NeteaseGamesGameInstaller.BuildPatchListPlan` |
| 引擎（mini-pack）下载与落盘 | ✅ | `NeteaseGamesEnginePatcher`：按 `Engine\md5_engine.txt` 逐文件下载，**按大小校验** |
| 内容下载（Range 续传 + 校验） | ✅ | `NeteaseGamesDownloader`；内容主机 `h74.gdl.netease.com` |
| 差分应用（写包 + 索引 + 版本） | ✅ | `NeteaseGamesUpdateApplier`：写 `LocalData\Patch\` 的 .mpk/.mpkinfo |
| 公告 / 轮播 / 背景图 | ✅ | 本地 `Documents\notice\*` + 远端 `release_launcher_notice_info.xml` |
| 内容 CDN 的 URL 签名 | ✅ | `NeteaseGamesUrlSigner`：`key1 = md5(secret + path + key2)`、`key2 = hex(epoch + 12h)`，纯本地计算 |
| HD server 调度服务 | ✅ | `NeteaseGamesDispatch`：`AUTH-PROJECT` / `AUTH-TOKEN` 头 + `/v2/?domain=` |
| 原生发布（NativeAOT） | ✅ | `PublishProfiles\ReleasePublish-O2/O6.pubxml` ⇒ `NeteaseGames.dll` + manifest + zip |
| GitHub Actions 发版 | ✅ | 打 `v*` 标签即发布 **pre-release**（v0.0.1 已发布） |

## 关键逆向结论（详见 `docs/NETEASE-RE-NeteaseGames.md`）

1. **HEX_ 清单信封**：`"HEX_"`(4 字节) + `Base64( XOR( zlib(明文), "netease_joker_h74_encrypt_key" ) )`
2. **LCFG 启动器配置**：`"LCFG"` + `0x01` + `IV(16)` + `AES-256-CBC(无 padding)`，
   key = `PCLauncher.exe` RVA `0x2204280` 起的 32 字节
3. **远端清单命名规律**：`<patch_list_url>/<prefix>_<branch>_<file>`，prefix = **OB**、branch = release
   （⚠️ 漏掉 `OB_` 前缀会拿到**陈旧分支**的清单）
4. **差分模型**：网易没有二进制差分，就是"松散文件 + mpk 分片"粒度增量
5. **CPS 标签**：与 HEX_ 同一套信封（无 `HEX_` 前缀）——`Base64(XOR(zlib(明文), 同一把 key))`
6. **🎯 直启契约**（从运行中的官方启动器只读捕获，实测有效）：
   ```
   <游戏根>\Engine\Binaries\Win64Release\ywzh.exe
     --start=Python --use-pso-precache=1 --python-args=innerdesktop;script_mode#RELEASE_MODE --dx12
   ```
   - **工作目录必须是二进制目录**（用安装根目录 ⇒ 停在 331 MB 无窗口并崩溃）
   - **不暂存 exe 副本**、**不传 `--fs-*`**、**不传 `--takeover`**
   - 游戏 exe 的 manifest 要求管理员 ⇒ 必须经 shell 启动（`UseShellExecute` + `runas`）
   - **不需要登录 SDK**：游戏自带登录界面（标题界面 → 开始游戏 → 账号选择页），
     它自己调用 UniSDK 登录；此前"必须服务端会话"的结论源于错误的启动方式
7. **内容主机**：清单在 `h74.update.netease.com` / `h74.gph.netease.com`，
   **引擎载荷只走 `h74.gdl.netease.com`**；路径形状 `/<版本>/<文件>`、`/<版本>/<段>/<文件>`，
   而 **mini-pack 载荷没有版本前缀**：`/loose_file/mini_pack_<版本>/<路径>`
8. **引擎文件清单**：`Engine\md5_engine.txt`（`<路径> <u64 哈希> <大小>`）；
   哈希是网易私有算法 ⇒ **按大小校验**；落盘时去掉结尾的 `.1`
9. **补丁目录归游戏管**：直启后 `pkgversion` / `pkgcheck` 由**游戏自己**刷新
10. **调度服务**：`GET https://<边缘IP>/v2/?domain=<域名>` + 头 `AUTH-PROJECT` / `AUTH-TOKEN`
    → `{"addrs":[...],"ttl":300}`

## 构建与发布

```bat
dotnet build Hi3Helper.Plugin.NeteaseGames.slnx -c Debug
```

原生发布（与其它 Collapse 插件一致）：

```bat
dotnet publish Hi3Helper.Plugin.NeteaseGames\Hi3Helper.Plugin.NeteaseGames.csproj ^
  -c Release -r win-x64 /p:PublishProfile=ReleasePublish-O2
tools\Indexer.exe Hi3Helper.Plugin.NeteaseGames\publish\Release
```

> ⚠️ 不要用 `--no-restore`（会让 AOT 静默降级成 IL 程序集）。

发版：打 `v*` 标签并推送 ⇒ GitHub Actions 自动构建并发布 **pre-release**
（<https://github.com/Vohrt/Hi3Helper.Plugin.NeteaseGames/releases>）。
