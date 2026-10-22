# 网易启动器：全新安装逻辑（静态逆向记录）

> 全部结论来自 `PCLauncher.exe`（148,999,680 B）的静态分析 + 远端清单实测，
> **未运行官方启动器做安装实验**。不确定项已明确标注。

---

## 1. 官方状态机（RTTI 实证）

`PCLauncher.exe` 里的补丁状态机（`AV*State` 类名）：

```
AVCheckLauncherFinishState     启动器自身是否完整
AVCheckGrayState               灰度名单
AVCheckVerState                版本比对      ← 判断"要不要更新"
AVCheckEngineVerState          引擎版本比对
AVCheckEngineValidState        引擎完整性
AVPreDownloadEngineState       引擎下载准备
AVDownloadEngineState          引擎下载
AVDoUpdateEngineState          引擎落盘
AVCheckEngineFinishState       引擎完成
AVCheckTransferState           传输检查（断点续传）
AVCheckTransferValidState      传输有效性
AVDoTransferState              执行传输
AVCheckPatchState              补丁检查      ← 决定要下哪些包
AVDownLoadPatchState           补丁下载
AVFinishState                  完成
```

**状态机没有“全新安装”分支**：全新安装与更新走**同一条链路**，
区别只在 `AVCheckVerState` / `AVCheckPatchState` 的比对结果为空 ⇒ 全部算“需要下载”。

⇒ **官方没有独立的安装流程，安装就是“全部都需要更新”**。

---

## 2. 两种 patchlist（关键，极易混淆）

远端存在**两个**清单文件，用途完全不同：

| 文件 | 大小 | 内容 | 谁用 |
|---|---|---|---|
| `patchlist_windows64_normal.txt` | 38.6 MB | `common` 509,184 条 + `windows64_common` 19,825 条 | **资源完整性校验**（虚拟资源路径 → 64 位哈希 + 大小） |
| `patchlist_windows64_normal_mpk.txt` | 17.5 MB | `common` 230,412 + `windows64_common` 2,026 + **`mpk_*` 50 个包** | **下载计划**（哪些分片要下） |

两者都被 `@exlistmd5@` 独立登记：

```json
"@exlistmd5@": {
  "patchlist_windows64_normal.txt":     16867045629764259494,
  "patchlist_windows64_normal_mpk.txt": 17220290977222519273,
  "sort_map_windows64_normal.txt":      74590789526129034,
  "preload_list_windows64_normal.txt":  1953689119806014473
}
```

注意 `patchlist_windows64_normal.txt` 的哈希 `16867045629764259494` **正是 `patchmd5` 第 1 行**。

### 包段结构（实证）

```json
"mpk_medium": {
  "Patch2000.mpk": [3996881272972637595, 7003971163209048021, ..., 15629001]
}
```

**最后一个数字是包大小，前面全部是分块哈希。**
实测 50 个包的 `entry[-1]` 与磁盘文件大小**全部一致**，合计 **767,892,070 B = 732.3 MB**。

---

## 3. 基础包 vs 补丁包（全新安装的核心）

磁盘实测：**186 个 `.mpk`，64.36 GB**。

| 类别 | 文件 | 数量 | 在清单里？ |
|---|---|---|---|
| 补丁分片 | `Patch1000-1014` / `Patch2000-2029` / `Patch2500` / `Patch3000` / `*.mpkinfo` | **50** | ✅ `_mpk` 清单的 `mpk_*` 段 |
| 基础包 | `Patch.mpk`、`Patch1-123.mpk` | **123** | ❌ 不在任何清单 |
| 引擎资源包 | `ResourceExA`~`ResourceExF` + 编号 | **13** | ❌ 固定命名 |

⇒ **`_mpk` 清单只描述“本次补丁的分片”，不是全量。**

基础包 `Patch*.mpk` 是**首装时下发的完整资源**，其清单不在 `patchlist` 里
（静态分析未找到来源；推测在 mini-pack / engine payload 链路，见下节标注）。

---

## 4. 引擎载荷（`CheckEngineVerState` 日志实证）

官方启动器的引擎检查有完整日志串：

