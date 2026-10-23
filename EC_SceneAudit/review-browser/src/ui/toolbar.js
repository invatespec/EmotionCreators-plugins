import { createElement, MousePointer2, Hand, Maximize, Undo2, Redo2, Save, FilePen, AlignStartVertical, AlignCenterVertical,
  AlignEndVertical, AlignStartHorizontal, AlignCenterHorizontal, AlignEndHorizontal, Columns3, Rows3, LayoutGrid, RotateCcw, Eye, EyeClosed } from 'lucide';
import { $ } from './dom.js';
import './toolbar.css';

const icons = { 'mode-select': MousePointer2, 'mode-pan': Hand, 'edge-visibility': Eye, fit: Maximize, undo: Undo2, redo: Redo2, save: Save,
  'align-left': AlignStartVertical, 'align-hcenter': AlignCenterVertical, 'align-right': AlignEndVertical,
  'align-top': AlignStartHorizontal, 'align-vcenter': AlignCenterHorizontal, 'align-bottom': AlignEndHorizontal,
  'distribute-h': Columns3, 'distribute-v': Rows3, arrange: LayoutGrid, 'game-layout': RotateCcw };

// 半开眼:上眼睑弧线加一道横线，遮住下半;lucide 没有现成的三态眼图标。
const EyeHalf = [
  ['path', { d: 'M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696z' }],
  ['path', { d: 'M9 12a3 3 0 0 1 6 0' }]
];
const EDGE_VIEWS = [
  { icon: Eye, label: '显示全部连线', next: '隐藏脚本跳转' },
  { icon: EyeHalf, label: '仅隐藏脚本跳转', next: '隐藏全部连线' },
  { icon: EyeClosed, label: '隐藏全部连线', next: '显示全部连线' }
];

// 保存按钮双图标:项目态软盘=更新项目快照,草稿态笔形=临时草稿。切换不重挂载视图。
export function setSaveMode(projectMode) {
  const control = $('save');
  control.querySelector('svg')?.remove();
  control.prepend(createElement(projectMode ? Save : FilePen, { width: 16, height: 16, 'aria-hidden': 'true' }));
  control.title = projectMode ? '保存当前审查（更新项目快照）' : '保存草稿（临时，不入项目）';
  control.setAttribute('aria-label', projectMode ? '保存当前审查' : '保存草稿');
}

export function mountToolbar({ data, state, actions, signal }) {
  for (const [id, icon] of Object.entries(icons)) {
    const control = $(id); control.classList.add('tool-button');
    if (id === 'save') continue;
    control.querySelector('svg')?.remove();
    control.prepend(createElement(icon, { width: 16, height: 16, 'aria-hidden': 'true' }));
  }
  setSaveMode(Boolean(state.projectSave));
  // 三态眼：全部 → 仅隐藏脚本跳转 → 隐藏全部，循环切换。
  function paintEdgeView() {
    const view = EDGE_VIEWS[state.edgeView] || EDGE_VIEWS[0], control = $('edge-visibility');
    control.querySelector('svg')?.remove();
    control.prepend(createElement(view.icon, { width: 16, height: 16, 'aria-hidden': 'true' }));
    control.title = `${view.label}，点击切换为${view.next}`;
    control.setAttribute('aria-label', `连线显示：${view.label}`);
  }
  paintEdgeView();
  $('edge-visibility').addEventListener('click', () => actions.setEdgeView((state.edgeView + 1) % 3), { signal });
  const menu = $('layout-menu'), anchor = $('align-anchor'), gap = $('layout-gap');
  const readGap = () => gap.value.trim() === '' ? null : Number(gap.value);
  for (const [id, value] of Object.entries({ 'align-left': 'left', 'align-hcenter': 'center', 'align-right': 'right', 'align-top': 'top', 'align-vcenter': 'middle', 'align-bottom': 'bottom', 'distribute-h': 'horizontal', 'distribute-v': 'vertical' })) {
    const control = $(id), mode = id.startsWith('align') ? 'align' : 'distribute';
    const preview = () => { if (!control.disabled && gap.checkValidity()) actions.previewLayout(mode, value, readGap(), anchor.value); };
    control.addEventListener('mouseenter', preview, { signal }); control.addEventListener('focus', preview, { signal });
    control.addEventListener('mouseleave', actions.clearLayoutPreview, { signal }); control.addEventListener('blur', actions.clearLayoutPreview, { signal });
    control.addEventListener('click', () => {
      actions.clearLayoutPreview(); if (!gap.reportValidity()) return;
      if (mode === 'align') actions.alignSelection(value, anchor.value); else actions.distributeSelection(value, readGap());
    }, { signal });
  }
  menu.addEventListener('toggle', () => { if (!menu.open) actions.clearLayoutPreview(); }, { signal });
  document.addEventListener('pointerdown', event => {
    if (!menu.contains(event.target)) { actions.clearLayoutPreview(); menu.open = false; }
  }, { capture: true, signal });
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && menu.open) { actions.clearLayoutPreview(); menu.open = false; } }, { signal });
  $('snap-enabled').addEventListener('change', event => { state.snapEnabled = event.target.checked; }, { signal });
  $('reset-route').addEventListener('click', actions.resetRoute, { signal });
  let selectionKey = '';
  function selection() {
    const ids = [...state.selected], key = ids.join('\0');
    if (key !== selectionKey) {
      const previous = anchor.value; anchor.replaceChildren(new Option('选区边界', ''), ...ids.map(id => new Option(data.nodes.find(node => node.id === id)?.name || id, id)));
      if (ids.includes(previous)) anchor.value = previous;
      selectionKey = key;
    }
    $('reset-route').disabled = !state.routeOffsets?.[state.selectedEdge];
  }
  selection(); return { selection, edgeView: paintEdgeView };
}
