import { $, el, button } from './dom.js';
import { nodeById, validLocation, severityLabel, cutNumber } from './domain.js';

export function mountSidebar({ data, state, actions }) {
  function tags(finding) {
    const result = el('div', 'finding-tags');
    // 补不全的精简撤销项缺严重程度与确定程度，宁可不显示也不显示空白徽标。
    if (finding.severity) result.append(el('span', `tag ${finding.severity}`, severityLabel[finding.severity]));
    if (finding.certainty) result.append(el('span', 'tag neutral', finding.certainty));
    return result;
  }
  function locationText(location) {
    if (!location) return '未提供可定位的节点';
    const node = nodeById(data, location.node);
    if (!validLocation(data, location)) return `${location.label || '位置不可用'} · ${location.reason || '无法对应节点'}`;
    return `${location.label || `${node.name}${location.cut == null ? ' · 节点级问题' : ` · CUT ${cutNumber(node, location.cut)}`}`}${location.associated ? ' · 外部脚本关联' : ''}`;
  }
  function renderList() {
    const findings = state.mode === 'report' ? data.findings.filter((item) => item.status === 'active') : [];
    const visible = state.filter ? findings.filter((item) => state.filter.ids.includes(item.id)) : findings;
    $('problem-count').textContent = `${visible.length} 条${state.filter ? '相关' : '有效'}问题`;
    $('filter-label').hidden = !state.filter;
    if (state.filter) $('filter-label').replaceChildren(el('span', '', state.filter.label), button('查看全部', actions.clearFilter));
    $('problem-list').replaceChildren(...visible.map((finding) => {
      const item = button('', () => actions.openFinding(finding.id), 'finding-card'); item.dataset.finding = finding.id;
      item.append(el('small', 'muted', finding.id), tags(finding), el('h2', '', finding.title), el('p', 'muted', locationText(finding.locations[0])));
      return item;
    }));
    if (!visible.length) $('problem-list').append(el('p', 'empty-message', state.mode === 'mismatch' ? '来源不匹配，已停止问题定位。节点池仍可浏览。' : '尚无可显示的报告问题。可以直接选择节点阅读。'));
    $('withdrawn').hidden = state.mode !== 'report';
    // 补不全的精简撤销项没有标题，退回问题 ID，避免整行显示 undefined。
    $('withdrawn-list').replaceChildren(...data.findings.filter((item) => item.status === 'withdrawn').map((finding) => button(`${finding.id} · ${finding.title || '（标题未提供）'}。${finding.revision || finding.quote}`, () => actions.openFinding(finding.id), 'withdrawn-entry')));
  }
  function section(title, text, className = '') {
    // 补不全的精简撤销项没有这些字段，整段省略比留一行空标题清楚。
    if (typeof text !== 'string' || !text.trim()) return document.createComment(title);
    const result = el('section', `explanation-section ${className}`); result.append(el('h3', '', title), el('p', '', text)); return result;
  }
  function renderDetail() {
    const finding = data.findings.find((item) => item.id === state.findingId); if (!finding) return;
    const heading = el('div', 'detail-heading'); heading.append(tags(finding), el('h2', '', finding.title || '（标题未提供）'));
    const locations = el('div', 'location-list'); locations.append(el('h3', '', '对应位置'));
    const unique = (finding.locations || []).filter((loc, index, all) => all.findIndex((other) => other.node === loc.node && other.cut === loc.cut && !!other.associated === !!loc.associated && (!loc.unavailable || other.label === loc.label)) === index);
    for (const location of unique) {
      const valid = validLocation(data, location), item = button(locationText(location), () => actions.locate(location), 'location-button');
      item.disabled = !valid;
      if (valid && state.main === location.node && (location.cut == null || state.cut === location.cut)) item.classList.add('active-location');
      locations.append(item);
    }
    if (finding.external) locations.append(el('p', 'muted', '共享脚本没有唯一所属 Part，请选择要查看的调用处。'));
    if (!unique.some((loc) => validLocation(data, loc))) locations.append(el('p', 'location-error', '无法定位原位置，未给其他节点添加标记。'));
    const evidence = el('details', 'evidence'); evidence.append(el('summary', '', '查看证据与范围'), el('pre', '', finding.quote), el('p', 'muted', data.title ? '引文核验通过仅说明证据存在，结论仍需结合触发条件判断。' : '以上为合成演示内容。'));
    const restore = button('返回问题位置', () => actions.restoreFindingLocation(), 'restore-location');
    $('detail-page').replaceChildren(heading, locations, restore, section('何时发生', finding.trigger), section('可能影响', finding.impact), section('建议先做', finding.recommendation, 'recommendation'), evidence);
    if (finding.revision) {
      const revision = el('details', 'evidence'); revision.append(el('summary', '', '查看修订理由'), el('p', '', finding.revision)); $('detail-page').append(revision);
    }
  }
  function render() {
    $('workspace').classList.toggle('side-collapsed', state.collapsed);
    $('expand').hidden = !state.collapsed;
    $('back').hidden = state.layer !== 2;
    $('side-title').textContent = state.layer === 1 ? '审查问题' : '问题说明';
    $('list-page').hidden = state.layer !== 1; $('detail-page').hidden = state.layer !== 2;
    if (state.layer === 1) renderList(); else renderDetail();
  }
  $('back').onclick = () => { state.layer = 1; render(); $('list-page').scrollTop = state.listScroll; };
  $('collapse').onclick = () => { state.collapsed = true; actions.render(); };
  $('expand').onclick = () => { state.collapsed = false; actions.render(); };
  return { render, renderDetail };
}
