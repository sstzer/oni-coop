# 调研笔记：ONI 渲染管线与"第二摄像机"可行性

> 2026-11-09 补注：这是早期"一份模拟、给每人串流画面"方向的调研，这个方向已经放弃，原因见 `architecture.md`。保留下来，是因为关于剔除和可见区域的结论在"按视野同步"时仍然有用。

> 对象：Oxygen Not Included，Unity **6000.3.5f2**，D3D11，内置渲染管线；
> `Assembly-CSharp.dll` 内部 build 号 **744825**（`KMod/Mod.cs:392`）。
> 反编译：`ilspycmd 11.2` → `decompiled/`（已 gitignore，不入库）。下文 `文件:行号` 均指 `decompiled/` 下的路径。

## 0. 结论速览

| 子系统 | 剔除依据 | 在第二摄像机（主视野外）里的预期表现 |
|---|---|---|
| 自然地块 `GroundRenderer` | `GridVisibleArea.GetVisibleArea()` → `Game.MainCamera` | **不绘制**（mesh 已重建，但只对主视野 chunk 调 `DrawMesh`） |
| 建筑方块 `BlockTileRenderer`（砖、窗、网格砖……） | 同上 | **不绘制** |
| KAnim（建筑、复制人、小动物、植物、物品、门……几乎所有精灵） | `KBatchedAnimUpdater` 32×32 chunk 可见性，基于 `Camera.main` 视口 ±4 格 | **完全不画**（不可见控制器写入 `numFrames=0`），且不推进帧 = 冻结 |
| KAnim batch 是否提交 | `KAnimBatchManager.UpdateActiveArea`（同一个 chunk 范围） | 视野外 batch 不提交 `DrawMesh` |
| 液体 `WaterCubes` | **无剔除**：全世界一张 quad，采样 sim 推送的全图液体纹理 | 应该能画出来；但主管线里液体是命令缓冲画进 MRT 的，独立摄像机需要自己把 `Water` 层加进 cullingMask。shader 可能采样屏幕空间的 `MRT*_Copy`，可能出现错位 |
| 落水粒子 `FallingWater` | **无剔除**，全部粒子 | 能画出来 |
| 属性纹理 `PropertyTextures`（雾战、气体颜色/压力、挖掘进度、温度、世界光照…） | CPU 只更新 `Camera.main` 视口 ±16 格的子区域 | 主视野外是**旧数据**；本次会话从未看过的区域 = 初始 0（雾战黑） |
| 属性纹理中 sim 外部推送的（Liquid / Flow / LiquidData / MaterialData / ExposedToSunlight） | 每帧全图 `LoadRawTextureData` | 正常 |
| 光源 `LightBuffer` | `GetVisibleAreaExtended(最大光照半径+4)`，且 `DrawMesh` 指定 `lightBufferCamera` | 视野外光源不画；光照缓冲是**屏幕空间** RT，只对应主视野 |
| 光照合成 / 日夜调色 / 覆盖层 | 挂在 baseCamera / overlayCamera 上的 `OnRenderImage`、`OnPreCull` 全屏 quad | 新摄像机默认都没有 → 画面是"未打光"的原始颜色 |

**一句话**：ONI 的可见性系统全部绑死在 `Camera.main` 一个视口上。新建一个摄像机，能"看到"的只有不做剔除的东西（液体、落水），地块和所有 KAnim 都会缺失。
游戏自带的延时摄影（Timelapser）**不是离屏摄像机**，而是把主摄像机挪过去、等一帧让所有剔除系统更新、再手动 `Render()`，之后挪回来。

## 1. 摄像机栈（`CameraController.OnPrefabInit`，`Assembly-CSharp/CameraController.cs:296-411`）

`CameraController` 的 transform 在 z=-100，所有摄像机都是它的子节点、本地坐标为 0，正交投影，`OrthographicSize` setter 会同时改 `cameras` 列表里的每一个（`:160-175`）。

按 depth 从低到高（d = baseCamera.depth）：

