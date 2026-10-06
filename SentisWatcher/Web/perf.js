// SentisWatcher: the performance page. How hard the server's game thread works over time - the frame, the
// physics, the rest of the simulation, the other blocks of the game's own profiler - and the garbage
// collector, the memory, the simulation speed. All of it from /api/perf (the table perf, a row every 5 s).

const $ = (s) => document.querySelector(s);
const MIN = 60_000, HOUR = 3600_000;
const COLORS = ['#5fb3f9', '#f5b041', '#58d68d', '#ec7063', '#af7ac5', '#48c9b0', '#f4d03f', '#eb984e', '#85c1e9', '#f1948a',
  '#a3e4d7', '#d7bde2', '#f9e79f', '#aed6f1', '#fad7a0'];

const state = { from: 0, to: 0, span: HOUR, points: [], hidden: {}, live: true };

function status(text, error = false) {
  const s = $('#status');
  s.textContent = text || '';
  s.classList.toggle('error', !!error);
}
window.addEventListener('error', (e) => status('Ошибка страницы: ' + e.message, true));
window.addEventListener('unhandledrejection', (e) => status('Ошибка: ' + (e.reason && e.reason.message || e.reason), true));

async function api(call, params = {}) {
  const url = new URL('/api/' + call, location.origin);
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null) url.searchParams.set(k, v);
  const r = await fetch(url);
  const body = await r.json();
  if (!r.ok) throw new Error(body.error || r.statusText);
  return body;
}

// ------------------------------------------------------------------ time, as the server's clock shows it

const pad = (n) => String(n).padStart(2, '0');
let zoneOffset = 0;
const shifted = (t) => new Date(t + zoneOffset);
function fmt(t, withDate = true) {
  const d = shifted(t);
  const time = `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
  return withDate ? `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${time}` : time;
}
const toInput = (t) => fmt(t).replace(' ', 'T');
const fromInput = (v) => Date.parse(v + 'Z') - zoneOffset;
const num = (v, d = 2) => (v === null || v === undefined || Number.isNaN(v)) ? '—' : Number(v).toLocaleString('ru-RU', { maximumFractionDigits: d });

// ------------------------------------------------------------------ a chart on a canvas

// series: { key, name, color, values (one per point, null for none), kind: 'line' | 'bar', width, dash, axis: 'left' | 'right' }
function chart(canvas, series, opts = {}) {
  const dpr = window.devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr);
  const g = canvas.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);
  const pts = opts.points || state.points;
  const left = 46, right = opts.right ? 46 : 12, top = 10, bottom = 22;
  const pw = w - left - right, ph = h - top - bottom;
  const shown = series.filter((s) => !state.hidden[s.key]);
  const maxOf = (axis) => {
    let m = 0;
    for (const s of shown) if ((s.axis || 'left') === axis) for (const v of s.values) if (v != null && v > m) m = v;
    for (const r of opts.refs || []) if ((r.axis || 'left') === axis && r.y > m && r.y < m * 3) m = r.y;
    return niceMax(m || 1);
  };
  const yMax = { left: opts.leftMax ?? maxOf('left'), right: opts.rightMax ?? maxOf('right') };
  const x = (i) => left + (pts.length <= 1 ? pw / 2 : (pts[i].t - state.from) / Math.max(1, state.to - state.from) * pw);
  const y = (v, axis = 'left') => top + ph - Math.min(1, v / yMax[axis]) * ph;

  // the grid and the axes
  g.font = '11px Segoe UI, sans-serif';
  g.fillStyle = '#8b95a5';
  g.strokeStyle = '#262c36';
  g.lineWidth = 1;
  for (let k = 0; k <= 4; k++) {
    const yy = top + ph * k / 4;
    g.beginPath(); g.moveTo(left, yy); g.lineTo(left + pw, yy); g.stroke();
    g.textAlign = 'right';
    g.fillText(num(yMax.left * (1 - k / 4), yMax.left < 10 ? 1 : 0), left - 4, yy + 4);
    if (opts.right) { g.textAlign = 'left'; g.fillText(num(yMax.right * (1 - k / 4), yMax.right < 10 ? 2 : 0), left + pw + 4, yy + 4); }
  }
  g.textAlign = 'center';
  const ticks = timeTicks(state.from, state.to, Math.max(2, Math.floor(pw / 110)));
  for (const t of ticks) {
    const xx = left + (t - state.from) / Math.max(1, state.to - state.from) * pw;
    g.strokeStyle = '#20252e'; g.beginPath(); g.moveTo(xx, top); g.lineTo(xx, top + ph); g.stroke();
    g.fillText(tickLabel(t, state.to - state.from), xx, h - 6);
  }
  for (const r of opts.refs || []) {
    g.strokeStyle = r.color || '#6b7688'; g.setLineDash([4, 4]);
    const yy = y(r.y, r.axis); g.beginPath(); g.moveTo(left, yy); g.lineTo(left + pw, yy); g.stroke();
    g.setLineDash([]);
  }

  // bars first, then lines over them
  const bars = shown.filter((s) => s.kind === 'bar');
  if (bars.length && pts.length) {
    const slot = Math.max(1, pw / Math.max(1, pts.length) * 0.8);
    for (let i = 0; i < pts.length; i++) {
      let base = 0;
      for (const s of bars) {
        const v = s.values[i] || 0;
        if (!v) continue;
        g.fillStyle = s.color;
        const y0 = y(base, s.axis), y1 = y(base + v, s.axis);
        g.fillRect(x(i) - slot / 2, y1, slot, Math.max(1, y0 - y1));
        base += v;
      }
    }
  }
  for (const s of shown.filter((s) => s.kind !== 'bar')) {
    g.strokeStyle = s.color; g.lineWidth = s.width || 1.6; g.setLineDash(s.dash || []);
    g.beginPath();
    let pen = false, lastT = null;
    for (let i = 0; i < pts.length; i++) {
      const v = s.values[i];
      // a gap in the data (the server was down): the line breaks
      const gap = lastT !== null && pts[i].t - lastT > Math.max(15_000, (state.to - state.from) / Math.max(1, pts.length) * 2.5);
      if (v == null || gap) { pen = false; if (v == null) continue; }
      if (!pen) { g.moveTo(x(i), y(v, s.axis)); pen = true; } else g.lineTo(x(i), y(v, s.axis));
      lastT = pts[i].t;
    }
    g.stroke(); g.setLineDash([]);
  }
  canvas._chart = { series: shown, x, left, pw, pts };
  // the range picked with the mouse: on every chart, they share the time axis
  if (selection) {
    const sx = (t) => left + (t - state.from) / Math.max(1, state.to - state.from) * pw;
    g.fillStyle = '#5fb3f933';
    g.fillRect(sx(selection[0]), top, sx(selection[1]) - sx(selection[0]), ph);
    g.strokeStyle = '#5fb3f9'; g.lineWidth = 1;
    g.strokeRect(sx(selection[0]) + 0.5, top + 0.5, sx(selection[1]) - sx(selection[0]) - 1, ph - 1);
  }
}

function niceMax(v) {
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  for (const m of [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]) if (m * p >= v) return m * p;
  return 10 * p;
}
function timeTicks(from, to, count) {
  const steps = [MIN, 2 * MIN, 5 * MIN, 10 * MIN, 15 * MIN, 30 * MIN, HOUR, 2 * HOUR, 3 * HOUR, 6 * HOUR, 12 * HOUR, 24 * HOUR];
  const step = steps.find((s) => (to - from) / s <= count) || 24 * HOUR;
  const first = Math.ceil((from + zoneOffset) / step) * step - zoneOffset;
  const out = [];
  for (let t = first; t <= to; t += step) out.push(t);
  return out;
}
function tickLabel(t, span) {
  const d = shifted(t);
  const hm = `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
  return span > 36 * HOUR ? `${pad(d.getUTCDate())}.${pad(d.getUTCMonth() + 1)} ${hm}` : hm;
}

