## 剪辑单击事件 `<event-next>`

`<event-next>` 命令：这一命令所记述的脚本内容，将在剪辑内单击时执行。

下面的例子为，本来在剪辑内单击会向下一剪辑移动，但如果「变量 1」等于 2 的话，就移动至「ADV2」的「剪辑 5」。

```
<script>
<event-next>
  <if value1="#hensu1#" operator="eq" value2="2">
    <goto-part name="ADV2" index="5"/>
  </if>
</event-next>
</script>
```

`<event-next>～</event-next>` 的内容不会自动执行，而是直到玩家单击时才开始执行。此外，这一命令不可以用在 `<if>` 和 `<loop>` 结构中。

## 选项单击事件 `<event-select>`

`<event-select>` 命令：这一命令所记述的脚本内容，将在单击选项时执行。**只能在选项文本框中使用这一命令。**

下面的例子为，本来单击选项会向选项所指的片段移动，但如果「变量 1」加 1 后的结果大于 5，就移动至另一片段「ADV3」。

```
<script>
<event-select>
  <compute result="#hensu1#" value1="#hensu1#" operator="+" value2="1"/>
  <if value1="#hensu1#" operator="gteq" value2="5">
    <goto-part name="ADV3"/>
  </if>
</event-select>
</script>
```

※只能在选项文本框中使用这一命令。此外，由于是写进选项文本框里的，此命令内也不能加入「隐藏文本框」选项。`<event-select>～</event-select>` 的内容直到玩家单击选项时才开始执行。这一命令不可以用在 `<if>` 和 `<loop>` 结构中。

## 片段指定事件 `<event-part>`（Ver.0.3 追加）

`<event-part>` 命令：可以预先为指定的片段（或指定片段的剪辑）登录一条脚本，在那一片段或剪辑显示出来时执行。※使用这一命令，就可以在 H Part 中执行脚本了。

```
<script>
<event-part name="ADV1" index="0">
  <variable name="#local1#" type="double" set="-4.0"/>
  <char-ctrl char="0" mode="rot" x="#local1#"/>
</event-part>
</script>
```

> **注：** 示例中的 `#local1#` 变量定义时未指定 `mode`，因此默认为全局变量（global），并非区块变量。

- `name` 指定要执行脚本的片段名
- `index` 指定剪辑编号。依然是从 0 开始。（可省略）

※这一命令登录的脚本，在来到指定片段时执行优先率最高。

## 指定间隔事件 `<event-update>`（Ver.0.3 追加，Ver.0.6 增强）

`<event-update>` 命令：可以以指定的间隔执行脚本。

下面的例子为，以 0.1 秒的间隔让角色 0 每次沿 X 轴方向旋转 1 度。

```
<script>
<variable name="#local1#" type="double" set="1.0"/>
<event-update wait="100" period="2000">
  <char-ctrl char="0" mode="rot" x="#local1#"/>
</event-update>
</script>
```

- `wait` 指定间隔的毫秒数。（推荐设为 100 毫秒左右）过小可能会出问题。此外如果剪辑使用了淡入淡出效果，可能会影响开始的间隔计算。
- `period` 指定一周期的毫秒数。（可省略）推荐设为 wait 的倍数。
- `count` **（Ver.0.6 新增）** 指定周期重复次数。（可省略）设为 0 以上时，`#SYS.EVENT.UPDATE.PERIOD.COUNT#` 会随周期递增，超过 count 后重置为 0。
- `start` **（Ver.0.6 新增）** 指定延迟开始（毫秒）。可以是变量也可以是定值。（可省略）
- `end` **（Ver.0.6 新增）** 指定结束时间（毫秒）。可以是变量也可以是定值。（可省略）

`<break>` 命令也可以停止这一命令。将这一命令和 `<char-ctrl>` 命令组合使用，就能做到在 1 帧剪辑内让人物动起来。

