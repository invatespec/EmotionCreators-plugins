// 性能入口：复用正式 mountView 与正式样式，只替换数据源为固定 seed 夹具。
import '../../src/ui/styles.css';
import '../../src/styles.css';
import { mountView } from '../../src/ui/view.js';
import { data, PERF_META } from './perf-data.js';

// 测试专用采样：记录真实 pointermove 处理器与 rAF 渲染回调的工作耗时。
// 帧间隔只反映刷新率与等待，不能当主指标。
const nativeRaf = window.requestAnimationFrame.bind(window);
const work = { pointermove: [], render: [] };
window.__perfWork = work;
window.__perfNativeRaf = nativeRaf;
window.__perfWork.reset = () => { work.pointermove.length = 0; work.render.length = 0; };

const addEventListener = EventTarget.prototype.addEventListener;
EventTarget.prototype.addEventListener = function (type, listener, options) {
  if (type === 'pointermove' && this?.id === 'viewport' && typeof listener === 'function') {
    const wrapped = function (event) {
      const start = performance.now();
      try { return listener.call(this, event); }
      finally { work.pointermove.push(performance.now() - start); }
    };
    return addEventListener.call(this, type, wrapped, options);
  }
  return addEventListener.call(this, type, listener, options);
};

window.requestAnimationFrame = callback => nativeRaf(now => {
  const start = performance.now();
  try { callback(now); }
  finally { work.render.push(performance.now() - start); }
});

const view = mountView(data, 'scene', { storage: false });
// 测量脚本通过该句柄重置相机/选区/路线、读取计数；只存在于性能入口。
window.__scenePerf = { view, data, meta: PERF_META };
