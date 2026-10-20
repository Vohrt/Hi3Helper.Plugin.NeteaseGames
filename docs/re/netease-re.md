# 网易《遗忘之海》(Sea of Remnants) 逆向与协议笔记

> **用途**：把 `.dsh\lab` 里那套研究脚本的结论落成可读文档。这里是**唯一**的协议参考；代码在
> `Hi3Helper.NeteaseGames.Core`，每条结论末标注了对应的实现位置。
>
> **来源**：2026-10-19/20 在本机对真实安装（`D:\ywzh\ywzh_extreme`）+ 运行中的官方启动器做只读观测与抓包，
> 另附一个 23 步自检记录（下文称 *自检*），每一步都可复核。
> **合规**：全部为**本地只读**分析 + 公开 CDN 的普通 GET 请求；没有破解登录、没有绕过鉴权、没有修改游戏文件。

---

## 1. 启动契约（最关键的结论）

游戏**不需要**官方启动器就能跑：它自带完整 UniSDK（`Engine\Binaries\Win64Release\NtUniSdk*.dll`、
`OrbitSDK.dll`、`mpay.dll`），登录界面在游戏内。

```
<安装根>\Engine\Binaries\Win64Release\ywzh.exe
  --start=Python --use-pso-precache=1 --python-args=innerdesktop;script_mode#RELEASE_MODE --dx12
```

四条**必须**满足的细节（每条都是踩坑换来的）：

| 要求 | 违反的后果 |
|---|---|
| **工作目录 = exe 所在目录**（`...\Win64Release`） | 用安装根作 cwd ⇒ 停在 331 MB、无窗口、随后崩溃 |
| **不暂存 exe 副本** | —（早期怀疑需要，实测不需要；启动器也不做） |
| **不传 `--fs-*` 系列参数** | 引擎按错误的资源根找包 ⇒ 启动失败 |
| **不传 `--takeover`** | 那是启动器接管游戏窗口的模式 |

另：
- `ywzh.exe` 的 manifest 是 **`requireAdministrator`** ⇒ 必须经 shell 提权启动（`UseShellExecute` + `runas`），
  直接 `CreateProcess` 会 `ERROR_ELEVATION_REQUIRED (740)`。
- 游戏会把自己以 **`ywzh_.exe`** 再起一次并让原进程退出 ⇒ 跟踪生命周期要同时看两个进程名。
- 游戏**自己刷新** `LocalData\Patch\pkgversion` / `pkgcheck`（见 §5）。
- 源码：`NeteaseGamesGameLauncher.cs`。

---

## 2. `HEX_` 清单信封

```
"HEX_" (4B) + Base64( XOR( zlib(plaintext) ) )
XOR key = ASCII "netease_joker_h74_encrypt_key"，按字节循环
```

- `patchmd5_windows64_normal.txt`、`patch_info`、`mpk_filelist.info`、`package_resource.info`、
  `*.patchinfo` / 部分 `*.mpkinfo` 都是这个信封。
- Base64 体可能带换行、可能缺 padding（都要容忍）。
- 源码：`NeteaseGamesCrypto.DecryptHexEnvelope`。

> **曾经走错的一步**：一开始按「AES + IV」假设去解，解不出来；实际是 **XOR + zlib**。
> 判断依据：解码后 deflate 失败但首字节族呈固定前缀、且 launcher 里有
> `[ConfigMgr] DecryptCpsTag: zlib uncompress failed` 的日志串。

### CPS 标签（同类信封，无 magic）

`Base64(XOR(zlib(明文)))` 与 §2 同一把 key。对应启动器文件 `LocalData\Patch\CPS_TAG`，内容形如
`_launcher_deploy_202609161132`。**插件不使用它**（游戏自己维护状态），实现已移除，仅在本文档留档。

---

## 3. 远端清单与版本

### 3.1 清单主机与命名

| 用途 | URL |
|---|---|
| 清单根 | `https://h74.update.netease.com/pl/` |
| mini-pack 清单 | `…/pl/OB_release_mini_pack_version.json` |
| patch 清单（明文文本 + JSON 尾巴） | `…/pl/OB_patchmd5_windows64_normal.txt` |
| 公告清单 | `…/pl/OB_release_launcher_notice_info.xml` |
| 内容根 | `https://h74.gph.netease.com/` |
| 引擎载荷（mini-pack） | `https://h74.gdl.netease.com/loose_file/mini_pack_<版本>/…` |

⚠️ **`OB_` 前缀不能省**：不带前缀拿到的是**冻结的旧分支**（自检里实测：不带前缀 = 3899999，带前缀 =
6534552）。实际分支名在 `config.xml` 的 `launcher_prefix` / `minipack_prefix` / `notice_prefix`。
源码：`NeteaseGamesEndpoints`。

