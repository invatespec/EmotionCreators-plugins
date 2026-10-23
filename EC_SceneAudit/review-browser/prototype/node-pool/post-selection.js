async page => {
  await page.setViewportSize({ width: 1440, height: 900 });
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Performance.enable');
  const metric = async () => Object.fromEntries((await cdp.send('Performance.getMetrics')).metrics.map(x => [x.name, x.value]));
  const delta = (a,b) => Object.fromEntries(['TaskDuration','ScriptDuration','LayoutDuration','RecalcStyleDuration','LayoutCount','RecalcStyleCount'].map(k => [k, +(b[k]-a[k]).toFixed(5)]));
  const trace = [];
  cdp.on('Tracing.dataCollected', ({value}) => trace.push(...value));
  await cdp.send('Tracing.start', { categories: 'devtools.timeline,blink.user_timing', transferMode:'ReportEvents' });
  const phases=[];
  async function phase(name, action) {
    await page.evaluate(n=>performance.mark(n+'-start'),name);
    const before=await metric(), start=Date.now();
    await action();
    const after=await metric();
    await page.evaluate(n=>performance.mark(n+'-end'),name);
    phases.push({name, wallMs:Date.now()-start, ...delta(before,after)});
  }
  const dom=()=>page.evaluate(()=>({selected:__scenePerf.view.state.selected.size, rows:document.querySelectorAll('.node-patch-connection').length, options:document.querySelectorAll('#node-patch-connections option').length,elements:document.querySelectorAll('*').length}));
  const rounds=[];
  for (const [name,zoom] of [['small',.45],['all',.09]]) {
    await page.evaluate(z=>{const {view}=__scenePerf; view.state.selected=new Set(); view.state.main=null; view.state.camera={x:40,y:110,zoom:z}; view.actions.render();},zoom);
    await page.waitForTimeout(100);
    const rect=await page.locator('#viewport').boundingBox();
    await page.mouse.move(rect.x+20,rect.y+80); await page.mouse.down();
    await page.mouse.move(rect.x+rect.width-25,rect.y+rect.height-55,{steps:12});
    await page.waitForTimeout(30);
    await phase(name+'-release',async()=>{await page.mouse.up();await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));});
    const counts=await dom();
    await phase(name+'-idle',()=>page.waitForTimeout(500));
    await phase(name+'-pan',async()=>{
      await page.mouse.move(rect.x+60,rect.y+100);await page.mouse.down({button:'right'});
      await page.mouse.move(rect.x+160,rect.y+160,{steps:10});await page.mouse.up({button:'right'});
      await page.waitForTimeout(100);
    });
    await phase(name+'-zoom',async()=>{await page.mouse.wheel(0,-120);await page.waitForTimeout(150);});
    rounds.push({name,...counts});
  }
  const completed=new Promise(r=>cdp.once('Tracing.tracingComplete',r));await cdp.send('Tracing.end');await completed;
  for (const p of phases) {
    const a=trace.find(e=>e.name===p.name+'-start')?.ts,b=trace.find(e=>e.name===p.name+'-end')?.ts;
    p.trace={};
    for(const n of ['Layout','UpdateLayoutTree','Paint','RasterTask','PrePaint','CompositeLayers']){
      const es=trace.filter(e=>e.name===n && e.ts>=a && e.ts<=b && e.dur);
      p.trace[n]={count:es.length,ms:+(es.reduce((s,e)=>s+e.dur,0)/1000).toFixed(3)};
    }
  }
  await cdp.detach();
  return {rounds,phases};
}
