# Dancing Mad (绝・凤凰歌剧场) Splatoon 脚本开发指南（AI 向）

本指南面向在 `SplatoonScripts/Duties/Dawntrail/Dancing Mad/` 目录下编写/维护 Splatoon
脚本的 AI 协作者。目标副本：**绝・凤凰歌剧场 (Dancing Mad)**，地图 territory ID `1363`。

> **场地几何**：本副本的战斗场地是一个以 **(100, 100)** 为圆心、半径约 **20 米**
> 的圆形场地。编写涉及坐标计算的元素（如 `RotateWorldPoint` 推算环形点位、判断
> 玩家是否站在场地边缘/中心等）时，可直接以这个圆心+半径作为基准参考
> （`P2_Forsaken.cs` 中 `MapEffect2TowerPos` 推算塔位坐标时用的旋转中心
> `(100, 0, 100)`/`(100, 0, 92)` 等也都是围绕该场地中心展开的）。

---

## 1. Splatoon 是什么、怎么运作

Splatoon 是一个 Dalamud 插件，核心功能是在游戏世界坐标系上叠加绘制 2D/3D 提示图形
（圆形、扇形、连线等），用来标注机制范围、安全区、目标点位等，帮助玩家判断站位。

绘制的最小单位是 **Element**（元素，`Splatoon/Serializables/Element.cs`），多个 Element
可以打包成 **Layout**（布局，`Splatoon/Serializables/Layout.cs`）。两者都是可被
JSON 序列化/反序列化、可在游戏内 GUI 中编辑、可导出为字符串的数据对象。

**脚本（Script）** 则是用 C# 写的"驱动程序"：它在合适的时机注册/创建 Element、
并根据读取到的游戏机制状态去动态修改这些 Element 的属性（启用/禁用、坐标、颜色、
文本、是否连线等），从而让画面随机制变化。

一句话总结数据流：

```
游戏内事件/状态 (战斗、读条、buff、连线、地图特效...)
        │   ←── 这是本指南第 5 节重点关注的"环境读取"部分
        ▼
脚本逻辑 (SplatoonScript 子类的回调方法)
        │
        ▼
Controller 修改已注册的 Element / Layout 属性
        │
        ▼
Splatoon 渲染引擎在屏幕上画出来
```

---

## 2. 脚本框架基础

### 2.1 基类与生命周期

所有脚本继承自 `Splatoon.SplatoonScripting.SplatoonScript`
(`Splatoon/SplatoonScripting/SplatoonScript.cs`)，或带配置的泛型版本
`SplatoonScript<TConfig>`（此时可通过 `C` 属性访问配置对象，配置类型需要无参构造函数）。

必须实现的成员：
- `Metadata` — 版本号、作者等信息，例如 `new(7, "NightmareXIV, Poneglyph")`
- `ValidTerritories` — 生效的地图 ID 集合。Dancing Mad 固定为 `[1363]`；传 `null`
  表示一直生效（即使下线），传空集合表示任意地图都生效

常用生命周期回调（按典型调用顺序理解）：

| 方法 | 触发时机 | 典型用途 |
|---|---|---|
| `OnSetup()` | 脚本编译加载后，仅一次 | 注册静态 Element/Layout、初始化只读数据表 |
| `OnEnable()` / `OnDisable()` | 进入/离开生效地图 | 不要在这里挂钩子；用于简单的开关逻辑 |
| `OnReset()` | 战斗开始/结束/禁用前 & 启用后；目标是清状态 | 清空内部缓存的状态变量（强烈建议实现） |
| `OnCombatStart()` / `OnCombatEnd()` | 进入/离开战斗 | 战斗相关的计时器重置 |
| `OnPhaseChange(int newPhase)` | 阶段切换（含手动切换） | 多阶段机制的状态机切换 |
| `OnDirectorUpdate(DirectorUpdateCategory category)` | 副本进度事件（进本/重开/团灭等） | 见第 5.6 节 |
| `OnUpdate()` | 每帧 | 持续性逻辑、轮询 `StatusList`、`Controller.Hide()` 等 |
| `OnSettingsDraw()` | 绘制脚本自身设置 UI | 仅在需要自定义配置界面时重写 |

