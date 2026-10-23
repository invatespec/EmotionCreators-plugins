## 条件分支 `<if>`

`<if>` 命令：创建脚本内的条件语句。

下面的例子为，检测「变量 1」的值是否为 100，如果是，则为「变量 1」加 1。`<if>` 后条件成立的话，会继续执行 `</if>` 前的所有语句。if 结构里可以嵌套新 if。

```
<script>
<if value1="#hensu1#" operator="eq" value2="100">
  <compute result="#hensu1#" value1="#hensu1#" operator="+" value2="1"/>
</if>
</script>
```

- `value1` 指定要比较的变量名或定值。
- `value2` 指定要比较的变量名或定值。
- `operator` 指定比较符号：

| 运算符 | 含义 |
|--------|------|
| `eq` | value1 = value2 |
| `not` | value1 ≠ value2 |
| `lt` | value1 < value2 |
| `gt` | value1 > value2 |
| `lteq` | value1 ≤ value2 |
| `gteq` | value1 ≥ value2 |

## 循环 `<loop>`

`<loop>` 命令：循环执行一段命令。

下面的例子为，「变量 1」若大于等于 100 即循环执行命令。条件的指定方法和 `<if>` 命令相同。可以嵌套。

```
<script>
<loop value1="#hensu1#" operator="gteq" value2="100">
  <compute result="#hensu1#" value1="#hensu1#" operator="+" value2="1"/>
</loop>
</script>
```

- `value1`、`value2`、`operator` 同 `<if>` 命令。

※`<loop>` 命令有陷入死循环的可能，敬请留意。如果一个脚本执行超过 **LimitTime 秒**（默认 5 秒，可在配置文件中更改）以上还未结束，会强制中断并显示「发生错误」。

## 终止循环 `<break>`

`<break>` 命令：中断当前循环。

下面的例子为，「变量 1」若大于等于 100 即循环执行命令，「变量 2」为 1 时终止循环。

```
<script>
<loop value1="#hensu1#" operator="gteq" value2="100">
  <compute result="#hensu1#" value1="#hensu1#" operator="+" value2="1"/>
  <if value1="#hensu2#" operator="eq" value2="1">
    <break/>
  </if>
</loop>
</script>
```

在 `<event-update>` 命令中也可以用 `<break>` 停止循环。（Ver.0.3 追加）

## 跳转至指定剪辑 `<goto-cut>`

`<goto-cut>` 命令：移动到当前片段内的另一剪辑。

下面的例子为，「变量 1」≥ 5 时，跳转至剪辑 3。

```
<script>
<if value1="#hensu1#" operator="gteq" value2="5">
  <goto-cut index="2"/>
</if>
</script>
```

- `index` 指定目标剪辑的编号。可以是变量也可以是定值。由于脚本内剪辑编号是从 0 开始的，所以写脚本时要减去 1。

**Ver.0.6 新增：** index 还支持以下特殊关键字：

| 关键字 | 含义 |
|--------|------|
| `next` | 下一剪辑 |
| `prev` | 上一剪辑（当前为剪辑 1 时不可用） |
| `end` | 当前片段的最终剪辑 |

※不要指定不存在的编号，会死机。

## 跳转至指定片段 `<goto-part>`

`<goto-part>` 命令：跳转至与现在不同的其他片段。

下面的例子为，「变量 1」≥ 5 的话，跳转至「ADV1」的「剪辑 3」。

```
<script>
<if value1="#hensu1#" operator="gteq" value2="5">
  <goto-part name="ADV1" index="2"/>
</if>
</script>
```

- `name` 指定目标片段名。请不要制作名字相同的片段。指定重名片段的话，会跳转至先制作出的片段。
- `index` 指定要跳转的剪辑编号。（可省略，默认跳转到剪辑 1）指定方法和 `<goto-cut>` 命令相同。如果跳转到 H 片段则不可以指定剪辑。

※如果指定了不存在的片段名，会移动到正常情况下该片段的下一片段。

