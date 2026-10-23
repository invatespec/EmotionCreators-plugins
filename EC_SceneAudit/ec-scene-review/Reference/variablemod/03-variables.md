## 定义变量 `<variable>`

`<variable>` 命令：可定义以下三种类型的变量。

- **String** — 字符串变量（如："早上好"）
- **Integer** — 整数型变量（64 位，如：114514）
- **Double** — 浮点型变量（64 位，如：3.1415926）

```
<script>
<variable name="#hensu1#" type="integer"/>
<variable name="#hensu2#" type="double" set="0"/>
<variable name="#hensu3#" type="string"/>
<variable name="#block-local1#" type="string" mode="block"/>
</script>
```

| 属性 | 说明 |
|------|------|
| `name` | 变量名，必须放在两个 `#` 中间，如 `#Love#` |
| `type` | 变量类型：`string`、`integer` 或 `double` |
| `set` | 初始值。（可省略）省略时默认为空字符串或 0 |
| `mode` | （可省略）`global`（全局）、`cut`（剪辑内）、`block`（区块） |

- **全局变量（global）**：所有片段和剪辑都可以引用。
- **剪辑变量（cut）**：只在当前这一 CUT 内生效，移动到下一 CUT 或下一片段后自动删除。
- **区块变量（block）**：只在该脚本块（`<script>~</script>`）中生效。脚本块运行结束后自动删除。子脚本块（`<sub-script>~</sub-script>`）中定义的 block 变量也会在子脚本运行结束后自动删除。关于「子脚本」详见后文。

※变量名不可与预设的系统变量重名。具体有哪些系统变量详见后文。

## 删除变量 `<delete>`

`<delete>` 命令：删除已定义的变量。`<initialize/>` 也可以删除变量。

```
<script>
<delete name="#hensu1#"/>
</script>
```

- `name` 指定要删除的变量名。

## 变量赋值 `<set>`

`<set>` 命令：可为一个变量赋予指定数值或另一个变量的值。

```
<script>
<set name="#hensu1#" value="100"/>       <!-- 让 hensu1 的新值为 100 -->
<set name="#hensu1#" value="#hensu2#"/>  <!-- 让 hensu1 的值等于 hensu2 -->
</script>
```

- `name` 指定变量名。
- `value` 指定新赋值或另一变量名。

## 变量运算 `<compute>`

`<compute>` 命令：可执行变量的运算。

下面的例子为「让变量 1 的值 = 变量 2 + 100」。

```
<script>
<compute result="#hensu1#" value1="#hensu2#" operator="+" value2="100"/>
</script>
```

- `result` 指定结果存入的变量名。
- `value1` 指定参与计算的第一个量。
- `value2` 指定参与计算的第二个量。
- `operator` 指定运算符：

| 运算符 | 含义 |
|--------|------|
| `+` | 加法 |
| `-` | 减法 |
| `*` | 乘法 |
| `/` | 除法 |
| `%` | 取余数 |

value1 和 value2 会被转换成 result 指定的类型进行运算。字符串型变量只能做加法（字符串拼接）。根据运算结果的不同，有可能发生错误（如位数过多内存溢出）。

## 变量加法 `<add>`（Ver.0.3 追加）

只想做简单加法时可以用 `<add>` 命令，比 `<compute>` 更简便。

下面的例子为「让变量 1 的值 = 变量 1 + 100」。

```
<script>
<add result="#hensu1#" value="100"/>
</script>
```
下面的例子为「让变量 1 的值 = 变量 1 - 100」。

```
<script>
<add result="#hensu1#" value="-100"/>
</script>
```

- `result` 指定要进行运算的变量名。
- `value` 指定加数。可以是变量名也可以是定值。value 会被转换成 result 指定的类型进行运算。字符串型变量相加时为字符串拼接。

## 生成随机数 `<random>`

`<random>` 命令：生成随机数。

```
<script>
<random result="#hensu1#" min="1" max="100" seed="0"/>
</script>
```

- `result` 指定要存入随机数的变量名。
- `min` 指定随机数下限。（可省略，默认 0）
- `max` 指定随机数上限。（可省略，默认 65535）
- `seed` 指定随机种子。（可省略。省略时不会初始化种子。）

※`<initialize/>` 执行后，会将种子初始化为当时的秒数×1000+毫秒的值。

