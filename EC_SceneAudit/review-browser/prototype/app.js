const byId = (id) => document.getElementById(id);
const preferenceKey = 'ec-scene-review-prototype.context-range.v1';
const supportedRanges = [1, 2, 4, 8];
const state = { mode: 'report', findingId: 'route', partId: 'crossing', anchor: 2, range: 2, defaultRange: 2, previous: '', next: '' };

function element(tag, className, text) {
  const item = document.createElement(tag);
  if (className) item.className = className;
  if (text !== undefined) item.textContent = text;
  return item;
}

function getPart(id) { return sceneReviewDemo.parts.find((part) => part.id === id); }
function getFinding() { return sceneReviewDemo.findings.find((finding) => finding.id === state.findingId); }
function feedback(message) { byId('feedback').textContent = message; }
function badge(text, kind) { return element('span', `tag ${kind || ''}`, text); }
function connections(partId, direction) { return sceneReviewDemo.routes.filter((route) => route[direction === 'next' ? 'from' : 'to'] === partId); }
function otherPartId(route, direction) { return route[direction === 'next' ? 'to' : 'from']; }
function routeName(route, direction) { return getPart(otherPartId(route, direction))?.name || route.targetName || '未收录的目标'; }

function restorePreference() {
  try {
    const saved = Number(localStorage.getItem(preferenceKey));
    if (supportedRanges.includes(saved)) state.range = state.defaultRange = saved;
  } catch { feedback('默认范围无法读取，当前使用前后各 2 个 CUT'); }
}

function selectItem(id) {
  if (state.mode === 'report') {
    const finding = sceneReviewDemo.findings.find((item) => item.id === id);
    state.findingId = id;
    state.partId = finding.partId;
    state.anchor = finding.cutIndex;
  } else {
    state.partId = id;
    state.anchor = 0;
  }
  state.range = state.defaultRange;
  state.previous = state.next = '';
  render();
  feedback(`已定位 ${getPart(state.partId).name} · CUT ${state.anchor + 1}`);
}

function renderList() {
  const isReport = state.mode === 'report';
  const items = isReport ? sceneReviewDemo.findings : sceneReviewDemo.parts;
  byId('list-title').textContent = isReport ? '审查问题' : '场景 Part';
  byId('list-count').textContent = items.length;
  byId('list-caption').textContent = isReport ? '影响程度与确定程度分别展示' : '没有报告也可以阅读资料';
  byId('item-list').setAttribute('aria-label', isReport ? '审查问题' : '场景 Part');
  byId('item-list').replaceChildren(...items.map((item, index) => {
    const button = element('button', 'issue-item');
    button.type = 'button';
    button.setAttribute('aria-current', String(item.id === (isReport ? state.findingId : state.partId)));
    button.append(element('span', 'item-number', `${String(index + 1).padStart(2, '0')} · ${isReport ? '示例问题' : 'PART'}`));
    if (isReport) {
      const tags = element('div', 'tags');
      tags.append(badge(item.severityLabel, item.severity), badge(item.certainty, 'certainty'));
      button.append(tags);
    }
    button.append(element('h3', '', isReport ? item.title : item.name));
    button.append(element('p', 'item-location', isReport ? `${getPart(item.partId).name} · CUT ${item.cutIndex + 1}` : `${item.cuts.length} 个 CUT`));
    button.onclick = () => selectItem(item.id);
    return button;
  }));
}

function selectRoute(direction, id) {
  state[direction] = id;
  renderReader();
  feedback(id ? `已切换${direction === 'previous' ? '前驱' : '后继'}阅读路线` : '保留候选路线，等待选择');
}

