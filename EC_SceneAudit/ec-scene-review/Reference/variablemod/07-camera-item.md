## 相机控制 `<camera-ctrl>`（Ver.0.6 新增）

`<camera-ctrl>` 命令：可以变更相机的位置、旋转、方向、FOV（视野角）以及重置相机。

```
<script>
<camera-ctrl mode="pos" type="rel" x="0" y="1.0" z="-2.0"/>
<camera-ctrl mode="rot" x="5.0"/>
<camera-ctrl mode="dir" z="10.0"/>
<camera-ctrl mode="fov" value="30.0"/>
<camera-ctrl mode="reset"/>
</script>
```

- `mode` 指定要改变的属性：
  - `pos` — 相机位置
  - `rot` — 相机旋转
  - `dir` — 相机方向
  - `fov` — 视野角（FOV），用 `value` 属性指定
  - `reset` — 将相机重置为进入该剪辑时的初始状态
- `type` 指定调整方法（可省略）：
  - `rel` — 相对原始值（默认）
  - `nowrel` — 相对当前值
  - `abs` — 绝对值
- `x`、`y`、`z` 指定坐标/旋转值（可以是变量也可以是定值）
- `value` 指定数值，用于 fov 模式

## 物品控制 `<item-ctrl>`（Ver.0.6 新增）

`<item-ctrl>` 命令：可以变更物品的位置、旋转和缩放。

```
<script>
<item-ctrl item="0" mode="pos" type="rel" x="0" y="0.5" z="0"/>
<item-ctrl item="0" mode="rot" z="45.0"/>
<item-ctrl item="0" mode="scale" x="1.5" y="1.5" z="1.5"/>
</script>
```

- `item` 指定物品编号（从 0 开始）。可以是变量也可以是定值
- `mode` 指定要改变的属性：
  - `pos` — 物品位置
  - `rot` — 物品旋转
  - `scale` — 物品缩放
- `type` 指定调整方法（可省略）：
  - `rel` — 相对原始值（默认）
  - `nowrel` — 相对当前值
  - `abs` — 绝对值
- `x`、`y`、`z` 指定坐标/旋转/缩放值