| 名称 | depth | 作用 |
|---|---|---|
| `Infrared` | d-1 | 红外覆盖层，cullingMask=0，靠 `Infrared` 组件自己画 |
| `Light Buffer` | d-1 | 只渲 `Lights` 层到屏幕尺寸的 `LightBuffer.Texture`（ARGBHalf） |
| `baseCamera MRT` | d-1 | **真正渲染世界几何的摄像机**。`MultipleRenderTarget.SetupProxy` 在 Start 之后一帧才创建（`MultipleRenderTarget.cs`），CopyFrom(baseCamera)，去掉 `Water` 层，`SetTargetBuffers` 输出到 MRT0/MRT1（屏幕尺寸） |
| `baseCamera` | d | cullingMask 被清为 0，只在 `OnPreCull` 画一张全屏 quad（MRT0）。挂了命令缓冲 `MRTLiquidInterception`（`BeforeForwardOpaque`：复制 MRT → `MRT0_Copy/MRT1_Copy`，再把 `WaterCubes` 渲进 MRT），`OnRenderImage` 链：`LightBufferCompositor`（打光、温度粒子等）→ `CameraRenderTexture`（写入全局 `_LitTex`） |
| `SimOverlayCamera` | d+1 | `SimDebugView` 层 → `_SimDebugViewTex` |
| `timelapseCamera` | d+2 | Clone(overlayCamera)，挂 `Timelapser`（不在 `cameras` 列表里） |
| `Overlay`（原 `Camera.main`，tag 已改成 Untagged） | d+3 | `CameraReferenceTexture` 把 `_LitTex` 画成全屏 quad，再叠 `PlaceWithDepth/Overlay/Construction` 层；`ColorCorrectionLookup`（日夜 LUT）、KAnim 后处理 |
| `overlayNoDepth`（**tag=MainCamera，即 `Camera.main`**） | d+4 | `Overlay/Place` 层，路径、范围可视化等 |
| `uiCamera` | d+5 | UI |
| `timelapseFreezeCamera` | d+8 | 截图期间显示冻结画面，平时 disabled |

要点：
- `Camera.main` 是 `overlayNoDepth`；`Game.MainCamera`（`Game.cs:668`）缓存了 `Camera.main`。所有剔除都基于它的视口。
- MRT0/MRT1、LightBuffer、`_LitTex` 全是 **`Screen.width × Screen.height` 的屏幕空间 RT**，每帧只有一份 → 想给第二视角复用完整光照管线，必须复制整套 RT 和后处理，或者分时复用。
- `Graphics.DrawMesh(..., camera: null, ...)` 会在当帧**所有摄像机**里画，包括帧末手动 `Camera.Render()` 的摄像机 → 只要让对应系统"提交"了视野外的 mesh，新摄像机就能画出来。

## 2. 可见区域

### 2.1 `GridVisibleArea`（`Assembly-CSharp/GridVisibleArea.cs`）
- `GetVisibleAreaExtended(padding)`（`:140-163`）：用 `Game.MainCamera.ViewportToWorldPoint` 求视口四角，裁剪到当前 world（`CameraController.GetWorldCamera`）。纯静态函数，没有"传入摄像机"的参数。
- `CameraController.VisibleArea = new GridVisibleArea(8)`，在 `CameraController.Update` 末尾刷新（`:1253`），保存当前/上一帧/上上帧三份，供 `VisibleAreaUpdater` 做进/出视野回调。
- 直接调用静态 `GetVisibleArea*()` 的地方：`World.LateUpdate`（地块）、`BlockTileRenderer`、`LightBuffer`、`ConduitFlowVisualizer`、`SolidConduitFlowVisualizer`、`LogicCircuitNetwork`、`DiseaseContainers`、`Infrared`、`AmbienceManager`。
  → **多视角的核心改造点之一：把这个函数改成"所有活动视角的并集"**（或按摄像机分别求）。

### 2.2 `Grid.GetVisibleExtents`（`Assembly-CSharp/Grid.cs:1735-1774`）
- 另一套独立实现，直接读 `Camera.main`。`GetVisibleCellRangeInActiveWorld` = 视口 ±4 格。
- 用户：`KBatchedAnimUpdater.UpdateVisibility`、`PropertyTextures.GetVisibleCellRange`（±16 格）、大部分 `OverlayModes`。

## 3. KAnim 批量渲染

