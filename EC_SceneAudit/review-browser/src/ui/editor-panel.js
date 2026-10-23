import { el, button } from './dom.js';
import './editor-panel.css';

const TABS = [
  { id: 'annotations', label: '作者标注' },
  { id: 'repairs', label: '修复清单' },
  { id: 'node-patches', label: '节点补丁' }
];

// 面板级开关:host 的 hidden 表示面板开合,body 的 data-footer 表示底栏显隐。
// 子模块只提供各自 <dialog class="embedded"> 的 footer,不感知这两个状态。
export function mountEditorPanel({ signal, onChange }) {
  const host = el('aside', 'editor-panel'); host.id = 'scene-editor'; host.hidden = true;
  host.setAttribute('aria-label', '场景编辑面板');
  host.dataset.footer = 'collapsed';
  const header = el('header', 'editor-panel-header');
  const title = el('div', 'editor-panel-title');
  title.append(el('strong', '', '编辑与补丁'));
  const footerToggle = button('展开操作栏', () => toggleFooter(), 'editor-panel-footer-toggle');
  header.append(title, footerToggle);
  const tabs = el('nav', 'editor-panel-tabs'); tabs.setAttribute('aria-label', '编辑页面');
  const body = el('div', 'editor-panel-body');
  const pages = new Map(), buttons = new Map();
  for (const tab of TABS) {
    const page = el('section', 'editor-page'); page.dataset.page = tab.id; page.hidden = true;
    const tabButton = button(tab.label, () => open(tab.id)); tabButton.id = `editor-tab-${tab.id}`; tabButton.setAttribute('aria-pressed', 'false');
    pages.set(tab.id, page); buttons.set(tab.id, tabButton); tabs.append(tabButton); body.append(page);
  }
  const resize = el('div', 'editor-resize'); resize.setAttribute('aria-hidden', 'true');
  host.append(resize, header, tabs, body);
  const workspace = document.getElementById('workspace');
  (workspace || document.body).append(host);
  let active = null;

  function toggleFooter() {
    const collapsed = host.dataset.footer === 'collapsed';
    host.dataset.footer = collapsed ? 'expanded' : 'collapsed';
    footerToggle.textContent = collapsed ? '收起操作栏' : '展开操作栏';
    footerToggle.setAttribute('aria-pressed', String(!collapsed));
  }
  function open(name) {
    if (!pages.has(name)) return false;
    active = name; host.hidden = false;
    workspace?.classList.add('editor-visible');
    for (const [id, page] of pages) page.hidden = id !== active;
    for (const [id, item] of buttons) item.setAttribute('aria-pressed', String(id === active));
    buttons.get(active)?.focus?.({ preventScroll: true });
    onChange?.(true);
    return true;
  }
  function close() { host.hidden = true; workspace?.classList.remove('editor-visible'); onChange?.(false); }
  function toggle(name = 'annotations') { return host.hidden ? open(name) : (close(), false); }

  const MIN_WIDTH = 300, MAX_WIDTH = 720;
  function setWidth(width) {
    const clamped = Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, width));
    workspace?.style.setProperty('--editor-width', `${clamped}px`);
  }
  resize.addEventListener?.('pointerdown', event => {
    event.preventDefault(); resize.setPointerCapture(event.pointerId);
    const startX = event.clientX, startWidth = host.getBoundingClientRect().width;
    const move = moveEvent => setWidth(startWidth - (moveEvent.clientX - startX));
    const up = () => { resize.removeEventListener('pointermove', move); resize.removeEventListener('pointerup', up); };
    resize.addEventListener('pointermove', move); resize.addEventListener('pointerup', up);
  }, { signal });

  let destroyed = false;
  function destroy() { if (destroyed) return; destroyed = true; close(); host.remove(); }
  // 同级子模块按注册顺序清理;宿主延后一个微任务移除,避免 repairs 等控件查询已被清空。
  signal?.addEventListener('abort', () => queueMicrotask(destroy), { once: true });
  return { page: name => pages.get(name), open, close, toggle, active: () => active,
    footer: () => host.dataset.footer, toggleFooter };
}