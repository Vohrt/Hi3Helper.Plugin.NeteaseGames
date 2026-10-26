# 厂商分卷布局规律（2026-10-26 实测）

> **背景**：插件原先由 `NeteaseGamesBaseLayoutPlanner` 按 524,288,000 B 自行装填分卷，
> 结果**游戏认不出**——启动即重下 60 GB。本文记录实测出的厂商真实布局规则。
>
> 对照样本：`C:\Program Files\ywzh_launcher\ywzh_extreme`（官方启动器装的）与
> `D:\yiwangzhihai`（Collapse 插件装的）。

---

## 结论

**厂商分卷 = chunk 池里若干「完整 chunk」的顺序拼接，chunk 之间无空隙，分卷之间也不为空。**

三条硬性质（对官方 125 个分卷全部实测成立）：

| 性质 | 实测 |
|---|---|
| 分卷内资源**首尾相接** | 125/125 分卷 `offset[i] + size[i] == offset[i+1]` |
| 分卷内**无尾部填充** | `max(offset+size)` == 文件长度，125/125 |
| 分卷边界**只切在 chunk 边界或 chunk 内部** | shard 1 是 89 个完整 chunk、shard 125 是 10 个完整 chunk；shard 0 的 1832 个资源里 1823 个是 88 个完整 chunk，剩余 9 个是被分卷边界截断的 chunk 尾部，由下一个分卷续接 |

**资源总量守恒**：索引 232,450 个路径**全部**在 `big_line0` chunk 池里（池多出的 3 个正是三个松散文件）。

## 与插件旧实现的三处差异（都是游戏拒绝的原因）

| 项 | 官方 | 插件旧实现 |
|---|---|---|
| 分卷数 | **125**（0–125） | 123（0–122） |
| 资源→分卷/偏移映射 | 权威 | **0% 相同** |
| 索引 `hash` 字段 | 232,450 条**全部有值** | **全部写 0** |

`hash` 很可能才是主因：游戏按 hash 校验每个资源，全 0 等于"全部无效"。
hash 的来源是远端 patch list（实测 **232,449/232,450** 与官方索引一致；唯一例外 `essential`）。

## 校验方法（可复算）

把 `D:\yiwangzhihai` 按官方索引重排后：

```
文件数          官方 509 / 本地 509
路径集合        完全相同
大小不同        0
资源位置        232,450 / 232,450 的 (shard, offset, size) 相同   ← 之前是 0%
抽样 300 资源   逐字节相同 300/300
关键文件 MD5    Patch.mpkinfo / ResourceExA.patchinfo / Patch.mpk /
                Patch125.mpk / ResourceExA16.mpk 全部相等
```

重排后**游戏可以正常进入**。

## 着色器层

同一套规律。官方 17 个分卷（`ResourceExA.mpk` + `ResourceExA1..16.mpk`），
索引是 `ResourceExA.patchinfo`。

⚠ **它的表名是 `info0`，不是 `info1`**（早期笔记与代码里的 `ShaderTableName = "info1"` 是错的）。
官方文件与插件产物都是 `info0` + 173,861 行。

## 插件需要改的地方

1. **`NeteaseGamesBaseLayoutPlanner` 改为「先加后判」**——当前实现是「先判空间够不够，不够就换卷」，
   厂商是「先放进去，放完发现超了就换下一卷」。一字之差，分卷数和边界全变：

   ```csharp
   // 现在的（错）：
   if (offset > 0 && offset + chunk.Size > ShardCapacity) { shard++; offset = 0; }
   // 应该是：
   if (offset > 0 && offset >= ShardCapacity) { shard++; offset = 0; }
   ```
   即**只在已经装够一个分卷之后才切**，保证 chunk 永不被切断。
2. **`WriteBaseIndex` 必须写真实 hash**——从 patch list 取（`NeteaseGamesPatchList.LooseFiles`），
   不能写 0。
3. **`ShaderTableName` 改为 `info0`**。
4. **松散文件落地到 `LocalData\Patch\`**，不是游戏根目录（`prerequisite`、`Misc/lazycode_zstd_dict`、
   `4a/4a31…`）。
5. **补 `Engine/md5_engine.txt`**（引擎清单，来自 mini pack 的 `loose_file/mini_pack_*/engine/`）。

## 分卷边界的切分规则

**⚠ 这一节是本文最不确定的部分，两次推测都被证伪，不要把下面的规则当成结论。**

已知的硬事实（125 个分卷全部实测）：

| 观察 | 值 |
|---|---|
| 分卷数 | 125（0–125） |
| 载荷/文件长度范围 | 60,029,811 – 638,199,016 B |
| 超过 524,288,000 B 的分卷 | 117 / 125 |
| 精确等于 524,288,000 B 的分卷 | **0** |
| 分卷内资源是否首尾相接 | **是，125/125** |
| 分卷内是否有尾部填充 | **否，125/125** |

**已证伪的两个推测：**

1. ~~「按 chunk 整块装填，装完超了才切」~~ —— 对不上。官方 shard 0 的 1832 个资源里，
   同一个 chunk 的资源**被打散穿插**：`Char_4` 出现 **61 次**、`r_7871` 24 次、`r_2221` 18 次。
   按整块装填只会让每个 chunk 连续出现一次。
2. ~~「先判后加 vs 先加后判」~~ —— 两种规则模拟出来的分卷数分别是 123 / 120，
   与官方的 125 都不符，且没有**任何一个**分卷的资源序列能对上。

**仍然未知：** 资源在分卷内的排列顺序由什么决定（既不是路径、也不是 hash、
也不是 chunk 池顺序、也不是索引 rowid 顺序）。厂商很可能是按一套本地的
「资源 → 分卷槽位」映射表写的，而那张表不在任何可下载的清单里。

⇒ **结论：厂商布局无法从公开数据推导。** 全新安装要复现它，只有两条路：
从官方启动器装的副本里抄（不可接受），或者接受布局不同并找出游戏到底校验什么。

## 尚未解决

- **`essential` 的 hash** 与 patch list 不一致（其余 232,449 条全部一致），来源未定位。
  它是 `big_common/essential_0|1` 里那个被反复重切的单资源 chunk，三代 MD5 互不相同。
- 分卷切分用的 chunk 池**代次**要与目标轮次一致：`big_{行0}` 与 `big_{@base_version@}` 的
  chunk 大小不同，用错代次切出的分卷数会不同（实测 125 vs 128）。

## 复现工具

`repack.py` / `repack_shader.py`（在 `%TEMP%\ntprobe\`）：
读官方索引 `(shard, offset)`，把本地已有资源重新放置，不重新下载。
