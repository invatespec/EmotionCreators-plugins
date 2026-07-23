# 地面阴影增强

## 这是什么？

EC 原版里，角色在地面上的影子只有一个简单的人体模型形状——穿了什么衣服、什么发型都看不出来。**这个功能可以让地面上的影子像人物之间的影子一样精细**，能看到完整的人物轮廓和服饰细节。

## 怎么开？

进游戏后按 F1 打开插件管理器，搜索"Lighting Enhance"，把 `Shadow Quality` → `Enabled` 改成 `true` 就行了。其他选项一般不用动，默认值就是最佳效果。

## 其他配置项

一般不需要动，默认值就是最佳效果。如果遇到问题再调：
- **CullingMaskAddChara** — **核心开关**。开启精细地面阴影。默认配合 `UseShadowProxies` 使用，不会让地图光直接照到角色。
- **UseShadowProxies** — 默认开启。用不可见的 Map layer 影子代理投射角色轮廓，而不是让地图光直接照到 Chara layer；这样能保留地面精细阴影，同时避免地图光抢走角色受光。
- **ShadowResolution** — 阴影贴图分辨率。`VeryHigh` 最清晰，觉得卡可以降到 `High` 或 `Medium`。
- **ShadowCustomResolution** — 自定义分辨率数值（-1 = 不用）。如果想手动指定如 4096，可以设这个。
- **ShadowStrength** — 阴影深浅。默认 `1.0`，越小越淡。
- **ShadowBias** — 阴影偏移。设太低阴影会贴得太紧出现锯齿，设太高阴影会漂移。默认 `0.05` 是平衡值。
- **ShadowNormalBias** — 法线偏移。类似上面，控制阴影紧贴表面的程度，默认 `0.4`。
- **ShadowNearPlane** — 阴影近平面。值越大近处阴影越少，默认 `0.2`。

## 角色自阴影方向同步

- **SyncCameraDirectionalLightEulerAngles** — 默认开启。游戏里角色身上有一处独立的阴影光，原本会跟着镜头一起转——镜头动了，阴影方向也跟着变。开启后，这处阴影的方向会锁定为你在光照面板里调好的角度，不再受镜头影响。如果关闭这个开关，镜头旋转时角色身上的阴影方向会跟着变回原版行为。

## 性能影响

开启后 GPU 需要额外计算高质量的阴影，**中低配电脑可能会掉帧**。如果觉得卡，可以把 `ShadowResolution` 降为 `High` 或 `Medium`。

## 注意事项

- 默认设置已经处理好了角色光和地图光的冲突，一般只需要打开 `Shadow Quality` → `Enabled`。
- `Light Priority` 是排查用的高级选项，不建议日常调整。尤其不要把地图光改成 `ForceVertex`，这可能会让地面阴影消失。
- 如果开启后觉得卡，优先把 `ShadowResolution` 从 `VeryHigh` 降到 `High` 或 `Medium`。

## 日光同步

整合了**日光同步**功能——地图灯光调整同样作用于 mod 地图里额外日光，同时增添额外日光的亮度倍数，默认是 `0`，可同时在插件管理器里和地图灯光调整：

- `Sunlight Sync` → `SunIntensityMultiplier`：值越大越亮，调完去游戏里动一下光照滑条就会生效。