// the values under the mouse, for every chart
const tip = $('#tip');
function hoverChart(canvas, ev) {
  const c = canvas._chart;
  const points = c && c.pts ? c.pts : state.points;
  if (!c || !points.length) { tip.hidden = true; return; }
  const r = canvas.getBoundingClientRect();
  const mx = ev.clientX - r.left;
  let best = 0, bestD = Infinity;
  for (let i = 0; i < points.length; i++) { const d = Math.abs(c.x(i) - mx); if (d < bestD) { bestD = d; best = i; } }
  const p = points[best];
  const rows = c.series.map((s) => `<div class="r"><span><span class="sw" style="background:${s.color}"></span>${s.name}</span><b>${num(s.values[best], 3)}${s.unit ? ' ' + s.unit : ''}</b></div>`);
  tip.innerHTML = `<div class="t">${fmt(p.t)}</div>` + rows.join('');
  tip.hidden = false;
  tip.style.left = Math.min(ev.clientX + 14, window.innerWidth - tip.offsetWidth - 8) + 'px';
  tip.style.top = ev.clientY + 14 + 'px';
}
for (const canvas of document.querySelectorAll('canvas')) {
  canvas.addEventListener('mousemove', (ev) => { if (!drag) hoverChart(canvas, ev); });
  canvas.addEventListener('mouseleave', () => { tip.hidden = true; });
}

// ------------------------------------------------------------------ the charts' time axis: zoom, select, pan