### 3.1 可见性（`Assembly-CSharp/KBatchedAnimUpdater.cs:269-336`）
- 世界按 32×32 格分 chunk（`KAnimBatchManager.CHUNK_SIZE`）。`LateUpdate` → `UpdateVisibility` 用 `Grid.GetVisibleCellRangeInActiveWorld` 求可见 chunk 矩形；新进入的 chunk 里的控制器 `SetVisiblity(true)`，离开的 `SetVisiblity(false)`。
- `KBatchedAnimController.SetVisiblity(false)`（`KBatchedAnimController.cs:904`）→ `SuspendUpdates(true)` + `SetDirty()`。
- `UpdateAnim`（`:295-345`）：不可见时直接 return（只有 `VisibilityType.OffscreenUpdate` 会推进 elapsedTime，但也不更新帧）→ **视野外动画冻结**，位置变化也不会写回 batch（因为 `transform.hasChanged` 的处理在被挂起的 UpdateAnim 里）。
- **关键**：`KBatchedAnimInstanceData.cs:168`
  `currentAnimNumFrames = target.IsVisible() ? target.GetCurrentNumFrames() : 0;`
  不可见控制器写进 GPU 数据的帧数是 0 → shader 不出几何 → **视野外 KAnim 根本不画**，不只是冻结。

### 3.2 提交（`Assembly-CSharp-firstpass/KAnimBatchManager.cs:184-252`）
- `UpdateActiveArea(vis_chunk_min, vis_chunk_max)`：只把落在可见 chunk 范围里的 `BatchSet` 放进 `activeBatchSets`；`Render()` 对 active 的 batch 调 `Graphics.DrawMesh(camera: null)`。
- 调用方：`World.LateUpdate`（`World.cs:121-150`），范围取自 `KBatchedAnimUpdater.GetVisibleArea`。
- `VisibilityType.Always` / UI 材质的 batch 不受剔除。

→ 要让第二视角看到 KAnim，需要**同时**改 ① `KBatchedAnimUpdater.UpdateVisibility` 的 chunk 范围（并集）② `World.LateUpdate` 传给 `UpdateActiveArea` 的范围。

## 4. 地块与液体

### 4.1 自然地块 `GroundRenderer`（`Assembly-CSharp/GroundRenderer.cs:486-514`）
- 16×16 chunk。`Render(vis_min, vis_max)` 只对可见 chunk 调 `DrawMesh`；但 `RebuildDirtyChunks()` 会把**所有**脏 chunk 都重建 → 视野外的 mesh 数据其实是新的，只是没提交。改成并集即可，成本主要是 draw call。
- `RenderAll()` = 全图 + `forceVisibleRebuild: true`（每次全部重建，很贵，只适合 timelapse）。

### 4.2 建筑方块 `BlockTileRenderer`（`Assembly-CSharp/Rendering/BlockTileRenderer.cs:697-722`）
- 同样 16×16 chunk，`GridVisibleArea.GetVisibleArea()` 决定范围，范围内逐 chunk `Rebuild`（有脏标记）+ `Render`。

### 4.3 液体 `WaterCubes`（`Assembly-CSharp/WaterCubes.cs`）
- 一张覆盖整个世界的 quad（bounds 无限大），layer=`Water`，材质采样全局液体属性纹理。
- 主管线中 `baseCamera MRT` 去掉了 `Water` 层，液体由 `MRTLiquidInterception` 的命令缓冲画进 MRT（`MRTLiquidInterception.cs`）。shader 很可能采样 `MRT0_Copy/MRT1_Copy`（屏幕空间，属于主视角）做折射/背景 → 独立摄像机里可能"透出"主视角的画面。待实验确认。
- 液体纹理 `PropertyTextures.Property.Liquid` 是 `updatedExternally`，每帧从 sim 全图拷贝（`PropertyTextures.cs:509-511`）→ 数据本身全图有效。

### 4.4 落水 `FallingWater.Render`（`Assembly-CSharp/FallingWater.cs:656`）
- 所有粒子一个 mesh，`DrawMesh(camera: null)`，无剔除。

### 4.5 属性纹理 `PropertyTextures.LateUpdate`（`Assembly-CSharp/PropertyTextures.cs:535-583`）
- 非外部推送的属性（FogOfWar、GasColour、GasPressure、GasDanger、SolidDigAmount、SolidLiquidGasMass、WorldLight、Temperature、StateChange、FallingSolid、Radiation）只更新 `Camera.main` 视口 ±16 格的子矩形，且每帧只轮换更新一个"非每帧"属性。
- 纹理在 `OnReset` 新建，初始全 0 → 本次会话从没进过主视野的区域，雾战 = 未探索（黑）、气体无色。
- 渲染时还经 `TextureLerper` 做时间混合（`TextureLerper.cs:110-133`）。

## 5. Timelapser（延时摄影）——不是离屏摄像机

