## 定义数组变量 `<array>`

`<array>` 命令：定义数组变量（一维或二维）。

下面的例子为，定义一个 10×10 的数组变量。

```
<script>
<array name="#array1#" type="integer" dim1="10" dim2="10"/>
</script>
```

- `name` 指定数组变量的名称。指定方法与 `<variable>` 命令相同
- `type` 指定数组变量的类型。指定方法与 `<variable>` 命令相同
- `dim1` 指定第一维的元素数
- `dim2` 指定第二维的元素数。（可省略，此时为一维数组变量）

※数组变量与普通变量的处理方式不同，`<compute>` 和 `<if>` 命令等不能直接引用数组变量，请通过 `<array-get>` 从变量中取得值。

## 数组变量的赋值 `<array-set>`

`<array-set>` 命令：为数组变量中指定的元素赋值。

下面的例子为，为数组变量 dim1=1, dim2=0 的元素赋「变量 1」的值。

```
<script>
<array-set name="#array1#" value="#hensu1#" dim1="1" dim2="0"/>
</script>
```

- `name` 指定数组变量的名称
- `value` 指定要代入的变量名或定值
- `dim1` 数组变量第 1 维索引
- `dim2` 数组变量第 2 维索引（可省略）

※数组的元素编号从 0 开始。如有 10 个元素，它们的编号为 0~9。

## 数组变量的取得 `<array-get>`

`<array-get>` 命令：取得数组变量中指定元素的值。

下面的例子为，为「变量 1」赋数组变量中 dim1=4, dim2=2 元素的值。

```
<script>
<array-get name="#array1#" result="#hensu1#" dim1="4" dim2="2"/>
</script>
```

- `name` 指定数组变量的名称
- `result` 指定要赋值的变量名
- `dim1` 数组变量第 1 维索引
- `dim2` 数组变量第 2 维索引（可省略）

