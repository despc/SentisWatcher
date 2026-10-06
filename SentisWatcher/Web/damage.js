// SentisWatcher: the damage page. Who fights whom over a range - players, bots, animals, NPCs - against which targets,
// with what weapons: the hits, the grinding, the shots fired, the blocks destroyed, the kills. From /api/damage (the
// table damage, summed over 2 s per attacker, weapon and target).

const $ = (s) => document.querySelector(s);
const MIN = 60_000, HOUR = 3600_000;
const KINDS = [
  ['player', 'игроки', '#5fb3f9'], ['bot', 'боты', '#f5b041'], ['animal', 'звери', '#ec7063'], ['npc', 'NPC', '#af7ac5'], ['none', 'без хозяина', '#8b95a5'],
];
const KIND_NAME = Object.fromEntries(KINDS.map(([k, n]) => [k, n]));
const REL_NAME = { enemy: 'враг', neutral: 'нейтрал', ally: 'союзник', own: 'своё', nobody: 'ничьё', unknown: '?' };
const EVENT_NAME = { hit: 'попадание', grind: 'распил', shot: 'выстрелы', destroyed: 'блок разрушен', kill: 'убит' };
const TARGET_NAME = { block: 'блоки', character: 'персонаж', animal: 'зверь' };

const state = { from: 0, to: 0, span: HOUR, data: null, hidden: {}, kind: 'all' };

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
const num = (v, d = 0) => (v === null || v === undefined || Number.isNaN(v)) ? '—' : Number(v).toLocaleString('ru-RU', { maximumFractionDigits: d });
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const kindTag = (k) => `<span class="kind ${esc(k)}">${esc(KIND_NAME[k] || k)}</span>`;

// ------------------------------------------------------------------ the chart: stacked bars by attacker kind, shots as a line

function drawChart() {
  const d = state.data;
  const canvas = $('#cDamage');
  const ratio = window.devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  canvas.width = w * ratio;
  canvas.height = h * ratio;
  const g = canvas.getContext('2d');
  g.scale(ratio, ratio);
  g.clearRect(0, 0, w, h);
  const kinds = KINDS.filter(([k]) => !state.hidden[k]);
  const n = d ? d.shots.length : 0;
  if (!n) return;
  const left = 56, right = 46, top = 8, bottom = 20;
  const pw = w - left - right, ph = h - top - bottom;
  const totals = Array.from({ length: n }, (_, i) => kinds.reduce((s, [k]) => s + (d.series[k]?.[i] || 0), 0));
  const maxD = Math.max(1, ...totals), maxS = Math.max(1, ...d.shots);
  g.font = '11px Consolas, monospace';
  g.fillStyle = '#8b95a5';
  g.strokeStyle = '#2c323c';
  for (let k = 0; k <= 4; k++) {
    const y = top + ph - ph * k / 4;
    g.beginPath(); g.moveTo(left, y); g.lineTo(left + pw, y); g.stroke();
    g.textAlign = 'right'; g.fillText(num(maxD * k / 4), left - 4, y + 4);
    if (!state.hidden.shots) { g.textAlign = 'left'; g.fillText(num(maxS * k / 4), left + pw + 4, y + 4); }
  }
  const bw = pw / n;
  for (let i = 0; i < n; i++) {
    let y = top + ph;
    for (const [k, , color] of kinds) {
      const v = d.series[k]?.[i] || 0;
      if (!v) continue;
      const bh = ph * v / maxD;
      g.fillStyle = color;
      g.fillRect(left + i * bw + 0.5, y - bh, Math.max(1, bw - 1), bh);
      y -= bh;
    }
  }
  if (!state.hidden.shots) {
    g.strokeStyle = '#d8dee9';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = 0; i < n; i++) {
      const x = left + (i + 0.5) * bw, y = top + ph - ph * d.shots[i] / maxS;
      if (i) g.lineTo(x, y); else g.moveTo(x, y);
    }
    g.stroke();
  }
  g.fillStyle = '#8b95a5';
  g.textAlign = 'left';
  g.fillText(fmt(d.from, false), left, h - 5);
  g.textAlign = 'right';
  g.fillText(fmt(d.to, false), left + pw, h - 5);
  canvas._layout = { left, pw, n, width: d.width, from: d.from, totals };
  // the range picked with the mouse, and while the wheel or the pan moved the range ahead of the data, where the
  // data on screen sits in it
  if (selection) {
    const x0 = left + (selection[0] - state.from) / Math.max(1, state.to - state.from) * pw;
    const x1 = left + (selection[1] - state.from) / Math.max(1, state.to - state.from) * pw;
    g.fillStyle = '#5fb3f933';
    g.fillRect(x0, top, x1 - x0, ph);
    g.strokeStyle = '#5fb3f9';
    g.strokeRect(x0 + 0.5, top + 0.5, x1 - x0 - 1, ph - 1);
  }
  if (d.from !== state.from || d.to !== state.to) {
    g.fillStyle = '#8b95a5';
    g.textAlign = 'center';
    g.fillText(`${fmt(state.from)} — ${fmt(state.to)} · загружаю…`, left + pw / 2, top + 12);
  }
}

