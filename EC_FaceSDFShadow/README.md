# EC_FaceSDFShadow

面部 SDF 硬边阴影插件。在角色脸上叠一层随光照旋转扫掠的硬边阴影，
得到赛马娘/原神那类动漫风格的明暗分界。

**当前状态：v2.0.0。需要先构建 shader bundle 才能运行。**

> 普通游戏用户请看 **[USAGE.md](USAGE.md)**（只讲用法）；
> 本 README 面向开发/维护者，含实现契约与机制说明。

## 版本史（仅保留各版本引入的现役能力）

- **v0.6~v0.11**：逐角色配置（ESF 扩展数据，多角色独立生效）；阈值图精简为手绘
  角度帧单一路径；颈带 N·L 复制品 crossfade 补 pitch 响应；全局参数持续跟随 +
  ME 定制锁存；取色器光强对齐。
- **v1.0.0**：首个正式版，默认值实测定标；USAGE.md 与手绘帧图例模板。
- **v1.1.0**：颈带阴影融合——复制品消费游戏真实阴影（先半兰伯特后乘遮挡），
  治缝两侧叠加过黑；代价：主域原生投影被清干净（颈带内由插件恢复）。机制见
  `docs/ec-knowledge/04-troubleshooting/overlay-native-shadow-stacking.md`。
- **v1.2~v1.3**：全脸遮挡恢复实验（审美否决，v2.0.0 移除）；发影 v2 混合架构
  （屏幕域 clip 位移 + RT 接收 + 单侧深度门 + max 融合）。
- **v1.4.0**：发影形态 B（光空间真投影，自建 hair-only shadow map，影贴真实
  脸 mesh、随光向与镜头解耦）+ 多角色逐主体 RT/矩阵隔离。
- **v1.5.0~v1.5.1**：饰品头发参与发影（ME 哑 shader 标记，材质级玩家意志随卡
  持久化）；深度门单侧软化窗（眨眼吞影修复）。
- **v2.1.0（当前）**：形态 A 深度门掠射感知——消费端按 NdV 把容差在侧脸轮廓带
  自动收紧到正面区约 1/30，拒掉被脸挡住的远侧贴脸侧发穿透影；正面区容差默认
  0.005 米（Debug 构建经 Debug 组 `SS_DepthTol` 可调，Release 固定）。
- **v2.0.0**：配置大改——发影项移入独立 **HairShadow** 栏重排
  （`00_ShadowEnabled` 默认开 / `01_Form` 枚举 ScreenSpace/LightSpace /
  `02~05_SS_*` 屏幕空间四件 / `06_Soft` 合并连续滑条 / `07_LS_Resolution`）；
  删 `09_FaceOcclusionRestore`（审美否决）、`19_HairShadowParts`（固定前发）、
  `22_HairShadowBias`（常量化 0.005）；干净底色与 `FaceNoSelfCast`（Advanced 节）
  跟随 General 主开关；Advanced 节 `DisplayPreset` 显示模式一键预设（标定于
  阴影密度 0.55）。pitch 渐暗门：光仰角进入头轴 37° 锥内渐变全暗（顶/底对称）。

## 工作原理

两件事：

1. **中和面部 ramp** — 把面部材质的 `_RampG` 覆盖成白图，游戏自带的 ramp 阴影
   在脸上失效。身体、头发不受影响。
2. **叠加 SDF 阴影** — 给 `cf_O_face` 的 SkinnedMeshRenderer 追加一个乘算材质，
   按烘焙出的阈值图画硬边阴影。

### 面部实时自阴影排除（07_FaceRealtimeShadowG）

EC 脸上除了 ramp 阴影，还有一层 Unity 实时自阴影（`SHADOWS_SCREEN` 采样 `_ShadowMapTexture`），
与 SDF 硬边阴影叠加会显得脏乱。`main_skin` 的 `_FaceShadowG` 属性是
**强度标量**：0=原版实时阴影，1=最强排除，中间值连续衰减。

- **设 1**：脸上自阴影（鼻侧/颊侧等自身投影）消失，只剩 SDF 硬边阴影；
  **头发在脸上的投影只稍微变淡、大体保留**（实测，正是想要的折中效果）
- **设中间值（如 0.5）**：自阴影压淡、头发投影保留更多
- **设 0**：原版行为，不做任何改动

机制与 `_RampG` 相同：`SetFloat` 建材质实例 local override，只影响面部材质，身体/头发不受影响
（实测证实）。逐角色软关闭（ME `Enable` 滑条拉 0）时自动还原 0=原版。

### 为什么能"只让脸没有原版阴影"

