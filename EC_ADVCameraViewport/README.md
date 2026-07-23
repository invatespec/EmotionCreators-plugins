# EC_ADVCameraViewport - ADV 摄像机视口 + UI 优化插件

## 功能说明

解决 Emotion Creators ADV 场景编辑时的多个 UI 问题：
1. **摄像机视口缩放** - 缩小渲染区域，四周留白，避免 UI 遮挡场景
2. **ADV Canvas 同步缩放** - 屏幕效果贴图和对话框跟随摄像机视口缩放
3. **自动调整 Chara State 面板** - 点击 Button Chara 时自动移动面板位置
4. **缩小 List 面板宽度** - 减少左侧列表面板占用空间

## 特性

### 摄像机视口
- **快捷键切换**：按 F8 快速开关视口缩放
- **可调缩放比例**：支持 50% - 100% 缩放（默认 75%）
- **位置微调**：支持水平/垂直偏移（-0.5 ~ 0.5）
- **居中显示**：画面自动居中，四周均匀留白

### UI 布局优化
- **智能监测 Toggle**：自动检测 "Button Chara" 的 Toggle 状态
- **动态调整位置**：Toggle 开启时自动移动 State 面板，关闭时恢复
- **List 面板缩放**：自动缩小左侧列表宽度（默认 70%）
- **ADV Canvas 同步**：对话框和屏幕效果跟随摄像机视口同步缩放和偏移

## 使用方法

1. 将 `EC_ADVCameraViewport.dll` 复制到 `BepInEx\plugins\` 目录
2. 启动游戏，进入 ADV 场景
3. 按 `F8` 键切换视口缩放
4. 点击 "Button Chara" 时，State 面板会自动移动到配置的位置

## 配置说明

配置文件：`BepInEx\config\EC_ADVCameraViewport.cfg`

```ini
[Viewport]
## 启用视口缩放功能
# Setting type: Boolean
# Default value: false
Enabled = false

## 视口缩放比例（0.5 ~ 1.0）。1.0 = 全屏，0.75 = 缩小到 75%
# Setting type: Single
# Default value: 0.75
# Acceptable value range: From 0.5 to 1
Scale = 0.75

## 视口水平偏移（-0.5 ~ 0.5）。负值向左，正值向右
# Setting type: Single
# Default value: 0
# Acceptable value range: From -0.5 to 0.5
OffsetX = 0

## 视口垂直偏移（-0.5 ~ 0.5）。负值向下，正值向上
# Setting type: Single
# Default value: 0
# Acceptable value range: From -0.5 to 0.5
OffsetY = 0

[Hotkeys]
## 切换视口缩放的快捷键
# Setting type: KeyboardShortcut
# Default value: F8
ToggleViewport = F8

[UILayout]
## 自动调整 Chara State 面板位置（监测 Button Chara toggle 状态）
# Setting type: Boolean
# Default value: true
AutoAdjustCharaState = true

## Chara State 面板的目标 Y 位置（屏幕高度百分比，0.0 ~ 1.0）。默认 0.667 适配 16:9 / 16:10 分辨率
# Setting type: Single
# Default value: 0.667
# Acceptable value range: From 0 to 1
CharaStatePosY = 0.667

## Chara State 面板的目标 X 位置（屏幕宽度百分比，0.0 ~ 1.0）
# Setting type: Single
# Default value: 0.003
# Acceptable value range: From 0 to 1
CharaStatePosX = 0.003

## 缩小 List 面板及按钮宽度
# Setting type: Boolean
# Default value: true
ShrinkListWidth = true

## List 面板宽度缩放比例（0.5 ~ 1.0）
# Setting type: Single
# Default value: 0.7
# Acceptable value range: From 0.5 to 1
ListWidthScale = 0.7