// All the charts show one range: the wheel zooms it around the mouse, a drag picks a part of it (then "show"), the
// pressed wheel moves it. The charts move at once with what they have; the data of the new range comes a moment later.
const MIN_SPAN = MIN, MAX_SPAN = 31 * 24 * HOUR;
let selection = null;           // [from, to] while a range is picked
let drag = null;                // { mode: 'click' | 'select' | 'pan', canvas, x, t, from, to }
const rangeHistory = [];
function timeAt(canvas, clientX) {
  const c = canvas._chart, r = canvas.getBoundingClientRect();
  const left = c ? c.left : 46, pw = c ? c.pw : r.width - 58;
  return state.from + Math.min(1, Math.max(0, (clientX - r.left - left) / Math.max(1, pw))) * (state.to - state.from);
}
function remember() {
  rangeHistory.push([state.from, state.to]);
  if (rangeHistory.length > 30) rangeHistory.shift();
  $('#rangeBack').hidden = false;
}
let reloadTimer = null;
function moveRange(from, to, now = false) {
  const span = Math.min(MAX_SPAN, Math.max(MIN_SPAN, to - from));
  const middle = (from + to) / 2;
  state.from = Math.round(middle - span / 2);
  state.to = Math.round(middle + span / 2);
  state.span = span;
  $('#from').value = toInput(state.from);
  $('#to').value = toInput(state.to);
  $('#live').checked = false;
  selection = null;
  $('#rangeBar').hidden = true;
  draw();
  clearTimeout(reloadTimer);
  reloadTimer = setTimeout(() => load(), now ? 0 : 350);
}
function hideSelection() {
  selection = null;
  $('#rangeBar').hidden = true;
  draw();
}
let wheeling = false;
for (const canvas of document.querySelectorAll('canvas')) {
  canvas.addEventListener('wheel', (e) => {
    if (!e.ctrlKey && !e.shiftKey) return;            // the plain wheel scrolls the page: the charts fill it
    e.preventDefault();
    if (!wheeling) { remember(); wheeling = true; setTimeout(() => { wheeling = false; }, 1500); }
    const at = timeAt(canvas, e.clientX);
    const k = e.deltaY > 0 ? 1.3 : 1 / 1.3;
    const span = Math.min(MAX_SPAN, Math.max(MIN_SPAN, (state.to - state.from) * k));
    const f = (at - state.from) / Math.max(1, state.to - state.from);
    moveRange(at - span * f, at - span * f + span);
  }, { passive: false });
  canvas.addEventListener('mousedown', (e) => {
    if (e.button === 1) {
      e.preventDefault();             // no auto-scroll
      remember();
      drag = { mode: 'pan', canvas, x: e.clientX, from: state.from, to: state.to };
      return;
    }
    if (e.button !== 0) return;
    drag = { mode: 'click', canvas, x: e.clientX, t: timeAt(canvas, e.clientX) };
  });
  canvas.addEventListener('auxclick', (e) => { if (e.button === 1) e.preventDefault(); });
}
window.addEventListener('mousemove', (e) => {
  if (!drag) return;
  if (drag.mode === 'pan') {
    const pw = drag.canvas._chart?.pw || drag.canvas.clientWidth;
    const shift = -(e.clientX - drag.x) / pw * (drag.to - drag.from);
    moveRange(drag.from + shift, drag.to + shift);
    return;
  }
  if (drag.mode === 'click' && Math.abs(e.clientX - drag.x) > 4) drag.mode = 'select';
  if (drag.mode === 'select') {
    const t = timeAt(drag.canvas, e.clientX);
    selection = [Math.min(drag.t, t), Math.max(drag.t, t)];
    $('#rangeBar').hidden = true;
    tip.hidden = true;
    draw();
  }
});
window.addEventListener('mouseup', (e) => {
  const d = drag;
  if (!d) return;
  drag = null;
  if (d.mode === 'click') { if (selection) hideSelection(); return; }
  if (d.mode !== 'select') return;
  if (!selection || selection[1] - selection[0] < 1000) { hideSelection(); return; }
  // the button over the chart the range was picked on, in the middle of the range
  const [a, b] = selection;
  const r = d.canvas.getBoundingClientRect(), c = d.canvas._chart;
  const mid = r.left + c.left + ((a + b) / 2 - state.from) / Math.max(1, state.to - state.from) * c.pw;
  const sameDay = fmt(a).slice(0, 10) === fmt(b).slice(0, 10);
  $('#rangeZoom').textContent = `Показать ${fmt(a, !sameDay)} – ${fmt(b, false)} ›`;
  const bar = $('#rangeBar');
  bar.style.left = Math.max(140, Math.min(window.innerWidth - 140, mid)) + 'px';
  bar.style.top = Math.max(4, r.top + 14) + 'px';
  bar.hidden = false;
});
$('#rangeZoom').addEventListener('click', () => {
  if (!selection) return;
  const [a, b] = selection;
  remember();
  moveRange(a, b, true);
});
$('#rangeCancel').addEventListener('click', () => hideSelection());
$('#rangeBack').addEventListener('click', () => {
  const back = rangeHistory.pop();
  $('#rangeBack').hidden = !rangeHistory.length;
  if (back) moveRange(back[0], back[1], true);
});
window.addEventListener('keydown', (e) => { if (e.key === 'Escape' && selection) hideSelection(); });
// the button stands over a place on the page: gone when the page scrolls under it
document.querySelector('main').addEventListener('scroll', () => { $('#rangeBar').hidden = true; });

