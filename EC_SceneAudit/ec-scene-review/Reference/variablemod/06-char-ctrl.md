## 角色控制 `<char-ctrl>`（Ver.0.3 追加）

`<char-ctrl>` 命令：可以变更指定角色的位置、面向、FK、IK（包括姿势和表情）。

下面的例子为，将角色 0 的位置沿 X 轴和 Y 轴各移动 3.5 单位，在 X 轴方向旋转 -4 度，右肩的 FK 在 Z 轴方向旋转 5 度。

```
<script>
<char-ctrl char="0" mode="pos" type="rel" x="3.5" y="3.5"/>
<char-ctrl char="0" mode="rot" x="-4.0"/>
<char-ctrl char="0" mode="fk_r_arm" index="1" z="5.0"/>
</script>
```

### 参数说明

- `char` 指定剧本登录的人物编号（从 0 开始）
- `mode` 指定需要改变的属性（详见下文）
- `index` 指定 IK / FK 的子部位。mode 为 `pos`、`rot` 时请省略
- `type` 指定调整方法（可省略）
  - `rel` — 相对原始值（默认）
  - `nowrel` — 相对当前值
  - `abs` — 绝对值
- `x`、`y`、`z` 指定坐标/旋转值
- `value` 指定数值，可为定值也可为变量（用于 face、hand 等模式）

### mode 一览

| mode | 说明 |
|------|------|
| `pos` | 角色位置 |
| `rot` | 角色方向 |
| `fk_body` | 角色身体 FK |
| `fk_r_leg` | 角色右腿 FK |
| `fk_l_leg` | 角色左腿 FK |
| `fk_r_arm` | 角色右臂 FK |
| `fk_l_arm` | 角色左臂 FK |
| `fk_r_hand` | 角色右手 FK |
| `fk_l_hand` | 角色左手 FK |
| `fk_breast` | 角色胸部 FK |
| `fk_son` | 角色钉钉 FK |
| `fk_hair` | 角色头发 FK |
| `fk_skirt` | 角色裙子 FK |
| `ik` 或 `ik_pos` | 角色 IK（位置）**（Ver.0.6 拆分）** |
| `ik_rot` | 角色 IK（旋转）**（Ver.0.6 新增）** |
| `face` | 角色的表情 |
| `hand` | 角色的手部动作 |
| `add` | 角色的追加旋转 |

※0.6 版中 `ik` 模式拆分为 `ik_pos`（位置控制）和 `ik_rot`（旋转控制）。旧版 `ik` 仍可使用，等同于 `ik_pos`。

※如果想要调整 FK 或 IK，必须事先将相关设置打开。

### FK / IK index 一览

详见文末附录。