```
CheckEngineVerState: Parsed JSON successfully
  loose_file_parent_dir: <路径>
  engine_version:        <版本>
  data_name:             <名称>
  current_version (remote): <版本>
  parent_dir:            <路径>
CheckEngineVerState::DoCheckVersion
  Local Engine Version:  ...
  Remote Engine Version: ...
  Engine needs update.          /  Engine is up to date, skip to FINISH
```

落盘逻辑（`DoUpdateEngineState` 日志）：

```
DoUpdateEngineState: Processing used_del_dir
DoUpdateEngineState: Processing mpk_res, deleting old files
DoUpdateEngineState: Copying mpk_res to game install path
DoUpdateEngineState: Cleaning up used_del_dir
DoUpdateEngineState: Deleting obsolete files (before copy)
```

⇒ 引擎载荷 = `mini_pack`（`loose_file_parent_dir` + `download_info` 块），
解包到临时目录（含 `used_del_dir` / `mpk_res` 子目录），再整体复制到安装目录。

**这与插件的 `NeteaseGamesEnginePatcher` 一致**（下载 mini pack → 解包 → 落盘）。

---

## 5. 引擎包文件系统（固定成员，实证）

`PCLauncher.exe` 字符串：

```
Package file systems... fs-package... fs-precmds... FileModule...
ResourceExA  ResourceExB  ResourceExC  ResourceExD  ResourceExE  ResourceExF  ShaderCache
/LocalData/Patch
PatchShaderCache
```

⇒ `ResourceExA-F` / `ShaderCache` 是**引擎按固定名字查找的包**，不是清单驱动。
它们必须存在，但**下载方式由 mini-pack 的 `download_info` 决定**。

---

## 6. 插件应如何对齐（结论）

| 场景 | 计划内容 |
|---|---|
| **Update** | `_mpk` 清单的 `mpk_*` 段中，与本地 patchlist 哈希不同的分片 |
| **Repair** | 本地清单认为当前的分片，逐个校验磁盘存在与大小 |
| **Install** | `_mpk` 清单的**全部** `mpk_*` 分片 + **引擎载荷（mini pack）** |

**Install 不等于 186 个包**：基础包 `Patch*.mpk` 由引擎载荷（mini-pack）提供，
`_mpk` 清单的 50 个分片是补丁层。两者相加才是完整安装。

### 已知不确定项（静态分析无法确定）

1. 基础包 `Patch.mpk` / `Patch1-123.mpk` 的确切下载来源
   —— 推测在 mini-pack 的 `download_info` 块里（引擎载荷解包后即包含）。
2. `sort_map_windows64_normal.txt` / `preload_list_windows64_normal.txt` 的用途。
3. 灰度名单 `@grayscale@` 对下载的影响。

**要确认这三项必须实测官方启动器的首装行为，当前按“不实测”处理。**

---

## 7. 复现脚本

```python
import base64, zlib, json, time, hashlib, urllib.request

KEY = b"netease_joker_h74_encrypt_key"
SECRET = "p99AkludZnNOkBNQd3ldWkLjGVfC"
HOST = "h74.gph.netease.com"

def dec(raw):
    if raw[:4] != b"HEX_": return raw
    body = b"".join(raw[4:].split()); body += b"=" * (-len(body) % 4)
    data = bytearray(base64.b64decode(body))
    for i in range(len(data)): data[i] ^= KEY[i % len(KEY)]
    return zlib.decompress(bytes(data))

def sign(path):
    k2 = format(int(time.time()) + 43200, "x")
    k1 = hashlib.md5((SECRET + path + k2).encode()).hexdigest()
    return f"https://{HOST}{path}?key1={k1}&key2={k2}"

base = "20261021113421287"
raw = urllib.request.urlopen(urllib.request.Request(
    sign(f"/{base}/patchlist_windows64_normal_mpk.txt"),
    headers={"User-Agent": "Mozilla/5.0"})).read()
t = dec(raw).decode("utf-8", "replace")
d = json.loads(t[t.find("{"):])

total = 0
for sec in ("mpk_script", "mpk_indep", "mpk_medium"):
    for name, entry in d.get(sec, {}).items():
        size = entry[-1]          # 最后一个是大小
        total += size
        print(f"{sec:12} {name:22} {size:>14,}")
print(f"合计 {total:,} B = {total/1024**2:.1f} MB")
```