function legend(el, series, extra = () => '') {
  el.innerHTML = series.map((s) =>
    `<label class="${state.hidden[s.key] ? '' : 'on'}" data-key="${s.key}"><input type="checkbox"><span class="sw" style="background:${s.color}"></span>${s.name}<span class="d">${extra(s)}</span></label>`).join('');
  el.querySelectorAll('label').forEach((l) => l.addEventListener('click', (e) => {
    e.preventDefault();
    const key = l.dataset.key;
    if (e.ctrlKey || e.metaKey) {
      // Ctrl+click: only this one shown; again on the only one shown - all of them back
      const alone = series.every((s) => (s.key === key) !== !!state.hidden[s.key]);
      for (const s of series) state.hidden[s.key] = alone ? false : s.key !== key;
    } else state.hidden[key] = !state.hidden[key];
    draw();
  }));
}

// ------------------------------------------------------------------ the page

const col = (key) => state.points.map((p) => p[key]);
const avg = (vs) => { const v = vs.filter((x) => x != null); return v.length ? v.reduce((a, b) => a + b, 0) / v.length : null; };
const max = (vs) => { const v = vs.filter((x) => x != null); return v.length ? Math.max(...v) : null; };
const min = (vs) => { const v = vs.filter((x) => x != null); return v.length ? Math.min(...v) : null; };

function draw() {
  const pts = state.points;
  // the frame: its average, the physics and the rest (logic), and the worst frames
  const frame = [
    { key: 'frame', name: 'кадр целиком', color: '#d8dee9', values: col('frame'), unit: 'мс' },
    { key: 'logic', name: 'логика (кадр без физики)', color: '#5fb3f9', values: col('logic'), unit: 'мс' },
    { key: 'physics', name: 'физика', color: '#f5b041', values: col('physics'), unit: 'мс' },
    { key: 'frameMax', name: 'худший кадр', color: '#d8dee9', values: col('frameMax'), width: 0.8, dash: [2, 2], unit: 'мс' },
    { key: 'physicsMax', name: 'худший шаг физики', color: '#f5b041', values: col('physicsMax'), width: 0.8, dash: [2, 2], unit: 'мс' },
  ];
  if (state.hidden.frameMax === undefined) state.hidden.frameMax = false;
  // scaled to the averages (a start-up frame of a second would flatten them): the worst frames above run off the top
  chart($('#cFrame'), frame, { refs: [{ y: 1000 / 60 }], leftMax: niceMax(Math.max(20, (max(col('frame')) || 0) * 2.5)) });
  legend($('#lFrame'), frame, (s) => ' ' + num(avg(s.values)) + (s.key.endsWith('Max') ? ', макс ' + num(max(s.values)) : ''));

  // the parts of the frame, each without the timed parts inside it; "other" is what none of them took
  const PARTS = [
    ['physics', 'физика (Havok)', '#f5b041'],
    ['entities_before', 'сущности до физики (гриды, блоки, персонажи)', '#5fb3f9'],
    ['entities_after', 'сущности после физики', '#58d68d'],
    ['game_logic', 'игровая логика (скрипты модов, логика блоков)', '#f1948a'],
    ['session', 'компоненты сессии (кроме сущностей и физики)', '#af7ac5'],
    ['plugins', 'плагины Torch', '#48c9b0'],
    ['invoke', 'задания из других потоков (Torch, плагины)', '#f7dc6f'],
    ['callbacks', 'завершение фоновых задач', '#85929e'],
    ['replication', 'сеть: репликация клиентам', '#5dade2'],
    ['network', 'сеть: приём и отправка пакетов', '#a9cce3'],
    ['save', 'сохранение мира (снимок и подготовка к нему)', '#ec7063'],
    ['torch', 'Torch (сам, без плагинов: его окно, коллекции, счётчики)', '#e59866'],
    ['game_loop', 'цикл игры (память платформы, статистика, GUI и ввод, монитор сети)', '#76d7c4'],
    ['session_own', 'сессия сама (GPS, лимиты блоков, запросы владения)', '#d7bde2'],
    ['other', 'прочее (кадр движка: рендер-прокси, профайлер игры)', '#bdc3c7'],
  ];
  const blocks = PARTS.map(([n, name, color]) => ({
    key: 'b:' + n, name, color, unit: 'мс',
    values: n === 'physics' ? col('physics') : pts.map((p) => p.blocks ? (p.blocks[n] ? p.blocks[n][0] : 0) : null),
    maxes: n === 'physics' ? col('physicsMax') : pts.map((p) => p.blocks ? (p.blocks[n] ? p.blocks[n][1] : 0) : null),
  }));
  chart($('#cBlocks'), blocks, { leftMax: niceMax(Math.max(2, ...blocks.map((b) => (max(b.values) || 0) * 1.5))) });
  legend($('#lBlocks'), blocks, (s) => ' ' + num(avg(s.values), 3) + ', макс ' + num(max(s.maxes), 1));

  // the collector. Its time in milliseconds of each second (the recorded share of the time in GC, % - 1% of a second is
  // 10 ms): a number to lay beside the frame's 16.7 ms, and the same whatever the width of a point
  const gcMs = col('gcTime').map((v) => v == null ? null : v * 10);
  const gc = [
    { key: 'gc0', name: 'сборки поколения 0', color: '#48c9b0', kind: 'bar', values: col('gc0') },
    { key: 'gc1', name: 'поколения 1', color: '#f4d03f', kind: 'bar', values: col('gc1') },
    { key: 'gc2', name: 'поколения 2 (полные)', color: '#ec7063', kind: 'bar', values: col('gc2') },
    { key: 'gcTime', name: 'время в GC за секунду', color: '#d7bde2', values: gcMs, axis: 'right', unit: 'мс' },
  ];
  chart($('#cGc'), gc, { right: true });
  legend($('#lGc'), gc, (s) => s.key === 'gcTime' ? ' ' + num(avg(s.values), 1) + ' мс, макс ' + num(max(s.values), 1) + ' мс' : ' всего ' + num(s.values.reduce((a, b) => a + (b || 0), 0), 0));

  const mem = [
    { key: 'working', name: 'в памяти (working set)', color: '#5fb3f9', values: col('working'), unit: 'МБ' },
    { key: 'private', name: 'выделено процессу (private)', color: '#af7ac5', values: col('private'), unit: 'МБ' },
    { key: 'managed', name: 'куча .NET', color: '#58d68d', values: col('managed'), unit: 'МБ' },
  ];
  chart($('#cMem'), mem);
  legend($('#lMem'), mem, (s) => ' ' + num(s.values[s.values.length - 1], 0) + ', макс ' + num(max(s.values), 0));

  const sim = [
    { key: 'sim', name: 'скорость симуляции', color: '#58d68d', values: col('sim') },
    { key: 'players', name: 'игроков онлайн', color: '#5fb3f9', values: col('players'), axis: 'right' },
  ];
  chart($('#cSim'), sim, { leftMax: 1.05, right: true, refs: [{ y: 1 }] });
  legend($('#lSim'), sim, (s) => s.key === 'sim' ? ' ' + num(avg(s.values), 2) + ', мин ' + num(min(s.values), 2) : ' макс ' + num(max(s.values), 0));

  drawSessionComponents();
  summary();
  drawSeriesCharts();
}