// ------------------------------------------------------------------ the chart's time axis: zoom, select, pan

const MIN_SPAN = MIN, MAX_SPAN = 31 * 24 * HOUR;
let selection = null;           // [from, to] while a range is picked
let drag = null;                // { mode: 'click' | 'select' | 'pan', x, t, from, to }
const rangeHistory = [];
// the time under a pixel of the chart, by the range asked for (the bars may still show the one before)
function timeAt(clientX) {
  const c = $('#cDamage'), l = c._layout, r = c.getBoundingClientRect();
  const left = l ? l.left : 56, pw = l ? l.pw : r.width - 102;
  return state.from + Math.min(1, Math.max(0, (clientX - r.left - left) / Math.max(1, pw))) * (state.to - state.from);
}
function remember() {
  rangeHistory.push([state.from, state.to]);
  if (rangeHistory.length > 30) rangeHistory.shift();
  $('#rangeBack').hidden = false;
}
const reloadSoon = (() => { let timer; return () => { clearTimeout(timer); timer = setTimeout(() => load(), 350); }; })();
function moveRange(from, to, now = false) {
  const span = Math.min(MAX_SPAN, Math.max(MIN_SPAN, to - from));
  const middle = (from + to) / 2;
  state.from = Math.round(middle - span / 2);
  state.to = Math.round(middle + span / 2);
  state.span = span;
  $('#from').value = toInput(state.from);
  $('#to').value = toInput(state.to);
  $('#live').checked = false;
  hideSelection(false);
  drawChart();
  if (now) load(); else reloadSoon();
}
function hideSelection(redraw = true) {
  selection = null;
  $('#rangeBar').hidden = true;
  if (redraw) drawChart();
}
function showRangeBar() {
  const c = $('#cDamage'), l = c._layout;
  const [a, b] = selection;
  const mid = l.left + ((a + b) / 2 - state.from) / Math.max(1, state.to - state.from) * l.pw;
  const sameDay = fmt(a).slice(0, 10) === fmt(b).slice(0, 10);
  $('#rangeZoom').textContent = `Показать ${fmt(a, !sameDay)} – ${fmt(b, false)} ›`;
  const bar = $('#rangeBar');
  bar.style.left = Math.max(130, Math.min(c.clientWidth - 130, mid)) + 'px';
  bar.hidden = false;
}
let wheeling = false;
$('#cDamage').addEventListener('wheel', (e) => {
  e.preventDefault();
  if (!wheeling) { remember(); wheeling = true; setTimeout(() => { wheeling = false; }, 1500); }
  const at = timeAt(e.clientX);
  const k = e.deltaY > 0 ? 1.3 : 1 / 1.3;
  const span = Math.min(MAX_SPAN, Math.max(MIN_SPAN, (state.to - state.from) * k));
  const f = (at - state.from) / Math.max(1, state.to - state.from);
  moveRange(at - span * f, at - span * f + span);
}, { passive: false });
$('#cDamage').addEventListener('mousedown', (e) => {
  if (e.button === 1) {
    e.preventDefault();             // no auto-scroll
    remember();
    drag = { mode: 'pan', x: e.clientX, from: state.from, to: state.to };
    return;
  }
  if (e.button !== 0) return;
  drag = { mode: 'click', x: e.clientX, t: timeAt(e.clientX) };
});
$('#cDamage').addEventListener('auxclick', (e) => { if (e.button === 1) e.preventDefault(); });
window.addEventListener('mousemove', (e) => {
  if (!drag) return;
  if (drag.mode === 'pan') {
    const pw = $('#cDamage')._layout?.pw || $('#cDamage').clientWidth;
    const shift = -(e.clientX - drag.x) / pw * (drag.to - drag.from);
    moveRange(drag.from + shift, drag.to + shift);
    return;
  }
  if (drag.mode === 'click' && Math.abs(e.clientX - drag.x) > 4) drag.mode = 'select';
  if (drag.mode === 'select') {
    const t = timeAt(e.clientX);
    selection = [Math.min(drag.t, t), Math.max(drag.t, t)];
    $('#rangeBar').hidden = true;
    $('#tip').hidden = true;
    drawChart();
  }
});
window.addEventListener('mouseup', () => {
  const d = drag;
  if (!d) return;
  drag = null;
  if (d.mode === 'click') hideSelection();
  else if (d.mode === 'select' && selection && selection[1] - selection[0] >= 1000) showRangeBar();
  else if (d.mode === 'select') hideSelection();
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

function drawLegend() {
  const d = state.data;
  const sum = (k) => (d?.series[k] || []).reduce((a, b) => a + b, 0);
  const items = KINDS.map(([k, name, color]) => [k, name, color, num(sum(k))])
    .concat([['shots', 'выстрелы', '#d8dee9', num((d?.shots || []).reduce((a, b) => a + b, 0))]]);
  $('#lDamage').innerHTML = items.map(([k, name, color, v]) =>
    `<label class="${state.hidden[k] ? '' : 'on'}" data-k="${k}"><span class="sw" style="background:${color}"></span>${name} <span class="d">${v}</span></label>`).join('');
}
$('#lDamage').addEventListener('click', (e) => {
  const l = e.target.closest('label');
  if (!l) return;
  state.hidden[l.dataset.k] = !state.hidden[l.dataset.k];
  drawChart(); drawLegend();
});
$('#cDamage').addEventListener('mousemove', (e) => {
  const c = e.currentTarget, l = c._layout, tip = $('#tip'), d = state.data;
  if (!l || !d || drag) return;
  const i = Math.floor((e.offsetX - l.left) / l.pw * l.n);
  if (i < 0 || i >= l.n) { tip.hidden = true; return; }
  const t = d.from + i * l.width;
  tip.innerHTML = `<div class="t">${fmt(t)} — ${fmt(t + l.width, false)}</div>` +
    KINDS.filter(([k]) => d.series[k]?.[i]).map(([k, name, color]) => `<div class="r"><span><span class="sw" style="background:${color}"></span>${name}</span><b>${num(d.series[k][i])}</b></div>`).join('') +
    `<div class="r"><span>выстрелов</span><b>${num(d.shots[i])}</b></div>`;
  tip.hidden = false;
  tip.style.left = e.clientX + 14 + 'px';
  tip.style.top = e.clientY + 14 + 'px';
});
$('#cDamage').addEventListener('mouseleave', () => { $('#tip').hidden = true; });

// ------------------------------------------------------------------ the tables

function drawSummary() {
  const d = state.data;
  const all = d.attackers;
  const sum = (key, list = all) => list.reduce((s, a) => s + (a[key] || 0), 0);
  const by = (k) => all.filter((a) => a.kind === k);
  const cards = [
    ['Урон', num(sum('damage')), 'попаданий ' + num(sum('hits'))],
    ['Распил', num(sum('grind')), 'шлифовкой'],
    ['Выстрелов', num(sum('shots')), 'всех видов оружия'],
    ['Убито', num(sum('kills')), 'персонажей и зверей'],
    ['Блоков разрушено', num(sum('destroyed')), ''],
    ['Игроки', num(sum('damage', by('player')) + sum('grind', by('player'))), 'урон и распил'],
    ['Боты', num(sum('damage', by('bot')) + sum('grind', by('bot'))), 'урон и распил'],
    ['Звери', num(sum('damage', by('animal'))), 'урон'],
  ];
  $('#summary').innerHTML = cards.map(([k, v, s]) => `<div class="card"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s}</div></div>`).join('');
}

function drawAttackers() {
  const d = state.data;
  const tabs = [['all', 'все']].concat(KINDS.map(([k, n]) => [k, n]));
  $('#kindFilter').innerHTML = tabs.map(([k, n]) =>
    `<button data-k="${k}" class="${state.kind === k ? 'on' : ''}">${n} <span class="dim">${k === 'all' ? d.attackers.length : d.attackers.filter((a) => a.kind === k).length || ''}</span></button>`).join('');
  const list = d.attackers.filter((a) => state.kind === 'all' || a.kind === state.kind);
  if (!list.length) { $('#attackers').innerHTML = '<tr><td class="dim">Никого за этот интервал.</td></tr>'; return; }
  const top = Math.max(...list.map((a) => a.damage + a.grind), 1e-9);
  $('#attackers').innerHTML = '<tr><th>Кто</th><th class="n">Урон</th><th>доля</th><th class="n">Попаданий</th><th class="n">Распил</th><th class="n">Выстрелов</th>' +
    '<th class="n">Убито</th><th class="n">Блоков</th><th class="n">Целей</th><th>Оружие</th><th>Последний раз</th></tr>' +
    list.map((a) => `<tr><td class="name">${kindTag(a.kind)}${a.kind === 'animal' ? '' : esc(a.name) || '<span class="dim">—</span>'}</td>` +
      `<td class="n">${num(a.damage)}</td><td class="share"><i style="width:${((a.damage + a.grind) / top * 100).toFixed(1)}%"></i><span></span></td>` +
      `<td class="n">${num(a.hits)}</td><td class="n">${num(a.grind)}</td><td class="n">${num(a.shots)}</td><td class="n">${num(a.kills)}</td>` +
      `<td class="n">${num(a.destroyed)}</td><td class="n">${num(a.targets)}</td><td class="weapons">${a.weapons.map(esc).join(', ')}</td><td>${fmt(a.last, false)}</td></tr>`).join('');
}
$('#kindFilter').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  state.kind = b.dataset.k;
  drawAttackers();
});

