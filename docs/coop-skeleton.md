# 联机骨架（路线图第 1 步，原版，2026-11-08）

> 第 1 步的实现记录（2026-11-08 起）。文中的"主机"指权威这一方。现在已经确定由服务器上的无界面 ONI 当权威，所有玩家都是玩家端，总体设计以 `architecture.md` 为准。

**这一步做了**：连接服务器；加入时校验游戏版本、DLC、mod；权威端把当前世界传给加入的玩家，玩家自动读档；每个人在自己画面上看到其他人的视野框、光标和延迟。每人有自己的镜头，消息里都带 worldId（为眼冒金星的多星球预留）。之后的同步见 `coop-sync.md`。

## 组成
| 部分 | 位置 | 说明 |
|---|---|---|
| 协议 + 客户端传输层 | `src/OniCoop.Shared/` | mod 和服务端共用的源码（不单独出 DLL）。TCP 走可靠消息（加入、存档、以后的指令），UDP 走光标/视野（UDP 不通时自动改走 TCP）。内置网络模拟（`delay/jitter/loss`），心跳在后台线程，游戏加载卡住主线程时不会掉线。 |
| 服务器的网络程序（原名中转服务端） | `server/CoopRelay/`，可执行文件 `dist/coop-relay/{linux-x64,win-x64}/` | 本身不跑游戏，负责房间、口令、加入校验、转发、传世界，并把最近一次世界和备份存在 `relay-data/rooms/<房间>/`。服务器上的权威 ONI 和玩家都连它。 |
| mod | `src/OniCoop/`，安装到 `mods/Dev/OniCoop` | 没配置时（`role: off`）什么都不做。 |
| 本地双开测试 | `tools/coop-local-test.sh` | 一条命令：本机起网络程序，A 当权威读档（`--server-mode` 时是无界面服务器），B 当玩家，走模拟的 300 ms 线路。 |

## 加入流程
1. 主机和玩家都连上服务端（Hello：口令、房间、名字、角色、游戏配置清单）。
2. 服务端校验（`Compat.cs`），不一致就拒绝并列出原因：
   - 游戏 build 不同、联机 mod 版本不同 → 拒绝；
   - 主机存档用到的 DLC 玩家没启用，或主机是原版存档而玩家开着眼冒金星 → 拒绝（规则同游戏自己的 `IsCompatableWithCurrentDlcConfiguration`）；
   - 主机启用的 mod 玩家没有 → 拒绝；玩家多出的 mod、创意工坊 mod 版本不同 → 只提示。
3. 主机进入游戏后，服务端向主机要世界：主机在主线程做一次世界快照（和游戏自己存档的序列化相同，但不写文件、不截预览图、不写殖民地摘要、不强制 GC），压缩、校验和上传都在后台线程；服务端校验 SHA-256 后发给所有等待的玩家。不动主机自己的存档和"当前存档"指针。
4. 玩家收到后写到 `Documents\Klei\OxygenNotIncluded\coop\received\<房间>.sav` 并自动加载；玩家端不自动存档。
5. 进游戏后每人每秒 10 次发送视野和光标；在同一个星球（worldId 相同）就画出来。

## 实测（本机双开，c02 存档 4.9 MB，玩家 B 模拟单程 150±15 ms、UDP 丢包 2 %）
- 主机进游戏后 **23–47 秒**玩家也进入游戏：主机刚进游戏时主线程还要忙 6–10 秒才处理请求（真实联机时主机早已在玩，没有这段），传输 0.4 秒，玩家读档 13–47 秒（47 秒是两个实例同时读档抢资源）。
- **主机卡顿**（同一局里对比，c02 未压缩 50.6 MB → 压缩后 4.9 MB）：游戏自己的存档 2.5–3.5 秒；联机快照只卡主线程 **1.4–1.7 秒**，压缩 0.4–0.5 秒和上传在后台。剩下这部分是一致快照必须付出的：序列化要读 Unity 对象（非线程安全），而且游戏每帧都在改世界。存档越大越久（约 30 MB/s 未压缩数据）。定期备份到服务端可以直接上传游戏自动存档已经写好的文件，不额外卡顿；只有新玩家加入、交接模拟时才需要现做快照。
- 显示的延迟：B 约 302–310 ms，A 0–1 ms。
- 自测 `coop-relay selftest`（36 项，无需游戏）全部通过：口令、协议版本、build/mod/DLC 校验、先到的玩家等待主机、并发加入只存一次档、3.5 MB 存档逐字节一致、UDP/TCP 两种光标路径、通用转发通道、模拟 300 ms 线路的 RTT、主线程冻结 5 秒不掉线、主机离开。