EC 的 ramp 阴影由 `Shader.SetGlobalTexture(_RampG, tex)` 设的**全局**贴图驱动，
游戏只在系统设置里提供全局开关（「影とアウトライン」），关掉全身阴影都会消失。

但 Unity 中**材质自身的属性值优先于全局值**。面部材质又恰好是逐角色克隆实例
（`CustomTextureControl.cs:28` 的 `clone=true`），所以只给它单独设一个白 ramp，
就能精确地只让脸失效。

> **机制说明**：`_RampG` 虽是全局属性 ID，但 `Material.SetTexture(ChaShader._RampG, tex)`
> 会在该材质实例上建一个 **local override**，只影响这个材质，身体/头发不受影响。
> 注意 `Material.GetTexture` 会因属性未声明报错，但 `SetTexture` 静默建 override——
> Set/Get 不对称，不要因"诊断说没这个属性"就放弃。切勿改成 `Shader.SetGlobalTexture`，
> 那会连身体一起中和。详见知识库
> [角色材质叠加层模式](../../docs/ec-knowledge/03-patterns/character-material-overlay-pattern.md)。

## 逐角色配置（ESF 扩展数据）

Enable 与 ShadowColor 存到角色卡扩展数据（ESF），多角色场景各角色独立控制：

- **首次装插件**：旧角色卡无扩展数据，默认 `Enable=true`，行为等同旧版"装上即生效"
- **逐角色开关**：在 MaterialEditor 面板调整 `FaceSDFOverlay` 的 `Enable` 滑条（0=关闭、1=开启），
  保存角色卡即生效。关闭时显示**原版阴影**（不是一片白），身体/头发不受影响
- **逐角色调色**：在 MaterialEditor 调整 `ShadowColor`（或取色器应用），保存角色卡后下次
  加载自动恢复；该属性从此锁定为本卡自己的值，不再受全局 `02_ShadowColor` 影响（v0.10 起，
  未定制过的卡则实时跟随全局）
- **逐角色自定义 SDF**（EC_Profile 描述指令指定，不存本插件卡数据）：见下节
- **总开关**：`00_Enabled` 关闭时强制禁用所有角色（全局兜底）
- **兼容性**：卸载插件不丢配置（ESF 保留），重装恢复；无插件环境角色卡正常加载不损坏场景

### 逐角色自定义 SDF：`@FaceSDFShadow:[目录名]` 指令

1. 在 `UserData/PluginData/EC_FaceSDFShadow/SDF/perChara/` 下建一个目录，放手工帧
   （纯数字 png，如 `0.png`..`8.png`）或 `SDF.png`，例如 `SDF/perChara/myLady/`
2. 在角色的 **EC_Profile 描述框**（人物制作 → ADK → Profile）任意位置写一行指令：

   ```
   @FaceSDFShadow:[myLady]
   ```

3. 保存角色卡。加载该角色时插件读描述、解析目录名，用 `SDF/perChara/myLady/` 里的图；
   没写指令的角色回退全局 `SDF/` 目录

规则：

- 目录名必须用**成对方括号**包住，否则无法与描述里的其他文字区分。半角 `[]`、全角 `［］`、
  中文 `【】` 都认；冒号半角 `:` 全角 `：` 都认；`@FaceSDFShadow` 大小写不敏感
- **不要用尖括号**：EC_Profile 描述框开了富文本，`<>` 会被当成标签吃掉
- 指令可以夹在正常描述文字里，前后有别的内容不影响；写多条只认第一条
- 目录名不能跨行，不能含 `..` 或路径分隔符（只能是 `perChara/` 下的单层目录名）
- 指令写错、目录不存在 → 控制台 warning 并回退全局 `SDF/`
- 依赖 EC_Profile（`com.deathweasel.bepinex.profile`，软依赖）；未安装时所有角色走全局目录

### 依赖

v0.6.0 起依赖前置插件（游戏通常已装）：
- `EC_BepisPlugins/EC_ExtensibleSaveFormat.dll`（ESF 角色卡扩展数据）
- `EC_Plugins/ECAPI.dll`（KKAPI 的 EC 移植版，提供 CharaController）
- `EC_Profile`（可选，软依赖）：只有用逐角色自定义 SDF 指令时才需要

### 为什么不替换 main_skin

`main_skin` 承载 ramp、描边、detail normal、liquid、高光、三层 overtex，
且无源码。重写必然与身体对不上色，还会和 MaterialEditor 打架。
其 `_ShadowColor` / `ShadowExtend` / `rimV` 实测均无法关闭 ramp 阴影
（`ShadowExtend` 只影响嘴唇）。

## 安装

1. **先构建 shader bundle**（一次性，见 `UnityProject/README.md`）
   - 需要 Unity 2017.4.24
   - 产出 `EC_FaceSDF.unity3d` 复制到 `Resources/`
