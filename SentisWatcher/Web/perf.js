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
  const pts = state.points;
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
  canvas._chart = { series: shown, x, left, pw };
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
  if (!c || !state.points.length) { tip.hidden = true; return; }
  const r = canvas.getBoundingClientRect();
  const mx = ev.clientX - r.left;
  let best = 0, bestD = Infinity;
  for (let i = 0; i < state.points.length; i++) { const d = Math.abs(c.x(i) - mx); if (d < bestD) { bestD = d; best = i; } }
  const p = state.points[best];
  const rows = c.series.map((s) => `<div class="r"><span><span class="sw" style="background:${s.color}"></span>${s.name}</span><b>${num(s.values[best], 3)}${s.unit ? ' ' + s.unit : ''}</b></div>`);
  tip.innerHTML = `<div class="t">${fmt(p.t)}</div>` + rows.join('');
  tip.hidden = false;
  tip.style.left = Math.min(ev.clientX + 14, window.innerWidth - tip.offsetWidth - 8) + 'px';
  tip.style.top = ev.clientY + 14 + 'px';
}
for (const canvas of document.querySelectorAll('canvas')) {
  canvas.addEventListener('mousemove', (ev) => hoverChart(canvas, ev));
  canvas.addEventListener('mouseleave', () => { tip.hidden = true; });
}

function legend(el, series, extra = () => '') {
  el.innerHTML = series.map((s) =>
    `<label class="${state.hidden[s.key] ? '' : 'on'}" data-key="${s.key}"><input type="checkbox"><span class="sw" style="background:${s.color}"></span>${s.name}<span class="d">${extra(s)}</span></label>`).join('');
  el.querySelectorAll('label').forEach((l) => l.addEventListener('click', (e) => {
    e.preventDefault();
    state.hidden[l.dataset.key] = !state.hidden[l.dataset.key];
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

  // the parts of the frame: the physics, the entities before and after it, the rest
  const PARTS = [
    ['physics', 'физика (Havok)', '#f5b041'],
    ['entities_before', 'сущности до физики (гриды, блоки, персонажи)', '#5fb3f9'],
    ['entities_after', 'сущности после физики', '#58d68d'],
    ['other', 'прочее: компоненты сессии, моды, сеть', '#af7ac5'],
  ];
  const blocks = PARTS.map(([n, name, color]) => ({
    key: 'b:' + n, name, color, unit: 'мс',
    values: n === 'physics' ? col('physics') : pts.map((p) => p.blocks ? (p.blocks[n] ? p.blocks[n][0] : 0) : null),
    maxes: n === 'physics' ? col('physicsMax') : pts.map((p) => p.blocks ? (p.blocks[n] ? p.blocks[n][1] : 0) : null),
  }));
  chart($('#cBlocks'), blocks, { leftMax: niceMax(Math.max(2, ...blocks.map((b) => (max(b.values) || 0) * 1.5))) });
  legend($('#lBlocks'), blocks, (s) => ' ' + num(avg(s.values), 3) + ', макс ' + num(max(s.maxes), 1));

  // the collector
  const gc = [
    { key: 'gc0', name: 'сборки поколения 0', color: '#48c9b0', kind: 'bar', values: col('gc0') },
    { key: 'gc1', name: 'поколения 1', color: '#f4d03f', kind: 'bar', values: col('gc1') },
    { key: 'gc2', name: 'поколения 2 (полные)', color: '#ec7063', kind: 'bar', values: col('gc2') },
    { key: 'gcTime', name: 'время в GC', color: '#d7bde2', values: col('gcTime'), axis: 'right', unit: '%' },
  ];
  chart($('#cGc'), gc, { right: true });
  legend($('#lGc'), gc, (s) => s.key === 'gcTime' ? ' ' + num(avg(s.values), 1) + '%, макс ' + num(max(s.values), 1) + '%' : ' всего ' + num(s.values.reduce((a, b) => a + (b || 0), 0), 0));

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

  summary();
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
    ['Время в GC', num(avg(col('gcTime')), 1) + '%', avg(col('gcTime')) > 5, 'полных сборок: ' + num(col('gc2').reduce((a, b) => a + b, 0), 0)],
    ['Память', num(last.working / 1024, 1) + ' ГБ', false, 'куча .NET ' + num(last.managed / 1024, 1) + ' ГБ'],
    ['Игроков онлайн', num(last.players, 0), false, 'макс ' + num(max(col('players')), 0)],
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
  const data = await api('perf', { from: state.from, to: state.to, points: Math.min(3000, Math.max(200, Math.round(width))) });
  state.points = data.points;
  draw();
  loadData = await api('load', { from: state.from, to: state.to, top: 200 });
  drawLoad();
  status(state.points.length ? `${state.points.length} точек, ${fmt(state.from)} — ${fmt(state.to)}` : 'Нет записей за интервал');
}

function setSpan(span) {
  state.span = span;
  const now = Date.now();
  $('#from').value = toInput(now - span);
  $('#to').value = toInput(now);
  load();
}

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