`Assembly-CSharp/Timelapser.cs:168-207`，协程每帧 `WaitForEndOfFrame`：
1. 第 1 帧末：`ReadPixels` 当前屏幕 → 冻结纹理，启用 `timelapseFreezeCamera` 盖住屏幕；`screenshotActive = true`（即 `GameUtil.IsCapturingTimeLapse()` 为真）；`DebugHandler.SetTimelapseMode` 隐藏 UI 并切 active world；`SetPostionAndOrtho` **把主 CameraController 移到目标位置并放大到能装下整个基地**；关掉覆盖层。
2. 第 2 帧：`CameraController.Update` 因为在截图中而不处理输入；各剔除系统按新位置更新；`IsCapturingTimeLapse()` 为真时：
   - `World.LateUpdate` → `groundRenderer.RenderAll()` + `UpdateActiveArea(0..9999)`（`World.cs:125-131`）
   - `BlockTileRenderer.Render` → 全图（`BlockTileRenderer.cs:701-705`）
   - `PropertyTextures` → 所有属性本帧都更新（仍限视口范围）；`TextureLerper` 直接取目标值
3. 第 2 帧末：`RenderAndPrint` → `CameraController.RenderForTimelapser`（`CameraController.cs:1459-1486`）：对 `baseCamera` 和 `overlayCamera` 临时换 `targetTexture` 和 cullingMask（序列化字段 `timelapseCameraCullingMask` / `timelapseOverlayCameraCullingMask`，值在资源里，运行时才能读到）后手动 `Render()`，`ReadPixels` 存 PNG，再把摄像机挪回。

对多视角的启示：
- 官方做法证明了"手动 `Camera.Render()` 到 RT + 帧末读回"可行。
- 但它依赖"主摄像机本帧就在那里"，以及 `IsCapturingTimeLapse` 这个全图开关。`IsCapturingTimeLapse` 能解决地块/方块/batch 提交，**解决不了 KAnim 可见性**（那条走 `KBatchedAnimUpdater` + `Grid.GetVisibleExtents`），timelapse 能看到 KAnim 只是因为主摄像机真的被挪过去并放大了。

## 6. 实验设计（实验 1）

目标：只验证"新建一个独立摄像机，能否正确渲染主摄像机视野之外的区域"，并用对照组区分"方法本身的差异"和"剔除导致的缺失"。

每 5 秒一组截图：

| 文件 | 内容 | 用途 |
|---|---|---|
| `screen` | 主屏幕截图（含 UI、完整光照） | 玩家看到的画面 |
| `ctrl_a` / `ctrl_b` | 新摄像机放在**主摄像机同一位置/缩放**，间隔 0.5 s 两张 | 对照组：看"新摄像机 + 原始 culling mask"本身能画出什么（无光照合成） |
| `off_a` / `off_b` | 新摄像机对准**另一个区域**（默认：打印舱 Printing Pod），间隔 0.5 s | 实验组：缺块 / KAnim 缺失 / 动画是否在动（a/b 差分） |
| `off_forced` | 下一帧临时让 `GameUtil.IsCapturingTimeLapse()` 返回 true（走官方全图分支）后再渲 | 验证"地块/方块缺失仅因剔除"；预测 KAnim 仍缺失 |

新摄像机配置：`CopyFrom(baseCamera MRT)`，cullingMask = MRT 摄像机的 mask | `Water`，`enabled=false`，帧末 `WaitForEndOfFrame` 里手动 `Render()` 到 RT，`ReadPixels` → PNG。同时在日志里打印整套摄像机栈（名称、depth、mask 解码、clearFlags、组件）和 timelapse mask 的实际值，供后续迭代。

预测（待验证）：
- `ctrl`：地块、方块、KAnim、液体都有，但没有光照/日夜调色（亮度与 `screen` 不同）。液体可能有问题（MRT copy）。
- `off_a/b`（与主视野不重叠时）：只有液体、落水、背景；无地块、无建筑方块、无 KAnim。`off_a ≈ off_b`。
- `off_forced`：地块、建筑方块出现；KAnim 仍缺失；雾战/气体可能是旧数据。

## 6.1 实验 1 结果（2026-11-08，session_20261108_174134，新档 cycle 0，256×384 地图，2560×1600）

运行时数据（`cameras.txt`）：
- `Camera.main` = `overlayNoDepth`；`baseCamera MRT` 的 mask = `0x7D423DEF`（不含 Water）。
- **雾战是 `Overlay` 摄像机上的后处理 `FogOfWarPostFX`**（同一摄像机上还有 `ColorCorrectionLookup`、`BloomEffect`、`GridCompositor`、`ClusterCoverPostFX`…）→ 原始摄像机里没有雾战，未探索区域会直接露出来。
- `timelapseCameraCullingMask = 0xAF4FFF37`（含 Water、Overlay、Place…）；`timelapseOverlayCameraCullingMask = 0`。