2. `dotnet build -c Release`（发布给普通用户用 Release，见下）
3. 把 `EC_FaceSDFShadow.dll` 放进 `BepInEx/plugins/Rainbowing/`

### Debug / Release 构建

诊断与实验功能（阈值图/材质/BlendShape 导出等快捷键）用 `#if DEBUG` 隔离：

- **Release 构建**（`dotnet build -c Release`）：剔除诊断功能，ConfigurationManager
  面板更干净，给普通用户。诊断快捷键（Debug 组）不注册；Debug 组调参项
  （`SS_DepthTol`）不注册、按内置常量生效。
- **Debug 构建**（`dotnet build -c Debug`）：保留全部诊断功能，开发排查用。

日常开发验证用 Debug，发布给用户用 Release。

## 配置（ConfigurationManager → Face SDF Shadow）

### General

| 键 | 默认 | 说明 |
|---|---|---|
| 00_Enabled | true | 总开关，关闭立即还原所有角色。逐角色开关改走角色卡（见上） |
| 01_NeutralizeFaceRamp | true | 中和面部 ramp。关掉则叠在原版阴影之上 |
| 02_ShadowColor | (0.79,0.43,0.00,0.27) | 阴影色（RGB 乘算、alpha 强度）。**实时生效**于所有未在 ME 定制该属性的角色；ME 改过（含取色器应用）的卡锁定自己的值，不再受本项影响（见下"全局跟随与 ME 定制锁存"） |
| 04_ThresholdBias | 0 | 明暗分界偏移，正值阴影更少。跟随/锁存语义同 02 |
| 05_SoftnessAngle | 3 | 阴影边缘软化（角度，°）。角度域恒定，不随镜头距离变化；0=硬边。跟随/锁存语义同 02 |
| 07_FaceRealtimeShadowG | 1 | **面部实时自阴影排除强度**（见下节）。0=原版行为；1=排除自阴影（默认，SDF 硬边阴影取代脸部原生自阴影） |

#### 全局跟随与 ME 定制锁存（v0.10.0）

02/04/05 的语义从"仅新角色初始值"改为**持续生效 + ME 定制豁免**：

- **未定制的卡**（旧卡默认如此）：属性实时跟随全局配置。scene 里没有 ME 界面，
  直接调 ConfigurationManager 的 02/04/05 即时改到所有未定制角色；maker 内同理。
- **定制过的卡**：一旦该属性被 MaterialEditor 修改（或取色器"应用到当前捏人角色"），
  即锁存"已定制"标志——该属性固定为本卡自己的值，全局不再覆盖，锁存随角色卡持久化
  （卸载重装、换机器不丢）。三个属性独立锁存：只改过 ShadowColor 的卡，04/05 仍跟随全局。
- 判定方式是哨兵检测（插件每 15 帧比对材质当前值与上次写入值），不查 ME 内部数据。
  ME 打开面板时若重放属性值也会触发锁存——"ME 有记录 = 定制过"，语义上可接受。
- 解锁方式（v1.0.0 起）：捏人界面取色器窗口的「恢复跟随全局（清除本卡定制）」按钮——
  清当前角色三项锁存并把材质推回全局值，保存角色卡后生效（ME 滑条的 Enable 等未保存
  状态不受影响）。在 ME 里把值改回全局值是不够的（标志仍锁存）。

#### 阴影取色器

捏人界面按 `F7` 呼出 IMGUI 工具窗，从实测色反算 `_ShadowColor`，免去手工凭感觉调色。
渲染管线含非线性环节（后处理），一步解析解不保证精确命中，工具提供**迭代校准**逐轮逼近：

1. 在 MaterialEditor 把 `Enable` 滑条拉到 **0**（软关闭，回原版）→ 点 **S 的"取色"**，
   点皮肤阴影处（如鼻下阴影），取"原版阴影色（目标）"
2. `Enable` 拉回 **1**（ramp 中和保持开）→ 点 **P 的"取色"**，点同一皮肤的邻近亮部，
   取"乘算基底色" → 控制台打出第 1 轮结果
3. 点**"应用到当前捏人角色"**直接写入（ME 面板同步；手动填的话 ME 数值框收 **0-1 float**，
   按日志 `结果：Color(0-1)` 行填，RGB 255 制只能在游戏调色板里用）
4. （校准轮）应用生效后 → 点 **V 的"取色"**，点当前脸上阴影处 → 自动出下一轮修正 →
   再应用；重复到日志提示"已收敛"（通常 2~3 轮）