> 提示：覆盖 `OnSettingsDraw` 后，脚本面板会自动出现"设置"区域
> (`DoSettingsDraw` 通过反射检测是否被重写)。

### 2.2 Controller —— 脚本与插件之间的桥梁

每个脚本实例自带一个 `Controller`（`Splatoon/SplatoonScripting/Controller.cs`），
是与插件交互的主要入口：

- **注册/管理元素与布局**：`RegisterElementFromCode`、`RegisterElementsFromMultilineCode`、
  `TryGetElementByName`、`GetElementByName`、`OriginalElements`（未被覆盖的初始副本，
  常用来恢复 `overlayText` 等被动态修改过的字段）
- **状态查询**：`InCombat`、`CombatSeconds` / `CombatMiliseconds`、`Phase`、
  `RolePosition`（基于团队优先级分配读取自己的站位角色）、`AttentionColor`
  （用户配置的"高亮颜色"，常用来给当前需要关注的元素上色）
- **批量控制**：`Hide(elements, layouts)` 一键隐藏所有已注册元素/布局（常在
  `OnUpdate` 开头调用，再按条件逐个 `Enabled = true`）
- **延时任务**：`Schedule(action, delayMs)`、`ScheduleReset(delayMs)`、
  `CancelSchedulers()`
- **地图特效查询**：`GetMapEffect(index)` 主动轮询某个地图特效槽位的状态值
- **配置/覆盖持久化**：`GetConfig<T>()`（配合 `SplatoonScript<T>`）、`SaveConfig()`

### 2.3 Element 关键字段（`Splatoon/Serializables/Element.cs`）

Element 字段非常多（>100 个），完整定义见源码。开发时最常用到的：

- 类型：`type` — 0 固定坐标圆/点，1 相对对象坐标，2 固定坐标连线，3 相对对象连线，
  4 相对对象扇形，5 固定坐标扇形
- 位置：`refX/refY/refZ`（基准坐标或目标对象筛选）、`offX/offY/offZ`（偏移）

  > **坐标轴对应关系（重要，写坐标计算时务必记住）**：Element 的
  > `(refX, refY, refZ)` **并不是**直接照搬游戏世界坐标 `(X, Y, Z)`，而是经过了
  > Y/Z 互换：**`refX` ↔ 世界 `X`，`refY` ↔ 世界 `Z`（水平面另一轴），
  > `refZ` ↔ 世界 `Y`（高度/竖直方向）**。换句话说，**Element 的 `(x, y)`
  > 对应的是游戏里的水平地面坐标 `(X, Z)`**，`z` 字段对应的才是竖直高度。
  > 证据见 `Splatoon/Utility/Utils.cs` 中 `XZY(point) = (point.X, point.Z, point.Y)`、
  > `Splatoon/Structures/LegacyPreset.cs` 中 `refX = X; refY = Z; refZ = Y;`。
  > 因此从世界坐标构造 Element 位置时（如 `MathHelper.RotateWorldPoint(new(100, 0, 100), ...)`
  > 这种 `Vector3(X, 高度, Z)` 形式，详见 `P2_Forsaken.cs` 的 `MapEffect2TowerPos`），
  > 通常需要 `.ToVector2()` 取出 `(X, Z)` 平面分量后再分别赋给 `refX`/`refY`。
- 外观：`radius`、`color`（ARGB uint）、`Filled`、`thicc`、`overlayText`、
  `overlayTextColor`、`Donut`、`coneAngleMin/Max`
- 目标筛选（`type` 为相对对象时）：`refActorComparisonType`（按名称/ModelID/ObjectID/
  DataID/NPCID/占位符 等匹配方式）、`refActorName`、`refActorDataID`、
  `refActorNPCNameID`、`refActorObjectID`、`refActorPlaceholder`
