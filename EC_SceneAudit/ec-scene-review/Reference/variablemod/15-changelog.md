# 版本更新记录

## Ver.0.6 新增功能

- **相机控制 `<camera-ctrl>`**：新增相机位置、旋转、方向、FOV 和重置功能
- **物品控制 `<item-ctrl>`**：新增物品位置、旋转和缩放控制
- **外部脚本加载 `<load-script>`**：支持从外部文件加载脚本
- **事件脚本重置 `<reset-script>`**：清除所有 event-part 和子脚本
- **周期计算 `<calc-update>`**：在 event-update 中便捷地进行插值计算
- **`<goto-cut>` 增强**：index 支持 `next`/`prev`/`end` 关键字
- **`<anime-cut>` 增强**：新增 `smooth` 平滑过渡属性
- **`<event-update>` 增强**：新增 `count`/`start`/`end` 属性
- **`<char-ctrl>` IK 拆分**：`ik` 拆分为 `ik_pos` 和 `ik_rot`
- **新增系统变量**：`#SYS.EVENT.UPDATE.TIME.HALF#`、`#SYS.EVENT.UPDATE.TIME.BASE#`、`#SYS.EVENT.UPDATE.TIME.TOTAL#`、`#SYS.EVENT.UPDATE.REPEAT.FLAG#`、`#SYS.EVENT.UPDATE.PERIOD.FLAG2#`、`#SYS.EVENT.UPDATE.PERIOD.COUNT#`
- **超 8 人角色自动注册系统变量**
- DLL 更名为 `EC_VariableMod_B52.dll`（BepInEx 5.2+ 专用）