取色为 5x5 均值，避开高光点与化妆纹理。初值公式：linear 域 `m = linear(S)/linear(P)`、
`alpha = 1-min(m)`、`C = (m-min(m))/(1-min(m))`；校准：`m ← m × linear(S)/linear(V)`。
换光照基本不用重算（比值抵消光照），换角色（ramp 不同）需重取 S/P。

**光强对齐选框**（v0.11）：捏人界面的 direct light 固定 intensity 1.0（CvsDrawCtrl 的
光照滑条只有角度、无强度），比 advscene 常用的 0.9 亮，直接取色会让反算色偏亮、
与 advscene 实际呈现对不上。取色前勾上「对齐 ADV 光强（0.9）」，取色器会把该灯
压到 0.9 再取样；**勾上仅影响取色环境**，取消勾选/关窗/切出场景自动还原原值，
不会永久改动玩家的光。找不到光源（CvsDrawCtrl/objLight 缺失）时选框自动禁用。

### StructuredSDF（手绘阈值图）

| 键 | 默认 | 说明 |
|---|---|---|
| ManualContourInterpolation | true | 对完整手工状态帧启用 chart-isolated contour 子帧插值（解决帧间时间台阶）。手绘帧通常只有约 9 张，raw 阈值扫掠会有明显台阶，故默认开；失败回 raw 状态帧阈值；不影响外部 `SDF.png` |
| ManualSDFBlurSigma | 2 | 手绘阈值场的空间高斯模糊 sigma（texel）。消除手绘帧边缘的 C1 台阶/白线，0=关 |
| ManualEndpointSnapDegrees | 1.5 | 端点钳位（度）。把光角钳离正面 0°/背面 180° 端点，端点处直接显示"偏离该度数"的平滑形态；0=关。代价：光扫过正面/背面时阴影形态冻结约 2×该度数 |
| NeckRampScale | 0.82 | 颈带 N·L 复制品的光照项缩放（t = NdotL01×本项 + NeckRampBias，NdotL01 为半兰伯特）。面部 SDF 窗口只看光 yaw，身体着色响应完整光向，颈缝两侧阴影边界不同步；复制品分支在面部 UV 颈带内用世界法线·光向出阴影量，把颈带还原成原生形态（带底近缝处纯复制品、带顶渐变回纯 SDF）。阴影乘数与面部 SDF 分支同款，颜色随 `02_ShadowColor`/ME 逐角色调色同步。定标：游戏内对照 body 侧颈部明暗调本项与 NeckRampBias，负值翻转明暗方向。纯显示参数，改值即生效、不重烘焙 |
| NeckRampBias | 0 | 颈带 N·L 复制品的光照项偏移（t = NdotL01×NeckRampScale + 本项），与 NeckRampScale 配套定标 |
| NeckEdgeSoftness | 0.008 | 颈带复制品的阴影边缘软化半宽 w（s = smoothstep(0.5-w, 0.5+w, t)）。0=硬边；越大边缘越软。纯显示参数，改值即生效、不重烘焙 |
| NeckBandTopV | 0.2 | 颈带 crossfade 带顶 V 坐标（带底 0.05 恒为纯复制品）。**上拉**可盖住下颌两侧遗留的静态 SDF 形状、收窄 y 向渐变宽度。纯显示参数，改值即生效、不重烘焙 |
| NeckReplicaCap | 1 | 颈带复制品压黑上限。自阴影开 + 底光时，复制品压到全黑会叠在引擎 receive_shadows 投影上成双层全黑分割线（下颚→颈上段）；调低本项只减淡这层压黑、原生投影层次透出来。光从上方/侧面时 1-neckLit 本就低于上限、行为不变，非逐方向补丁。1 = 与不设限逐位一致。纯显示参数，改值即生效、不重烘焙 |
| NeckShadowCompensation | 1 | 颈带复制品真实阴影消费权重（阶段 2 A2）。**须配合主开关 `00_Enabled`（清底色）**：底色被清干净后，复制品采样屏幕空间阴影图、把遮挡合进光能再过阴影曲线（与身体单次着色同构，全程乘算、因子 ≤1，无 A1 除法方案的 HDR 过驱风险）。0 = 旧式合成（N·L 独立判光）。自阴影关时阴影图未绑定、C# 推 `_ShadowmapAvail=0`，两式同值。纯显示参数，改值即生效、不重烘焙 |

### HairShadow（v2.0.0 起独立栏）

干净底色契约：插件生效期间（General 主开关 × 逐角色 ME 开关）face renderer 的 `receiveShadows` 恒为 false——白 ramp 消不掉底色上的原生投影，必须从 renderer 层清掉，颈带由插件全权负责；主开关关闭/软关/卸载还原。开启期间 ME 面板的同名 renderer 开关会被轮询压回。**该行为没有独立配置项，不受 00_ShadowEnabled 影响。**