- 连线相关：`tether`、`LineEndA/B`、`ExtraTetherLength`
- 距离/朝向限制：`LimitDistance`、`DistanceSourceX/Y/Z`、`DistanceMin/Max`、
  `LimitRotation`、`FaceMe`

> **最常见的写法**：在游戏内用 Splatoon 编辑器手动摆好元素 → 导出为 JSON 字符串
> → 粘贴进 `Controller.RegisterElementFromCode("""...""")`（参考 `P1_Arrows.cs`
> 整个文件，几乎全部由导出的 JSON 构成）。脚本代码的工作通常只是去
> **启用/禁用、改色、改文字、绑定 `refActorObjectID`**，而不是从零计算几何形状。

---

## 3. 目录结构与现有脚本

```
SplatoonScripts/
├── update.csv                    # 脚本名@版本,URL —— 脚本"商店"索引，新增/升级脚本需登记
├── blacklist.csv                 # 拉黑特定脚本版本
└── Duties/Dawntrail/Dancing Mad/
    ├── P1_*.cs                   # P1 (戦闘1 / Ultima Weapon 阶段) 相关脚本
    ├── P2_Forsaken*.cs           # P2 塔判定 (Forsaken) 相关，含多个迭代/实验版本
    ├── P2_Trine_*.cs             # P2 三角 (Trine) 机制
    └── P2_Forsaken_Pattern/      # 针对不同"缺塔顺序"模式的专精解法脚本
```

文件命名约定：`<阶段前缀>_<机制英文名>_<可选附加说明>.cs`，例如
`P1_Wave_Cannon_Tower_Priority.cs`、`P2_Missing_1238_4567_KT_Strat.cs`。
命名空间统一为 `SplatoonScriptsOfficial.Duties.Dawntrail.Dancing_Mad`
（空格替换为下划线）。

`update.csv` 中每行格式：
`<命名空间>@<类名>,<版本号>,<raw GitHub 文件 URL>`。新增脚本或升级 `Metadata` 中的
版本号时，应同步在此登记（这是脚本面板检测更新的依据）。

---

## 4. 脚本组织模式总结（从现有代码归纳）

1. **静态可视化资源**：`OnSetup` 中通过 JSON 一次性注册大量 Element（来自插件内
   导出），运行时只切换 `Enabled`/颜色/文本（如 `P1_Arrows.cs`、`P2_Forsaken.cs`）。
2. **带配置的脚本**：继承 `SplatoonScript<Config>`，`Config` 是普通类（POCO，
   字段即配置项），通过 `C.XXX` 访问；常配合 `PriorityData`/`PriorityList`
   （`Splatoon/SplatoonScripting/Priority/`）来做"优先目标/搭档"绑定，详见
   `P2_Forsaken.cs` 中的 `Prio1 : PriorityData`。
3. **状态机式脚本**：内部维护 `enum State` + 私有字段记录已观测的事件序列，
   靠多个 `On*` 回调推进状态机，最终决定该显示哪些元素（如
   `P1_Wave_Cannon_Tower_Priority.cs`、`P2_Forsaken_beta.cs`）。
4. **`OnUpdate` 中先 `Controller.Hide()` 再按条件显示**是非常常见的模式，
   保证每帧画面与最新判定结果一致，避免"残留"显示。
5. **`OnReset` 必须清空所有内部状态字段**——否则下一次开荒/重开会带着上一次
   的脏状态进入，导致误判。

---

## 5. 环境/机制读取分类（重点）

脚本要"画对东西"，前提是先"读懂游戏在干什么"。Splatoon 暴露了一整套
事件回调与轮询接口，用于在不修改游戏文件、仅依赖 Dalamud/ECommons 提供的数据
的前提下探测 boss 机制与环境状态。以下按"读取手段"分类，并标注 Dancing Mad
现有脚本中的真实用法作为参考样例。

### 5.1 读条/施法检测 —— `OnStartingCast`

