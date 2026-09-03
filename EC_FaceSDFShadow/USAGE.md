# EC_FaceSDFShadow 使用说明

给角色脸上加一层动漫风格的硬边阴影——就像赛马娘/原神那种清晰的明暗分界线，阴影会跟着光照方向平滑移动。

## 安装

**前置**（游戏一般都装过）：

- BepInEx
- EC_ExtensibleSaveFormat（角色卡扩展数据）
- ECAPI.dll
- MaterialEditor（强烈推荐，用来单独调每个角色）
- EC_Profile（可选，只有「给某个角色单独换阴影形状」时才需要）

## 快速调整（按 F1 打开配置面板）

配置在 **Face SDF Shadow** 分组里，改了立刻生效、所有角色一起变：

| 想改什么 | 用哪个 | 怎么调 |
|---|---|---|
| 阴影颜色和浓淡 | 02_ShadowColor | 建议去ME里调 |
| 阴影边界的位置 | 04_ThresholdBias | 加大 = 阴影变少，减小 = 阴影变多 |
| 阴影边缘软硬 | 05_SoftnessAngle | 0 = 硬边；调大 = 边缘更柔 |
| 脸上原本的实时阴影 | 07_FaceRealtimeShadowG | 默认 1 已排除；设 0 恢复原版 |
| 全部关闭 | 00_Enabled | 关掉后所有角色回到原版阴影 |

**StructuredSDF 分组**：颈部过渡（脸和脖子的阴影衔接），默认值已调好。
- **ManualSDFBlurSigma** — 消除阴影阈值场锯齿
- **ManualEndpointSnapDegrees** — 光扫过正面/背面时阴影形态冻结约 2×该度数让阴影形态显示正常

如果你用其他的阴影轮廓,需要自己去调：

- **NeckRampScale / NeckRampBias** — 对齐脖子两侧的明暗
- **NeckEdgeSoftness** — 脖子阴影的边缘软硬
- **NeckBandTopV** — 往上拉可以盖掉下颌两侧不动的残留阴影，也收窄竖向渐变
- **NeckReplicaCap** — 开自阴影 + 底部打光时，若下颚到脖颈上段有一条"过黑分割线"，
  调低它（如 0.5）只减淡插件那层、让游戏原生投影层次透出来；平时不用动
- **NeckShadowCompensation** — 默认 1（推荐）：插件层自己读游戏真实阴影、和 N·L 合成
  一道工序（配合主开关 00_Enabled 使用，底色由它清干净）。设 0 回旧式对比

**HairShadow 分组**：头发投在脸上的影子，默认**开**。00_ShadowEnabled 只管发影；
脸底色上游戏画的投影由总开关 00_Enabled 负责清（主开关开着就清）。

| 键 | 默认 | 作用 |
|---|---|---|
| 00_ShadowEnabled | 开 | 发影总开关 |
| 01_Form | ScreenSpace | 形态切换，见下 |
| 02_SS_ShiftX / 03_SS_ShiftY | 0.01 / 0.008 | ScreenSpace 形态：影的横向/竖直移动量程 |
| 04_SS_BaseX / 05_SS_BaseY | 0 | ScreenSpace 形态：影带初始位置 |
| 06_Soft | 0.25 | 软边：0 = 锐边；调大 = 更软 |
| 07_LS_Resolution | 2048 | LightSpace 形态：阴影图精度 |

**Advanced 分组**：
- **FaceNoSelfCast**（默认开）— 脸不作为实时投影源
- **DisplayPreset**（默认 light smooth）— 显示模式一键预设，一次写入 05_SoftnessAngle、
  NeckEdgeSoftness、NeckRampScale、06_Soft 四项；标定于阴影密度 0.55，
  其他密度需自行调整阴影色。

| 预设 | 05_SoftnessAngle | NeckEdgeSoftness | NeckRampScale | 06_Soft |
|---|---|---|---|---|
| light smooth | 5 | 0.088 | 0.78 | 0.3 |
| light hard | 0 | 0.008 | 0.819 | 0.0 |
| depth smooth | 0 | 0.1866 | 0.7 | 0.8 |
| depth hard | 0 | 0.001 | 0.664 | 0.0 |
| absolute smooth | 30 | 0.137 | 0.65 | 1 |