function drawVictims() {
  const list = state.data.victims;
  if (!list.length) { $('#victims').innerHTML = '<tr><td class="dim">Никому за этот интервал.</td></tr>'; return; }
  $('#victims').innerHTML = '<tr><th>Кому</th><th class="n">Урон</th><th class="n">Попаданий</th><th class="n">Убито</th><th class="n">Блоков разрушено</th><th>Гриды</th><th>Кто бил</th></tr>' +
    list.map((v) => `<tr><td class="name">${esc(v.name)} <span class="dim">${esc(TARGET_NAME[v.kind] || v.kind)}</span></td><td class="n">${num(v.damage)}</td>` +
      `<td class="n">${num(v.hits)}</td><td class="n">${num(v.kills)}</td><td class="n">${num(v.destroyed)}</td>` +
      `<td class="weapons">${v.grids.map(esc).join(', ')}</td><td class="weapons">${v.by.map(esc).join(', ')}</td></tr>`).join('');
}

// the map around the moment, the attacker's track on it when it is a player or a bot
function mapLink(r) {
  const who = (r.attackerKind === 'player' || r.attackerKind === 'bot') && r.attackerId ? `&tracks=player:${r.attackerId}&sel=player:${r.attackerId}` : '';
  return `index.html#from=${r.t - 10 * MIN}&to=${r.t + 10 * MIN}&t=${r.t}${who}`;
}

