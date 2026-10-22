# 网易启动器版本判定逻辑（逆向记录）

> 目的：让插件的“是否需要更新”判断与**官方启动器**完全一致。
> 版本号问题反复出过三次（误报永久更新 / 更新后仍提示更新），根因都是**读错了来源**。
> 本文记录实证结论，改版本相关代码前必须读。

---

## 1. 结论速查

| 用途 | 权威来源 | 位置 |
|---|---|---|
| **远端**当前版本 | `@metadata@.@private_res_version@` | 远端 `OB_patchmd5_windows64_normal.txt` |
| **本地**已装版本 | **`pkgversion`**（插件写入的提交点） | `LocalData\Patch\pkgversion` |
| 官方启动器的记账 | `patchmd5_*.txt` **第 4 行** | `LocalData\Patch\patchmd5_windows64_normal.txt` |
| ~~`normal.latest_pkgversion`~~ | ❌ **绝不能用** | 别的分支的陈旧值（5282637 vs 真实 6582435） |
| ~~`gameversion` 末段~~ | ❌ 客户端写，更新后不刷新 | `LocalData\Patch\gameversion` |
| ~~`versiondata.json`~~ | ❌ 客户端写，更新后不刷新 | `Documents\versiondata.json` |

**判定式**：`本地 pkgversion != 远端 @private_res_version@` ⇒ 需要更新。

---

## 2. `patchmd5` 的四行结构（实证）

远端与本地**完全同构**：

```
20261022122731826          ← 同步时间戳（yyyyMMddHHmmssfff，17 位）
16867045629764259494       ← patchlist 哈希（64 位）
0                          ← 保留
6582435                    ← @private_res_version@ = 已安装版本
{ "normal": {...}, "@metadata@": {...} }
```

实证对照：

| | 远端 `OB_patchmd5` | 本地 `patchmd5`（更新前） |
|---|---|---|
| 行0 | `20261022122731826`（同步时刻） | `20261016125738025`（10-16 同步） |
| 行1 | `16867045629764259494` | `7586518639554130336` |
| 行2 | `0` | `0` |
| 行3 | **`6582435`** | **`6534552`**（旧版本） |

- 本地 `行3` == 本地 `metadata.@private_res_version@` ✅ 自洽
- 本地 `行0` ≠ 本地 `metadata.@base_version@`（`20261014065508363`）
  ⇒ **行0 是同步时刻，不是 base_version**

---

## 3. 官方启动器的实现（二进制证据）

`PCLauncher.exe`（148,999,680 B）内嵌 CPython，版本逻辑在 Python 模块里：

```
MLauncher.py     MLauncher.check / MLauncher.aftercheck / MLauncher.setPath
MChecker.py      MChecker.check / MChecker.aftercheck
                 MChecker.SimplePatchMpk.check_mpk_info / try_load / load_mpk_info
                 MChecker.SimplePatchMpk.update_mpk_data_size / update_engine_mpk_info
                 MChecker.SimplePatchMpk.get_mpk_data_relative_path / open_plyvel_db / load_meta_info
```

关键常量（`MChecker` 里）：

```
MPK_INFO_UPDATED   INFO_MD5   INFO_SIZE   MPK_DATA_SIZE
essential_md5      filelist_data / filelist_size
md5_cache_ok / size_cache_ok   md5cache / sizecache
INFO_BEGIN / INFO_DIRTY / INFO_INDEX / INFO_ISDIR
```

⇒ 官方对**内容**的校验是 **mpk 索引 + 64 位哈希 + 大小**（不是单一版本号），
所以插件更新后索引与数据一致即可，官方不会逐文件重算。

UI 状态键（`k_*`）说明判定结果如何映射到按钮：

```
k_version  k_update  k_launch  k_install  k_check_ver  k_start_down_patch  k_repair_content
```

`pkgversion` 在 exe 里只出现 **1 次**，且紧邻：

```
"Patch State %s"   "_no_check_exe_md5_"   "\BinPatch"   "repair_engine remove"
"PythonMgr::CheckExeMd5"   "data_name"   "parent_dir"   "download_info"
```

⇒ `pkgversion` 是 **C++ 侧**的补丁状态输入；真正的“是否需要更新”由 Python 的
`MLauncher.check` 决定，而它读的是 `patchmd5`。

---

## 4. 历史 bug（同一个根因，三种表现）

| # | 表现 | 错因 | 修法 |
|---|---|---|---|
| 1 | 永久提示有更新 | 用 `normal.latest_pkgversion`（5282637）当远端版本 | 改用 `@private_res_version@` |
| 2 | 更新装完仍提示更新 | 本地版本读 `versiondata.json`（客户端写的旧值） | 本地改读 `pkgversion` |
| 3 | **官方启动器不认插件装的更新** | **只写 `pkgversion`，不写 `patchmd5`** | **更新后同步重写 `patchmd5`** |

**根因统一描述**：`gameversion` / `versiondata.json` / `patchmd5` 都是
**游戏客户端或官方启动器写的文件**，插件更新不会自动刷新它们。
只写 `pkgversion` 会让所有读别处的消费者看到旧版本。

---

## 5. 插件的正确做法

更新成功后（`NeteaseGamesUpdateApplier`）**按顺序**写：

1. 分片 → 索引 → `pkgversion`（提交点，全部成功才写）
2. `patchmd5_windows64_normal.txt`（`NeteaseGamesLocalState.WriteLauncherRecord`）
   - 复用刚安装的远端清单原文
   - 替换 `@private_res_version@` / `@private_patch_version@` / `@private_script_version@`
   - 第 4 行 = 新版本，第 1 行沿用清单哈希，第 0 行用当前时间戳
   - **保留** `normal.latest_pkgversion`（保持与官方写入的一致）
3. `gameversion` 末段、`versiondata.json` 的 `package_version` / `patch_version`（best effort）

加密：`HEX_` + Base64(XOR(zlib(text), `netease_joker_h74_encrypt_key`))，
加解密见 `NeteaseGamesCrypto.EncryptHexEnvelope` / `DecryptHexEnvelope`（已验证往返）。

---

## 6. 复现脚本

```python
import base64, zlib, json
raw = open(r"D:\ywzh\ywzh_extreme\LocalData\Patch\patchmd5_windows64_normal.txt","rb").read()
body = b"".join(raw[4:].split()); body += b"=" * (-len(body) % 4)
data = bytearray(base64.b64decode(body)); key = b"netease_joker_h74_encrypt_key"
for i in range(len(data)): data[i] ^= key[i % len(key)]
plain = zlib.decompress(bytes(data)).decode()
print([l for l in plain.splitlines() if l.strip()][:4])   # 四行头部
```