### 3.2 `patchmd5` 文本结构

四行数字 + 一个 JSON：

```
20261016125738025            ← 引擎版本时间戳
7586518639554130336          ← 远端文件列表（patchlist）的 64 位 hash
0
6534552                      ← 兼容位（老分支的包版本）
{"normal":{"latest_pkgversion":6534552}, "@metadata@":{"@base_version@":20261014065508363,"@engine_version@":"…"}}
```

- **包版本**取 `normal.latest_pkgversion`（权威）；`@metadata@.@base_version@` 是内容根下的**版本目录名**，
  下载路径形如 `<base_version>/mpk_script/script.mpkinfo`。
- 老分支的包版本可能落在第 3/4 行，所以实现里保留"取两者较大值"的兜底。
- 源码：`NeteaseGamesPatchManifest.Parse`。

### 3.3 本地版本来源（全部是磁盘文件）

| 文件 | 含义 |
|---|---|
| `LocalData\Patch\pkgversion` | 包版本（数字） |
| `LocalData\Patch\patchmd5_windows64_normal.txt` | 本地已知的最新补丁（同上，`latest_pkgversion`） |
| `LocalData\Patch\gameversion` | `CN_windows_<hash>_<pkgver>_<patchver>` |
| `Documents\versiondata.json` | `{package_version, patch_version, engine_version, shader_version}` |
| `Engine\Config\Built.version` | 引擎版本字符串 |

源码：`NeteaseGamesLocalState.Read`（带按 mtime/size 的解析缓存，17 MB 清单只解一次）。

---

## 4. 内容下载

### 4.1 URL 签名（必需，否则 403）

```
key2 = hex( unix_time + 43200 )  // 8 位小写十六进制，+12 小时
key1 = md5( SECRET + path + key2 )   // path = URL 的路径部分，不含 host/query
URL  = https://<host><path>?key1=<key1>&key2=<key2>
```

`SECRET = "p99AkludZnNOkBNQd3ldWkLjGVfC"`（位于 `PCLauncher.exe` 的 DRPF 模块旁）。

**回归验证**（四条真实抓包，全部逐字节吻合）：

| path | key2 | key1 |
|---|---|---|
| `/20261014065508363/mpk_medium/Patch2000.mpk` | `6ad7c991` | `4727210511e67d04e0617282450707b6` |
| `/20261014065508363/mpk_script/script.mpkinfo` | `6ad7c991` | `531ad5c57942cda1eec739f1b495d305` |
| `/20261014065508363/patchlist_windows64_normal_mpk.txt` | `6ad7c990` | `fb2971228fb750c266d754ea39831fa0` |
| `/loose_file/x/abc` | `6ad7c990` | `4dc2d63d2741be49e05b8935d43b53a6` |

源码：`NeteaseGamesUrlSigner`；回归用例在 `Hi3Helper.NeteaseGames.Tests/PathAndCryptoTests.cs`。

### 4.2 路径形状

```
/<base_version>/<file>                     ← 文件清单（patchlist_*_mpk.txt 等）
/<base_version>/<section>/<file>           ← mpk 分片；section ∈ mpk_script / mpk_medium / mpk_indep
/loose_file/mini_pack_<版本>/<相对路径>     ← 引擎载荷（只在 h74.gdl.netease.com 上，其它主机 404）
```

源码：`NeteaseGamesContentEndpoints.GetContentUrl`。

### 4.3 分片与索引

- 分片就是 `.mpk`（单个最大 ~800 MB），**内容按 offset 直存**，不需要解包。
- `script.mpkinfo` 之类的索引是 `HEX_` 信封 + 私有二进制：
  ```
  u32 version(=1) u32 count
  重复: u16 pathLen, path(UTF-8), u32 offset, u32 size, u32 flags(=2000)
  16B trailer
  ```
  （自检：`script.mpkinfo` 解出 **50325** 条）
- ⚠️ 本地 `Patch.mpkinfo` / `ResourceExA.patchinfo` 是 **SQLite**（表 `info1/2/3/7/8(filepath BLOB, value BLOB)` +
  `meta`），键是**二进制 hash 不是路径**，所以没法直接还原文件名——这两份是**游戏自己的记账**，插件不碰。
- 网易**没有二进制差分**：增量 = 逐个 mpk 分片 / 逐个松散文件（与官方一致）。

---

## 5. 补丁目录归游戏管

直启后游戏会刷新：`pkgversion`、`pkgcheck`、`Patch.mpkinfo`、`ResourceExA.patchinfo`、`latest_engine_ver`、
`devicelimit.json`、`env_check.json`。

⇒ **插件不应覆盖这些"记账"文件**，只负责把内容（mpk / 松散文件 / 引擎文件）放到正确位置，并写
`pkgversion` 作为"本次更新已完成"的提交点（且只在全部成功之后写）。

