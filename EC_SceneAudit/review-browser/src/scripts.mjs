import { dependencyLocations } from './locations.mjs';
import { extractScriptBlock, parseGotoCommands } from './goto-script.mjs';

// 共享脚本 goto：<load-script> 读入外部文件后走与 inline 相同的 AnalyzeScript。
// AnalyzeScript 先字面找 "<script>"、再 XDocument.Parse 并取根 script（ScriptAnalyticsClass.cs:4679-4703）；
// 文件里没有字面小写 <script> 时游戏按普通文本处理，既不执行也不报错，所以这里不补包装、不猜块。

function labelFor(command) {
  const cut = command.toCut === null || command.toCut === undefined ? '' : ` · CUT ${command.toCut + 1}`;
  const status = command.status && command.status !== 'resolved' ? ` · ${command.status}` : '';
  return command.command === 'goto-part'
    ? `共享脚本 goto-part · ${command.targetName || '未知目标'}${cut}${status}`
    : `共享脚本 goto-cut · ${command.index ?? ''}${cut}${status}`;
}

function edgeFromCommand(call, fileId, callIndex, command, commandIndex) {
  const resolved = command.status === 'resolved';
  return {
    id: `dependency:${fileId}:${callIndex}:${commandIndex}`,
    kind: 'script', layer: 'goto', sourceKind: 'dependency-goto',
    command: command.command,
    from: call.node, fromCut: call.cut,
    to: command.to ?? null, toCut: command.toCut ?? null,
    targetName: command.targetName || '',
    label: labelFor(command),
    reason: resolved ? null : (command.reason || command.status),
    status: command.status, hint: command.hint ?? null, dynamic: !!command.dynamic,
    uncertain: !!command.uncertain,
    origin: { pointer: call.reference?.pointer ?? null, nodeId: call.node, cutIndex: call.cut,
      dependencyFileId: fileId, sourceKind: 'dependency-goto' },
  };
}

export function scriptEdges(bundle, model) {
  const edges = [];
  for (const [fileId, member] of bundle.files) {
    if (member.meta.role !== 'dependency') continue;
    if (typeof member.text !== 'string') {
      model.notices.push(`${member.meta.sourcePath} 的编码未确认，无法读取脚本关系。`);
      continue;
    }
    const calls = dependencyLocations(bundle, model, fileId);
    if (!calls.length) continue;

    const block = extractScriptBlock(member.text);
    if (!block.found) {
      model.notices.push(`共享脚本 ${member.meta.sourcePath} 没有可执行的 <script> 块，游戏按普通文本处理，未展开跳转关系。`);
      continue;
    }
    if (!block.valid) {
      model.notices.push(`共享脚本 ${member.meta.sourcePath} 的 <script> 无法完整解析，未展开跳转关系。`);
      continue;
    }

    calls.forEach((call, callIndex) => {
      const origin = { pointer: call.reference?.pointer ?? null, nodeId: call.node, cutIndex: call.cut };
      const parsed = parseGotoCommands(block.blockText, origin, model.nodes);
      parsed.commands.forEach((command, commandIndex) =>
        edges.push(edgeFromCommand(call, fileId, callIndex, command, commandIndex)));
      for (const item of parsed.diagnostics || [])
        model.notices.push(`共享脚本 ${member.meta.sourcePath}：${item.message}`);
    });
  }
  return edges;
}
