# 实测：官方启动器首次运行行为（2026-10-22）

> 用 `tools/probe-fresh-install.ps1 -WatchExisting` 观测官方启动器的一次真实运行。
> 结论**推翻了**此前基于静态分析的判断，务必以本文为准。

---

## 1. 启动器没有"选择安装目录"

实测：启动器**没有路径选择项，也没有"开始更新"按钮**。启动后**直接开始下载**，
下了约 800 MB 后显示"可以开始游戏"。

⇒ 无法通过"指向空目录"来观测全新安装。它会直接操作 `setting.ini` 里记录的安装目录
（`InstallPath=D:\ywzh\ywzh_extreme`）。

---

## 2. 决定性发现：基础包**可以下载**

本次运行下载并落盘了 **20 个包**（18:21，全部 `LastWriteTime` 一致）：

| 包 | 大小 | 类型 |
|---|---|---|
| `Patch115.mpk` | 500.1 MB | **基础包** |
| `Patch117.mpk` | 501.5 MB | **基础包** |
| `Patch119.mpk` | 455.8 MB | **基础包** |
| `Patch121.mpk` | 272.9 MB | **基础包** |
| `Patch122.mpk` | 170.2 MB | **基础包** |
| `Patch124.mpk` | 92.4 MB | **基础包** |
| `Patch125.mpk` | 83.6 MB | **基础包** |
| `Patch126.mpk` | 70.6 MB | **基础包** |
| `Patch127.mpk` | 66.4 MB | **基础包** |
| `Patch8.mpk` | 91.4 MB | **基础包** |
| `ResourceExA10.mpk` | 153.5 MB | 引擎资源包 |
| `ResourceExA13.mpk` | 61.0 MB | 引擎资源包 |
| `ResourceExA14.mpk` | 51.6 MB | 引擎资源包 |
| `ResourceExA15.mpk` | 94.6 MB | 引擎资源包 |
| `Patch1000-1003.mpk` | 15.0 / 13.7 / 20.2 / 13.7 MB | 补丁分片 |
| `Patch2000.mpk` | 14.9 MB | 补丁分片 |
| `Patch3000.mpk` | 43.2 MB | 补丁分片 |

**⇒ 之前"基础包全部 404、无法下载"的结论是错的。** 只是**下载地址不在我穷举过的那些路径**。

---

## 3. 下载机制的线索（来自观测到的临时文件）

```
LocalData\Patch\tmp_file\com.temp.orbit\<16位hash>-big_common_Char_726[-1--1].tmp
LocalData\Patch\tmp_file\com.temp.orbit\0786f6fa00605b7e-mpk_medium_s_file.mpkinfo_0_8395844[0-8395844].tmp
LocalData\Patch\tmp_file\com.temp.orbit\07d82e4b900f3f25-mpk_script_Patch1000.mpk_12582912_15767230[12582912-15767230].tmp
```

可读出：

