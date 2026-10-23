// 仅用于交互设计；不作为真实 Scene 的证据或 AI 报告。
const sceneReviewDemo = {
  parts: [
    { id: 'arrival', name: '小镇入口', cuts: [
      { lines: [['旁白', '雨停了，石板路上还留着浅浅的水。']] },
      { lines: [['同行者', '我们先到路口看看，再决定去哪里。']] }
    ] },
    { id: 'crossing', name: '路口选择', cuts: [
      { lines: [['旁白', '左边是热闹的集市，右边通向车站。']] },
      { lines: [['同行者', '时间还早，你想先去哪里？']] },
      { lines: [['选择', '接下来去哪里？']], choices: ['去集市逛逛', '去车站等车'] }
    ] },
    { id: 'market', name: '集市', cuts: [
      { lines: [['摊主', '欢迎。体验摊位每次收取十枚代币。']] },
      { lines: [['旁白', '摊主启动了计时器。'], ['同行者', '先试一会儿，结束后我们再去车站。']] },
      { lines: [['选择', '还要再体验一次吗？']], choices: ['再来一次', '去车站'] }
    ] },
    { id: 'station', name: '车站', cuts: [
      { lines: [['站务员', '请准备好车票，我来核对下一班车。']] },
      { lines: [['同行者', '坐下休息一会儿吧。']] },
      { lines: [['旁白', '远处传来了列车进站的广播。']] }
    ] }
  ],
  routes: [
    { id: 'arrival-crossing', from: 'arrival', to: 'crossing', kind: '普通连接', label: '继续前行' },
    { id: 'crossing-market', from: 'crossing', to: 'market', kind: '普通连接', label: '去集市逛逛' },
    { id: 'crossing-night', from: 'crossing', to: null, targetName: '夜间车站', kind: '脚本跳转', label: '去车站等车' },
    { id: 'market-market', from: 'market', to: 'market', kind: '普通连接', label: '再来一次' },
    { id: 'market-station', from: 'market', to: 'station', kind: '普通连接', label: '去车站' }
  ],
  findings: [
    {
      id: 'route', partId: 'crossing', cutIndex: 2, severity: 'minor', severityLabel: '影响较小', certainty: '待核实',
      title: '车站分支的目标名称需要核对',
      trigger: '在「路口选择」中选择「去车站等车」，且相应脚本实际执行。',
      impact: '示例脚本指向「夜间车站」，演示资料中没有这个 Part。实际后续路线仍需核对，不能据此判定一定卡死。',
      recommendation: '先核对作者预期的目的地，再在游戏中选择这一分支，观察实际进入的 Part。',
      quote: '<goto-part name="夜间车站" />',
      evidence: '演示脚本的目标名称，与演示资料中的 Part 名称集合不匹配。',
      coverage: '示例只用于演示跨 Part 路线选择、目标缺失和问题定位。'
    },
    {
      id: 'timer', partId: 'market', cutIndex: 1, severity: 'major', severityLabel: '影响较大', certainty: '有条件成立',
      title: '重复体验时，扣费事件可能重复生效',
      trigger: '选择「再来一次」返回集市，并且先前注册的计时事件仍然存在。',
      impact: '如果重复注册会保留多个处理器，一次计时可能触发多次扣费。当前示例没有运行时证据。',
      recommendation: '记录一次与两次进入后的扣费次数，并核对离开 Part 时是否清理了旧事件。',
      quote: '演示逻辑：进入集市 → 注册计时扣费 → 选择再次体验 → 返回集市',
      evidence: '演示关系中有返回自身的路线；是否会重复注册和执行，需要额外证据。',
      coverage: '事件行为为交互示例中的假设，不代表 Variable Mod 的确定语义。'
    },
    {
      id: 'dependency', partId: 'station', cutIndex: 0, severity: 'info', severityLabel: '资料提示', certainty: '已确认缺口',
      title: '车票检查的外部脚本未收录',
      trigger: '进入「车站」的首个 CUT，并调用示例外部脚本。',
      impact: '无法从当前演示资料判断检票条件及失败后的处理，不能推断一定放行或阻止继续。',
      recommendation: '补齐对应批次的外部脚本，再审查检票条件和两个结果分支。',
      quote: '<load-script file="edit/scene/demo/ticket.xml" />',
      evidence: '演示清单中将该文件标记为缺失，页面保留缺口，不补造脚本内容。',
      coverage: '外部脚本内容未读；缺口本身明确，运行影响未知。'
    }
  ]
};
