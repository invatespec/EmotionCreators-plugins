## 周期计算 `<calc-update>`（Ver.0.6 新增）

`<calc-update>` 命令：在 `<event-update>` 中使用，用于计算在 start～end 范围内按比例插值的结果。是对常用插值计算模式的便捷封装。

```
<script>
<event-update wait="100" period="2000" start="0" end="2000">
  <calc-update result="#val#" value1="0" value2="100" mode="seq"/>
  <char-ctrl char="0" mode="rot" x="#val#"/>
</event-update>
</script>
```

- `result` 计算结果存入的变量名
- `value1` 起始值
- `value2` 结束值
- `start` 插值范围起点（相对于 event-update 的时间轴）。（可省略，默认 0）
- `end` 插值范围终点。（可省略，默认 `#SYS.EVENT.UPDATE.TIME.MAX#`）
- `period` 指定周期匹配值。当 `#SYS.EVENT.UPDATE.PERIOD.COUNT#` 不等于此值时跳过计算。（可省略）
- `flag` 指定 flag 匹配值。当 `#SYS.EVENT.UPDATE.PERIOD.FLAG#` 不等于此值时跳过计算。（可省略）
- `flag2` 指定 flag2 匹配值。当 `#SYS.EVENT.UPDATE.PERIOD.FLAG2#` 不等于此值时跳过计算。（可省略）
- `mode` 插值模式。（可省略，默认 `seq`）

| mode | 说明 |
|------|------|
| `seq` | 从 value1 到 value2 线性插值 |
| `seq_r` | 同 seq，但当 PERIOD.FLAG=1 时反向（即乘以 -1） |
| `rev` | 往返插值（利用 PERIOD.FLAG 判断方向） |
| `rev_r` | 同 rev，但当 PERIOD.FLAG2=1 时反向 |
| `add` | 直接加上 value2 - value1 的步进值（常用于持续累加） |
| `add_r` | 同 add，但当 PERIOD.FLAG=1 时反向 |

