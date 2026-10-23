## 子脚本 `<sub-script>`

`<sub-script>` 命令：可以预先为一个较复杂的脚本定义一个新名。以后只要引用这一新名即视为引用了整个脚本，减轻码字负担。

下面的例子为，将一脚本（令角色 N（N=hensu1）在 X 轴方向上旋转 1 度）定义为名为「sample001」的子脚本。

```
<script>
<sub-script name="sample001" param="1">
  <char-ctrl char="#SUB.PARAM.1#" mode="rot" x="1.0"/>
</sub-script>
<call-sub name="sample001" param1="#hensu1#"/>
</script>
```

- `name` 指定子脚本的名称
- `param` 指定引用参数的个数。（可省略，默认 0）指定 1 以上后，`<sub-script>` 中就可以使用系统变量 `#SUB.PARAM.1#` 来代替原脚本中的变量位置。引用第二个参数时记述为 `#SUB.PARAM.2#`，以此类推。

`<break/>` 命令可以从子脚本中强制返回到 `<call-sub>` 调用处。

## 子脚本的调用 `<call-sub>`（Ver.0.3 追加）

`<call-sub>` 命令：调用 `<sub-script>` 命令定义的子脚本。

```
<script>
<call-sub name="sample001" param1="#hensu1#"/>
</script>
```

- `name` 已定义的子脚本名称
- `param1`、`param2`……在 `<sub-script>` 引用了几个参数就要写几个。不能多写也不能省略。仅用于读取的话可以指定为定值，值要发生变化（或运算）的场合则必须指定为变量。

## 加载外部脚本 `<load-script>`（Ver.0.6 新增）

`<load-script>` 命令：从外部文件加载并执行脚本。文件路径相对于 EC 的 data 目录。

```
<script>
<load-script file="myscript.xml"/>
</script>
```

- `file` 指定脚本文件路径。可以是变量也可以是定值。文件需为 UTF-8 编码的 XML 格式，内容为 `<script>～</script>` 中可编写的脚本。文件本身必须自带字面小写 `<script>…</script>` 标签：游戏用字面查找定位该块，缺标签（或写成 `<SCRIPT>`、`<script >`）时整份按普通文本处理，不执行也不报错；多段只执行第一段。详见 [export-format.md](../export-format.md) 的外部依赖一节。

## 重置事件脚本 `<reset-script>`（Ver.0.6 新增）

`<reset-script>` 命令：清除所有通过 `<event-part>` 登录的脚本以及所有子脚本定义。

```
<script>
<reset-script/>
</script>
```

此命令无需任何参数。

