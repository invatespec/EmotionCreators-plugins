# EC_ItemParentPreserve - ADV 物品切换连接目标时保持位置

## 功能说明

ADV 场景中,物品可以连接到角色(`Non` / `CharaRoot` / `CharaLeftHand` / `CharaRightHand`)。原版切换连接目标时,物品的局部坐标不变、父级变了,导致物品在场景中的位置和朝向跟着跳动。

本插件在切换连接目标前后自动保持物品的世界位置与世界旋转不变:切换前记录世界位姿,切换后按新父级反算局部量写回,Transform 面板数值同步刷新。结果直接写入原生数据,场景保存后位置一致,不产生额外数据。

## 使用方法

1. 将 `EC_ItemParentPreserve.dll` 复制到 `BepInEx\plugins\` 目录
2. 进入 ADV 场景,选中一个物品,在 `State` 面板的 Transform 区域找到 gizmo 编辑按钮
3. 该按钮左侧会多出一个「保持位置」按钮,点击切换开/关(关闭时按钮变暗)
   - 开启:切换连接目标 / 连接角色时,物品位置不动
   - 关闭:恢复原版行为

## 配置说明

配置文件:`BepInEx\config\EC_ItemParentPreserve.cfg`

```ini
[General]
## 保持位置开关的默认状态
# Setting type: Boolean
# Default value: true
EnableByDefault = true
```

若 UI 注入失败(日志有 Warning),功能仍按 `EnableByDefault` 生效。

## 已知限制

- 只保持 position 与 rotation,不处理 scale
- 只作用于 ADV 场景的物品连接 UI;cut 加载 / 场景重进不受影响
- 仅对 Emotion Creators 官方 UI 层级测试;其他修改 TransformUICtrl 的插件可能影响按钮注入

## 依赖

- BepInEx 5.4.x