---

## 6. 公告 / 媒体

- 公告清单：`…/pl/OB_release_launcher_notice_info.xml`（明文 XML，`ActivityInfo` + `AnnounceInfo`，
  每条有 `title="one:<id>"`、`url[title=zh_CN]`、`begin/end_timestamp`）。
- 单条公告 JSON：`https://h74.update.netease.com/game_notice/<id>_zh_CN`（**无鉴权**，实测 200）。
- 公告图片：`…/game_notice/image/<id>.png|jpg`。
- 游戏自己也缓存了一份：`Documents\notice\*.json` + `notice_list_cache.json`（离线兜底用）。
- ⚠️ 公告图多为 1026×246 的横幅条，**不适合当主视觉**；插件的背景优先用游戏缓存的大图，其次才是内置兜底。

源码：`NeteaseGamesLauncherApiNews` / `NeteaseGamesLauncherApiMedia`。

---

## 7. 环境与登录

- UniSDK 通过环境变量把登录态交给游戏：`UNISDK_LOGIN_JSON`、`UNISDK_JF_ACCESS_TOKEN`、
  `UNISDK_JF_REFRESH_TOKEN`、`UNISDK_JF_GAS_JWTTOKEN`、`UNISDK_SAUTH_STATUS`。
- **实测结论：不需要注入这些变量游戏也能登录**（游戏自带登录界面）。插件保留"若存在已保存会话则注入"的
  能力，但注入只发生在启动前后一瞬间，且会话以 **DPAPI（CurrentUser）** 加密存放（`%APPDATA%\Hi3Helper\NeteaseGames\session.dat`）。
- ⚠️ `runas` 经 UAC broker 启动时子进程环境由 broker 重建，注入**可能不生效**——所以它只能是"锦上添花"，
  不能是登录的唯一途径（当前实现即如此）。

源码：`NeteaseGamesSession`。

---

## 8. 注册表（安装路径发现）

```
HKCU/HKLM\Software\OB_ywzh_launcher_PC
  AdditionalPaths = <游戏目录>            ← 优先用这个
  InstPath        = <启动器目录>          ← 含 4 个 U+200B 零宽空格，必须清洗；游戏目录是其子目录 ywzh_extreme
  APP_EXE_NAME
```

源码：`NeteaseGamesLocalState.FindExistingInstallPath` / `CleanPath`。

---

## 9. 已否决 / 走不通的路（避免重走）

| 尝试 | 结论 |
|---|---|
| 用 `--takeover` 让启动器接管 | 那是启动器把游戏窗口 reparent 进自己窗口的模式，不是"静默启动" |
| 用 `--fs-native` / `--fs-root` 指资源根 | 引擎会按错的根找包，反而起不来 |
| 在安装根以 cwd 启动 | 331 MB 处崩溃 |
| 伪造 `LocalData\Patch\ywzh.exe` 标记文件 | **不需要**：直启完全不需要它（早期注释里写"缺了就拒绝启动"是错的，已删除） |
| 自己算 `Hash` 写进 `PatcherSDK` 风格终态 | 网易这套没有等价的"终态文件"，记账全在游戏侧 |
| 用 `HDiff`/`SharpHDiffPatch` 做增量 | 网易不发二进制补丁，纯文件/分片粒度 |
| 关闭证书校验访问内容 CDN | 完全没必要：正规 HTTPS + 有效证书；且 `Dispatch` 那套会把**远端下发的 IP** 喂给客户端，属于 MITM 入口，已删除 |

---

## 10. 尚未解决 / 待验证

1. **松散文件的下载 URL 形状**：`patchlist` 里 `common`/`windows64_common` 段有 231,619 条，但自检里
   `<base_version>/<相对路径>` 的命中率还没跑满；`mpkexclude`（778 条）明确是"不进 mpk"的文件，必须能找到。
   → 当前实现：松散文件变更时**拒绝把更新标记为完成**（宁可让用户看到失败，也不留半更新）。
2. **引擎载荷的校验**：`md5_engine.txt` 里是网易私有 64 位 hash，未定位算法 ⇒ 现在**只按大小校验**
   （比不校验好，但不是完整校验）。
3. **`--headless` 启动器开关**的语义（对"完全无 UI 更新"有用，但当前架构不需要）。
4. 游戏内热更（`hotfix_cache` 那一路）不受插件影响，也不需要管。

---

## 附：证据在哪儿

研究脚本与抓包样本在 `.dsh\lab`（157 MB，**未纳入版本库**）。本目录的 `docs/re/` 收纳了其中的
**结论与关键样本**；原始脚本属于一次性工具，不建议整体入库（含 CA 私钥、抓包 dump 等敏感物）。