- 临时目录：`LocalData\Patch\tmp_file\`，下载器子目录 `com.temp.orbit\`
- 文件名格式：`<hash>-<section>_<name>_<start>_<end>[<start>-<end>].tmp`
  - `mpk_script_Patch1000.mpk_12582912_15767230` ⇒ **分块续传**，从 12,582,912 到 15,767,230
  - `[-1--1]` 表示未知范围（`big_common_*` 这类资源组）
- `big_common_Char_726` / `big_common_Sounds_507` / `big_shader_dx12_914`
  ⇒ **bigpatch 通道**，按资源组切块，不是按包名

其他观测到的文件：

- `LocalData\Patch\PreDownload.mpk` + `PreDownload.mpkinfo`（+ `-shm` / `-wal`）
  ⇒ **预下载数据库**（LevelDB），官方用它记录"已下载待应用"的内容
- `LocalData\Patch\switch_to_launcher` ⇒ 状态标记
- `LocalData\Patch\tmp_file\patchlist_windows64_normal_mpk.txt` ⇒ **`_mpk` 清单只在临时目录出现过**

---

## 4. 两个版本号是不同维度（关键，之前一直搞错）

官方启动器跑完后，磁盘上是这样：

```
pkgversion        = 6521279          ← 基础包 / 客户端版本
gameversion       = CN_windows_0a66d2e2a_6521279_6582435
patchmd5 第 4 行   = 6582435          ← 补丁版本（官方判定依据）
metadata.res_version = 6582435
```

| 值 | 含义 | 出处 |
|---|---|---|
| **6521279** | 基础包 / 客户端版本 | `gameversion` 第 4 段、`pkgversion` |
| **6582435** | 补丁版本 | `gameversion` 第 5 段、`patchmd5` 第 4 行 |

**官方判断"要不要更新"用的是 6582435（补丁版本），不是 `pkgversion`。**

⇒ 插件之前让 `pkgversion` 优先是**错的**，会在每次官方启动器运行后误报永久更新。

---

## 5. 远端清单会滞后于实际下发的包

实测矛盾：

| 来源 | `Patch1000.mpk` 大小 |
|---|---|
| 磁盘实际 | **15,767,230** |
| 官方写的本地清单 | **15,767,230** ✅ 一致 |
| 远端 `patchlist_..._mpk.txt` | 13,283,727 ❌ 旧值 |

⇒ 远端清单**不可作为"是否需要下载"的唯一依据**。正确做法：
**本地清单 + 磁盘大小**判定，远端只用于发现新版本号。

---

## 6. 官方写入的 patchmd5 与远端逐字节一致

实测官方落盘的 `patchmd5_windows64_normal.txt`（HEX_ 加密）解密后，四行头部与远端
`OB_patchmd5_windows64_normal.txt` **完全相同**：

```
[0] 20261022122731826
[1] 16867045629764259494
[2] 0
[3] 6582435
```

⇒ 印证了 `launcher-version-logic.md` 的结论：官方就是把远端清单原样存下来。

---

## 7. 官方合成的本地 patchlist

官方写的 `LocalData\Patch\patchlist_windows64_normal.txt`（9.3 MB，HEX_ 加密）**不是**
远端任何一个文件的原样拷贝，而是**合成**的：

| 字段 | 值 |
|---|---|
| `mapping_version` | `20261021113421287`（= 远端 `@base_version@`） |
| `common` | 230,421 条（远端 `_mpk` 版是 230,412，远端完整版是 509,184） |
| `windows64_common` | 2,026 条 |
| `mpk_script` / `mpk_indep` / `mpk_medium` | 16 / 2 / 32 = **50 个包** |
| `mpkexclude` | 778 条 |
| `mapping_ignorelist` | 33 条 |

⇒ 官方的本地清单 = **`_mpk` 清单 + `mapping_version` + `mapping_ignorelist`**，
且 `common` 只保留与当前包相关的子集。这是官方"记账"用的工作副本。

---

## 8. 对插件的结论

1. **本地版本**读 `patchmd5` 第 4 行（补丁版本），`pkgversion` 只作回退 —— 已修。
2. **Update 判定**用「本地清单记录的包大小 vs 磁盘实际大小」，不依赖远端清单的哈希 —— 已修。
3. **基础包可下载**，但下载通道（bigpatch / orbit 下载器）尚未定位，
   静态穷举 `mpk_script` / `mpk_indep` / `mpk_medium` / `bigpatch` / `base` / `full` 等 section 全部 404。
4. 全新安装仍不可行，但**原因变了**：不是"包不存在"，而是**下载通道未定位**。

---

## 9. 下一步（若继续）

- 抓包拿 `Patch115.mpk` 的真实 URL（`pktmon` 在用户环境不可用，需换 Wireshark / Fiddler）
- 或解析 `PreDownload.mpkinfo`（LevelDB），里面记录了待下载对象与其来源
- `tmp_file\com.temp.orbit\` 的 `<hash>` 前缀可能就是内容寻址的 key