function drawRecent() {
  const list = state.data.recent;
  if (!list.length) { $('#recent').innerHTML = '<tr><td class="dim">Ничего за этот интервал.</td></tr>'; return; }
  $('#recent').innerHTML = '<tr><th>Время</th><th>Что</th><th>Кто</th><th>Чем</th><th>По кому / чему</th><th>Отношение</th><th class="n">Урон</th><th class="n">Раз</th><th>Где</th></tr>' +
    list.map((r) => `<tr><td>${fmt(r.t, false)}</td><td>${esc(EVENT_NAME[r.kind] || r.kind)}</td>` +
      `<td class="name">${kindTag(r.attackerKind)}${esc(r.attacker)}${r.from ? ` <span class="dim">(${esc(r.from)})</span>` : ''}</td>` +
      `<td class="weapons">${esc(r.weapon)}</td>` +
      `<td class="name">${r.kind === 'shot' ? '<span class="dim">—</span>' : esc(r.target) + (r.victim && r.targetKind === 'block' ? ` <span class="dim">${esc(r.victim)}</span>` : '')}</td>` +
      `<td>${r.kind === 'shot' ? '' : `<span class="rel ${esc(r.relation)}">${esc(REL_NAME[r.relation] || r.relation)}</span>`}</td>` +
      `<td class="n">${r.kind === 'shot' || r.kind === 'kill' || r.kind === 'destroyed' ? '' : num(r.amount)}</td><td class="n">${num(r.count)}</td>` +
      `<td>${r.x === 0 && r.y === 0 && r.z === 0 ? '' : `<a href="${mapLink(r)}" title="на карте, в этот момент">${num(r.x)}, ${num(r.y)}, ${num(r.z)}</a>`}</td></tr>`).join('');
}