// the part "session" by component, every frame (perf.components): the heaviest on average, and every one that had a long frame
function drawSessionComponents() {
  const pts = state.points;
  const totals = {}, worst = {};
  for (const p of pts) for (const [n, [a, m]] of Object.entries(p.components || {})) {
    totals[n] = (totals[n] || 0) + a;
    worst[n] = Math.max(worst[n] || 0, m);
  }
  const names = Object.keys(totals);
  if (!names.length) {
    $('#nSession').textContent = 'За этот интервал замера по компонентам нет (пишется с версии от 05.10.2026).';
    chart($('#cSession'), [], { points: [] });
    $('#lSession').innerHTML = '';
    return;
  }
  $('#nSession').textContent = '';
  const heavy = names.sort((a, b) => totals[b] - totals[a]).slice(0, 10);
  const chosen = [...new Set([...heavy, ...names.filter((n) => worst[n] >= 2)])];
  const rest = names.filter((n) => !chosen.includes(n));
  const series = chosen.map((n, i) => ({
    key: 'sc:' + n, name: componentName(n), color: COLORS[i % COLORS.length], unit: 'мс',
    values: pts.map((p) => p.components ? (p.components[n] ? p.components[n][0] : 0) : null),
    maxes: pts.map((p) => p.components ? (p.components[n] ? p.components[n][1] : 0) : null),
  }));
  if (rest.length) series.push({
    key: 'sc:', name: 'остальные', color: '#bdc3c7', unit: 'мс',
    values: pts.map((p) => p.components ? rest.reduce((s, n) => s + (p.components[n] ? p.components[n][0] : 0), 0) : null),
    maxes: pts.map((p) => p.components ? Math.max(0, ...rest.map((n) => p.components[n] ? p.components[n][1] : 0)) : null),
  });
  chart($('#cSession'), series, { leftMax: niceMax(Math.max(0.5, ...series.filter((s) => !state.hidden[s.key]).map((s) => (max(s.values) || 0) * 1.3))) });
  legend($('#lSession'), series, (s) => ' ' + num(avg(s.values), 3) + ', худший кадр ' + num(max(s.maxes), 1));
}

// ------------------------------------------------------------------ session components and plugins over time (/api/loadseries)