## 用法

### 服务端（家里的 Linux 服务器）
```
scp dist/coop-relay/linux-x64/coop-relay 服务器:/opt/oni-coop/
# 在服务器上
cd /opt/oni-coop && ./coop-relay serve --port 47810 --token <口令> --data /opt/oni-coop/data --log /opt/oni-coop/relay.log
```
### 实际部署（2026-11-09）：服务器上的 `~/oni-coop/`，通过 SSH 隧道访问
所有东西都放在服务器用户目录下的一个文件夹里：

| 文件 | 用途 |
|---|---|
| `coop-relay` | 中转服务端，自带运行时的 linux-x64 单文件版（服务器上只有 .NET 8） |
| `run.sh` | 启动脚本：端口 47810（TCP + UDP），监听地址从 `bind` 读（没有这个文件时是 `127.0.0.1`），令牌从 `token` 读 |
| `token` | 房间令牌（权限 600），玩家的 `-coop-token` 或 `config.json` 里的 `token` 填它 |
| `relay-data/` | 每个房间最新的世界存档 |
| `relay.log` | 日志 |
| `oni-coop-relay.service` | systemd 用户服务，已经链接到 `~/.config/systemd/user/` 并启用；用户 linger 已开，开机自动启动，崩溃 5 秒后自动重启 |
| `README.txt` | 状态、重启、卸载的命令 |

- 更新：`gzip -1 -c dist/coop-relay/linux-x64/coop-relay | ssh s 'gunzip > ~/oni-coop/coop-relay.new && chmod +x ~/oni-coop/coop-relay.new && mv ~/oni-coop/coop-relay.new ~/oni-coop/coop-relay'`，然后 `ssh s systemctl --user restart oni-coop-relay`。
- SSH 隧道只是给开发测试用的临时办法（`ssh -N -L 47810:127.0.0.1:47810 s`，游戏里的服务器地址填 `127.0.0.1:47810`；隧道只转 TCP，游戏会自己从 UDP 退回 TCP）。
- 正式联机打算用内网穿透或开放公网防火墙，到时的改法：
  - **内网穿透**：比如服务器上跑 frpc，把 **TCP 和 UDP 的 47810** 都转到本机 `127.0.0.1:47810`，`bind` 保持默认。
  - **路由器端口转发或开防火墙**：`echo 0.0.0.0 > ~/oni-coop/bind`，开放 TCP 和 UDP 47810。
  - 两种情况改完都要执行 `systemctl --user restart oni-coop-relay`，玩家的 `server` 填公网地址加端口。
  - 只开 TCP 也能玩：光标和视野框改走 TCP，丢包时会卡一下。
  - 对公网开放后，唯一的门槛就是令牌，令牌要保管好。
- 本机双开测试走服务器：`tools/coop-local-test.sh --remote s --sim off …`。会在本机 47811 端口开隧道，并从服务器读取令牌和日志。

**真实线路实测**（这台电脑到服务器往返约 247 ms，没有丢包；两份游戏都在这台电脑上，经同一条隧道）：
- **进入游戏**：
  - 上传世界：4.88 MB 用 16.7 s，约 0.3 MB/s，受这边上行带宽限制；
  - 下载：约 2.4 s；读档：约 14 s；
  - 从主机进入游戏到玩家进入游戏，共约 33 s。