结论：
| 现象 | 证据 | 判断 |
|---|---|---|
| 对照组（新摄像机放在主摄像机位置）几乎完整 | `set005_ctrl_a` vs `screen`：地块、复制人、植物、液体都在；只差光照/泛光/调色/UI（差异像素约 15–19%） | "独立摄像机 + 原始 mask"这条路本身可行 |
| 地块按 16 格 chunk 剔除 | `set005_edge`：主视野右边界 x≈146，地块画到 x=160（`GridVisibleArea` 向上取整到 16 的倍数）后截断；`edge_forced` 补齐 | 预测正确 |
| KAnim 画到 32 格 chunk 边界 | `edge` 中 x=146–160 的植物/小动物可见 | 符合（不过视野外区域没有 KAnim，这一点没能直接验证） |
| 视野外：没有地块，露出 TerrainBG 背景/气体平面 | `off_a` 右侧灰云 + 液体口袋 | 预测正确 |
| **视野外出现主视角的"鬼影"** | `set005_off_a/b` 正中间出现打印舱和复制人（就是主视野的内容），位置和屏幕上一致 | 某个世界空间 shader 采样了屏幕空间的 `MRT*_Copy`/`_MRT*`（推测是液体） |
| 视野外画面会自己变化 | `off_anim` 变化像素 5–24%，同期对照组 <1%；条带边界在 0.5 s 内移动 | 采样了陈旧/不断变化的屏幕空间或属性纹理数据，不是真实动画 |
| "本局从未看过"的区域属性纹理为空 | `set000_off_forced`：x>209 的区域地块虽然画出来了，但到处是白色空洞和破边；x<209（开局镜头拉远时看过）正常 | 预测正确：地块 shader 依赖只在视口附近更新的属性纹理 |
| 刚读档暂停时液体纹理未就绪 | set 0–2（暂停）水体呈白色竖条纹，取消暂停后变正常 | sim 推送的纹理要等模拟跑起来才有 |
| 走 timelapse 全图分支的代价 | `World.LateUpdate` 0.2–0.7 ms → 43–71 ms（`RenderAll` 强制重建全部 chunk） | 不能每帧用，要做"并集 + 脏标记" |
| 洋红（清屏色）占比始终 0 | TerrainBG 的背景/气体平面覆盖了整个世界 | "缺失"表现为露出背景，而不是透明 |

## 7. 后续迭代方向（实验 1 之后）

**实验 2（已实现）**：把第二视角区域登记进 `MultiViewRegistry`，并入 5 处剔除：`World.LateUpdate`（为额外区域补画地块）、`BlockTileRenderer`（覆写 `GridVisibleArea` 后再跑一次 `Render`）、`KBatchedAnimUpdater.UpdateVisibility`（多矩形）、`KAnimBatchManager.UpdateActiveArea`（取外包矩形）、`PropertyTextures`（为额外区域更新所有 CPU 属性纹理）。另外拍 `off_2pass`（液体用自己的 MRT copy 第二遍渲染）、按图层拆分、`off_nounion`（A/B 对照），以及 `truth`（主摄像机跳过去一帧截屏作为真实画面）。

原始计划：

1. 把三处可见范围改成"所有视角并集"：`GridVisibleArea.GetVisibleAreaExtended`、`Grid.GetVisibleExtents`（及 `GetVisibleCellRangeInActiveWorld`）、`World.LateUpdate` 的 `UpdateActiveArea` 范围。注意 `KBatchedAnimUpdater` 用的是"矩形"，多视角相距很远时并集矩形会很大 → 需要改成多矩形 / chunk 集合。
2. `PropertyTextures` 对每个视角的区域都更新（成本 ∝ 面积）。
3. 光照：每个视角需要一套自己的 MRT + LightBuffer + `_LitTex`，或者分时复用（每帧渲一个视角）。
4. 音效剔除 `SoundCuller`、`VisibleAreaUpdater` 回调、`LightSymbolTracker` 等"只关心可见区"的逻辑要决定是否按多视角处理。
5. 性能：`GroundRenderer.RenderAll` 每次强制全重建，不能每帧用；应改为并集范围 + 脏标记。