两个重载：
```csharp
public override void OnStartingCast(uint source, uint castId)
public override unsafe void OnStartingCast(uint sourceId, PacketActorCast* packet)
```
- 简化版 `(source, castId)`：判断敌方何时开始读某个技能 ID，是判定"机制即将发生"
  最常见、最早期的信号源。15 个 Dancing Mad 脚本中使用。
- 指针版 `PacketActorCast*`：能拿到更底层的数据如 `ActionType`、`ActionID`，用于
  需要区分动作类型（技能/物品/...）的场景。参考 `P2_Forsaken_Fixed_Partner.cs`：
  ```csharp
  if (packet->ActionType == (int)ActionType.Action) { ... CastAllThingsEnding.Contains(packet->ActionID) ... }
  ```
- 用途举例：预判将要出现的机制类型、清空/初始化对应状态机、在技能读条阶段就提前
  显示参考点位。

### 5.2 技能结算/命中检测 —— `OnActionEffectEvent`

```csharp
public override void OnActionEffectEvent(ActionEffectSet set)
```
- 在技能**真正命中/生效**的瞬间触发（区别于 `OnStartingCast` 的"开始读条"）。
  `set.Action?.RowId` 取动作 ID，`set.TargetEffects` 遍历命中目标。
- 典型用法（`P2_Forsaken.cs`）：统计"塔"爆炸技能 (`ActionTowerExplode = 47806`)
  命中了哪些玩家，记录 `FirstTakers` 名单，用于之后判断谁该进/出：
  ```csharp
  if (set.Action?.RowId == ActionTowerExplode) {
      TowerCount++;
      foreach (var x in set.TargetEffects)
          if (((uint)x.TargetID).TryGetPlayer(out var p)) FirstTakers.Add(p.ObjectId);
  }
  ```
- 另见 `P1_Wave_Cannon_Tower_Priority.cs` 用具体 `actionId` 判断阵型初始化时机。
- 旧接口 `OnActionEffect(...)` 已标记 `[Obsolete]`，新脚本一律使用
  `OnActionEffectEvent`。

### 5.3 连线（tether）检测 —— `OnTetherCreate` / `OnTetherRemoval`

```csharp
public override void OnTetherCreate(uint source, uint target, uint data2, uint data3, uint data5)
public override void OnTetherRemoval(uint source, uint data2, uint data3, uint data5)
```
- 用于探测游戏内"连线类"机制（连线分摊、连线指向等）：哪个对象与哪个玩家之间
  出现了/消失了连线，以及连线的附加参数 (`data2/3/5`，常用来用一组"指纹"区分
  连线种类，参考各脚本里的 `LooksLikeImageTether(data2, data3, data5)` 辅助函数)。
- 典型用法：
  - `P1_GravenImage_Reminder.cs`：判断连线终点是否是自己 (`target == BasePlayer.EntityId`)，
    再按连线源对象的 `DataId` 和坐标解析出"提示文本"。
  - `P1_Teletrouncing_Image3.cs` / `P1_Wave_Cannon_Tower_Priority.cs`：把连线目标
    解析为 `IPlayerCharacter`，结合连线源位置归类 (`ClassifySource`)，构建
    "玩家 → 分配结果"的映射表。
- 常配合 `uint.TryGetObject/TryGetPlayer`、`uint.GetObject()` 等 ECommons 扩展方法
  把 EntityId 转换为可读的游戏对象。

### 5.4 buff/debuff 检测 —— `OnGainBuffEffect` / `OnRemoveBuffEffect` / `OnUpdateBuffEffect`

```csharp
public override void OnGainBuffEffect(uint sourceId, Status Status)
public override void OnRemoveBuffEffect(uint sourceId, Status Status)
public override void OnUpdateBuffEffect(uint sourceId, Status status)
```
- 检测目标对象**获得/失去/刷新**某个状态（buff/debuff），`Status.StatusId` 是状态
  ID，`Status.RemainingTime` 是剩余时间——这是判断"谁被点名了""谁该去哪""还剩
  多久"最直接的数据源。
