# 遗忘之海（Sea of Remnants）官方更新协议 — 逆向实测记录

> 本文件的每条结论都有实测证据（内存 dump / 服务器探测 / 磁盘校验）。
> 与旧文档不同，这里**只写已证实的内容**，未证实的一律列在 §9。

> ## ⚠ 2026-10-24 复核：以下结论已被推翻，正文已就地更正
>
> 复核环境：`D:\ywzh\ywzh_extreme` + 线上 `20261023182924387` / `6593596`，方法见 `docs/NETEASE-FIX-PLAN.md` 附录 A。
>
> | 原结论 | 复核结果 |
> |---|---|
> | §1 "不带签名 → 403" | **只对 `h74.gdl.netease.com`（引擎载荷）成立**。`h74.gph.netease.com` 的内容路径裸请求就是 200，`key1/key2` 可有可无。 |
> | §5.4 `big_` 目录 = `big_` + **行 0** | **错**。行 0 目录只放本轮新增（60 抽 1 命中）；要取全量必须用 **`@base_version@`**（60/60、15/15、15/15）。 |
> | §5.4 索引 `[20:24]` = 累积**结束**偏移 | **错**。`[20:24]` 是**起始**偏移，`[0:4]` 是资源大小；`max(start+size)` 与 128 个分卷中 **127 个**文件大小精确相等。 |
> | §5.4 "差值 = 分卷填充，可达 447 MB" | **错**。那些差值就是最后一条资源的大小。真实填充只有 `Patch100` 的 1,876,274 B。 |
> | 隐含前提"`PatchN.mpk` 由多个 `big_common` 条目合并" | **未证实**。条目级/块大小两种比对 0 命中，见 §5.4 末尾。 |
> | §5.2 "144 个 / 64 GB 基础包" | 实际 **128 个分卷 / 64.23 GB**（`Patch.mpk` + `Patch1..127`）。 |
>
> 其余章节（HEX_ 信封、patchmd5 四行结构、patchlist 分段、mpkinfo 格式、记账文件、启动契约、版本判定）**复核通过**。

逆向对象：`D:\ywzh\ywzh_launcher\Win64Release\PCLauncher\PCLauncher.exe`
（PyInstaller 式单体，PE32+，149 MB，导出 `do_unzip_dir` / `do_unzip_dir_inner`；
 内置静态 Python 3.14 + OpenSSL，模块 `py_launcher` → `MLauncher` → `MPatch` / `patch_utils.*`）

---

## 1. 主机与端点（内存实测字符串）

| 用途 | 平台 | 主机 |
|---|---|---|
| MD5/清单源 | `PATCH_MD5_HOST_RELEASE` | `h74.update.netease.com`（`http`/`https`，端口 7200/443） |
| 内网同步 | `PATCH_MD5_HOST_RELEASE` 内网 | `h74.list.inner.netease.com` |
| 老集群 | — | `listsvr.x.netease.com:6678/patch_list/h74/`、`update.x.netease.com/patch_list/h74/` |
| 内容源 | `PATCH_SERVER_HOST_RELEASE` | `h74.gph.netease.com`（443/7200） |
| 内容内网 | — | `h74.gph.inner.netease.com` |
| 海外 | `*_OVERSEA` | `h74na.gph.easebar.com`、`h74na.update.easebar.com` |
| SDK | — | `mgb-h74.unisdk.netease.com/h74/sdk/` |
| 服务器时间 | — | `https://h74-ms-uwsgi.netease.com/tools/server_time` |
| HTTPDNS | — | `httpdns.nie.netease.com`、`https://dns.update.netease.com/hdserver` |

前缀常量：`PATCH_SERVER_PREFIX` / `PATCH_MD5_PREFIX`（运行时展开为 `patch_prefix` / `md5_prefix`）。
`https://h74.update.netease.com/pl/` 是清单根（`patchmd5_*` 就在这里，**200 OK，明文**）。

### URL 签名（**只有引擎载荷主机需要**，实测 2026-10-24 修正）

```
key2 = hex32(unix_time + 43200)          # 12 小时后的时间戳，小写十六进制
key1 = md5_hex(SECRET + path + key2)     # path 含前导 "/"，不含查询串
URL  = https://{content_host}{path}?key1={key1}&key2={key2}
SECRET = p99AkludZnNOkBNQd3ldWkLjGVfC
```

**需要签名的：** `h74.gdl.netease.com`（引擎载荷 `/loose_file/...`）。裸请求 **403**，带签名 200。
**不需要签名的：** `h74.gph.netease.com`（`/{content_version}/...` 的清单与 mpk 分块）。
裸请求直接 **200**；带 `key1=deadbeef&key2=deadbeef` 或正确的 key1 也都是 200。

```
实测（2026-10-24）：
  gph /20261023182924387/mpk_script/Patch1000.mpk              无签名 → 200, 16150134 B
  gph /20261023182924387/mpk_script/Patch1000.mpk             伪造签名 → 200, 16150134 B
  gdl /loose_file/mini_pack_202610151759/.../cclibdll.dll.1    无签名 → 403
  gdl 同路径                                                    有效签名 → 200, 183016 B
```

> 旧结论"`/{content_version}/mpk_script/Patch1000.mpk` 不带签名 → 403"**只在当时那条特定路径上成立**，
> 不能推广。签名照带没有坏处（服务端忽略），但**引擎载荷不带签名一定 403**，这是硬要求。