**01_Form 两种形态**（默认 ScreenSpace）：ScreenSpace 是上面说的屏幕位移（02~05 生效）；
**LightSpace 是"光空间真投影"**：仿游戏原始的发影效果.

LightSpace 形态的旋钮：**07_LS_Resolution** 调阴影图精度（1024 低 / 2048 中，默认 /
4096 高，只有极限特写才值得开）。06_Soft 两种形态都控软边（LightSpace 的软影半径
随它变宽）。

**BlendCompensation 分组**：尽量让表情（眨眼、眯眼等）不去拉动阴影。默认开启，不用动。

## 只改某一个角色（MaterialEditor）

在 ME 面板找到 `FaceSDFOverlay`：

- **Enable** — 拉到 0 = 这个角色关闭阴影（其他角色不受影响），拉回 1 恢复
- **ShadowColor / ThresholdBias / SoftnessAngle** — 单角色微调

注意：在 ME 里改过哪个属性，哪个属性就变成「这个角色自己的」，不再跟随全局；
没改过的属性继续跟随全局。保存角色卡后一直生效。

恢复跟随全局：在捏人界面按 F7 打开取色器窗口，点「恢复跟随全局（清除本卡定制）」
——当前角色的三项定制全部清除、颜色/边界/软度回到全局值，保存角色卡后生效。

## 饰品头发受影（进阶）

饰品槽里的头发（不是本体头发）默认不参与发影投影。想让某个饰品头发也挡光投脸：

1. 捏人界面选中该饰品 → 打开 MaterialEditor → **复制材质**（Copy Material）
2. 对**复制出来的**材质（名字带 `.MECopy`）点 Change Shader，选 `Rainbowing/HairShadowMarker`

- 标记随角色卡/服装卡保存，读卡、换装后自动恢复
- 撤销：**在 ME 里删除复制材质**
- 别把标记 shader 换到**原材质**上——饰品本体会被隐藏，插件会打警告提示

## 用取色器配阴影颜色（捏人界面按 F7）

你告诉工具「原版阴影的颜色」和「皮肤的颜色」，它算出该填的阴影色：

1. ME 把 FaceSDFOverlay 的 Enable 拉到 0，点 **S 的取色**，点脸上阴影处（期望的阴影色）
2. Enable 拉回 1，点 **P 的取色**，点旁边亮着的皮肤（受光肤色）
3. 点 **应用**；再点 **V 的取色** 点当前阴影处、点校准、再应用——
   重复第 3 步直到颜色匹配（一般 10 轮）

**光强对齐选框**：设置光强为0.9，可显示原本的肤色(应该是原本肤色,反正我用下来没问题)
取消勾选或关窗自动还原，无损。

算好的颜色在控制台日志里，或直接点「应用到当前捏人角色」。

## 自己画阴影形状（进阶）

如果你觉得我画的阈值图太垃可以自己去 `UserData/PluginData/EC_FaceSDFShadow/SDF/` 画一套,需要注意：

1. 这里不知道写什么,删掉,下面2又感觉怪怪的,ai写的东西有些很诡异,怪不得ai会产生幻觉注意力下降(滑稽
2. 最好放 9 张 `1024×1024` 的 PNG，文件名必须是纯数字从0递增.
   - `0.png` = 光在正后方，`8.png` = 光在正前方
   - **白色 = 被照亮，黑色 = 阴影**
   - 只需要画光从角色脸左边来的样子，右边会自动镜像
   - 想自定义每张的角度还可以新建`SDF/angles.txt`（每行一个 0~1 之间的数，从 1.0 递减到 0.0）
   - 也可以用已经烘焙好的外部 `SDF.png`, 与阈值图同目录(不推荐, 算法不一样)。

规则：

- 图片必须正方形、至少 1024×1024（更大也行，会自动缩到 1024）
- 文件直接放在 `SDF/` 文件夹里
- 数字必须从 0 连续排到最大，不能缺号

**只给某一个角色用**：把帧放在 `SDF/perChara/文件夹名/` 里，然后在角色的 Profile 描述框任意位置写一行：

```
@FaceSDFShadow:[文件夹名]
```

方括号用半角 `[]`、全角 `［］` 或 `【】` 都行。没写指令的角色用全局 `SDF/` 的帧。

## 其他说明

尽量不要用一些死亡角度打光, 脖子阴影接缝会穿帮.