- 典型用法：
  - `P1_DoubleTroubleTrap_AutoMarker.cs`：检测到陷阱类 debuff 后启动延时计时器，
    到点自动发出标记指令。
  - `P1_Teletrouncing_Image3.cs`：把方向类 debuff (`DirectionFromStatus`) 映射成
    机制方向，记录到 `_lineAssignments`。
  - `P2_Forsaken_beta.cs`：识别"被点名/缺塔" (`IsMissingStatus`) 并解析具体债务
    类型 (`DebuffFromStatus`)，驱动整个状态机进入工作状态。

### 5.5 地图特效检测 —— `OnMapEffect` / `Controller.GetMapEffect`

```csharp
public override void OnMapEffect(uint position, ushort data1, ushort data2)
uint Controller.GetMapEffect(uint mapEffectIndex)   // 主动轮询版本
```
- "地图特效"是游戏底层用于驱动场地变化/隐藏机制的信号通道，与实际地图坐标无直接
  关系，需要先打表 `position → 实际坐标/含义` 的映射（参考 `P2_Forsaken.cs` 中
  `MapEffect2TowerPos` 用 `MathHelper.RotateWorldPoint` 预先计算 16 个塔位坐标）。
- Dancing Mad 中最重要的用途是 **侦测"塔"的生成**：
  ```csharp
  if (IsTowerSpawnMapEffect(data1, data2) && IsTowerMapPosition(position))
      AddTowerSpawnPosition(position);   // 记录生成顺序，供后续逻辑判断"缺塔模式"
  ```
  几乎所有 `P2_Forsaken_Pattern/*.cs` 脚本都复用了这一套 `IsTowerSpawnMapEffect` +
  `IsTowerMapPosition` + `AddTowerSpawnPosition` 的检测组合，差异只在于拿到顺序后
  如何分配/标注玩家职责。
- `Controller.GetMapEffect(index)` 是被动回调之外的**主动查询**接口，适合在
  `OnUpdate` 里按需读取某个槽位当前状态（而不是等待变化事件）。

### 5.6 副本流程事件 —— `OnDirectorUpdate`

```csharp
public override void OnDirectorUpdate(DirectorUpdateCategory category)
public override void OnDirectorUpdate(nint directorPtr, uint targetId, DirectorUpdateCategory a3, ...) // 底层版本
```
- 用于感知"进本/重新挑战/团灭"等宏观流程节点，是清空状态、重置状态机的最佳时机
  （比单纯依赖 `OnCombatStart/End` 更可靠，能覆盖团灭/中途退本等情况）。
- 17 个 Dancing Mad 脚本使用，最常见写法（`P2_Trine_Beta.cs`）：
  ```csharp
  public override void OnDirectorUpdate(DirectorUpdateCategory category)
  {
      if (category.EqualsAny(DirectorUpdateCategory.Commence, DirectorUpdateCategory.Recommence, DirectorUpdateCategory.Wipe))
          ResetState();
  }
  ```

### 5.7 对象效果检测 —— `OnObjectEffect`

```csharp
public override void OnObjectEffect(uint target, uint entityId/*data1*/, uint actionId/*data2*/)
```
- 检测特定游戏对象上触发的"效果事件"（如动画播放、标记物激活），常用于捕捉
  没有读条/没有 buff 但有视觉/动画变化的隐藏判定信号。
- 典型用法：
  - `P1_GravenImage_Reminder.cs`：通过目标对象的 `DataId` 过滤出"动画标记物"，
    再用 `data1/data2` 的特定组合值判断该标记是被"激活"还是"关闭"。
  - `P2_Trine_Beta.cs` / `P2_Trine_Effects.cs`：捕获"三角"机制的"telegraph"信号
    (`CaptureTrineTelegraphSignal` / `TryCaptureTelegraph`)，提前预判范围。