- **同步**：
  - 自动测试全部通过：指令、打印舱、物体、建筑、设置、全局状态，两边都没有报错；
  - 复制人和小动物 117 个都对上，平均误差 0.001–0.03 格；
  - 抖动的 98% 分位约 160–190 ms（本机模拟时约 30 ms），玩家端自动把缓冲调到约 0.41–0.44 s，迟到的关键帧 0.2%。
- **流量**（约 3.5 分钟）：
  - 复制人和小动物 666 KB；
  - 物体 346 KB；
  - 全局状态 97 KB；
  - 建筑动画 63 KB；

  合计约每秒 6 KB，另有格子和管道的同步。

**真实线路复测（2026-11-14）**，加入画面同步各项（外观、建筑状态、反馈、流星雨等）之后，无界面服务器和玩家端都在这台电脑上，经隧道走服务器上的网络程序（往返约 250 ms，无丢包；服务器到玩家要经过两段线路，比正式部署多一段）：
- **进入游戏**：世界 4.9 MB，上传 4.0 s、下载约 1.5 s、读档 12–15 s。服务器断开再连回后，玩家端自动重新拿世界，正常。
- **同步对比**（指令、物体、建筑、设置、全局、状态、报告、外观、建筑状态、流星）：6 个对比时刻全部一致，117 个复制人和小动物平均误差 0.000 格；流星位置误差 0.00 格，导弹拦截 4 次。面板设置从玩家点下到显示服务器结果约 0.65 s（两段线路各一个来回）。
- **流畅度**（运动测试各阶段）：断粮只在频繁切换速度时出现（0.5%），跳变 0；玩家端最慢一帧 26–100 ms。缓冲 0.4–1.0 s（同一台电脑模拟线路时 0.24–0.48 s），加上最快一条消息本身约 0.25 s 的路程，玩家看到的比服务器晚约 0.65–1.25 s。
- **流量**：玩家端收 21–47 KB/s（1x 约 21，3x 缩到最远约 47），发 0.2–0.4 KB/s。
- **找到并修掉**：服务器每次存档后把存档（约 5 MB）作为备份上传给网络程序，原来一次性塞进发送队列，之后的游戏消息都排在它后面；3x 和存档阶段，消息比最快一条晚到的 p98 到 1.1 s、最大 1.6 s。现在备份分 16 KB 小块，只在没有游戏消息要发时、按每秒约 96 KB 发（5 MB 约 50 s）；有人加入时要的世界照常全速发（先把没发完的备份发完，网络程序同一时间只收一份）。存档阶段的 p98 从 476 ms 降到 134 ms，最大从 908 ms 降到 337 ms；3x 阶段 p98 从 1093 ms 降到 616 ms（剩下的主要是这台笔记本上服务器在 3x 时自己的卡顿）。正式部署时服务器和网络程序在同一台机器上，备份不走外网。

### 自己部署
路由器转发 **TCP 和 UDP 的 47810**（只开 TCP 也能用：光标改走 TCP，丢包时会卡顿）。长期运行可用 systemd：
```
[Unit]
Description=ONI coop relay
After=network-online.target

[Service]
WorkingDirectory=/opt/oni-coop
Environment=COOP_TOKEN=<口令>
ExecStart=/opt/oni-coop/coop-relay serve --port 47810 --data /opt/oni-coop/data --log /opt/oni-coop/relay.log
Restart=on-failure

[Install]
WantedBy=multi-user.target
```
限制：每个服务器最多 4 个房间、每房间 8 人、存档 ≤ 128 MB、每人 UDP ≤ 60 包/秒、30 秒没有任何流量就断开。

