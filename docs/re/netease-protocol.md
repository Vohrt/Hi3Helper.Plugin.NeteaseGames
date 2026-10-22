# 网易 · 遗忘之海（Sea of Remnants）更新协议

> **本文只写实测结论。** 每条都标注了验证方式。未验证的内容一律不写，改为在
> [§8 待完成](#8-待完成) 中登记。

---

## 1. 两个不同的版本号（最容易搞错的一点）

远端清单 `OB_patchmd5_windows64_normal.txt` 是 **4 行头部 + JSON**：

```
20261022122731826          ← 行0  内容版本 = 内容服务器的目录名
16867045629764259494       ← 行1  patchlist 的哈希
0                          ← 行2  保留
6582435                    ← 行3  补丁版本
{ "normal": {...}, "@metadata@": {...} }
```

| 值 | 含义 | 用途 |
|---|---|---|
| **行0** `20261022122731826` | **内容版本** | **所有下载 URL 的目录就是它** |
| 行1 | patchlist 哈希 | 校验清单 |
| **行3** `6582435` | **补丁版本** | **判断"是否需要更新"就比这个** |
| `@metadata@.@base_version@` `20261021113421287` | 仅供参考 | **不是下载目录** |

### 为什么必须用行0（实测对比）

| 请求 | 返回大小 | 官方实际安装 |
|---|---|---|
| `/{行0}/mpk_script/Patch1000.mpk` | **15,767,230** | 15,767,230 ✅ |
| `/{@base_version@}/mpk_script/Patch1000.mpk` | 13,283,727 | ✗ |

用行0 拉 `patchlist_windows64_normal_mpk.txt` 得到 **9,298,776 B**，与官方启动器写在
`LocalData\Patch\patchlist_windows64_normal.txt` 的**字节数完全相同**，且 50 个包大小与磁盘
**逐个吻合**。用 `@base_version@` 则有 9 个包对不上。

> 这就是之前"本地比远端新"假象的真正原因：**用错了目录，取到的是旧包。**
> 官方不可能以本地为准，那样它永远发现不了更新。

### 本地版本从哪读（实测）

官方启动器跑完后磁盘上是：

```
pkgversion          = 6521279     ← 基础包版本
gameversion         = CN_windows_0a66d2e2a_6521279_6582435
patchmd5 行3        = 6582435     ← 补丁版本（更新判断用这个）
authored versiondata = {"package_version":6521279,"patch_version":"6582435"}
```

**本地补丁版本 = `patchmd5` 第 3 行**。`pkgversion` 是另一个维度，不能拿来比。

---

## 2. 内容服务器的 URL 形状（实测可下载）

```
https://h74.gph.netease.com/{内容版本}/{section}/{文件名}?key1={签名}&key2={过期}
```

- `section` 为空时省略该级（清单文件就在根下）
- 签名：`key1 = md5(SECRET + path + key2)`，`key2 = hex(epoch + 43200)`

| 对象 | 路径 | 实测 |
|---|---|---|
| patch 清单 | `/{ver}/patchlist_windows64_normal_mpk.txt` | 200, 9,298,776 B |
| 资源清单 | `/{ver}/patchlist_windows64_normal.txt` | 200, 38,644,179 B |
| 补丁分片 | `/{ver}/mpk_script/Patch1000.mpk` | 200, 15,767,230 B |
| 索引 | `/{ver}/mpk_script/script.mpkinfo` | 200, 1,870,468 B |
| 索引 | `/{ver}/mpk_medium/s_file.mpkinfo` | 200, 8,395,696 B |
| 索引 | `/{ver}/mpk_indep/indep.mpkinfo` | 200, 312,768 B |
| 排序表 | `/{ver}/sort_map_windows64_normal.txt` | 200, 473,532 B |
| 预载表 | `/{ver}/preload_list_windows64_normal.txt` | 200, 24 B |

远端清单本身在另一台主机：`https://h74.update.netease.com/pl/OB_patchmd5_windows64_normal.txt`
（**明文，无 HEX_ 包装**）。

---

## 3. patchlist 的内容

```json
{
  "common":            { "<虚拟路径>": [<64位哈希>, <大小>], ... },   // 约 23 万条
  "windows64_common":  { ... },                                      // 约 2 千条
  "mpkexclude":        [ ... ],                                      // 778 条
  "file_sections":     ["common","windows64_common","mpk_script","mpk_indep","mpk_medium"],
  "mpk_script":        { "Patch1000.mpk": [<分块哈希...>, <总大小>], ... },  // 16 个
  "mpk_indep":         { "Patch3000.mpk": [...], "indep.mpkinfo": [...] },   // 2 个
  "mpk_medium":        { "Patch2000.mpk": [...], ... }                       // 32 个
}
```

**关键**：包条目数组的**最后一个数字是包大小**，前面全是分块哈希。

```
Patch2000.mpk: [3996881272972637595, 7003971163209048021, ..., 15629001]
                                                                  ^^^^^^^^ 大小
```
实测 50 个包的 `entry[-1]` 与磁盘/远端**逐个吻合**。

### `common` / `windows64_common` **不是磁盘文件**

那 23 万条（`Char/…`、`Sounds/…`、`VA/…`）是**引擎内部虚拟资源路径**，内容存放在 `.mpk` 里。
实测抽样 2000 条，磁盘上 **0 条存在**。不要尝试逐个下载。

---

## 4. 索引文件（mpkinfo / patchinfo）

三个可下载的索引：`script.mpkinfo`、`s_file.mpkinfo`、`indep.mpkinfo`。

格式（**实测 100% 解析成功**：50,443 + 232,990 + 13,135 条，0 丢失）：

```
[HEX_ 信封]
  u32 version = 1
  u32 count
  重复 count 次：
    u16 nameLength
    char[nameLength] name
    u32 offset
    u32 size
    u32 marker
```

`marker` **是包 ID**，不是标志位。实测映射：

| marker | 对应包 | 校验 |
|---|---|---|
| 2000 | `Patch1000.mpk` | `最大(offset+size)` = 15,767,230 = 磁盘 ✅ |
| 2002 | `Patch1001.mpk` | 14,327,433 ✅ |
| 2004 | `Patch1002.mpk` | 21,167,503 ✅ |
| 4000 | `Patch2000.mpk` | 15,629,001 ✅ |
| 6000 | `Patch3000.mpk` | 43,672,833 ✅ |

⇒ **索引自带每个包的精确大小和内容布局**，可以用来校验包是否与索引配套。

`ResourceExA.mpkinfo` / `Patch.mpkinfo` / `ResourceExA.patchinfo` 是 **SQLite**，
`info0(filepath, value)`，`value` 28 字节：

```
[0:4]   size
[8:10]  marker 0x0001
[10:16] 6 字节内容哈希
[16:20] offset
[20:24] cumulative 偏移
```

累积偏移链实测连续（`0 → 89,839 → 450,465 → 498,410`）。

---

## 5. 官方启动器对本地文件做了什么（实测）

一次更新（2026-10-22 18:21）写入的范围：

| 文件 | 说明 |
|---|---|
| `patchlist_windows64_normal.txt` | 合成版：`_mpk` 清单 + `mapping_version` + `mapping_ignorelist`，HEX_ 加密 |
| `patchmd5_windows64_normal.txt` | 远端清单的**原样拷贝**，HEX_ 加密（四行逐字节相同） |
| `pkgversion` | 基础包版本 |
| `gameversion` / `versiondata.json` | 客户端版本记录 |
| `Patch.mpkinfo` / `ResourceExA.patchinfo` | SQLite 索引，随包重写 |
| `mpk_script/*.mpk` 等 | 实际包 |

临时文件命名（下载过程中出现，完成后清理）：

```
LocalData\Patch\tmp_file\com.temp.orbit\<16位hash>-<section>_<name>_<start>_<end>[<start>-<end>].tmp
例：07d82e4b900f3f25-mpk_script_Patch1000.mpk_12582912_15767230[12582912-15767230].tmp
```

`LocalData\Patch\PreDownload.mpk` + `PreDownload.mpkinfo`（LevelDB）为预下载库，完成即清理。

**启动器没有"选择安装目录"选项，也没有"开始更新"按钮**——启动即按其 `setting.ini`
里的 `InstallPath` 直接开始（实测）。

---

## 6. 引擎载荷（mini pack）

`/pl/release_release_mini_pack_version.json`（**注意：当前该地址给的是灰度分支
`202603182149`，与本分支不符**）。本分支实际使用的清单由插件从
`loose_file/mini_pack_<版本>/` 获取：

```json
{
  "loose_file_parent_dir": "loose_file/mini_pack_202610151759",
  "engine_version": "261014173721_4a476f25b_release",
  "data_name": "mini_pack_202610151759.data",
  "current_version": "202610151759",
  "parent_dir": "mini_pack/mini_pack_202610151759",
  "download_info": [ {"md5": "...", "size": 69839049}, ... ]   // 20 块，合计 1,332 MB
}
```

解包后 287 个文件 / 1,332 MB，落在 `Engine\` 下。

---

## 7. 更新判定与落盘顺序

**判定**（与官方一致）：

```
需要更新  ⇔  本地 patchmd5 行3  ≠  远端 行3
```

**计划**：取 `/{行0}/patchlist_windows64_normal_mpk.txt` 的 `mpk_*` 段，逐个比较
`entry[-1]`（远端大小）与磁盘文件大小，不符或缺的就下载。

**落盘顺序**：分片 → 索引 → 版本号。`pkgversion` 是提交点，**全部成功才写**。

**更新完成后**必须刷新 `patchmd5`（官方读它判定），否则官方启动器会重复提示更新。

---

## 8. 待完成

1. **全新安装（0 → 完整游戏）**
   基础包 `Patch.mpk` / `Patch1-127.mpk` / `ResourceExA*.mpk` 共 **144 个 / 64 GB**。
   实测：它们**不在** patchlist 里，且在内容服务器下穷举
   `mpk_script/ mpk_indep/ mpk_medium/ mpk_common/ mpk_base/ base/ big/ bigpatch/ full/ normal/ patch/ res/`
   全部 404。
   但实测**官方确实下载了它们**（2026-10-22 18:21 落了 `Patch8/115/117/119/121-127`、
   `ResourceExA10/13/14/15`，约 2.4 GB）。临时文件名前缀 `big_common_*` / `big_shader_*`
   指向 `@bigpatchopen@=True` 的 bigpatch 通道，**该通道的清单与 URL 尚未定位**。
   ⇒ 在定位之前，`Install` 直接抛 `NotSupportedException`，不假装成功。

2. **引擎载荷按内容校验**（现在只比大小）——网易的 64 位哈希算法未定位。

3. **`@bigpatchmd5@` / `@allpatchlistdiffmd5@` 的用途**——只知存在，作用未证实。

4. **`sort_map_windows64_normal.txt`**（473 KB）用途未证实。

5. **社交/媒体**：见 `README.md` 的功能表，未验证的部分不在此声明。