| 键 | 默认 | 说明 |
|---|---|---|
| 00_ShadowEnabled | true | **发影总开关**：关 = 停止发影并释放逐角色 mask RT。不影响干净底色（见上）（原 11 号） |
| 01_Form | ScreenSpace | **发影形态**：ScreenSpace=屏幕位移（02~05 生效，默认）/ LightSpace=**光空间正交投影**（自建 shadow map：头发渲进一张以光为视点的正交深度图，脸上像素投回光空间比深度）。LightSpace 的影贴在真实脸 mesh 上、随鼻梁眉弓起伏变形、随光向旋转，且与镜头完全解耦。SDF 面影两形态都保留。改档立即重建（原 20 号） |
| 02_SS_ShiftX | 0.01 | ScreenSpace 专属：发影 **X 移动量程（米）**。百分比映射：光从正面扫到背面转过 3/4 行程时影到最远处，之后保持——匀速、可控（原 12 号） |
| 03_SS_ShiftY | 0.008 | ScreenSpace 专属：发影 **Y 移动量程（米）**。保持小于 02 才有经典动画观感（影横向滑为主、竖向少量）；光上抬影下移（原 13 号） |
| 04_SS_BaseX | 0 | ScreenSpace 专属：发影**初始 X 偏移（米）**，不依赖光向；负值反侧（原 14 号） |
| 05_SS_BaseY | 0 | ScreenSpace 专属：发影**初始 Y 偏移（米）**，不依赖光向；负值反向（原 15 号） |
| 06_Soft | 0.25 | **软边（原 17+21 合并为连续滑条）**：ScreenSpace 下 0=锐边（全分辨率 RT 单点采样）、>0=**2× 超采样 RT + 消费端 3×3 软核**，核间距=值×6px；LightSpace 下 PCF 块间距=值×6texel（0=单块）。RT 倍率只在跨 0 时重建，中间值即时推送 |
| 07_LS_Resolution | 2048 | LightSpace 专属：阴影图边长。1024=低（≈8MB）/ 2048=中（≈32MB，默认）/ 4096=高（≈128MB），显存按角色数线性（原 23 号） |

深度门 bias 定死为 0.005 米（C# 常量 `HairShadowBiasMeters` 每帧推送）：贴头皮碎斑 ↔ 眨眼吞影的实测折中，不暴露配置。
已删除：`09_FaceOcclusionRestore`（全脸遮挡恢复，审美否决，shader 侧恢复分支一并清除）、`19_HairShadowParts`（部件档位固定为前发——后发入镜会把影子糊到脸侧/下颌；饰品 shader 标记收集不受影响）、`07_LS_Bias`（见上，定死常量）。发影参与渲染的部件 = 前发 + 被 ME 标记的饰品。

阈值图只支持 `cf_O_face` 与 `cf_O_face_SphN`，两者共用已验证一致的 UV 布局。
取图链只有两级：`SDF/` 下的角度帧 → 同目录 `SDF/SDF.png`。**都拿不到就整层不生效**。
v0.7 移除了两条旧来源——代码生成的固定 UV 鼻翼/颊上模板，以及 8 位
`<mesh>_structured.png`，两者实测都把三角光和鼻影画成圆圈。
运行时贴图常驻 `RGBAHalf`，避免 8 位阈值量化造成旋转分层。

**帧必须直接放在该目录下**：扫描不递归子目录（放进 `_orig/` 之类会被忽略），
文件名必须是纯数字。每一步取图结果都会打日志，素材没被读到时看
`Manual SDF source dir for 'cf_O_face': <实际目录>` 这条即可定位。

#### 手工角度帧

在 `UserData/PluginData/EC_FaceSDFShadow/SDF/` 放置同一 UV 对齐的 `1024x1024` PNG。
运行时阈值图保持 1024；输入必须是方形且至少 1024x1024，高于 1024 的输入会按 bilinear
降采样到 1024，小于 1024 或非方图会拒绝并继续既定回退链。文件名**必须是纯数字**
（如 `0.png`、`8.png`），不带任何前缀。

帧语义（双边界受光窗口，2026-08-19 起）：白色 = 受光、黑色 = 阴影，只需画**左光**一套
（右光由 UV 水平镜像得到）。每个 texel 被提取成一个受光窗口 `[lo, hi]`：光照
sideAngle 落在窗口内 = 受光，窗口外 = 阴影——「暗→亮→暗」的双峰时序（如下颚带：
正面暗、侧光亮、背面暗）是合法输入。通道打包（R/B=右光 hi/lo、G/A=左光 hi/lo）
全部由插件生成，用户不画。可直接照抄的九帧模板见 [Resources/exampleSDF/](Resources/exampleSDF/)
（`0.png`=背面光 → `8.png`=正面光，白=受光黑=阴影）。