// what the game's session components do, for the ones most often seen
const COMPONENT_NAMES = {
  'MySector': 'MySector — запуск обновления сущностей (своё время, без самих сущностей)',
  'MyPhysics': 'MyPhysics — физика (без шага Havok, он в своей части)',
  'MyEntityComponentUpdater': 'обновление компонентов сущностей',
  'MySpaceFaunaComponent': 'фауна: появление волков и пауков',
  'MyEncounterGenerator': 'встречи в космосе (NPC-гриды)',
  'MyProceduralWorldGenerator': 'процедурные астероиды',
  'MyAiRvoComponent': 'ИИ: обход препятствий',
  'MyAIComponent': 'ИИ: боты-животные',
  'MySessionComponentWarningSystem': 'система предупреждений игрокам',
  'MyPlanetEnvironmentSessionComponent': 'окружение планет (деревья, кусты)',
  'MyGamePruningStructure': 'дерево поиска сущностей',
  'MySessionComponentContainerDropSystem': 'сброс контейнеров с наградами',
  'MySessionComponentSafeZones': 'безопасные зоны',
  'MySessionComponentEconomy': 'экономика',
  'MyGridsStorageSessionComponent': 'хранилище гридов (терминал услуг)',
  'MySessionComponentWeather': 'погода',
  'MyTrashRemoval': 'уборка мусора',
  'MySessionComponentTrash': 'уборка мусора',
  'MyFloatingObjects': 'плавающие предметы (руда, выброшенное)',
  'MySectorWeatherComponent': 'погода',
  'MyPlanetaryEncountersGenerator': 'встречи на планетах (NPC-гриды)',
  'MyHazardExposureComponent': 'опасности окружения для персонажей',
  'MySessionComponentSmartUpdater': 'отложенные обновления блоков (SmartUpdater)',
  'MySessionComponentAntiCheat': 'античит',
  'MyExplosions': 'взрывы',
  'MyEnvironmentalParticles': 'частицы окружения',
  'Simulate': 'сеть: шаг репликации (MyReplicationLayer.Simulate)',
};
function componentName(full) {
  if (!full) return 'остальные';
  if (full === 'small') return 'мелкие (меньше 0,005 мс в среднем и 0,5 мс в худшем кадре)';
  const short = full.split('.').pop();
  return COMPONENT_NAMES[short] ? `${COMPONENT_NAMES[short]} <span class="dim">(${short})</span>` : short;
}

const seriesData = { component: null, plugin: null, pb: null, gridPhysics: null, gridLogic: null };
function drawSeries(kind, canvas, legendEl, nameOf, noteEl) {
  const d = seriesData[kind];
  if (!d || !d.t.length) {
    noteEl.textContent = kind === 'pb' ? 'За этот интервал ни один программируемый блок не запускался.'
      : kind === 'gridPhysics' && d && d.on ? 'За этот интервал замеров нет (физика по гридам пишется с версии от 06.10.2026, и только когда есть активные гриды).'
      : d && !d.on ? 'Замер выключен (Torch → SentisWatcher → Performance, или !watch load on).' : 'За этот интервал замеров нет.';
    chart(canvas, [], { points: [] });
    legendEl.innerHTML = '';
    return;
  }
  noteEl.textContent = '';
  const pts = d.t.map((t) => ({ t }));
  const series = d.series.map((s, i) => ({
    key: kind + ':' + s.name, name: nameOf(s.name, s), color: s.name ? COLORS[i % COLORS.length] : '#bdc3c7', unit: 'мс',
    values: s.ms, maxes: s.max,
  }));
  chart(canvas, series, { points: pts, leftMax: niceMax(Math.max(kind === 'pb' ? 0.02 : 0.5, ...series.filter((s) => !state.hidden[s.key]).map((s) => (max(s.values) || 0) * 1.3))) });
  legend(legendEl, series, (s) => ' ' + num(avg(s.values), 3) + ', худший кадр ' + num(max(s.maxes), 1));
}
function drawSeriesCharts() {
  drawSeries('component', $('#cComponents'), $('#lComponents'), componentName, $('#nComponents'));
  drawSeries('plugin', $('#cPlugins'), $('#lPlugins'), (n) => n || 'остальные', $('#nPlugins'));
  drawSeries('pb', $('#cPb'), $('#lPb'), (n, s) => n ? esc(n) + (s.owner ? ` <span class="dim">— ${esc(s.owner)}</span>` : ' <span class="dim">— без владельца</span>') : 'остальные', $('#nPb'));
  const grid = (n, s) => n ? esc(n) + (s.owner ? ` <span class="dim">— ${esc(s.owner)}</span>` : '') : 'остальные';
  drawSeries('gridPhysics', $('#cGridPhysics'), $('#lGridPhysics'), grid, $('#nGridPhysics'));
  drawSeries('gridLogic', $('#cGridLogic'), $('#lGridLogic'), grid, $('#nGridLogic'));
}

// ------------------------------------------------------------------ panels fold at their titles

// which are folded is this viewer's own (the browser keeps it); the ones folded until asked for
const FOLDED_AT_FIRST = ['cMem', 'cGc', 'cPlugins', 'cComponents'];
const FOLD_KEY = 'watcher.perf.folded';
function foldedPanels() {
  try {
    const saved = JSON.parse(localStorage.getItem(FOLD_KEY) || 'null');
    if (saved && typeof saved === 'object') return saved;
  } catch (e) { /* no storage here: the defaults */ }
  return Object.fromEntries(FOLDED_AT_FIRST.map((id) => [id, true]));
}
const folded = foldedPanels();
for (const panel of document.querySelectorAll('.board .panel')) {
  const id = panel.querySelector('canvas, table')?.id;
  const title = panel.querySelector('h3');
  if (!id || !title) continue;
  panel.classList.add('folds');
  panel.classList.toggle('folded', !!folded[id]);
  title.title = (title.title ? title.title + ' ' : '') + 'Щелчок — свернуть или развернуть.';
  title.addEventListener('click', () => {
    folded[id] = !folded[id];
    panel.classList.toggle('folded', folded[id]);
    try { localStorage.setItem(FOLD_KEY, JSON.stringify(folded)); } catch (e) { /* not kept, then */ }
    // a chart drawn while folded had no width
    if (!folded[id]) draw();
  });
}