### 玩家（每个人）
1. 把 `OniCoop` 文件夹放进 `Documents\Klei\OxygenNotIncluded\mods\Dev\`，在游戏的模组菜单里启用。
2. 编辑 `mods\Dev\OniCoop\config.json`（第一次启动会自动生成）：
   ```json
   { "role": "guest", "server": "服务器地址:47810", "token": "<口令>", "room": "default", "name": "你的名字" }
   ```
   所有玩家都写 `"guest"`，停在主菜单，等服务器把世界传过来。服务器上的 ONI 用 `-coop-role server -coop-load <存档> -batchmode -nographics` 启动；`"host"` 只用于本机开发测试。
3. 两边的游戏版本、DLC 开关、mod 列表要一致（不一致时屏幕上方会写原因）。

也可以用启动参数临时覆盖：`-coop-role`、`-coop-server`、`-coop-token`、`-coop-room`、`-coop-name`、`-coop-load <存档>`（主机自动读档）、`-coop-sim delay=150,jitter=15,loss=0.02`、`-coop-window x,y,w,h`、`-coop-shots <秒>`、`-coop-wander`、`-coop-quit-after <秒>`、`-coop-savebench`（主机进游戏 20 秒后对比两种存档的耗时）。

### 本地测试
```
./build.sh OniCoop                      # 编译并安装 mod（游戏需关闭）
dotnet run --project server/CoopRelay -c Release -- selftest
tools/coop-local-test.sh [--sim off] [--quit-after 120]
```
`coop-relay bot --server 地址 --token 口令 --name Bot --out x.sav` 可以当一个不跑游戏的第三名玩家（下载世界后绕圈发送光标）；`--manifest mods/Dev/OniCoop/manifest.b64` 让它冒充本机的游戏配置。

## 排查：弹窗也进日志
弹窗只出现在屏幕上，读日志看不到（例如双开时的"无法开始新游戏"，日志里只有一行 `Cannot initialize filesystem. [WorldGenFilesFail]`）。联机开启时 mod 会记录（`DialogWatch.cs`）：
- 所有提示框（`ConfirmDialogScreen`、`InfoDialogScreen` 等，经 `KModalScreen.OnShow`）：`[Coop] dialog: 类型: 标题 | 正文 | 按钮`；
- 游戏的崩溃/报错窗口（`KCrashReporter.ShowDialog`，包括读档期间延迟弹出的）：报错内容 + 调用栈；
- 游戏退出时的调用来源（`App.QuitCode`）；
- 测试模式（开了 `-coop-shots`）下再各截一张图：`coop/shots/<名字>_dialogNN_<类型>.png`。

```
grep -a "\[Coop\] dialog" Documents/ONI-local-test/instanceA.log
```

## 开发中踩到的坑
- 游戏程序集把很多类型放在全局命名空间（`Wire`、`Timer`……），它们比文件顶部的 `using` 优先 → 所有源文件的 `using` 都写在 `namespace { }` 里面。
- 原版存档的 DLC 列表里有一个空字符串（表示本体），校验时要跳过。
- `mods.json` 里本地 mod 的 `label.version` 必须是游戏算出的值（目录名哈希），否则游戏当成"已更新"，下次启动自动禁用。
- 游戏每次窗口尺寸变化都会写进玩家的画面设置 → 测试窗口模式下 mod 拦截了 `GraphicsOptionsScreen.OnResize`。
- 双开时两个实例同时启动会抢 `WorldGenDataSave.worldgen`，主菜单报"无法开始新游戏"、卡住 → MultiLaunch 等 A 的日志出现 `World settings reload complete` 再启动 B。
- 游戏读档要一分钟左右，期间主线程不动：心跳和 RTT 计时都放在网络线程。`Time.unscaledTime` 在加载帧里停在加载开始时刻，计时改用 `realtimeSinceStartup`。
- 从 WSL 启动的 Windows 程序会继承输出管道，导致脚本等到游戏退出才返回 → 游戏用 ShellExecute 启动，服务端用 `Start-Process` 启动。