无 `angles.txt` 时零配置：帧索引须连续 0..maxIndex，帧序约定 `0=背面（sideAngle 1.0）
→ maxIndex=正面（sideAngle 0.0）`，均匀覆盖 sideAngle 域 0..1（9 帧 → 4=0.5）。
也可放 `SDF/angles.txt`（每行一个 sideAngle，第 k 行 = 第 k 个存在帧；须严格递减且
含 1.0/0.0 端点）做真正非均匀覆盖，否则拒绝帧集回退。外部 `SDF.png` 放在同层
`SDF/SDF.png`，与手绘帧同目录。

按 `Ctrl+F8`（Debug 构建）后，转换出的左右阈值诊断图写入
`UserData/PluginData/EC_FaceSDFShadow/manual/`，并在插件根目录额外导出 dilation 前的
`<mesh>_manual_pre_dilation_coverage.png`；R/G 的 `0/15/30/45/60/75/90deg` lit slices 仍写入
`manual/`。该 coverage 才能用于 chart/洞距离隔离，不能用 `<mesh>_structured.png` 的膨胀后 alpha 反推。开启 `ManualContourInterpolation` 后日志还会输出
chart 数、transition 数、distance/total ms 和 fallback 数；默认 raw 路径不变。

如果只提供外部 `SDF.png`，当前插件按其 `0..90°` 归一化约定乘 `0.5` 转为 mode 2 的 `0..180°`
阈值；该 fallback 尚未通过 90° 端点验收（输入最大灰度平台的 after-last-frame 语义未定），因此推荐
同时保留状态帧，用 0/30/60/90 度回放检查方向和比例。

### 关于手工帧锯齿（2026-08-09）

contour 子帧插值（ManualContourInterpolation）解决的是**帧间时间台阶**（大块 texel 锁在同一角度区间），它保留关键帧
空间轮廓，**不解决**低分辨率二值源帧的像素阶梯。当前运行时拒绝小于 1024 的输入；要消除
空间锯齿必须直接提供 1024 源帧或
带抗锯齿过渡带的手绘帧；仅调 `05_SoftnessAngle`/模糊无法消除像素阶梯。

### 关于锯齿

阴影边缘的锯齿有两个来源，都不靠 shader 端解决：

- **空间锯齿**（源帧像素阶梯）：只能提供 1024 以上、带抗锯齿过渡带的手绘帧。
- **帧间台阶**（C1 不连续导致的白线/阶梯）：靠 `ManualSDFBlurSigma` 对阈值场做空间高斯
  平滑，实测默认 2 即可消除；调大更平滑但轮廓会外扩。`ManualContourInterpolation`
  负责时间维度的子帧插值，两者互补。
- **端点折线**（仅正面 0°/背面 180° 恰好打光时，边界呈一段段直线拼的折线；偏离 1° 即平滑）：
  光角在端点时可见边界恰好是"零距离等高线"，骑在手绘帧轮廓的原始像素锯齿上；偏离端点后
  等高线偏移进插值建出的连续坡所以平滑。靠 `ManualEndpointSnapDegrees` 把光角钳离端点
  （水位抬高、阈值场不动），端点处显示已平滑的偏离形态。改它即时生效（shader uniform），
  不触发阈值图重烘焙。

### BlendCompensation（表情 UV 补偿）

眨眼/眯眼/闭单眼等表情会把皮肤连同 SDF 阈值图案一起拉动，本组按 blendshape 权重驱动
5 分区 affine UV 偏移把图案"钉"回皮肤。v0.10 起默认开启。

| 键 | 默认 | 说明 |
|---|---|---|
| BlendCompensation | true | 主开关。开启后颊/眼分区始终参与补偿 |
| BlendCompMouth | true | 嘴区 affine 也参与（默认开）。离线拟合显示嘴区 affine 修正有限，此开关保留作对比/回退 |
| DefClBase | 0 | def_cl（闭眼）blendshape 的基线权重，先扣再累加。0=中性姿势全睁眼（与离线拟合一致）；若阈值图是按游戏"睁眼 1"姿势（def_cl=23）画的、中立面有静态偏移，填 23 归零 |
| Gain | 0.6 | 补偿幅度乘子。0.6 为 2026-08-16 Gram 投影修正后的实测最优（1.0=完整离线拟合幅度、屏幕上略过冲）。图案与皮肤反向移动调低，仍拖拽则向 1 调高 |
| LiveMode | true | 运行时实测补偿：每帧 BakeMesh 对照"无表情参照"（面部无表情时自动捕获，约 10 帧稳定；眼/嘴 def 插值通道豁免，默认闭嘴姿势也算无表情）。对任意头 mesh 与任意驱动源（blendshape、FBSAssist 颊动画、骨骼、第三方）都正确。参照建立前由离线表兜底（眨眼/单闭眼离线表已足够） |