- `update.x.netease.com` / `listsvr.x.netease.com` 需要**另一种认证**（裸请求 403），插件不走这两处。

---

## 2. 远端清单 `OB_patchmd5_windows64_normal.txt`

- 地址：`https://h74.update.netease.com/pl/OB_patchmd5_windows64_normal.txt`（**明文，无 HEX_ 包装**）
- 格式：**4 行头部 + JSON**

```
20261023122834919          ← 行0 内容版本（内容主机目录名）
10590933344404223586       ← 行1 patchlist 指纹
0                          ← 行2 保留
6589318                    ← 行3 补丁版本（更新判定用）
{ "normal": {...}, "@metadata@": {...} }
```

### `normal` 段
```json
{"mininum_pkgversion":0, "review_pkgversion":[], "new_package_url":"", "latest_pkgversion":5282637}
```

### `@metadata@` 段（2026-10-23 实测全量）
```json
{
  "@grayscale@": {"@usehttp2@": true, "@http2chance@": 50, "@earlypatchreloadrate@": 100},
  "@bigpatchmd5@": 6128997482385675719,
  "@bigshadercachelistmd5@": {"bigshadercachelist_dx11.txt": ..., "bigshadercachelist_dx12.txt": ...},
  "@pkgshaderkey@": {},
  "@bigpatchmaxratio@": 1000000,
  "@private_res_version@": "6589318",
  "@private_patch_version@": "6589318",
  "@private_script_version@": "6589318",
  "@allpatchlistdiffmd5@": {"20261021113421287": 2678726427502316576},
  "@is_full@": false,
  "@tinker@": true,
  "@forceupdatehigherlocal@": false,
  "@shadercachelistmd5@": {"shadercachelist_dx9.txt": ..., "dx11": ..., "dx12": ...},
  "@patch_branch@": "release",
  "@bigpatchopen@": true,
  "@mpkinfolist@": ["script.mpkinfo", "s_file.mpkinfo", "indep.mpkinfo"],
  "@exlistmd5@": {"sort_map_windows64_normal.txt": ..., "patchlist_windows64_normal.txt": ...,
                  "patchlist_windows64_normal_mpk.txt": ..., "preload_list_windows64_normal.txt": ...},
  "@base_version@": 20261021113421287,
  "@allowbackgroundpatchversioninlauncher@": 3,
  "@forcerestartafterpatch@": false,
  "@engine_version@": "261021114347_cd5d7a8d5_release",
  "@normalpatchthreshold@": 1048576
}
```

**关键结论（实测）**
- 下载目录 = **行0**，不是 `@base_version@`。实测行0 下 `patchlist_windows64_normal_mpk.txt`
  解出来与官方启动器写到磁盘的 `patchlist_windows64_normal.txt` **逐字节相同**。
- `@base_version@` 与行0 不同（`20261021113421287` vs `20261023122834919`），二者都可用但
  `@base_version@` 指向旧一轮内容；必须用行0。
- `@bigpatchopen@ = true` 是**分支开关**，不是"安装通道"。

---

## 3. 补丁清单 `patchlist_windows64_normal.txt`（两处同名文件）

| 位置 | 名字 | 大小 | 内容 |
|---|---|---|---|
| 内容主机 `/{行0}/` | `patchlist_windows64_normal.txt` | 21,314,476 | 全量：只有 `common`(509,199) / `windows64_common`(19,825) / `mpkexclude`(778) / `mapping_*` |
| 内容主机 `/{行0}/` | `patchlist_windows64_normal_mpk.txt` | 9,299,048 | 含 `mpk_script` / `mpk_medium` / `mpk_indep` 段（`common` 只有 230,427 条） |
| 磁盘 `LocalData\Patch\` | `patchlist_windows64_normal.txt` | 9,299,048 | **官方启动器写的是 `_mpk` 那份**（与远端 `_mpk` MD5 逐字节相同） |

两者都是 `HEX_` 信封：`"HEX_" + Base64(XOR(zlib(json)))`，XOR 键 `netease_joker_h74_encrypt_key`。

### JSON 段
```
common              509,199 条  → 虚拟资源路径 → [64位哈希, 大小]   （实测磁盘 0 文件命中，都在 mpk 内）
windows64_common      1,984~19,825 条 → 同上
mpkexclude              778 条  → 775 个 *.mdb + prerequisite / 4a/<hash> / Misc/lazycode_zstd_dict
                         （2026-10-24 复核：那 3 个非 mdb 的**在盘上存在**，775 个 .mdb 不存在）
