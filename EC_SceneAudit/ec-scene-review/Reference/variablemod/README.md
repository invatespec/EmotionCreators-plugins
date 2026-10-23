# EC_VariableMod 使用方法 Ver.0.6

插件原地址：https://yuki-portal.com/uploader/emotioncreator/35442/

> 本文档已按主题拆分。写代码时按命令类别只读对应文件，不要整包加载。

## 按需阅读索引

| 文件 | 内容 | 命令 |
|------|------|------|
| [00-install.md](00-install.md) | 安装 | — |
| [01-terms.md](01-terms.md) | 用语（片段/剪辑） | — |
| [02-script-basics.md](02-script-basics.md) | 脚本编写基础 | `<initialize/>` `<invisible/>` |
| [03-variables.md](03-variables.md) | 变量 | `<variable>` `<delete>` `<set>` `<compute>` `<add>` `<random>` |
| [04-control-flow.md](04-control-flow.md) | 控制流与跳转 | `<if>` `<loop>` `<break>` `<goto-cut>` `<goto-part>` |
| [05-anime-hpart.md](05-anime-hpart.md) | 动画剪辑 / H 片段 | `<anime-cut>` `<h-part-set>` |
| [06-char-ctrl.md](06-char-ctrl.md) | 角色控制 | `<char-ctrl>` |
| [07-camera-item.md](07-camera-item.md) | 相机 / 物品 | `<camera-ctrl>` `<item-ctrl>` |
| [08-events.md](08-events.md) | 事件脚本 | `<event-next>` `<event-select>` `<event-part>` `<event-update>` |
| [09-subscripts.md](09-subscripts.md) | 子脚本 / 外部脚本 | `<sub-script>` `<call-sub>` `<load-script>` `<reset-script/>` |
| [10-arrays.md](10-arrays.md) | 数组 | `<array>` `<array-set>` `<array-get>` |
| [11-calc-update.md](11-calc-update.md) | 周期插值计算 | `<calc-update>` |
| [12-system-variables.md](12-system-variables.md) | 预设系统变量 `#SYS.*` | — |
| [13-dialog-debug-config.md](13-dialog-debug-config.md) | 对话框变量 / Debug / 配置 | — |
| [14-appendix-indexes.md](14-appendix-indexes.md) | FK/IK/Face/Hand/Add index 附录 | — |
| [15-changelog.md](15-changelog.md) | Ver.0.6 更新记录 | — |

## 快速定位

- 写变量/运算 → `03-variables.md`
- 写 if/loop/goto → `04-control-flow.md`
- 写角色动作/表情 → `06-char-ctrl.md` + `14-appendix-indexes.md`
- 写 event-update 动画 → `08-events.md` + `11-calc-update.md` + `12-system-variables.md`
- 查系统变量 → `12-system-variables.md`