### 5.8 直接轮询玩家状态 —— `IPlayerCharacter.StatusList`

不依赖事件回调，而是在 `OnUpdate` 里每帧主动遍历 `pc.StatusList`：
```csharp
pc.StatusList.Where(x => x.StatusId.EqualsAny(Arrows.Keys) && x.RemainingTime > 0.5f)
             .OrderBy(x => x.RemainingTime)
```
- 适合需要"当前快照"而非"变化事件"的场景：例如 `P1_Arrows.cs` 通过统计自己身上
  当前同时存在的方向箭头 debuff 数量及其剩余时间排序，推断出"我应该走的两段方向"。
- 相比 `OnGainBuffEffect`，轮询方式天然能处理"多个同类 debuff 共存、需要按剩余
  时间排序"等更复杂的判断逻辑，但要小心每帧开销与抖动（建议结合
  `EzThrottler`/`FrameThrottler` 限频）。

### 5.9 战斗/阶段宏观状态 —— `Controller` 只读属性

不属于事件回调，而是脚本里**随时可查询**的状态量，常用作各类判断的"前置条件"：
- `Controller.InCombat` / `Controller.CombatSeconds` / `Controller.CombatMiliseconds`
  —— 是否在战斗中、战斗已进行多久（可用于"多少秒后必定触发某机制"这类基于时间线
  的硬编码判断）
- `Controller.Phase` / `OnPhaseChange(int)` —— 当前阶段编号（多阶段本的状态机基础）
- `Controller.Scene` —— 底层 `ActiveScene` 指针读取的场景值
- `Controller.RolePosition` —— 基于团队优先级分配（Priority 系统）解析出"我在阵型
  中的角色定位"，用于需要按"职责分配"显示不同点位的脚本（结合
  `Splatoon/SplatoonScripting/Priority/*` 的 `PriorityData`/`PriorityList` 体系）

### 5.10 小结：如何为新机制选择"读取手段"

| 你想知道… | 优先尝试的回调/接口 |
|---|---|
| boss/NPC 要放某个技能了 | `OnStartingCast` |
| 某个技能真正命中了谁 | `OnActionEffectEvent` |
| 谁和谁之间连线了/断了 | `OnTetherCreate` / `OnTetherRemoval` |
| 谁身上多了/少了某个 buff，还剩多久 | `OnGainBuffEffect` / `OnRemoveBuffEffect` / `StatusList` 轮询 |
| 场地/隐藏机制的状态变化（如塔何时何地生成） | `OnMapEffect` / `Controller.GetMapEffect` |
| 进本/重开/团灭等流程节点（用于重置状态） | `OnDirectorUpdate` |
| 某个场景物体播放了动画/触发了视觉效果 | `OnObjectEffect` |
| 现在是第几阶段、战斗进行了多久 | `Controller.Phase`、`Controller.CombatSeconds` |

> 实战建议：先用 `SplatoonScripts/Tests/` 下的探测脚本（如
> `CastStartingTest.cs`、`ActionEffectTest.cs`、`TetherProcessor2Test.cs`、
> `DisplayMapEffect.cs`、`StatusListMonitoring.cs`、`DirectorUpdateTest.cs`、
> `ObjectEffectTest.cs` 等）在本地或录像回放中跑一遍，把目标机制对应的真实
> ID/data1/data2/StatusId 记录下来，再据此编写正式脚本中的判定条件。这些 ID
> 因版本而异，必须用真实数据校验，不能凭猜测硬编码。

---

## 6. 编码规范（摘自 `CONTRIBUTING.md`，对 AI 协作同样适用）

- **本仓库明确允许在"可加载脚本/模块"中使用 AI 辅助编写**（这正是
  `SplatoonScripts/` 下内容的属性），但要保持代码人类可读。
- 不要重构已有代码、不要替换现有实现方式（如把 if 链换成 switch、把 opcode
  换成 hook 等）——新脚本独立成文件即可，不要"顺手"动现有脚本的实现细节。