mpk_script               16 个包（Patch1000..1014 + script.mpkinfo）
mpk_medium               32 个包（Patch2000..2029 + Patch2500 + s_file.mpkinfo）
mpk_indep                 2 个包（Patch3000 + indep.mpkinfo）
mapping_version    "20261021113421287"
mapping_ignorelist  40~245 条
quality_res_cgs     { "wide_super": [ ... nbs 列表 ] }
file_sections       ["common","windows64_common","mpk_script"...]
```

包条目数组：**最后一个是包大小**，前面全是分块哈希。
```
Patch2000.mpk: [3996881272972637595, ..., 15656291]  ← 末位 = 大小
```

**实测校验（2026-10-23）**：远端 50 个包的大小与磁盘**逐个吻合**；`common` 段 509,199 条
合计 **64,400,962,688 B ≈ 64 GB** —— 这就是游戏资源本体，全部装在 mpk 里。

---

## 4. 索引文件格式（实测 100% 解析）

`script.mpkinfo` / `s_file.mpkinfo` / `indep.mpkinfo`，同为 `HEX_` 信封：

```c
u32 version = 1;
u32 count;
repeat count {
  u16 nameLen;
  char name[nameLen];
  u32 offset;
  u32 size;
  u32 marker;      // = 包 ID，不是标志位
}
```

实测解析：`script.mpkinfo` 50,443 条（消费 2,471,715 / 2,471,731 字节）。

**marker → 包对应（实测 `max(offset+size)` = 该包的载荷长度）**

| marker | 包 | 载荷长度 | 磁盘文件长度 |
|---|---|---|---|
| 2000 | Patch1000.mpk | 16,095,638 | 16,095,638 ✅ |
| 2002 | Patch1001.mpk | 14,327,433 | 14,327,433 ✅ |
| … 每 +2 | Patch1000+n | … | … |
| **2018** | **Patch1009.mpk** | **17,923,658** | **17,985,424** ⚠ 差 61,766 |
| 2020..2028 | Patch1010..1014 | … | … |
| 4000 | Patch2000.mpk | 15,656,291 | 15,656,291 ✅ |
| 6000 | Patch3000.mpk | 45,261,483 | 45,261,483 ✅ |

⇒ 索引可用来判断"包是否被截断"，无需网易 64 位哈希。

> ⚠ **实测陷阱（2026-10-23）**：`max(offset+size)` 是<b>载荷</b>长度，**不是文件长度**。
> `Patch1009.mpk` 在磁盘与内容主机上都是 17,985,424 B，而索引（marker 2018）只覆盖到 17,923,658 B ——
> 包尾有 61,766 B 填充。若按"相等"校验，会把完好的包判成损坏并重下 18 MB。
> **正确语义：磁盘大小 ≥ 载荷长度 才算完好**（短了才是截断）。

实测三个索引的解析完整性（本地磁盘）：

| 索引 | 条目 | 分片数 | marker 范围 |
|---|---|---|---|
| `script.mpkinfo` | 50,443 | 15 | 2000–2028（步长 2） |
| `s_file.mpkinfo` | 232,998 | 31 | 4000–4058 及 5000 |
| `indep.mpkinfo` | 13,130 | 1 | 6000 |

分片 id 规则（实测推出并逐个验证）：`mpk_script` 基址 2000、`mpk_medium` 基址 4000、
`mpk_indep` 基址 6000，**步长 2**，按包名里的数字升序排位。

> ⚠ **2026-10-24 复核补充：这个"按序号推算"的规则有一个例外。**
> `s_file.mpkinfo` 里最后 31 个 marker 是 `4000..4058`（30 个，步长 2）**加一个 5000** ——
> `Patch2500.mpk` 的 marker 是 **5000**，不是按序号算出来的 4060。
> 按序号推算会让 `Patch2500.mpk` 的布局校验**永远查不到索引而放行**。
> 正确做法：**marker 必须从索引里真实读出来的集合取**，推不出来时视为"无法校验"（拒绝），而不是"通过"。
>
> 核对结果（2026-10-24，真实索引 vs 磁盘）：
> `script.mpkinfo` 15/15 分片 `max(offset+size) == 文件大小`（`Patch1009` 除外，见上）；
> `s_file.mpkinfo` **31/31** 精确相等（含 marker 5000 → `Patch2500` 36,034,912 B）；
> `indep.mpkinfo` 1/1 精确相等。

---

## 5. 基础包（Patch.mpk / ResourceExA*）—— 真实机制（实测）

### 5.1 它们**不经过**内容主机的版本目录

实测在 `/{行0}/` 下穷举
`Resources.mpk`、`ResourceExA.mpk`、`Patch.mpk`、`pk_package/`、`pkg/`、`mpk/`、
`md5/`、`data/`、`file/`、`/xx/` 分片路径 → **全部 404**。

### 5.2 它们是**独立的一个包簇**，有自己的清单 `mpk_filelist.info`

磁盘根目录实测存在：

```
ResourceExA.mpk      62,389,447      ResourceExA.mpkinfo      64
ResourceExB.mpk      51,137,805      ResourceExB.mpkinfo      64
Resources.mpk         1,881,037      Resources.mpkinfo    13,664
Resources1.mpk       28,984,619
Resources2.mpk      110,382,228
mpk_filelist.info           520
package_resource.info    12,684
```

`mpk_filelist.info` 解密后（`HEX_` 信封）：
```
5e7eadfc182b5d97ea95b019d60d3b55784d81a8482e4e8128398d49c139e1a4   ← 簇指纹（sha256 形态）
ResourceExA.mpk   62389447   cb424697843095e6d45d7f20aa641b36   ← 名字 \t 大小 \t md5
ResourceExA.mpkinfo      64   d05a0633056838a267a14e57734f48e3
...
```
**实测 8 个文件的大小与 MD5 全部与磁盘一致**（这是本协议里唯一能真正做内容校验的地方）。

### 5.3 这套包簇与**每次更新**无关，但**会被更新改写**

实测两次真实更新（10-22、10-23）里，`Patch1..127` 中有 **10 个分卷被重写**：

```
Patch8    95,807,964   2026-10-22 18:21:50
Patch115 524,428,400   2026-10-22 18:21:19
Patch117 525,871,976   2026-10-22 18:21:22
Patch119 480,860,383   2026-10-22 23:39:27
Patch121 286,110,631   2026-10-22 18:21:52
Patch122 178,429,883   2026-10-22 18:21:52
Patch124  96,886,132   2026-10-22 18:21:52
Patch125  87,675,152   2026-10-22 18:21:52
Patch126  76,739,485   2026-10-23 15:54:04
Patch127  69,645,591   2026-10-22 18:21:52
```

其余 117 个仍保持首次安装（2026-10-20 14:06–14:47）的时间戳。

### 5.4 ✅ 基础包通道**已定位**（2026-10-23 内存取证）

**之前"通道不可达"的结论是错的** —— 错在 URL 路径少了一个 `big_` 前缀目录。

从运行中的 `PCLauncher.exe` 内存里直接读到了官方构造的真实 URL：

```
https://h74.gph.netease.com/big_20261023122834919/bigpatchlist_windows64_normal.txt
https://h74.gph.netease.com/big_20261023122834919/big_common/Char_736
https://h74.gph.netease.com/big_20261023122834919/big_common/r_8921
https://h74.gph.netease.com/big_20261023122834919/big_shader/dx12_916
https://h74.gph.netease.com/big_20261023122834919/big_common/resource.repository_1
https://h74.gph.netease.com/big_20261023122834919/bigshadercachelist_dx12.txt
```

**路径模板（实测 200）**

| 对象 | 路径 |
|---|---|
| 基础包清单 | `/{big_<version>}/bigpatchlist_windows64_normal.txt` |
| 基础包分块 | `/{big_<version>}/big_common/{条目名}` |
| 着色器分块 | `/{big_<version>}/big_shader/{dx11\|dx12}_{N}` |
| 着色器清单 | `/{big_<version>}/bigshadercachelist_{dx11\|dx12}.txt` |

> ### ⚠ 2026-10-24 复核：「`<version>` = 行 0」是**错的**，应该是 `@base_version@`
>
> 三轮独立抽样（每轮用不同的随机种子、不同的清单）：
>
> | 抽样来源 | `big_{行0}` | `big_{@base_version@}` |
> |---|---|---|
> | 60 个（行0 清单条目） | **1 / 60 = 200** | 60 / 60 = 200 |
> | 15 个（行0 清单条目） | — | 15 / 15 = 200 |
> | 15 个（base_version 清单条目） | 0 / 15 | 15 / 15 = 200 |
>
> 两份 `bigpatchlist`（11,124,133 B / 11,122,614 B）**都能在 `big_{@base_version@}` 下取到全部条目**
> ⇒ 那个目录是**持续累积**的分块池。而 `big_{行0}` 下只有 9 个"本轮新增"条目能取到（8/8 命中），
> 其余全 404。
>
> 具体数字（2026-10-24，`base_version = 20261021113421287`，`行0 = 20261023182924387`）：
> `big_{base_version}/bigpatchlist` = 11,122,614 B；`big_{行0}/bigpatchlist` = 11,124,133 B；
> `bigshadercachelist_dx12.txt` 分别为 8,906,483 / 8,906,674 B。
>
> 推论：`big_` 目录名是**官方启动器当前那一轮的 base_version**，不是内容版本。
> 之前从内存里读到的 `big_20261023122834919` 应当是当时的 base_version（无法回溯验证，但与本次观测一致）。
>
> 另外：**这个主机不需要签名**。裸请求即 200（见 §1 的修正）。

**`bigpatchlist` 结构（实测，明文 JSON，无 `HEX_` 包装，11,124,133 B）**

```json
{
  "big_common":           { "<条目名>": [<大小>, [<包含的资源路径>...], <64位哈希>], ... },  // 10,406 条 / 59.37 GB
  "big_windows64_common": { "r_0": [<大小>, [<资源路径>...], <哈希>], ... },                  // 18 条 / 0.09 GB
  "file_sections":        ["big_common", "big_windows64_common"],
  "mapping_ignorelist":   [...],
  "mapping_version":      "20261021113421287"
}
```

`big_common` 合计 **63,744,692,060 B ≈ 59.37 GiB**，与磁盘上 `Patch1..127` 的 63,707,951,860 B 相差
**132,714,178 B**（≈0.2%，2026-10-24 实测）⇒ 这就是基础包的来源，但不是逐字节一一对应。

磁盘上基础层完整口径：`Patch.mpk` + `Patch1..127` = **128 个文件 / 64,232,426,422 B ≈ 64.23 GB**。

**下载验证（2026-10-24 复核，全部使用 `big_{@base_version@}` 目录）**

| 条目 | 结果 |
|---|---|
| `big_common/Char_736` | **200**，5,396,903 B（与清单声明一致） |
| `big_common/r_8921` | **200**，4,955,759 B |
| `big_shader/dx12_916` | **200**，18,250,392 B |
| `bigshadercachelist_dx12.txt` | **200**，8,906,483 B |
| 随机 60 个 `big_common` 条目 | **60 / 60 = 200** |
| 同一批 60 个走 `big_{行0}` | **1 / 60 = 200**（其余 404） |

> 结论修正：`big_{@base_version@}` 是**完整可用**的（不是"只保留本次变更"）；
> `big_{行0}` 才是"只放本轮新增"。旧文写的"内容主机只保留本次更新涉及的分块"应理解为**对行0 目录**成立。

**关于"基础分卷是合并产物"这个前提（2026-10-24 复核：未证实）**

旧文依据"`Patch126.mpk`（76,739,485 B）在清单里找不到同尺寸条目"推断分卷由多个 big 条目合并。
本次做了两种更强的检验，**均为 0 命中**：

1. **条目级**：把 6 个随机 `big_common` 分块解压成块序列，取每块的 `md5(解压后)`，在 10 个被改写过的
   基础分卷（8/115/117/119/121/122/124/125/126/127）的 234,910 条索引条目里用同法算 md5 比对 → 0 命中。
2. **块大小序列**：把分块的 ZSTD 块大小序列（含 296 B 元数据块）与每个分卷的索引 size 序列做子串匹配 → 0 命中。

已确认的事实只有：

- `big_common` 分块与 `PatchN.mpk` **都是**「`ZSTD` + u32 解压长度 + frame」的重复容器；
- `Patch126.mpk` 的 381 条索引与文件**严格连续**（381/381 首尾相接，末条结束 = 文件大小 76,739,485）；
- 其中 31 条是纯 ZSTD 块，350 条以 `02 02 02 01` 开头、每条前 296 B 是同一段元数据头。

⇒ **"分卷由分块合并"目前没有任何观测支持**，也可能只是"同一内容池的两种不同切分"。
在拿到 `fenbao_mode ≠ 0` 的真实改写过程之前，不要基于这个前提写代码。

**官方调用链（内存 report 实测原样字符串）**

```
patch_core.py:patch_logic:9873 <- patch_core.py:process:10648 <- patch_core.py:<lambda>:9764
<- threadpool.py:poll:167 <- patch_core.py:_drain_pool_results:7991 <- patch_core.py:poll:8027
<- headless_patch.py:patch_tick:499
```

> ⚠ **仍未完成**：`PatchN.mpk` 的**合并规则**（哪些 big 条目按什么顺序拼成哪一个分卷）尚未还原。
>
> ### ✅ 但基础分卷的**本地索引**已破解（2026-10-23）——足以校验完整性
>
> `LocalData\Patch\Patch.mpkinfo` **不是**增量层那种二进制索引，而是一个真正的
> **SQLite 数据库**（`SQLite format 3`，页大小 4096，8,030 页）。
>
> ```
> table info0(filepath TEXT PRIMARY KEY, value BLOB)   rootpage = 4
> table meta                                            rootpage = 2
> ```
>
> 实测 **234,910 行**，`value` 恒为 28 字节；三个字段**明文可读**：
>
> | 偏移 | 类型 | 含义 |
> |---|---|---|
> | `[8:10]` | u16 | **分卷 ID**（`Patch{ID}.mpk`），取值 **0..127** |
> | `[0:4]` | u32 | 该资源的大小 |
> | `[20:24]` | u32 | 该资源在分卷内的**起始偏移** |
>
> ### ⚠ 2026-10-24 复核：上面 `[20:24]` 的旧解释（"累积结束偏移"）是**错的**
>
> 正确关系是 **结束 = `[20:24]` + `[0:4]`**。按分卷 ID 分组取 `max(start + size)`，
> 与磁盘上 128 个分卷核对：
>
> | 分卷 | 条目 | 旧口径 `max(start)` | 新口径 `max(start+size)` | 文件实际 |
> |---|---|---|---|---|
> | Patch.mpk (0) | 4,191 | 523,713,530 | **524,474,562** | **524,474,562** ✅ |
> | Patch8 | 235 | 82,683,402 | **95,807,964** | **95,807,964** ✅ |
> | Patch54 | 1,197 | 224,842,991 | **672,533,543** | **672,533,543** ✅ |
> | Patch100 | 2,252 | 522,501,540 | 522,590,406 | 524,466,680（差 1,876,274） |
> | Patch127 | 330 | 69,626,847 | **69,645,591** | **69,645,591** ✅ |
>
> **128 个分卷里 127 个 `max(start + size) == 文件大小`（误差 0）。**
> 唯一不符的 `Patch100` 差 1,876,274 B，是**真实**的尾部填充。
>
> ⇒ 旧文所谓"差值 = 分卷填充，实测 13,953 .. 447,690,552 字节"**全部推翻**：
> 那些差值（如 `Patch54` 的 447,690,552）就是该分卷**最后一条资源自身的大小**，不是填充。
> 旧文"`Patch8` 的填充 13,124,562 恰好等于 `resource.repository_1`"也因此不成立。
>
> **这个错误导致的真实后果**：`FindTruncatedBaseShards` 用旧口径做下界，永远认为分卷"足够长"。
> 实测把 `Patch54.mpk` 截断到 300,000,000 B（真值 672,533,543）后，函数仍返回 **0 个**。
>
> 实现：`Hi3Helper.NeteaseGames.Core.NeteaseGamesBaseIndex`（零依赖的 SQLite B-tree 遍历器）
> + `NeteaseGamesGameInstaller.FindTruncatedBaseShards`。
> 未用 NuGet 的 SQLite 包：`SQLitePCLRaw 2.1.11` 带 **高危漏洞公告**，且会给 AOT 插件塞进一个无关原生库。
>
> **破坏性验证（2026-10-23）**：把 `Patch1/2/3.mpk` 各截断到 1 MB 后，
> `FindTruncatedBaseShards` 精确报出这 3 个分卷；未损坏的安装报 0 个。
>
> ⚠ 上面这条 **2026-10-24 复核时无法复现**：用同一函数、把 `Patch54` 截断到 300 MB，
> 返回 0 个。原因就是 `[20:24]` 被当成结束偏移 ⇒ 下界只有 224,842,991 B，300 MB 仍然"够长"。
> 修法与验收见 `docs/NETEASE-FIX-PLAN.md` 阶段 0.1。
>
> ### `prerequisite`（未完全逆向，已放弃）
>
> `LocalData\Patch\prerequisite`（125 字节）是三条目资源清单，
> magic `{s#`，其后为重复的 `<tag><u24 长度><ASCII 载荷><分隔字节>`，
> 三个载荷实测为 `4a/4a3101465e82fbbdc7e08f834fc82419`（38 字符）、
> `606374654f248bc73bee7857bb3819c4`、`0dededce4737238b3f51389b3c18455264`。
> 首块声明长度 52 而载荷只有 38 字符，分隔字节语义未定 ⇒ **帧格式未完全确定，已放弃实现**
> （对整体目标贡献极小，不值得继续试错）。
>
> **已实测的合并机制（内存取证）**
>
> - Python 数据类签名原样：
>   `PendingBatch(batch_id: int, fenbao_name: object = None, infos: list = <factory>, data_size: int = 0, session_ref: dict = None, is_chunked: bool = False, chunked_complete: bool = False)`
> - 会话/分批函数：`begin_fenbao_session` / `suspend_fenbao_session` / `_create_pending_batch` /
>   `_confirm_batch` / `_rollback_batch` / `_rollback_incomplete_chunked_batches` /
>   `_drain_pending_merge_fenbao` / `merge_fenbao_tmp_iter`
> - 状态字段：`_pending_batches` / `_batch_id_counter` / `_current_fenbao_name` / `_fenbao_session_active` /
>   `fenbao_migration_map`（`get_/set_fenbao_migration_map`）
> - 断言：`FenbaoTmpPatchMpk requires a non-empty fenbao_name` ⇒ **fenbao_name 是合并单元**
> - 常量：`_FENBAO_TMP_MPK_MAX_INDEX`、`FenbaoTmp_` 前缀、`%s.mpk` 模板、`%s%d.mpk`
>
> **已排除的排序假设（逐条实测，均 0 命中）**
>
> 用 `tools/re/pack_test.py` 对 117 个「首次安装后未被改写」的分卷做比对：
>
> | 假设 | 结果 |
> |---|---|
> | 按条目名字典序贪心装填 | 123 个分卷，0 匹配 |
> | 按大小升序 / 降序 | 124 个，0 匹配 |
> | 按第三字段（64 位哈希）升序 / 降序 | 124 个，0 匹配 |
> | 按名字里的全局数字索引升序 / 降序 | 123 个，0 匹配 |
> | 容量常量 512,000,000 | 分卷数 **127（与磁盘一致）**，但大小仍 0 匹配 |
>
> 关键反证：磁盘 `Patch11.mpk` 只比 500 MiB 多 **5,822 字节**，116/117 个分卷都**超过** 500 MiB，
> 而容量取 512 MiB 时只有 8 个超出 ⇒ 容量常量与顺序都还没定准。
>
> **结论**：分卷的**内容布局**已由 `Patch.mpkinfo` 完全掌握（可校验、可定位每个资源），
> 但**从远端重建**分卷所需的合并顺序仍取决于 `fenbao_migration_map` 等运行时状态。
> 还原它需要捕获一次真实的「基础包被改写」更新过程。
>
> 因此插件对含 `@bigpatchopen@` 的轮次继续**拒绝执行**（`VerifyBaseTierCompletable`）——这是安全侧。



---

## 6. 引擎载荷（mini pack）—— 实测可用

`https://h74.update.netease.com/pl/OB_release_mini_pack_version.json`
```json
{"loose_file_parent_dir":"loose_file/mini_pack_202610151759",
 "engine_version":"261014173721_4a476f25b_release",
 "data_name":"mini_pack_202610151759.data", "current_version":"202610151759",
 "parent_dir":"mini_pack/mini_pack_202610151759",
 "download_info":[{"md5":"...","size":69839049}, ... 20 块]}
```
- 文件列表：`https://h74.gdl.netease.com/loose_file/mini_pack_<ver>/md5_engine.txt`（带签名）→ 实测 200，21,811 B，**287 条**。
- 每条：`<相对路径> <64位哈希> <大小>`，路径带 `.1` 后缀表示最新一代。
- 实测 287 条中 277 条与磁盘 `Engine\` 下同名文件**大小完全一致**；
  其余 10 条是 `used_del_dir\...` 删除标记（应跳过）。
- 注意内容主机 `h74.gph.netease.com` 对该路径 404，**必须用 `h74.gdl.netease.com`**。

---

## 7. 官方启动器的安装/更新状态机（PCLauncher.exe 内嵌 Python，内存实测）

状态名（`*State::OnEnter` 实测字符串）：
```
CheckEngineVerState → CheckEngineValidState → PreDownloadEngineState
→ DownloadEngineState → DoUpdateEngineState
另外：CheckGrayState / CheckLauncherFinishState
patch 侧阶段（PATCH_* / PREPATCH_*）：
  INIT → ENV_CHECK → NETWORK → SERVER_LIST → LAUNCHER_VER → CATEGORY → DEVICE_SUPPORT
  → PATCH_MD5 → PATCHLIST → SHADER_CACHE_LIST → BIG_PATCH → CHECK_LIST → DOWNLOAD_HINT
  → DOWNLOAD → WRITE_FILES → FINISH_PATCH_MPK → DELETE_PREPATCH → FINISH
```
关键函数（实测字符串）：
`_fetch_md5` / `_fetch_patch_list` / `_big_patch_list_apply` / `_build_big_patch_tables` /
`_get_patch_file_url` / `_get_big_patch_file_url` / `_orbit_fetch_big_patch_bulk` /
`orbit_builtin_hash_verify` / `_verify_file_md5` / `_write_version_info` / `_mpk_verify_res` /
`_is_mpkinfo_db_corrupted` / `MpkinfoCorrupt.tag` / `MPK_PATCHING_SUFFIX`。

Python 侧可调用的入口（PCLauncher 调 `py_launcher` 成员）：
`check_need_launcher_patch` / `do_manul_check_from_launcher` / `confirm_download_patch` /
`manual_pause_download` / `manual_resume_download` / `download_speed_limit` / `finish_patch`。

### 记账文件（实测）
| 文件 | 内容 |
|---|---|
| `LocalData\Patch\patchmd5_windows64_normal.txt` | 远端 `OB_patchmd5` 的**原样副本**，`HEX_` 包装；第 3 行 = 已装补丁版本 |
| `LocalData\Patch\patchlist_windows64_normal.txt` | 下载目录下的 `*_mpk` 清单（`HEX_` 包装） |
| `LocalData\Patch\pkgversion` | 基础包版本 `6521279`（明文） |
| `LocalData\Patch\gameversion` | `CN_windows_<hash>_<pkgversion>_<patchversion>` |
| `LocalData\Patch\latest_engine_ver` | `202610151759` |
| `Documents\versiondata.json` | `{"package_version":6521279,"patch_version":"6589318","engine_version":"...","shader_version":"..."}` |
| `LocalData\Patch\shadercachelist_dx12.txt` | 着色器缓存清单（7,985,520 B，与远端 `/{行0}/shadercachelist_dx12.txt` **MD5 相同**） |

### ⚠ `patchmd5` 的行 0 是**内容版本**，不是时间戳（实测反例）

对比 2026-10-23 的同一时刻：

```
远端 OB_patchmd5 行0 = 20261023122834919
磁盘 patchmd5    行0 = 20261023122834919      ← 完全相同
```

官方启动器把远端清单**逐字节复制**到磁盘，行 0 因此是**内容服务器的目录名**。
写入时若把它替换成本地时间戳（旧实现 `DateTime.Now.ToString("yyyyMMddHHmmssfff")` 就是这么做的），
就会**永久丢失"这份安装来自哪个内容目录"的唯一记录**，导致依赖它的判断（如着色器清单是否需要刷新）失效。

### ⚠ `versiondata.json` 的两个版本**不能混写**（实测反例）

```
package_version = 6521279    ← 基础包版本，补丁更新不会推进它
patch_version   = "6593596"  ← 补丁版本，每次更新推进
```

旧实现把**补丁版本**同时写进 `package_version`，会写坏客户端自己的记账。更新只应改 `patch_version`。

> ### ⚠ 同类错误在 `pkgversion` 上仍然存在（2026-10-24 实测）
>
> `NeteaseGamesGameInstaller` 把 `plan.PackageVersion`（= 补丁版本 `6593596`）直接写进
> `LocalData\Patch\pkgversion`，把原来的基础包版本 `6521279` **覆盖**掉。
> 实测：造一份 `pkgversion=6521279` 的安装 → 跑 `ApplyAsync` → 文件变成 `6593596`。
>
> ⇒ 修法：`NeteaseGamesDownloadPlan` 必须把 **PatchVersion（写 `patchmd5` / `versiondata.json`）**
> 与 **PackageVersion（基础包版本，只在基础层真的变了时才写）** 分成两个字段。
> 见 `docs/NETEASE-FIX-PLAN.md` P0-4。

### `shadercachelist_dx12.txt` 必须单独规划（实测）

它**不在** patchlist 里（patchlist 只有 50 个包），但官方启动器确实维护它。
刷新条件是「本地缺失」或「内容版本与安装时记录的不同」，不能无条件重下（8 MB）。

---

## 8. 更新判定（与官方一致）

```
需要更新 ⇔ 本地 patchmd5 行3 ≠ 远端 行3
```
2026-10-23 实测：本地 `6585899`，远端 `6589318` ⇒ **确实有更新**（差 3419）。

**计划粒度**：拿远端 `/{行0}/patchlist_windows64_normal_mpk.txt` 的 `mpk_*` 段，
逐个比 `entry[-1]`（远端大小）与磁盘文件大小；不符/缺失即下载。
索引（`script/s_file/indep.mpkinfo`）也在 `mpk_*` 段里，天然被覆盖。

---

## 9. 未证实 / 待办

1. **基础包通道已定位且可用**（见 §5.4）：`bigpatchlist_windows64_normal.txt`（11.1 MB，明文）
   描述 59.37 GiB 的基础包分块，路径为 `/{big_<@base_version@>}/big_common/{条目名}`，
   随机 60 个抽样 60/60 = 200。
   **但 `PatchN.mpk` 的合并规则仍未还原**，且"分卷由多个 big 条目合并"这个前提本身
   **在 2026-10-24 的复核中未获任何支持**（两种独立比对均 0 命中，见 §5.4）。
   要还原需捕获一次真实的"基础包被改写"更新（`fenbao_mode ≠ 0` 时），当前 `fenbao_mode = 0` 无法复现。
   ⇒ 插件对含 `@bigpatchopen@` 的轮次**是否拒绝**，不应一刀切：应按 `@bigpatchmd5@` 是否变化判断。
   当前实现无条件拒绝，导致**每次更新都失败**（2026-10-24 实测），这是缺陷而非安全侧。
2. `@bigpatchmd5@` / `@allpatchlistdiffmd5@` 的**具体算法**未复现（已知是 64 位摘要，作用为分卷/全量切换判定）。
3. **`sort_map_windows64_normal.txt` / `preload_list_windows64_normal.txt` 已证实是临时文件**：
   两者远端都可下载（473,368 B / 24 B，`HEX_` 信封；解密后分别是
   `{"<资源路径>": <pack id>}` 映射表与 `[2500]` 分片号），但**真实安装目录里不存在**。
   ⇒ 官方用完即清理，插件**不应**持久化它们（旧文档把它们列为"待下载"是错的）。
4. 网易 64 位内容哈希算法未定位。已排除标准 MD5/SHA1/SHA256 的任意 8 字节窗口截断
   （对 `sort_map` 与 `preload_list` 实测无一命中）。
   **替代方案**：用 `*.mpkinfo` 的 `max(offset + size)` 做"是否截断"校验（§4）。
   （旧文写"注意填充陷阱"是基于错误的字段语义；实际上 `max(offset+size)` 与文件大小 127/128 精确相等。）
5. `Lcfg` 配置容器（`PCLauncher\config\90bb9e...`）是 OpenSSL EVP 加密，密码未解出；
   但所需端点已全部从内存字符串取得，**不影响实现**。

### 逆向方法备忘（下次继续时直接复用）

`PCLauncher.exe` 是单体 PE，内嵌静态 Python 3.14；协议逻辑在 **zstd 压缩 + 加密**的 Python 模块里
（`lazycode_zstd_dict`、`MReloadImporter.create_module_with_encrypted_file`），
**静态搜字符串只能拿到文件名，拿不到 URL 与逻辑**。有效的办法是：

1. 启动官方启动器并让它**完成一次真实更新**（会产生真实 URL）；
2. 用 `tools/re/MemRegion` 按标记 dump 该进程的**整段内存**（需管理员）；
3. 直接在 dump 里搜 URL 片段与上报 JSON —— 官方会把自己的请求 URL、
   `orbit_id`、`patch_stage`、`popup_reason`（**含 Python 调用链与行号**）全部写进内存 report。

`tools/re/` 下的 `MemScan`（窗口 dump）、`MemRegion`（整段 dump）、`inspect_region.py`
（十六进制 + ASCII 窗口）、`disasm_code.py`（3.11+ 字节码粗解）是本轮全部结论的来源。

---

## 10. 发布与部署验证（2026-10-23）

| 步骤 | 实测结果 |
|---|---|
| AOT 发布 | `dotnet publish -c Release -r win-x64 /p:PublishProfile=ReleasePublish-O2` → 进入 `Generating native code` |
| 产物检查 | `SoR.dll` 7,721,984 B，**无 `BSJB` IL 元数据**、含 `TryGetApiExport` ⇒ 真原生，未静默降级 |
| Indexer | 识别成功：`Sea of Remnants Plugin` / **0.4.0.0** / API 0.1.5.0 |
| 清单自校验 | `Assets[0]` size 与 MD5(base64) 与磁盘 DLL **完全一致** |
| 宿主加载 | 日志：`Successfully loaded plugin: Sea of Remnants Plugin ... with version 0.4.0 and standard version 0.1.5` |
| 区域初始化 | 日志：`Initializing game: 遗忘之海 - 中国大陆 ... completely initialized!`，无错误 |
| 版本读数 | Console：`GetCurrentGameVersion` 与 `GetApiGameVersion` **都是 0.0.6589318** ⇒ 判定"无需更新"，与远端一致 |
| 媒体渲染 | 背景为遗忘之海海岛画面、公告为真实公告（10月22日停服更新公告等）、横幅为官方活动图 |

> 注：旧版 0.2.2 部署后首屏会残留上一个游戏（原神）的背景与公告，**重启宿主后恢复正常**。
> 这是宿主的导航缓存行为，不是插件缺陷（AGENTS.md 坑 #9 已记录同类现象）。

### 启动契约复核

`ywzh.exe` 的嵌入清单实测为：

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

⇒ 必须 `UseShellExecute + runas`（插件已如此实现）。子系统为 GUI(2)。
默认命令行：

```
ywzh.exe --start=Python --use-pso-precache=1 --python-args=innerdesktop;script_mode#RELEASE_MODE --dx12
```

**注意**：清单是 **ASCII 编码**的，用 UTF-16 搜索会漏掉（本节结论即由 ASCII 搜索得到）。
