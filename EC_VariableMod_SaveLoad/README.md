# EC_VariableMod_SaveLoad

## 这插件是干什么的
- 给 `EC_VariableMod` 增加剧情内存档/读档。
- 存的是变量状态 + 当前 ADV 位置。
- 读档后会恢复变量，并跳回存档对应的 part/cut。

## 怎么写指令
在 ADV 文本里写到 `<script>` 里。

```xml
<script><save value="save1" /></script>
<script><load value="save1" /></script>
<script><load value="save1" jump="true" /></script>
<script><scan-saves /></script>
<script><save-folder value="SharedFolder" /></script>
```

## 规则
- `save`：立即存档。
- `load`：立即读档，只恢复变量，不跳转。
- `load jump="true"`：读档后跳到当时存档的位置。
- `jump` 只认英文跳转值：`1` / `true` / `yes` / `on` / `jump`。
- 没写 `jump`，或者写了别的值，都视为不跳转。
- `save-folder`：设置存档目录名称，替代默认的场景标题。value 为空则重置为默认。
- **所有存档指令必须作为 `<script>` 的直接子元素使用**，不可嵌套在 `<event-select>`、`<event-next>`、`<if>`、`<loop>` 等延迟执行或条件执行的标签内部。这是因为 SaveLoad 在 CUT 加载时同步处理指令，无法感知 VariableMod 的延迟执行语义。

```xml
<!-- ✅ 正确 -->
<script>
  <save value="s1" />
  <load value="save1" />
</script>

<!-- ❌ 错误：嵌套在延迟标签里，不会按预期时机执行 -->
<script>
  <event-select>
    <load value="save1" />
  </event-select>
</script>
```

## 存档位置
```text
游戏目录/UserData/edit/Otherscene/存档目录/存档名.xml
```

- 默认：存档目录 = 场景标题，即 `Otherscene/场景标题/存档名.xml`。
- 如果使用了 `<save-folder value="xxx"/>`，存档目录 = 设定的名称，所有 `save`/`load`/`scan-saves`/自动存档都使用该目录。
- 在需要多章节共享存档时（例如剧本分成 3 章，分别命名 `Scene-ch1` / `Scene-ch2` / `Scene-ch3`），只需在每章第一个 CUT 写上相同的 `save-folder` 值即可。

### 多章节共享存档（跨场景读取）
当多个章节的剧本设定了**不同的场景标题**，但又想让存档互通时：

```xml
<!-- 第 1 章，第一个 CUT -->
<script><save-folder value="MyStory" /></script>
<script><scan-saves /></script>

<!-- 第 2 章，第一个 CUT -->
<script><save-folder value="MyStory" /></script>
<script><scan-saves /></script>
<script><load value="save_ch1_boss" /></script>
```

- 手动存档由作者自己控制文件名（如 `save_ch1_boss`、`save_ch2_start`），不同章用不同名不会冲突。
- 从**其他章节**的存档读档时，跳转会**自动禁止**（跨场景的 part/cut 在新场景中无效），只恢复变量。
- `save-folder` 的值会写入存档文件，即使标签因 bug 丢失也能从存档中恢复。

## 存档日期显示
- 读档成功后，自动注入全局变量 `#SAVE_{存档名}_DATE#`（例如 `#SAVE_save1_DATE#`）。
- 格式：`yyyy/MM/dd HH:mm:ss`（本地时间）。
- 在 load 之后的任意文本框中写入 `#SAVE_{存档名}_DATE#` 即可显示存档日期。
- **注意**：`<load>` 和 `#变量#` 不能写在同一个CUT。VariableMod 先替换文本，SaveLoad 后执行 load，所以日期变量只能在 load 的**下一个**CUT中生效。

## 存档日期扫描
- 使用 `<scan-saves />` 指令，扫描当前存档目录下所有存档文件，只读取时间戳并注入日期变量。
- **不恢复任何游戏变量，不触发跳转**，仅注入 `#SAVE_{存档名}_DATE#` 供文本框引用。
- 典型用法：在场景第一个文本框中放置 `<script><scan-saves /></script>`，之后所有文本框都能通过 `#SAVE_{存档名}_DATE#` 显示各存档日期。
- 重启游戏后变量会消失，需要重新执行 `<scan-saves />`。

## 自动存档
不用在剧本里写任何标签，插件会按节奏自动存档。通过配置开启。

### 配置项
位于 `BepInEx/config/EC_VariableMod_SaveLoad.cfg` 的 `[AutoSave]` 段（首次运行游戏后自动生成）：

| 键 | 默认 | 说明 |
|------|------|------|
| `Enabled` | `false` | 是否开启自动存档。默认关闭。 |
| `IntervalCuts` | `10` | 每隔多少个 CUT 自动存档一次。**最小 5**。 |

### 行为
- 自动存档文件名：
  - 未设置 `save-folder`（默认）：`AutoSave.xml`，每次覆盖。
  - 设置了 `save-folder`：`AutoSave-{场景标题}.xml`，各章独立不互相覆盖。
- 自动存档和普通存档一样，可以读取。未设置 `save-folder` 时：`<script><load value="AutoSave" /></script>`。设置了 `save-folder` 时：`<script><load value="AutoSave-Scene-ch1" /></script>`。
- 它也会被 `<scan-saves />` 扫描，注入日期变量 `#SAVE_AutoSave_DATE#` 或 `#SAVE_AutoSave-场景标题_DATE#`。
- **注意**：请不要把手动存档命名为 `AutoSave` 或 `AutoSave-{场景标题}`，否则会和自动存档互相覆盖。

## HPlay 前进键 / 自动前进
不用在剧本里写标签，通过配置控制。

### 配置项
位于 `BepInEx/config/EC_VariableMod_SaveLoad.cfg` 的 `[Advance]` 段（首次运行游戏后自动生成）：

| 键 | 默认 | 说明 |
|------|------|------|
| `Key` | `Return` | HPlay 中手动前进的按键，默认 Enter。 |
| `AutoToggleKey` | `Backspace` | 切换自动前进开/关。建议避开 Ctrl，左右 Ctrl 是游戏自带快速前进。 |
| `AutoEnabled` | `false` | 是否开启自动前进。默认关闭。 |
| `AutoIntervalSeconds` | `2.5` | 有文本动画时：文本动画播放完成后的等待秒数；无文本/文本动画关闭时：固定自动前进等待秒数。 |

### 行为
- 手动前进键会立即前进到下一个 CUT/PART。
- 自动前进开启后，如果当前 CUT 有启用文本动画的文本框，会等最慢的文本动画播放完成，再等待 `AutoIntervalSeconds`。
- 如果当前 CUT 没有文本框、文本为空，或所有文本框都关闭了文本动画，则直接等待 `AutoIntervalSeconds` 后自动前进。
- 遇到选项分支时不会自动选择，仍需玩家点击选项。
- 按下非前进键/非自动开关键的任意键，会关闭自动前进。

## 前置
- 必须先装 `EC_VariableMod`。
- 这是它的兼容增强插件，不是独立存档系统。