- 新增功能要自成一体，不应让插件主体依赖它。
- 与现有脚本风格保持一致（命名、目录结构、`Metadata`/`ValidTerritories` 写法等）。
- 任何改动都不应导致用户已保存的配置被重置。

## 7. 开发与登记流程建议

1. 在游戏内用 Splatoon 自带编辑器搭建/调整可视化元素，导出 JSON，作为
   `OnSetup` 中 `RegisterElementFromCode`/`RegisterElementsFromMultilineCode`
   的输入（不要手写几十个字段的 JSON）。
2. 用 `SplatoonScripts/Tests/` 中的探测脚本在录像/本地先确认目标机制对应的
   真实事件类型与参数（见 5.10 的小结表）。
3. 编写脚本类：确定 `Metadata`、`ValidTerritories = [1363]`，按需选择
   `SplatoonScript` 或 `SplatoonScript<Config>`，实现必要的 `On*` 回调与
   `OnReset` 状态清理。
4. 命名遵循现有约定（`<阶段>_<机制>_<说明>.cs`，命名空间
   `SplatoonScriptsOfficial.Duties.Dawntrail.Dancing_Mad`）。
5. 新脚本/版本更新后，在 `SplatoonScripts/update.csv` 中登记
   `<命名空间>@<类名>,<版本号>,<raw 文件 URL>`。

---

## 附录 A：如何判断玩家职能（MT/ST/H1/H2/D1~D4）

很多机制的解法依赖"谁该去做什么"——比如"MT 去 A 点，ST 去 B 点，D1~D4 按编号
站对应位置"。但游戏本身只暴露 Job（职业），并不会告诉你"谁是主坦/副坦/治疗1/2/
近战1/2/远敏1/2"这种**编队内的具体分工编号**。Splatoon 为此提供了两层互补的机制：

### A.1 自动判断大类角色 —— `IPlayerCharacter.GetRole()`

```csharp
player.GetRole() == CombatRole.Tank   // / .Healer / .DPS
```
- 来自 ECommons 的扩展方法，纯粹根据玩家当前 **Job** 自动推导出 `CombatRole`
  （`Tank`/`Healer`/`DPS`），**无需任何用户配置**，游戏数据本身就能算出来。
- 实战示例：
  - `P1_Graven3_FinalSpread.cs:539-541`
    ```csharp
    var tanks   = members.Where(x => x.GetRole() == CombatRole.Tank).ToList();
    var healers = members.Where(x => x.GetRole() == CombatRole.Healer).ToList();
    var dps     = members.Where(x => x.GetRole() == CombatRole.DPS).ToList();
    ```
  - `P1_Wave_Cannon_Tower_Priority.cs`、`P2_Trine_Beta.cs` 中也大量用
    `GetRole() == CombatRole.Tank` 来分拣坦克与非坦克。
- **局限**：两个坦克的 `CombatRole` 都是 `Tank`，四个 DPS 的 `CombatRole` 都是
  `DPS`——`GetRole()` 只能判断到大类，**判断不出 MT/ST、H1/H2、D1~D4 这种具体编号**。

### A.2 精确判断具体编号 —— Priority 系统（需要用户预先手动分配）

因为编队内"谁是 1 号谁是 2 号"这种关系无法从游戏数据里直接读出，Splatoon
设计了一整套 **Priority（优先级）系统**
（`Splatoon/SplatoonScripting/Priority/*`），本质是让**用户在游戏内手动分配一次**，
脚本再去查询结果：

- **角色位置枚举** `RolePosition`（`Priority/RolePosition.cs`）：
  `T1, T2, H1, H2, M1, M2, R1, R2`（坦克1/2、治疗1/2、近战DPS1/2、远敏DPS1/2）。
