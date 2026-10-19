# Hi3Helper.Plugin.NeteaseGames

Collapse Launcher 的 **网易游戏** 插件（发行商 Core + 每游戏一个 preset）。
首个支持对象：**《遗忘之海》/ Sea of Remnants**（网易自研 **Messiah** 引擎，UE4 派生）。

> 目标：**完全绕过官方 PCLauncher** —— 用户只需装游戏本体，下载 / 更新 / 启动全部由 Collapse 负责。

## 仓库结构

```
Hi3Helper.NeteaseGames.Core/        发行商 Core（程序集 NeteaseGames.Core）
Hi3Helper.Plugin.NeteaseGames/       薄插件（程序集 NeteaseGames，导出 TryGetApiExport）
Hi3Helper.Plugin.Core/               Collapse 上游 ABI（子模块）
```

## 已实现能力

| 能力 | 状态 | 依据 |
|---|---|---|
| 检测安装（注册表 `OB_ywzh_launcher_PC`，注意 `InstPath` 里的 U+200B 零宽空格） | ✅ | `NeteaseGamesLocalState.FindExistingInstallPath()` |
| 本地版本（包版本 / 补丁版本 / 引擎版本 / shader） | ✅ | `pkgversion` + `patchmd5_windows64_normal.txt` + `Documents\versiondata.json` |
| 远端版本（有无更新） | ✅ | `https://h74.update.netease.com/pl/release_patchmd5_windows64_normal.txt`（HEX_ 加密） |
| 远端 mini-pack 清单（差分块列表） | ✅ | `/pl/release_release_mini_pack_version.json` |
| 全量文件清单（23 万文件 + 50 个 mpk 分片） | ✅ | 本地 `patchlist_windows64_normal.txt`（HEX_ 加密，17.4 MB JSON） |
| 差分计划（文件/分片粒度增量，与官方一致） | ✅ | `NeteaseGamesGameInstaller.BuildPatchListPlan` |
| 下载（Range 续传 + MD5 校验） | ⚠️ | 代码就绪，**内容 CDN 鉴权待破**（`h74.gdl.netease.com` 边缘 403） |
| 公告 / 轮播 / 背景图 | ✅ | 本地 `Documents\notice\*` + 远端 `release_launcher_notice_info.xml` |
| 直启游戏（不走官方启动器） | ✅ | `NeteaseGamesGameLauncher.Launch` → `ywzh.exe` + 官方参数 |

## 关键逆向结论（详见 `docs/NETEASE-RE-NeteaseGames.md`）

1. **HEX_ 清单信封**：`"HEX_"`(4 字节) + `Base64( XOR( zlib(明文), "netease_joker_h74_encrypt_key" ) )`
2. **LCFG 启动器配置**：`"LCFG"` + `0x01` + `IV(16)` + `AES-256-CBC(无 padding)`，
   key = `PCLauncher.exe` RVA `0x2204280` 起的 32 字节
3. **远端清单命名规律**：`<patch_list_url>/<branch>_<file>`，branch = `release`
4. **差分模型**：网易没有二进制差分，就是"松散文件 + mpk 分片"粒度增量

## 构建

```bat
dotnet build Hi3Helper.Plugin.NeteaseGames.slnx -c Debug
```

NativeAOT 发布配置（PublishProfiles / CompileAOTAndShip.bat）待补。

## 授权

仅用于个人学习与自用；网易游戏资源版权归网易所有。