### Advanced

| 键 | 默认 | 说明 |
|---|---|---|
| RenderQueue | 2360 | 叠加层渲染顺序。EC 面部材质是 2350，必须大于它 |
| ColorMixerKey | F7 | 阴影取色器呼出键（单键，仅在捏人界面生效）。见上节"阴影取色器" |
| FaceNoSelfCast | true | **脸不作为实时投影源**（原 10 号）：干净底色生效期间把 face renderer 的 shadowCastingMode 设 Off——屏幕空间阴影图分不清投影来源，脸自己的投影混回来是纯黑，只能从源头切除（外部投影保留）。代价：LightingEnhance 地面 proxy 缺脸部贡献。**ShadowsOnly 会隐形 renderer，禁止**。本项需开启 ConfigurationManager 的 Show advanced settings 才显示（Advanced tag） |
| DisplayPreset | light smooth | **显示模式一键预设**：按选中档写入 05_SoftnessAngle、NeckEdgeSoftness、NeckRampScale、06_Soft 四项（各自既有生效链接管，即时生效/重建）。标定于阴影密度 0.55。档位表：<br>`light smooth`=5/0.088/0.78/0.3、`light hard`=0/0.008/0.819/0.0、`depth smooth`=0/0.1866/0.7/0.8、`depth hard`=0/0.001/0.664/0.0、`absolute smooth`=30/0.137/0.65/1 |

改动 ManualContourInterpolation / ManualSDFBlurSigma 会自动废弃阈值图缓存并重新生成。

## 逐角色软开关（MaterialEditor）

每个角色的叠加材质在 MaterialEditor 的 `Rainbowing/FaceSDFOverlay` 下暴露四项，由插件在运行时动态登记
（Enable / ShadowColor / ThresholdBias / SoftnessAngle）：

- **Enable** — 滑条 0~1，单独关闭某个角色的面部 SDF 阴影而其他角色不受影响。
  默认生效（插件 Apply 时 `SetFloat(_Enable, 1)`），玩家在 ME 里把滑条拉到 0 软关闭：
  shader 输出白 + 还原面部 ramp（显示原版阴影），overlay 仍留在材质数组里、面板始终可见，
  拉回 1 自动恢复。
- **ShadowColor / ThresholdBias / SoftnessAngle** — 未改过时实时跟随全局
  `02_ShadowColor`/`04_ThresholdBias`/`05_SoftnessAngle`；在 ME 里改过（或取色器
  "应用到当前捏人角色"）即锁定为本卡自己的值，全局不再覆盖，锁存标志随卡保存
  （详见"全局跟随与 ME 定制锁存"）。

机制：shader 内 `_Enable < 0.5` 时 frag 直接输出白色（乘算不压暗=不生效），否则走阴影逻辑；
`_Enable` 是普通 Float 属性，ME 的 Float 滑条写回 `SetFloat("_Enable")`。不依赖 keyword 变体。

若未安装 MaterialEditor：插件正常工作，全局 `00_Enabled` 开关保底，无逐角色软开关能力（登记失败降级，不报错）。

## 诊断（Debug 构建快捷键）

| 快捷键 | 说明 |
|---|---|
| Ctrl+F8 | 清缓存重新生成阈值图，并导出诊断 PNG 到 `UserData/PluginData/EC_FaceSDFShadow/` |
| Ctrl+F9 | 打印面部材质的贴图属性 |
| Ctrl+F10 | 枚举面部 BlendShape 通道（index/name/weight） |
| Ctrl+F11 | 打印下一帧的表情 UV 补偿累加值 |
| Ctrl+F12 | BakeMesh 验证：分离 blendshape 与骨骼的顶点位移贡献 |

阈值图 shader 采样：R/G 是左右侧受光窗口边界，B/A 是对应通道有效标记；侧光角用
`abs(yaw)/PI`（正面 0、背面 1）。过渡带宽 = `max(fwidth×1.5px 抗锯齿下限, _SoftnessAngle/180)`，
软化在**角度域**恒定、不随镜头距离变化（v0.8 起；旧的屏幕像素域方案拉远时会把阈值场
帧轮廓的 C1 折线显影成白线，已废弃）。`_ShadowColor.a` 明确定义为阴影强度。

## 手绘阈值图