- **显示名称**由 `Splatoon/Gui/Popup/PriorityPopupWindow.cs` 中的两套字典决定
  （取决于用户设置项 `P.Config.PrioUnifyDps`）：
  - 普通命名（`NormalNames`）：T1/T2/H1/H2/**M1/M2/R1/R2**
  - DPS 统一命名（`DpsUniformNames`）：T1/T2/H1/H2/**D1/D2/D3/D4**
    （即 M1→D1，M2→D2，R1→D3，R2→D4）
  - **T1 ≈ "MT"（主坦），T2 ≈ "ST"（副坦）是约定俗成的叫法**——枚举本身没有
    字面意义上的 MT/ST，只有 T1/T2。

- **分配流程**：用户打开游戏内 "Splatoon Priority Editor" 弹窗
  (`PriorityPopupWindow`，可通过 `Edit Roles` 按钮唤起)，把 8 个固定位置
  (T1~R2) 逐一拖拽/绑定到当前队伍的 8 个真实成员上；也可以不按角色而按
  "姓名 + 职业"配置（参见 `JobbedPlayer.IsInParty`，`IsRole=false` 时走名字/Job
  匹配逻辑）。

- **脚本侧查询接口**：
  1. **查自己**：`Controller.RolePosition`（`Controller.cs:471`）——遍历
     `P.PriorityPopupWindow.Assignments`，找到名字与自己匹配的那一项，再用其
     下标去 `PriorityPopupWindow.RolePositions` 列表中取出对应的 `RolePosition`。
  2. **查任意人 / 声明自己需要的优先级结构**：继承 `PriorityData`
     （`Priority/PriorityData.cs`）并按需重写 `GetNumPlayers()`（如
     `P2_Forsaken.cs` 中的 `Prio1 : PriorityData` 表示只需要 1 名"搭档"），
     然后调用：
     - `GetPlayer(predicate, position)` —— 按条件取优先级列表中第 N 个匹配者
     - `GetPlayers(predicate)` —— 按条件取出全部匹配者（保持优先级顺序）
     - `GetOwnIndex(predicate)` —— 取自己在匹配结果中的下标

### A.3 实战中两层机制如何组合使用

典型做法是：先用 `GetRole()` 筛出"两个坦克/四个 DPS"这样的大类集合，
再用 `PriorityData`/`PriorityList` 给同大类的成员排出"谁是 1 号、谁是 2 号"
的明确顺序（因为光凭 Job 分不出主坦副坦）。代表性代码见
`P2_Trine_Beta.cs` 的 `GetOrderedTanksForFinal()`：
```csharp
// 优先：从用户配置的 Priority 列表中取出排好序的坦克
var orderedTanks = C.PriorityData
    .GetPlayers(m => m.IGameObject is IPlayerCharacter p && p.GetRole() == CombatRole.Tank)
    .Select(m => (IPlayerCharacter)m.IGameObject)
    .Take(2)
    .ToList();

// 兜底：如果用户没有正确配置 Priority 列表，退化为按 EntityId 排序的近似方案
var partyTanks = GetPartyPlayers()
    .Where(p => p.GetRole() == CombatRole.Tank)
    .OrderBy(p => p.EntityId)
    .ToList();
```

### A.4 速查表

| 想知道… | 用什么 | 是否需要用户预先配置 |
|---|---|---|
| 这个人是坦克/治疗/输出（大类） | `player.GetRole() == CombatRole.Tank/Healer/DPS` | 否，按 Job 自动推导 |
| 这个人具体是 MT/ST/H1/H2/D1~D4（细分编号） | `Controller.RolePosition`（查自己）或 `PriorityData`/`PriorityList`/`JobbedPlayer`（查任意人，需脚本主动声明并由用户在游戏内 Priority Editor 中手动分配一次） | 是；未配置时通常需要写一个基于 `EntityId` 等稳定键的兜底排序逻辑 |

> 写新脚本时，如果机制需要"给每个人分配不同的点位/职责"，优先考虑复用
> Priority 系统而不是自造一套排序规则——这样能与本仓库其它脚本的用户体验保持一致
> （用户已经熟悉这套配置流程），也能拿到"用户可手动纠正自动判断结果"的能力。
