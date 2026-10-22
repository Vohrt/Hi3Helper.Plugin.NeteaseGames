# 观测官方启动器「全新安装」行为

> 目标：确定基础包 `Patch.mpk` / `Patch1..123.mpk` / `ResourceExA..` 从哪来。
> 这些包 59 GB，占完整安装 92%，内容服务器全部 404，任何清单都不描述它们。
>
> **不会**真的下载 59 GB：观察 1~3 分钟（够看清请求模式和落盘文件名）就中断。

---

## 准备：选一个空的观测目录

要求：
- **不同盘或不同父目录**，绝不能指向 `D:\ywzh\ywzh_extreme`（会破坏现有安装）
- 剩余空间 ≥ 10 GB（够看清模式，不必装完）
- 路径不要有空格和中文

本脚本默认用 `E:\sor-probe`，如果没 E 盘就改成你有空间的盘。

---

## 步骤 1：跑观测脚本（约 3 分钟）

在 **管理员 PowerShell** 里执行（脚本需要 pktmon 和文件监控权限）：

```powershell
cd "C:\Users\Voheart\source\repos\Collapse\Hi3Helper.Plugin.NeteaseGames\tools"
.\probe-fresh-install.ps1 -ProbeDir "E:\sor-probe" -WatchExisting
```

`-WatchExisting` 会同时监视现有安装 `D:\ywzh\ywzh_extreme`，如果启动器想往那里写，脚本会立刻用红色标出来（建议加上）。

默认 180 秒后自动停止；想改时长用 `-Duration 120`。

脚本会：
1. 建空目录，记录初始状态
2. 启动网络抓包（`pktmon`，只抓 443）
3. 每 2 秒快照一次目录（文件名 + 大小）
4. 提示你操作官方启动器
5. 你按 `Enter` 结束后，输出三份报告到 `tools\probe-output\`

---

## 步骤 2：在官方启动器里操作（脚本运行期间）

1. 打开 `D:\ywzh\ywzh_launcher\Win64Release\PCLauncher\PCLauncher.exe`
2. 让它**指向观测目录** `E:\sor-probe`
   - 如果界面有"选择安装位置/更改路径/浏览"，选 `E:\sor-probe`
   - 如果没有，跳过这步（脚本仍能抓到网络请求，只是文件落在原目录，**不要让它真下 59 GB**）
3. 点"安装 / 开始安装 / 下载"
4. **观察 1~3 分钟**，看到它开始下载后就够了
5. 回到 PowerShell 按 `Enter` 结束

> ⚠️ 如果发现它要往 `D:\ywzh\ywzh_extreme` 里写（而不是观测目录），
> **立刻关闭启动器**并回来按 Enter。脚本会对比并报告这个情况。

---

## 步骤 3：把报告发我

脚本输出在：

```
tools\probe-output\
  file-timeline.txt      目录里出现了哪些文件、什么顺序、多大
  network.txt            请求了哪些域名/路径
  summary.txt            汇总判断
```

把这三个文件的内容贴给我（或者告诉我生成了，我自己读）。

---

## 我要从里面找什么

| 观察点 | 说明 |
|---|---|
| **是否出现 `Patch.mpk`** | 出现了 ⇒ 基础包来自某个可下载 URL，我能从抓包里拿到地址 |
| **请求的域名** | 除了 `h74.gph.netease.com`，是否还有别的 CDN（`cdn_map` 配的） |
| **请求路径模式** | 是否形如 `/{version}/{name}`、`/base/...`、`/full/...` |
| **是否先下一个小清单** | 可能有个"全量清单"文件，拿到它就能推出所有包名 |
| **`setting.ini` 变化** | `InstallResType=extreme` 是否影响分支 |
| **是否写 `_BinPatch_Dl`** | 官方临时目录名（二进制里见过） |

只要拿到**基础包的下载 URL 形状**，我就能把全新安装实现出来。
如果抓包显示基础包根本不走 HTTP（比如从内置资源释放），那结论就是"确实做不到"，也有定论了。

---

## 备选：只抓网络（更省事）

如果不想让启动器碰任何磁盘，可以只做网络观测：

```powershell
# 管理员 PowerShell
pktmon start --capture --pkt-size 128 -f C:\temp\probe.etl
# → 打开启动器，点安装，观察 1~2 分钟，关闭启动器
pktmon stop
pktmon etl2txt C:\temp\probe.etl -o C:\temp\probe.txt
```

然后把 `C:\temp\probe.txt` 里所有含 `h74`、`gph`、`netease` 的行发我。
缺点：看不到落盘文件名，但足以判断基础包是否走 HTTP。

---

## 安全说明

- 脚本**只读**现有安装（`D:\ywzh\ywzh_extreme`），不做任何修改
- 观测目录是新建的，可随时整个删除
- 抓包只记录 TLS 连接的**目标地址**（内容加密，看不到明文），不含任何凭据
- 全程不需要登录游戏账号