function summary() {
  const pts = state.points;
  if (!pts.length) { $('#summary').innerHTML = '<div class="dim">За этот интервал записей нет (замеры пишутся с того момента, как сервер запущен с этой версией плагина).</div>'; return; }
  const last = pts[pts.length - 1];
  const frame = avg(col('frame')), physics = avg(col('physics'));
  const cards = [
    ['Кадр сейчас', num(last.frame) + ' мс', last.frame > 16.7, 'в среднем ' + num(frame) + ' мс'],
    ['Худший кадр', num(max(col('frameMax'))) + ' мс', max(col('frameMax')) > 50, 'за интервал'],
    ['Физика', frame ? Math.round(physics / frame * 100) + '%' : '—', false, 'кадра в среднем (' + num(physics) + ' мс)'],
    ['Скорость симуляции', num(last.sim), last.sim < 0.95, 'мин за интервал ' + num(min(col('sim')))],
    ['Время в GC', num(avg(col('gcTime')) * 10, 1) + ' мс', avg(col('gcTime')) > 5, 'за секунду · ' + 'полных сборок: ' + num(col('gc2').reduce((a, b) => a + b, 0), 0)],
    ['Память', num(last.working / 1024, 1) + ' ГБ', false, 'куча .NET ' + num(last.managed / 1024, 1) + ' ГБ'],
    ['Игроков онлайн', num(last.players, 0), false, 'макс ' + num(max(col('players')), 0)],
    ['Гридов', last.grids == null ? '—' : num(last.grids, 0), false, max(col('grids')) == null ? 'пишется с версии от 06.10.2026' : 'макс ' + num(max(col('grids')), 0) + ', мин ' + num(min(col('grids')), 0)],
  ];
  $('#summary').innerHTML = cards.map(([k, v, bad, s]) => `<div class="card"><div class="k">${k}</div><div class="v ${bad ? 'bad' : ''}">${v}</div><div class="s">${s}</div></div>`).join('');
}

// ------------------------------------------------------------------ who loads the game thread (/api/load)

const LOAD_TABS = [
  ['player', 'Игроки'], ['grid', 'Гриды'], ['plugin', 'Плагины'], ['component', 'Компоненты сессии'],
  ['system', 'Движок'], ['entity_component', 'Компоненты сущностей'], ['parallel', 'Параллельно'], ['other', 'Прочие сущности'], ['character', 'Персонажи'],
];
const SYSTEM_NAMES = {
  'physics': 'Физика (Havok)',
  'entities.parallel': 'Параллельные обновления сущностей',
  'entities.once_before_frame': 'Первое обновление новых сущностей',
  'entities.simulate': 'Simulate сущностей',
  'network.simulate': 'Сеть: репликация',
  'entities.invoke_later': 'Отложенные вызовы сущностей',
  'entities.apply_changes': 'Добавление/удаление сущностей в обновлениях',
  'entities.drain_init_work': 'Ожидание фоновой инициализации сущностей',
  'entities.delete': 'Удаление сущностей',
  'entities.create': 'Появление сущностей, собранных в фоне',
  'entities.game_logic': 'Игровая логика сущностей (скрипты модов)',
};
let loadData = null, loadTab = 'player';
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

function drawLoad() {
  $('#loadTabs').innerHTML = LOAD_TABS.map(([k, name]) =>
    `<button data-k="${k}" class="${k === loadTab ? 'on' : ''}">${name} <span class="dim">${(loadData?.kinds?.[k] || []).length || ''}</span></button>`).join('');
  const d = loadData;
  if (!d || !d.frames) {
    $('#loadNote').textContent = d && !d.on ? 'Замер выключен (Torch → SentisWatcher → Performance, или !watch load on).' : 'За этот интервал замеров нет.';
    $('#loadTable').innerHTML = '';
    return;
  }
  $('#loadNote').textContent = `Замерено кадров: ${num(d.frames, 0)}, в среднем ${num(d.frame)} мс, худший ${num(d.frameMax, 1)} мс` +
    (d.on ? '' : ' · сейчас замер выключен');
  const items = d.kinds[loadTab] || [];
  const top = Math.max(...items.map((i) => i.ms), 1e-9);
  const owner = loadTab === 'grid';
  const name = (i) => loadTab === 'system' ? (SYSTEM_NAMES[i.name] || i.name) : loadTab === 'player' && !i.ownerId ? '<span class="dim">без владельца</span>' : esc(i.name) || '<span class="dim">—</span>';
  $('#loadTable').innerHTML = `<tr><th>${loadTab === 'player' ? 'Игрок' : 'Имя'}</th>${owner ? '<th>Владелец</th>' : ''}${loadTab === 'player' ? '<th class="n">Гридов</th>' : ''}` +
    `<th class="n">мс в кадре</th><th>доля кадра</th><th class="n">худший кадр, мс</th></tr>` +
    items.map((i) => `<tr><td class="name">${name(i)}</td>${owner ? `<td>${esc(i.owner) || '<span class="dim">—</span>'}</td>` : ''}` +
      `${loadTab === 'player' ? `<td class="n">${i.grids}</td>` : ''}` +
      `<td class="n">${num(i.ms, 3)}</td><td class="share"><i style="width:${(i.ms / top * 100).toFixed(1)}%"></i><span>${num(i.ms / d.frame * 100, 1)}%</span></td>` +
      `<td class="n ${i.max > 16.7 ? 'bad' : ''}">${num(i.max, 2)}</td></tr>`).join('');
}
$('#loadTabs').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  loadTab = b.dataset.k;
  drawLoad();
});