function renderRouteControls() {
  const controls = [];
  for (const direction of ['previous', 'next']) {
    const routes = connections(state.partId, direction);
    if (routes.length < 2) continue;
    const label = element('label', 'route-control', `${direction === 'previous' ? '前一个' : '后一个'} Part 有 ${routes.length} 条候选路线`);
    const select = element('select');
    select.dataset.direction = direction;
    select.add(new Option('请选择阅读路线', ''));
    routes.forEach((route) => select.add(new Option(`${route.label} → ${routeName(route, direction)} · ${route.kind}${otherPartId(route, direction) ? '' : ' / 未收录'}`, route.id)));
    select.value = state[direction];
    select.onchange = () => selectRoute(direction, select.value);
    label.append(select);
    controls.push(label);
  }
  byId('route-controls').replaceChildren(...controls);
}

function graphColumn(direction) {
  const column = element('div', 'flow-column');
  const routes = connections(state.partId, direction);
  if (!routes.length) column.append(element('span', 'subtle', '没有已知连接'));
  for (const route of routes) {
    const button = element('button', `flow-node${state[direction] === route.id ? ' selected' : ''}`);
    button.type = 'button';
    button.append(element('span', '', routeName(route, direction)), element('small', '', `${route.kind}${otherPartId(route, direction) ? '' : ' · 未收录'}`));
    button.onclick = () => selectRoute(direction, route.id);
    column.append(button);
  }
  return column;
}

function renderGraph() {
  const current = element('div', 'flow-node current');
  current.append(element('strong', '', getPart(state.partId).name), element('small', '', '当前 Part'));
  byId('flow-graph').replaceChildren(graphColumn('previous'), element('span', 'flow-arrow', '→'), current, element('span', 'flow-arrow', '→'), graphColumn('next'));
}

function transition(part, direction, visited) {
  const routes = connections(part.id, direction);
  if (!routes.length) return { message: '已到达已知路线边界。' };
  const selected = part.id === state.partId ? routes.find((route) => route.id === state[direction]) : null;
  const route = selected || (routes.length === 1 ? routes[0] : null);
  if (!route) return { message: `「${part.name}」有多条${direction === 'next' ? '后继' : '前驱'}路线，未选择的部分暂不展开。`, warning: true };
  const target = getPart(otherPartId(route, direction));
  if (!target) return { message: `目标「${routeName(route, direction)}」未收录，无法展示后续对白。`, warning: true };
  if (visited.has(target.id)) return { message: `路线再次回到「${target.name}」，在此停止展开循环。`, warning: true };
  return { target, route };
}

function contextSide(direction) {
  const step = direction === 'next' ? 1 : -1;
  const visited = new Set([state.partId]);
  const result = [];
  let part = getPart(state.partId);
  let index = state.anchor + step;
  let remaining = state.range;
  while (remaining > 0) {
    if (index >= 0 && index < part.cuts.length) {
      result.push({ part, index });
      index += step;
      remaining -= 1;
      continue;
    }
    const change = transition(part, direction, visited);
    if (!change.target) { result.push(change); break; }
    part = change.target;
    visited.add(part.id);
    index = direction === 'next' ? 0 : part.cuts.length - 1;
    result.push({ message: `${change.route.kind} · ${change.route.label}` });
  }
  return direction === 'previous' ? result.reverse() : result;
}

function cutCard(part, index, focus) {
  const cut = part.cuts[index];
  const card = element('article', `cut${focus ? ' focused' : ''}`);
  if (focus) card.id = 'focused-cut';
  const header = element('div', 'cut-heading', `CUT ${index + 1}`);
  if (focus) header.append(badge(state.mode === 'report' ? '问题位置' : '当前查看', 'active'));
  card.append(header);
  for (const [speaker, text] of cut.lines) {
    const line = element('div', 'dialogue-line');
    line.append(element('span', 'speaker', speaker), element('p', 'dialogue-text', text));
    card.append(line);
  }
  for (const choice of cut.choices || []) card.append(element('span', 'choice-text', choice));
  return card;
}

function renderContext() {
  const part = getPart(state.partId);
  const rows = [...contextSide('previous'), { part, index: state.anchor, focus: true }, ...contextSide('next')];
  const output = [];
  let previousPart = '';
  for (const row of rows) {
    if (row.message) { output.push(element('p', `reading-note${row.warning ? ' warning' : ''}`, row.message)); continue; }
    if (previousPart !== row.part.id) output.push(element('p', 'part-divider', `PART · ${row.part.name}`));
    previousPart = row.part.id;
    output.push(cutCard(row.part, row.index, row.focus));
  }
  byId('reading-context').replaceChildren(...output);
}