function draw() {
  if (!state.data) return;
  drawSummary();
  drawChart();
  drawLegend();
  drawAttackers();
  drawVictims();
  drawRecent();
}

// ------------------------------------------------------------------ loading

async function load() {
  state.from = fromInput($('#from').value);
  state.to = fromInput($('#to').value);
  status('Загружаю…');
  const buckets = Math.min(400, Math.max(40, Math.round(($('#cDamage').clientWidth || 1200) / 6)));
  const [from, to] = [state.from, state.to];
  const attacker = $('#attacker').value, victim = $('#victim').value;
  const data = await api('damage', { from, to, relation: $('#relation').value, attacker, victim, buckets, recent: 300 });
  // the range or the choice moved on meanwhile: the next answer is the one
  if (from !== state.from || to !== state.to || attacker !== $('#attacker').value || victim !== $('#victim').value) return;
  state.data = data;
  fillPlayers($('#attacker'), 'всех', data.sources || [], attacker);
  fillPlayers($('#victim'), 'всем', data.targets || [], victim);
  draw();
  status(`${num(state.data.rows)} записей, ${fmt(state.from)} — ${fmt(state.to)}`);
}

// the players to choose from (the sources: everyone who dealt something over the range, the targets: everyone who took
// something); the chosen one stays in the list even when the range has nothing of theirs
function fillPlayers(select, all, players, chosen) {
  const known = select.querySelector(`option[value="${CSS.escape(chosen)}"]`)?.textContent;
  const options = players.map((p) => [String(p.id), p.name + (p.kind === 'bot' ? ' (бот)' : '')]);
  if (chosen !== '0' && !options.some(([id]) => id === chosen)) options.unshift([chosen, known || chosen]);
  select.innerHTML = `<option value="0">${all}</option>` + options.map(([id, name]) => `<option value="${esc(id)}">${esc(name)}</option>`).join('');
  select.value = chosen;
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

$('#reload').addEventListener('click', () => { $('#live').checked = false; load(); });
$('#relation').addEventListener('change', () => load());
$('#attacker').addEventListener('change', () => load());
$('#victim').addEventListener('change', () => load());
$('#last15').addEventListener('click', () => setSpan(15 * MIN));
$('#lastHour').addEventListener('click', () => setSpan(HOUR));
$('#lastDay').addEventListener('click', () => setSpan(24 * HOUR));
$('#lastWeek').addEventListener('click', () => setSpan(7 * 24 * HOUR));
window.addEventListener('resize', () => drawChart());
setInterval(() => { if ($('#live').checked && !document.hidden) setSpan(state.span); }, 10_000);

(async function start() {
  const clock = await api('now');
  zoneOffset = (clock.offsetMinutes || 0) * 60_000;
  $('#zone').textContent = clock.zone;
  setSpan(HOUR);
})().catch((e) => status('Ошибка: ' + e.message, true));
