const cut = (speaker, text, choices = []) => ({ boxes: [{ speaker, text }], choices });
const walk = (place, count) => Array.from({ length: count }, (_, i) => cut('旁白', `${place}的第 ${i + 1} 段演示对白。雨后的路面渐渐干了，远处传来交谈声。`));

// 合成场景覆盖交互边界，不是用户资料包或独立 AI 的结论。
export const data = {
  bundleId: 'node-pool-demo-v1', manifestSha256: 'synthetic-fixture-v1',
  nodes: [
    { id: 'entry', name: '小镇入口', cuts: [cut('旁白', '雨停了，石板路上还留着浅浅的水。'), cut('同行者', '先到路口，再决定去哪里。')] },
    { id: 'crossing', name: '路口选择', cuts: [cut('旁白', '左边是集市，右边通向车站。'), cut('同行者', '时间还早，你想先去哪里？'), cut('选择', '接下来去哪里？', ['去集市逛逛', '去车站等车'])] },
    { id: 'market', name: '集市', cuts: [cut('摊主', '体验一次收取十枚代币。'), { boxes: [{ speaker: '旁白', text: '摊主启动了计时器。' }, { speaker: '同行者', text: '先试一会儿，结束后我们再去车站。这段较长的演示文本用于检查预览截断；完整文本窗口应当保留全部内容，而不是只展示卡片上看得到的部分。' }], choices: [] }, ...walk('集市', 8), cut('选择', '还要继续体验吗？', ['再来一次', '去广场'])] },
    { id: 'station', name: '车站', cuts: [cut('站务员', '请准备车票，我来核对下一班车。'), cut('同行者', '坐下休息一会儿吧。'), cut('旁白', '远处传来进站广播。')] },
    { id: 'square', name: '广场会合', cuts: [cut('同行者', '我们在广场会合了。'), cut('选择', '是继续散步，还是返回集市？', ['返回集市', '结束散步'])] },
    { id: 'ending', name: '散步结束', cuts: [cut('旁白', '灯亮了，一天的散步到此结束。')] },
    { id: 'memory', name: '支线 · 旧照片', cuts: walk('相册', 4) },
    { id: 'letter', name: '支线 · 未寄出的信', cuts: walk('信件', 3) },
    { id: 'epilogue', name: '支线 · 后记', cuts: walk('后记', 2) },
    { id: 'secret', name: '夜间车站', cuts: walk('夜间车站', 3) },
    { id: 'draft', name: '待整理 · 片段 A', cuts: walk('未连接片段', 2) },
    { id: 'empty', name: '待整理 · 空片段', cuts: [] }
  ],
  edges: [
    { id: 'e1', from: 'entry', to: 'crossing', kind: 'normal', label: '继续前行', fromCut: 1, toCut: 0 },
    { id: 'e2', from: 'crossing', to: 'market', kind: 'normal', label: '去集市逛逛', fromCut: 2, toCut: 0 },
    { id: 'e3', from: 'crossing', to: 'station', kind: 'normal', label: '去车站等车', fromCut: 2, toCut: 0 },
    { id: 'e4', from: 'market', to: 'square', kind: 'normal', label: '去广场', fromCut: 10, toCut: 0 },
    { id: 'e5', from: 'station', to: 'square', kind: 'normal', label: '离开车站', fromCut: 2, toCut: 0 },
    { id: 'e6', from: 'square', to: 'ending', kind: 'normal', label: '结束散步', fromCut: 1, toCut: 0 },
    { id: 'e7', from: 'square', to: 'market', kind: 'normal', label: '返回集市', fromCut: 1, toCut: 0 },
    { id: 'e8', from: 'memory', to: 'letter', kind: 'normal', label: '读完相册', fromCut: 3, toCut: 0 },
    { id: 'e9', from: 'letter', to: 'epilogue', kind: 'normal', label: '收好信件', fromCut: 2, toCut: 0 },
    { id: 's1', from: 'station', to: 'memory', kind: 'script', label: '查看旧照片', fromCut: 2, toCut: 1 },
    { id: 's2', from: 'epilogue', to: 'secret', kind: 'script', label: '回想夜间车站', fromCut: 1, toCut: 2 },
    { id: 's3', from: 'crossing', to: null, targetName: '夜间站台', kind: 'script', label: '隐藏选项', fromCut: 2, toCut: null }
  ],
  findings: [
    { id: 'F-01', status: 'active', severity: 'major', certainty: '有条件成立', title: '返回集市后，计时事件可能重复生效', locations: [{ node: 'market', cut: 1 }, { node: 'square', cut: 1 }, { node: 'market', cut: 1 }], trigger: '重复进入集市，且此前注册的处理器仍存在。', impact: '可能一次计时多次扣费；当前只是演示假设，没有运行时证据。', recommendation: '分别观察首次与再次进入时的扣费次数。', quote: '进入集市 → 注册计时 → 返回集市 → 再次注册' },
    { id: 'F-02', status: 'active', severity: 'minor', certainty: '待核实', title: '隐藏分支指向未收录的站台', locations: [{ node: 'crossing', cut: 2 }], trigger: '隐藏选项及其跳转脚本执行。', impact: '目标「夜间站台」未收录，无法确认后续。', recommendation: '核对目标名称与作者预期路线。', quote: '<goto-part name="夜间站台" />' },
    { id: 'F-03', status: 'active', severity: 'info', certainty: '已确认资料缺口', title: '共享检票脚本内容缺失', locations: [{ node: 'station', cut: 0, associated: true }, { node: 'market', cut: 1, associated: true }], external: true, trigger: '任一调用处实际执行外部检票脚本。', impact: '仅能找到调用位置，无法审查脚本正文。', recommendation: '补充同批次的外部依赖，再核对条件。', quote: '<load-script file="edit/scene/demo/ticket.xml" />' },
    { id: 'F-04', status: 'active', severity: 'info', certainty: '待核实', title: '未连接片段需要作者确认用途', locations: [{ node: 'draft', cut: null }], trigger: '演示资料中未发现这个 Part 的已知连接。', impact: '无法判断是待制作内容还是保留素材；不视为确定缺陷。', recommendation: '由作者说明该片段用途。', quote: 'Part：待整理 · 片段 A' },
    { id: 'F-05', status: 'active', severity: 'minor', certainty: '位置未核验', title: '报告引用的位置已无法找到', locations: [{ node: 'missing-node', cut: 5 }], trigger: '问题引用了演示资料中不存在的位置。', impact: '保留说明，不给其他节点打标。', recommendation: '重新取得匹配资料或修正报告引用。', quote: '原位置不可用' },
    { id: 'F-06', status: 'withdrawn', severity: 'major', certainty: '已撤销', title: '旧结论：车站无法继续', locations: [{ node: 'station', cut: 2 }], trigger: '旧报告未注意到普通出口。', impact: '该结论已经撤销。', recommendation: '保留撤销记录，不计入节点标记。', quote: '已找到车站到广场的正常连接。' }
  ]
};