function renderReader() {
  byId('reader-title').textContent = `${getPart(state.partId).name} · CUT ${state.anchor + 1}`;
  byId('location-label').textContent = state.mode === 'report' ? '问题对应的对白与上下文' : '按 Part 阅读';
  byId('reader-tag').textContent = '演示对白';
  byId('context-range').value = state.range;
  byId('range-caption').textContent = `${state.range === state.defaultRange ? '当前使用默认范围' : '本次临时范围'} · 默认前后各 ${state.defaultRange} 个 CUT；切换问题时恢复默认`;
  renderRouteControls();
  renderGraph();
  renderContext();
}

function detailSection(title, text, className) {
  const section = element('section', `detail-section ${className || ''}`);
  section.append(element('h3', '', title), element('p', '', text));
  return section;
}

function renderDetail() {
  const target = byId('finding-detail');
  if (state.mode !== 'report') {
    const empty = element('div', 'empty-report');
    empty.append(element('h3', '', '尚未导入审查报告'), element('p', '', '可以先浏览 Part、对白与相关流程。资料本身不会被当作 AI 的问题结论。'));
    target.replaceChildren(empty);
    return;
  }
  const finding = getFinding();
  const tags = element('div', 'tags');
  tags.append(badge(finding.severityLabel, finding.severity), document.createTextNode(' '), badge(finding.certainty, 'certainty'));
  const evidence = element('details', 'evidence');
  evidence.append(element('summary', '', '查看依据与原始位置'), element('p', '', finding.evidence), element('pre', '', finding.quote));
  evidence.append(element('p', 'subtle', `演示 Part：${getPart(finding.partId).name}；CUT 原始索引 ${finding.cutIndex}，页面显示 ${finding.cutIndex + 1}。`));
  const locate = element('button', '', '定位到对应对白');
  locate.type = 'button';
  locate.onclick = () => { byId('focused-cut').scrollIntoView({ behavior: 'auto', block: 'center' }); feedback('已定位到问题对应的 CUT'); };
  evidence.append(locate);
  target.replaceChildren(tags, element('h3', 'detail-title', finding.title), detailSection('何时发生', finding.trigger), detailSection('可能影响', finding.impact), detailSection('建议先做', finding.recommendation, 'recommendation'), evidence, detailSection('审查范围与限制', finding.coverage));
}

function render() {
  const mismatch = state.mode === 'mismatch';
  byId('workspace').hidden = mismatch;
  byId('mismatch-panel').hidden = !mismatch;
  byId('demo-mode').value = state.mode;
  byId('workspace-description').textContent = mismatch ? '来源无法对应时，先处理匹配关系。' : '选中一条问题，沿相关对白和路线阅读。';
  if (mismatch) return;
  renderList();
  renderReader();
  renderDetail();
}

function setMode(mode) {
  state.mode = mode;
  if (mode === 'mismatch') { render(); feedback('来源不匹配，已停止展示问题定位'); return; }
  selectItem(mode === 'report' ? 'route' : 'crossing');
}

byId('demo-mode').onchange = (event) => setMode(event.target.value);
byId('browse-only').onclick = () => setMode('scene');
byId('context-range').onchange = (event) => { state.range = Number(event.target.value); renderReader(); feedback('已调整本次范围，默认值保持不变'); };
byId('save-default').onclick = () => {
  state.defaultRange = state.range;
  renderReader();
  try { localStorage.setItem(preferenceKey, String(state.defaultRange)); feedback(`已保存默认范围：前后各 ${state.defaultRange} 个 CUT`); }
  catch { feedback('当前会话已使用新默认值；浏览器未允许保存，重新打开后可能恢复'); }
};
restorePreference();
render();
