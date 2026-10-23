# ■■ 关于预设系统变量 ■■

本 Mod 中含有预先设定好的变量——系统变量。玩家自己定义的变量不可与系统变量重名。系统变量名主要以 `#SYS.～` 开头。这些变量会在剪辑显示出来、脚本开始执行前的一刻自动设置好。

## 系统信息

| 变量名 | 说明 |
|--------|------|
| `#SYS.DATE.YEAR#` | 当前年份 |
| `#SYS.DATE.MONTH#` | 当前月份 |
| `#SYS.DATE.DAY#` | 当前日期 |
| `#SYS.TIME.HOUR#` | 当前时刻（小时） |
| `#SYS.TIME.MINUTE#` | 当前时刻（分） |
| `#SYS.TIME.SECOND#` | 当前时刻（秒） |
| `#SYS.TIME.MILLI#` | 当前时刻（毫秒） |

※以上时间相关变量是在剪辑显示出来前就已定义好的，如果存在 `<event-next>` 等延时性命令，可能会和变量参与执行时的实际时间有出入。

## 片段信息

| 变量名 | 说明 |
|--------|------|
| `#SYS.PART.NAME#` | 当前片段名 |
| `#SYS.CUT.INDEX#` | 当前剪辑编号 |
| `#SYS.PART.PRE.NAME#` | 前一片段名 |
| `#SYS.PART.PRE.SELECT#` | 前一选项编号（0～最大 4）。利用此变量可判断选择了哪一选项。H 片段的选择结果也可取得。无选项的片段总取到 0 |

## 人物信息

| 变量名 | 说明 |
|--------|------|
| `#SYS.CHAR1.NAME#` | 角色 1 的姓名 |
| `#SYS.CHAR1.BIRTH.MONTH#` | 角色 1 的出生月 |
| `#SYS.CHAR1.BIRTH.DAY#` | 角色 1 的出生日 |
| `#SYS.CHAR1.BLOODTYPE#` | 角色 1 的血型（0=A、1=B、2=O、3=AB） |
| `#SYS.CHAR1.SEX#` | 角色 1 的性别（0=男、1=女） |
| `#SYS.CHAR1.PERSONALITY#` | 角色 1 的性格 |
| `#SYS.CHAR1.FK.HAIR.MAX#` | 角色 1 发型 FK 点的最大值 |
| `#SYS.CHAR1.FK.SKIRT.MAX#` | 角色 1 裙子 FK 点的最大值 |

更改变量名中的数字（1~8）即可指定其他角色。

※未分配的角色位无法指定。无法指定男性的性格。如果替换角色，变量值会变为替换后角色的值。**Ver.0.6 起**，超过 8 人的角色也会自动注册系统变量。

## H Part 信息（Ver.0.3 追加）

| 变量名 | 说明 |
|--------|------|
| `#SYS.HPART.MAIN.MOTION#` | 主要组现在的动作：0=待机、1=正戏、2=绝顶、3=同时、4=中出、5=外射、6=绝顶后、7=同时后、8=中出后、9=外射后 |
| `#SYS.HPART.MAIN.SPEED#` | 主要组正戏的速度（1.0~3.0）。正戏以外调用时取到 0 |
| `#SYS.HPART.SUB1.MOTION#` | 次要 1 组现在的动作 |
| `#SYS.HPART.SUB1.SPEED#` | 次要 1 组正戏的速度 |
| `#SYS.HPART.FINISH.ORG#` | 绝顶次数 |
| `#SYS.HPART.FINISH.SAME#` | 同时次数 |
| `#SYS.HPART.FINISH.INSIDE#` | 中出次数 |
| `#SYS.HPART.FINISH.OUTSIDE#` | 外射次数 |

※`#SYS.HPART.SUB?#` 设置了几个次要组就可以指定几个。

## event-update 命令相关的信息（Ver.0.3 追加，Ver.0.6 增强）

在 `<event-update>` 命令中设定好 wait 和 period 后，每当 event-update 脚本执行一次，下列系统变量就会随之更新：

| 变量名 | 说明 |
|--------|------|
| `#SYS.EVENT.UPDATE.TIME.MAX#` | period 值 |
| `#SYS.EVENT.UPDATE.TIME.HALF#` | **（Ver.0.6 新增）** period ÷ 2 |
| `#SYS.EVENT.UPDATE.TIME.BASE#` | **（Ver.0.6 新增）** wait 值 |
| `#SYS.EVENT.UPDATE.TIME.TOTAL#` | **（Ver.0.6 新增）** 累计时间 |
| `#SYS.EVENT.UPDATE.TIME.NOW#` | 当前的 wait 累计值。例如设定 wait=100 时，如果电脑配置充裕，会以 100→200→300 这样更新。超过 period 时返回到 0。当 period=0 时该值保持 0 |
| `#SYS.EVENT.UPDATE.INDEX.MAX#` | 最大 index 值（period ÷ wait） |
| `#SYS.EVENT.UPDATE.INDEX.NOW#` | 当前的 index 值。超过 period 值时返回到 0 |
| `#SYS.EVENT.UPDATE.REPEAT.MAX#` | 最大 index 值（折返）= period ÷ wait ÷ 2 |
| `#SYS.EVENT.UPDATE.REPEAT.NOW#` | 当前的 index 值（折返模式：1→2→3→2→1…） |
| `#SYS.EVENT.UPDATE.REPEAT.FLAG#` | **（Ver.0.6 新增）** 折返标志：0=上行阶段、1=下行阶段 |
| `#SYS.EVENT.UPDATE.PERIOD.FLAG#` | 在 0 和 1 之间周期切换（初始为 0）。当前 index 值超过最大 index 时变化一次。0=奇数回、1=偶数回 |
| `#SYS.EVENT.UPDATE.PERIOD.FLAG2#` | **（Ver.0.6 新增）** 在 0 和 1 之间周期切换。每两次 PERIOD.FLAG 翻转为 0 时切换一次 |
| `#SYS.EVENT.UPDATE.PERIOD.COUNT#` | **（Ver.0.6 新增）** 周期计数器。每完成一个 period 递增 1。当 `<event-update>` 设置了 count 属性时，超过 count 后重置为 0 |