`SDF.png`：角度帧不可用时的后备来源，放在与角度帧同一目录。按 `0..90°` 归一化约定读入，
乘 `0.5` 转为 mode 2 的 `0..180°` 阈值。优先使用多帧输入。

## 与 EC_FaceNormalSmooth 的关系

两者独立、可同时启用、无代码耦合：

- **EC_FaceNormalSmooth** 改渲染法线，让**全头**明暗过渡柔和（它会换 mesh 实例）
- **EC_FaceSDFShadow**（本插件）在**脸部**叠硬边阴影

本插件会检测 mesh 被替换并自动重取阈值图。

## 已知限制

- **yaw-only**：面部主体只响应光照的水平旋转。极端俯仰由 pitch 渐暗门兜底
  （进入头轴 37° 锥渐变全暗，顶/底对称）；锥外的中等俯仰无逐角度素材、
  冻结水平帧（标准 SDF 面部阴影皆如此，非缺陷）。颈带由 N·L 复制品分支
  消费完整光向（含 pitch），不受此限。
- 阈值图只支持 `cf_O_face` / `cf_O_face_SphN`。眼睛/眉毛/睫毛/鼻线是独立
  renderer，不受影响。
- 捏脸不影响阈值图：捏脸走骨骼变换（`sibFace` 的 vctPos/vctRot/vctScl），
  不改 bind 空间顶点与 UV，故同一 headId 共用一张图。

## 兼容性

EC 全场景（捏脸/HEdit/HPlay/ADV）。换头、换角色、切场景后自动重新生效。
与 MaterialEditor 共存：本插件只追加材质、只改 `_RampG` 与 `_FaceShadowG` 两个全局面材质属性
（均 local override 只作用面部实例）；
叠加层材质的 `_ShadowColor` 等三项形态参数未被 ME 定制时实时跟随全局 02/04/05，
定制后锁定为该卡自己的值（v0.10 起）。
插件运行时向 ME 注入 `Rainbowing/FaceSDFOverlay` 专属属性桶（`Enable` / `ShadowColor` /
`ThresholdBias` / `SoftnessAngle`）供逐角色软开关与调色，未装 ME 时全局开关保底。

## 网络参考

调研/实现过程中实际参考过的工作：

**面部 SDF 阴影（整体形态参照）**
- [赛马娘/原神风格卡通渲染分析（知乎）](https://zhuanlan.zhihu.com/p/232450616) — SDF 面部阴影的做法与 industry 惯例
- [仿原神渲染 2.0 技术文档-角色渲染篇](https://himoqiuhan.github.io/2023/12/07/Projects-GenshinLikeRenderingInURP2-Avatar/index.html) — SDF/Ramp 章节确认了本插件的方向

**头发投影（发影双形态的来源）**
- [NPR 仿星铁角色逐物体阴影 — Garden of Recollection](https://km.stalomeow.com/p/toon-main-light-character-shadow/) — 光空间 per-object shadow map 的完整实现（形态 B 的直接参照：包围盒→View/Ortho→接收端矩阵采样）
- [PerObjectShadowSRP](https://github.com/GavinKG/PerObjectShadowSRP) — 同思路的 SRP 开源实现
- [Unity Manual: Shadow mapping](https://docs.unity3d.com/6000.2/Documentation/Manual/shadow-mapping.html) — 正交 shadow map 与深度比较的基础约定
- [UWA 二次元卡通渲染——进阶技巧（基于深度的额发投影）](https://blog.uwa4d.com/archives/USparkle_Carton.html) — built-in 管线的 depth-based 刘海投影，与形态 B 同族
- [MetaHookSv HairShadow](https://github.com/hzqst/MetaHookSv/blob/main/memory/HairShadow.md) — 屏幕空间 stencil 遮挡位图方案（调研对比项，防 acne 的顶点偏移思路被记录）
- [MooaToon：自定义头发阴影](https://mooatoon.com/docs/Tutorial/ControlTheShapeOfShadows) — 可控形状的额发阴影（资产面片路线，因发型不可泛化而未采用）

**调研覆盖但未采用的路线**（判负理由见知识库 offset-shadow-hybrid-rt.md）
- [Unity Projector 组件](https://docs.unity3d.com/2022.3/Documentation/Manual/class-Projector.html) / [Dynamic Shadow Projector 资产](https://assetstore.unity.com/packages/tools/particles-effects/dynamic-shadow-projector-35558) — 投影器重投路线
- [仿终末地渲染学习记录（知乎）](https://zhuanlan.zhihu.com/p/2054958065078871199) / [UE5 卡通渲染·刘海阴影（知乎）](https://zhuanlan.zhihu.com/p/1972323352069797451) — 静态阴影面片/贴花路线
