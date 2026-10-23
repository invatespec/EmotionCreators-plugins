// 用 playwright-cli run-code --filename 执行；页面须为构建后的 perf.html。
async page => {
  const check = (value, message) => { if (!value) throw new Error(message); };
  await page.reload();
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.waitForFunction(() => !!window.__scenePerf);
  const errors = [];
  const onError = error => errors.push(error.message);
  page.on('pageerror', onError);
  const counts = () => page.evaluate(() => ({
    selected: __scenePerf.view.state.selected.size,
    rows: document.querySelectorAll('.node-patch-connection').length,
    options: document.querySelectorAll('#node-patch-connections option').length,
    nodes: __scenePerf.data.nodes.length
  }));
  const paint = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const selectAll = async () => {
    await page.evaluate(() => {
      const { view } = __scenePerf;
      view.state.selected = new Set(); view.state.selectedEdge = null; view.state.main = null;
      view.state.camera = { x: 40, y: 110, zoom: .09 }; view.actions.render();
    });
    const rect = await page.locator('#viewport').boundingBox();
    await page.mouse.move(rect.x + 20, rect.y + 80); await page.mouse.down();
    await page.mouse.move(rect.x + rect.width - 25, rect.y + rect.height - 55, { steps: 8 });
    await page.mouse.up(); await paint();
  };
  const cdp = await page.context().newCDPSession(page);
  const memory = async () => {
    await cdp.send('HeapProfiler.collectGarbage');
    return cdp.send('Memory.getDOMCounters');
  };
  const rounds = [];
  let before;
  for (let i = 0; i < 6; i++) {
    await selectAll();
    const value = await counts();
    check(value.selected === value.nodes, '框选未覆盖全部节点');
    check(value.rows === 376 && value.options === value.rows, '空闲连接行展开了多余目标');
    rounds.push(value);
    if (i === 0) before = await memory();
  }
  const after = await memory();
  check(after.jsEventListeners <= before.jsEventListeners + 2, '重复框选积累事件监听');
  check(after.nodes <= before.nodes + 100, '重复框选保留了旧 DOM');

  await page.locator('#editor-open').click();
  await page.locator('#editor-tab-node-patches').click();
  const first = page.locator('#node-patch-connections select').first();
  await page.locator('#node-patch-search').focus();
  await page.keyboard.press('Tab');
  const targets = await first.evaluate(select => ({
    focused: select === document.activeElement,
    source: select.closest('label').dataset.source,
    slot: Number(select.closest('label').dataset.slot),
    original: select.value,
    values: [...select.options].map(option => option.value)
  }));
  check(targets.focused && targets.values.length === 319, '键盘聚焦未展开全部目标');
  check(!targets.values.includes(targets.source), '目标包含来源自身');
  check(await page.evaluate(values => __scenePerf.data.nodes.filter(n => n.id !== document.querySelector('.node-patch-connection').dataset.source).every(n => values.includes(n.id)), targets.values), '合法目标丢失');
  await page.locator('#node-patch-search').focus();
  check((await counts()).options === 376, '失焦未释放目标选项');

  // 鼠标首次打开原生下拉后，用键盘提交不同目标，检查统一写入口与撤销。
  await first.click();
  check(await first.locator('option').count() === 319, '鼠标打开未展开全部目标');
  await page.keyboard.press('Home'); await page.keyboard.press('End'); await page.keyboard.press('Enter');
  const edited = await page.evaluate(({ source, slot }) => __scenePerf.view.state.nodePatch.connections.find(c => c.sourceUid === source && c.slot === slot), targets);
  check(edited?.after.targetUid === targets.values.at(-1), '原生下拉没有提交选中的目标');
  check(edited.before.targetUid === targets.original, '编辑改变了原始目标证据');
  await page.locator('#undo').click();
  check(await first.inputValue() === targets.original, '撤销没有恢复目标');
  await page.locator('#redo').click();
  check(await first.inputValue() === targets.values.at(-1), '重做没有恢复修改');
  await page.locator('#undo').click();

  await first.focus();
  await page.evaluate(() => {
    window.__savedTargetAction = __scenePerf.view.actions.setConnectionTarget;
    __scenePerf.view.actions.setConnectionTarget = () => ({ ok: false, error: '测试拒绝' });
  });
  await first.selectOption(targets.values.at(-1));
  check(await first.inputValue() === targets.original, '写入失败未还原选择器');
  check((await page.locator('#node-patch-message').textContent()).includes('测试拒绝'), '失败提示丢失');
  await page.evaluate(() => { __scenePerf.view.actions.setConnectionTarget = window.__savedTargetAction; delete window.__savedTargetAction; });
  await page.locator('#node-patch-search').focus();

  await page.locator('#node-patch-filter').selectOption('all');
  const retained = await first.elementHandle();
  await page.evaluate(() => {
    const { view, data } = __scenePerf;
    view.state.selected = new Set([data.nodes[1].id]); view.actions.selectionChanged();
  });
  check(await retained.evaluate(select => select.isConnected), '全部范围因选区变化重建连接');
  await retained.dispose();
  await page.locator('#node-patch-filter').selectOption('selection');
  check(await page.evaluate(() => [...document.querySelectorAll('.node-patch-connection')].every(row => __scenePerf.view.state.selected.has(row.dataset.source))), '选区筛选不一致');
  await page.locator('#node-patch-filter').selectOption('modified');
  check((await counts()).rows === 0, '已修改筛选未排除原始连接');
  await page.locator('#node-patch-filter').selectOption('all');
  await page.locator('#node-patch-search').fill(targets.source);
  check((await counts()).rows > 0 && (await counts()).rows < 376, '搜索未缩小连接范围');
  await page.locator('#node-patch-search').fill('');
  check((await counts()).rows === 376, '清空搜索未恢复全部连接');
  await page.screenshot({ path: '.tmp/scene-perf-harness/selection-fixed.png' });
  check(errors.length === 0, errors.join('\n'));
  page.off('pageerror', onError);
  await cdp.detach();
  return { passed: true, rounds, memory: { before, after }, targets: targets.values.length,
    checks: ['重复全选', 'DOM 与监听数量', '键盘聚焦', '鼠标下拉提交', '全部合法目标', '自连排除', '撤销重做', '失败恢复', '范围筛选', '搜索'] };
}