async function load() {
  state.from = fromInput($('#from').value);
  state.to = fromInput($('#to').value);
  const width = $('#cFrame').clientWidth || 1200;
  status('Загружаю…');
  const [from, to] = [state.from, state.to];
  const stale = () => from !== state.from || to !== state.to;       // the range moved on meanwhile: the next load is the one
  const data = await api('perf', { from, to, points: Math.min(3000, Math.max(200, Math.round(width))) });
  if (stale()) return;
  state.points = data.points;
  draw();
  const loaded = await api('load', { from, to, top: 200 });
  if (stale()) return;
  loadData = loaded;
  drawLoad();
  const series = await Promise.all([
    api('loadseries', { from, to, kind: 'component', top: 10 }),
    api('loadseries', { from, to, kind: 'plugin', top: 10 }),
    api('loadseries', { from, to, kind: 'pb', top: 40 }),
    api('gridseries', { from, to, what: 'physics', top: 10 }),
    api('gridseries', { from, to, what: 'logic', top: 10 }),
  ]);
  if (stale()) return;
  [seriesData.component, seriesData.plugin, seriesData.pb, seriesData.gridPhysics, seriesData.gridLogic] = series;
  drawSeriesCharts();
  status(state.points.length ? `${state.points.length} точек, ${fmt(state.from)} — ${fmt(state.to)}` : 'Нет записей за интервал');
}

function setSpan(span) {
  state.span = span;
  selection = null;
  $('#rangeBar').hidden = true;
  const now = Date.now();
  $('#from').value = toInput(now - span);
  $('#to').value = toInput(now);
  load();
}

// ------------------------------------------------------------------ who is online: now, not from the records

async function loadOnline() {
  let players;
  try {
    players = await api('online');
  } catch (e) {
    $('#nOnline').textContent = '';
    $('#onlineTable').innerHTML = `<tr><td class="dim">Игра не ответила: ${esc(e.message)}</td></tr>`;
    return;
  }
  $('#nOnline').textContent = '(' + players.length + ')';
  if (!players.length) { $('#onlineTable').innerHTML = '<tr><td class="dim">Никого нет.</td></tr>'; return; }
  $('#onlineTable').innerHTML = '<tr><th>Игрок</th><th>Фракция</th><th>Чем управляет</th><th>Где</th></tr>' +
    players.map((p) => `<tr><td class="name">${esc(p.name)}${p.real ? '' : ' <span class="dim">бот</span>'}</td><td>${esc(p.faction || '—')}</td>
      <td class="name">${p.grid ? `<a href="structures.html#${p.gridId}">${esc(p.grid)}</a>` : p.dead ? '<span class="dim">не возродился</span>' : 'пешком'}</td>
      <td>${p.x == null ? '—' : `<a href="index.html#tracks=player:${p.identity}&sel=player:${p.identity}" title="на карте">${Math.round(p.x)}, ${Math.round(p.y)}, ${Math.round(p.z)}</a>`}</td></tr>`).join('');
}
loadOnline();
setInterval(() => { if (!document.hidden) loadOnline(); }, 10_000);

$('#reload').addEventListener('click', () => { $('#live').checked = false; load(); });
$('#last15').addEventListener('click', () => setSpan(15 * MIN));
$('#lastHour').addEventListener('click', () => setSpan(HOUR));
$('#lastDay').addEventListener('click', () => setSpan(24 * HOUR));
$('#lastWeek').addEventListener('click', () => setSpan(7 * 24 * HOUR));
window.addEventListener('resize', () => draw());
setInterval(() => { if ($('#live').checked) setSpan(state.span); }, 10_000);

(async function start() {
  const clock = await api('now');
  zoneOffset = (clock.offsetMinutes || 0) * 60_000;
  $('#zone').textContent = clock.zone;
  setSpan(HOUR);
})().catch((e) => status('Ошибка: ' + e.message, true));