## 同步缩放 ADV Canvas（屏幕效果贴图和对话框）以匹配摄像机视口
# Setting type: Boolean
# Default value: true
ScaleADVCanvas = true
```

### 配置项说明

#### Viewport（摄像机视口）
- **Enabled**：是否启用视口缩放（通过快捷键切换时会自动修改）
- **Scale**：缩放比例
  - `1.0` = 全屏显示（无缩放）
  - `0.75` = 缩小到 75%，四周各留 12.5% 空白
  - `0.5` = 缩小到 50%，四周各留 25% 空白
- **OffsetX / OffsetY**：视口偏移量（相对于居中位置）
  - `0.0` = 居中显示
  - `0.1` = 向右/向上偏移 10%
  - `-0.1` = 向左/向下偏移 10%
- **ToggleViewport**：切换快捷键（默认 F8）

#### UILayout（UI 布局）
- **AutoAdjustCharaState**：自动调整 Chara State 面板位置
  - 监测 "Button Chara" Toggle 状态
  - Toggle 开启时移动到目标位置，关闭时恢复原位
- **CharaStatePosX**：State 面板的目标 X 坐标（屏幕宽度百分比）
  - 默认 `0.003` ≈ 左侧边缘（1600×900 下约 5px）
  - `0.0` = 屏幕最左侧，`1.0` = 屏幕最右侧
- **CharaStatePosY**：State 面板的目标 Y 坐标（屏幕高度百分比）
  - 默认 `0.667` ≈ 屏幕 2/3 高度（1600×900 下约 600px，1920×1080 下约 720px）
  - `0.0` = 屏幕底部，`1.0` = 屏幕顶部
  - 推荐值：16:9 分辨率用 `0.667`，16:10 分辨率用 `0.667`
- **ShrinkListWidth**：缩小 List 面板宽度
  - 影响："Canvas ADVPart/List" 面板
- **ListWidthScale**：List 宽度缩放比例（默认 0.7 = 缩小到 70%）
- **ScaleADVCanvas**：同步缩放 ADV Canvas
  - 对话框和屏幕效果（CommonSpace/ADV Canvas）跟随摄像机视口同步缩放和偏移
  - 确保对话框不会超出缩小后的渲染区域

## 技术原理

### 摄像机视口缩放
通过调整 Unity Camera 的 `rect` 属性：
```csharp
// 缩放到 75%，居中 + 用户偏移
float scale = 0.75f;
float centerOffset = (1f - scale) * 0.5f; // 0.125
float finalX = centerOffset + offsetX;
float finalY = centerOffset + offsetY;
camera.rect = new Rect(finalX, finalY, scale, scale);
```

### UI 自动调整
1. **Harmony 补丁**：在 `ManipulateUICtrl.Init` 后置补丁中初始化
2. **Toggle 监听**：通过 `toggle.onValueChanged.AddListener` 监听状态变化
3. **位置切换**：保存原始位置，Toggle 开启时移动，关闭时恢复
4. **百分比坐标**：使用 `Screen.width/height` 动态计算实际像素位置
   - 例如：0.667 × 900px = 600px（1600×900 分辨率）
   - 例如：0.667 × 1080px = 720px（1920×1080 分辨率）

### List 面板缩放
直接修改 `RectTransform.sizeDelta`：
```csharp
rectTransform.sizeDelta = new Vector2(originalWidth * scale, height);
```

### ADV Canvas 同步缩放
通过同步 `cameraADVCanvas.rect` 与主相机 viewport，使对话框和屏幕效果跟随渲染区域：

```csharp
// ADVCameraViewport 会同时缩放两个相机：
// 1) cam.thisCmaera     — 主 3D 场景相机
// 2) cameraADVCanvas    — ADV Canvas 专用相机（对话框、屏幕特效）
// 两个相机的 rect 保持同步，确保 UI 渲染在场景区域内部
cameraADVCanvas.rect = new Rect(finalX, finalY, scale, scale);
```

**为什么不用 ScreenSpaceCamera 切换方案？**

| 尝试 | 失败原因 |
|------|---------|
| 修改 ADV Canvas 的 renderMode/worldCamera | 游戏用专用相机 `cameraADVCanvas` 渲染 Canvas，`ADV.SetCanvasCamera()` 会覆盖 |
| 修改 ADV Canvas 的 sizeDelta/position | ScreenSpaceOverlay 下 position 被忽略，子组件 Layout 锁定 sizeDelta |
| ✅ **同步 cameraADVCanvas.rect** | 不改 Canvas 绑定，只缩放专用相机 viewport，与主相机同步 |

## 适用场景

- ADV 场景角色姿势编辑（避免 State 面板遮挡）
- ADV 场景灯光调整（缩小摄像机视口留出空间）
- ADV 场景道具摆放（缩小 List 面板腾出空间）

## 兼容性

- **游戏版本**：Emotion Creators
- **BepInEx**：5.x
- **Unity**：2017.4.24

## 版本历史

### v2.0.0 (2026-06-22)
- 新增：自动调整 Chara State 面板位置（监测 Button Chara Toggle）
- 新增：缩小 List 面板及按钮宽度
- 新增：视口位置偏移配置（OffsetX / OffsetY）
- 优化：使用 Harmony 补丁自动初始化 UI 调整

### v1.0.0 (2026-06-22)
- 初始版本
- 支持快捷键切换视口缩放
- 支持自定义缩放比例

## 许可

此插件为开源项目，遵循项目许可协议。

## 作者

Claude Code

