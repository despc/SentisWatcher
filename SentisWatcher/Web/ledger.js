// SentisWatcher: the inventory ledger page. What a player's or a grid's inventories held over time, where every
// change came from, and the anomalies the ledger found.

const $ = (s) => document.querySelector(s);
const HOUR = 3600_000;

const state = {
  from: 0, to: 0,
  kind: null, id: null,          // the subject
  data: null,                    // /api/ledger
  anomalies: [], names: {},      // /api/anomalies
  shown: new Set(),              // items on the chart
  hover: null,                   // time under the mouse on the chart
  cursor: null,                  // time clicked on the chart
  selAlert: null,
};

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

const pad = (n) => String(n).padStart(2, '0');
let zoneOffset = 0;
function fmt(t, withDate = true) {
  const d = new Date(t + zoneOffset);
  const time = `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
  return withDate ? `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${time}` : time;
}
const toInput = (t) => fmt(t).replace(' ', 'T');
const fromInput = (v) => Date.parse(v + 'Z') - zoneOffset;
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const num = (v, d = 2) => (v === null || v === undefined) ? '' : Number(v).toLocaleString('ru-RU', { maximumFractionDigits: d });
// an amount readable at any size: two decimals from 1 up, three significant digits below (0,00673, not 0)
const amt = (v) => {
  if (!v) return '0';
  const a = Math.abs(v);
  return a >= 1 ? num(v, a >= 10_000 ? 0 : 2) : Number(v).toLocaleString('ru-RU', { maximumSignificantDigits: 3 });
};
const signed = (v) => (v > 0 ? '+' : '') + amt(v);
// the decimals two amounts need for their difference to show: 299,58 and 299,58 are 299,5867 and 299,5799
const decimalsFor = (difference) => {
  const a = Math.abs(difference);
  return !a || a >= 0.01 ? 2 : Math.min(6, Math.ceil(-Math.log10(a)) + 1);
};
const debounce = (f, ms) => { let h; return (...a) => { clearTimeout(h); h = setTimeout(() => f(...a), ms); }; };
const itemName = (item) => item.split('/').slice(1).join('/') + (item.startsWith('Ore/') ? ' (руда)' : item.startsWith('Ingot/') ? ' (слиток)' : '');

// ------------------------------------------------------------------ what the words mean

const ALERTS = {
  bypass: ['Изменение в обход учёта', 3],
  dupe_transfer: ['Перенос создал предметы', 3],
  production_without_input: ['Производство без входа', 3],
  pickup_excess: ['Подобрано больше, чем лежало', 3],
  grind_excess: ['Резак выдал больше, чем в блоке', 3],
  bad_amount: ['Недопустимое количество', 3],
  overfilled: ['Переполнен объём', 2],
  tank_overfilled: ['Бак полнее полного', 2],
  external_source: ['Выдано плагином или модом', 2],
  unknown_source: ['Неизвестный источник', 2],
  paste_by_player: ['Вставка игроком', 2],
};
const alertLabel = (kind) => (ALERTS[kind] || [kind])[0];
const severity = (kind) => (ALERTS[kind] || [0, 1])[1];
const SEV_COLOR = { 3: '#ec7063', 2: '#f5b041', 1: '#aeb6bf' };

const KINDS = {
  refine: 'Переработка', assemble: 'Сборка', disassemble: 'Разборка', drill: 'Бур', grind: 'Резак', weld: 'Сварка',
  build: 'Стройка', pickup: 'Подбор', consume: 'Расход', drop: 'Выброшено', throw: 'Выброс коннектором', move: 'Внутри грида',
  store: 'Магазин', contract: 'Контракт', trade: 'Обмен', npc: 'NPC', reactor: 'Реактор', bag: 'Контейнер после смерти или разрушения',
  respawn: 'Возрождение', prefab: 'Префаб', paste: 'Вставка', split: 'Разделение или слияние гридов', seen: 'Первый снимок',
  ownership: 'Смена владельца', regrid: 'Переход на другой грид', gone: 'Исчезло вместе с блоком', other: 'Прочий расход',
  unexplained: 'Необъяснено', script: 'Скрипт сценария', admin: 'Админ', clear: 'Очистка', save: 'Сохранение', async: 'Фоновая загрузка',
  untraced: 'Не определено (слишком часто, чтобы читать стек)',
  untracked: 'До включения учёта',
};
function kindLabel(kind, names = {}) {
  if (kind.startsWith('transfer@')) {
    const id = kind.slice(9);
    return 'Перенос: ' + (names[id] ? `«${names[id]}»` : 'грид ' + id);
  }
  if (kind.startsWith('load:')) return 'Появилось (' + kindLabel(kind.slice(5), names) + ')';
  if (kind.startsWith('plugin:')) return 'Плагин ' + kind.slice(7);
  if (kind.startsWith('mod:')) return 'Мод ' + kind.slice(4);
  if (kind.startsWith('unknown:')) return 'Неизвестно (' + kind.slice(8) + ')';
  return KINDS[kind] || kind;
}
// how a source looks: bad - nothing honest does it, warn - from outside the game's rules
function kindClass(kind) {
  const k = kind.startsWith('load:') ? kind.slice(5) : kind;
  if (k === 'unexplained') return 'bad';
  if (k === 'untracked') return 'old';
  if (k.startsWith('unknown:') || k.startsWith('plugin:') || k.startsWith('mod:') || k === 'script' || k === 'admin' || k === 'paste') return 'warn';
  return '';
}

// ------------------------------------------------------------------ range and subject

function readRange() {
  state.from = fromInput($('#from').value);
  state.to = fromInput($('#to').value);
}
function setRange(from, to, remember = true) {
  if (remember && state.from && (from !== state.from || to !== state.to)) {
    rangeHistory.push([state.from, state.to]);
    $('#rangeBack').hidden = false;
  }
  state.from = Math.round(from); state.to = Math.round(to);
  $('#from').value = toInput(state.from);
  $('#to').value = toInput(state.to);
  saveHash();
  loadAll();
}
const rangeHistory = [];
$('#rangeBack').addEventListener('click', () => {
  const back = rangeHistory.pop();
  if (back) setRange(back[0], back[1], false);
  $('#rangeBack').hidden = !rangeHistory.length;
});

function saveHash() {
  const h = new URLSearchParams();
  h.set('from', state.from); h.set('to', state.to);
  if (state.id) { h.set('kind', state.kind); h.set('id', state.id); }
  history.replaceState(null, '', '#' + h.toString());
  const map = new URLSearchParams();
  map.set('from', state.from); map.set('to', state.to);
  if (state.id && state.kind !== 'entity') { map.set('tracks', state.kind + ':' + state.id); map.set('sel', state.kind + ':' + state.id); }
  $('#toMap').href = 'index.html#' + map.toString();
}

async function loadAll() {
  await Promise.all([loadAnomalies(), state.id ? loadLedger() : Promise.resolve()]);
}

async function choose(kind, id, t) {
  state.kind = kind; state.id = String(id);
  state.cursor = t ?? state.cursor;
  holdings = null;
  state.shown.clear();
  saveHash();
  await loadLedger();
  if (t) showChangesNear(t);
}

// ------------------------------------------------------------------ anomalies

async function loadAnomalies() {
  const data = await api('anomalies', { from: state.from, to: state.to });
  state.anomalies = data.alerts;
  Object.assign(state.names, data.names);
  const kinds = [...new Set(state.anomalies.map((a) => a.kind))].sort((a, b) => severity(b) - severity(a));
  const select = $('#anomalyKind');
  const was = select.value;
  select.innerHTML = '<option value="">все виды</option>' +
    kinds.map((k) => `<option value="${k}">${esc(alertLabel(k))} (${state.anomalies.filter((a) => a.kind === k).length})</option>`).join('');
  select.value = kinds.includes(was) ? was : '';
  renderAnomalies();
  drawStrip();
}

function subjectAlerts() {
  const d = state.data;
  if (!d) return [];
  return d.alerts;
}

function filteredAnomalies() {
  const kind = $('#anomalyKind').value;
  let list = $('#onlySubject').checked && state.data ? subjectAlerts() : state.anomalies;
  if (kind) list = list.filter((a) => a.kind === kind);
  return list;
}

function renderAnomalies() {
  const list = filteredAnomalies();
  $('#stripCount').textContent = state.anomalies.length ? `(${state.anomalies.length})` : '— нет';
  if (!list.length) {
    $('#anomalies').innerHTML = `<div class="more">${state.anomalies.length ? 'Нет аномалий под фильтр.' : 'За интервал аномалий нет.'}</div>`;
    return;
  }
  const names = { ...state.names, ...(state.data?.names || {}) };
  $('#anomalies').innerHTML = [...list].reverse().slice(0, 500).map((a, i) => {
    const who = a.actor && a.actor !== '0' ? (names[a.actor] || a.actor) : '';
    const sel = state.selAlert && state.selAlert.t === a.t && state.selAlert.kind === a.kind && state.selAlert.entity === a.entity;
    return `<div class="item${sel ? ' sel' : ''}" data-i="${i}">
      <span class="when">${fmt(a.t)}</span><span class="kind sev${severity(a.kind)}">${esc(alertLabel(a.kind))}</span>
      ${who ? `<span class="who">${esc(who)}</span>` : ''}
      <div class="detail">${esc(a.detail)}</div>
      <div class="actions">
        ${who ? `<button data-act="player" title="Инвентари этого игрока за интервал">Инвентари игрока</button>` : ''}
        ${a.entity && a.entity !== '0' ? `<button data-act="entity" title="Инвентарь этого блока">Этот инвентарь</button>` : ''}
        <a class="map" href="${mapLink(a)}" title="Открыть этот момент на 3D-карте">на карте</a>
      </div></div>`;
  }).join('') + (list.length > 500 ? `<div class="more">и ещё ${list.length - 500} — сузьте интервал или фильтр</div>` : '');
  const shown = [...list].reverse();
  for (const el of $('#anomalies').querySelectorAll('.item')) {
    const a = shown[Number(el.dataset.i)];
    el.addEventListener('click', (e) => {
      state.selAlert = a;
      const act = e.target.dataset?.act;
      if (act === 'player') choose('player', a.actor, a.t);
      else if (act === 'entity') choose('entity', a.entity, a.t);
      else if (!e.target.closest('a')) { state.cursor = a.t; drawChart(); drawStrip(); showChangesNear(a.t); renderAnomalies(); }
    });
  }
}

function mapLink(a) {
  const h = new URLSearchParams();
  h.set('from', Math.max(state.from, a.t - HOUR / 2)); h.set('to', Math.min(state.to, a.t + HOUR / 2)); h.set('t', a.t);
  if (a.actor && a.actor !== '0') { h.set('tracks', 'player:' + a.actor); h.set('sel', 'player:' + a.actor); }
  return 'index.html#' + h.toString();
}

// ------------------------------------------------------------------ the strip of anomalies over the range

function fitCanvas(canvas) {
  const dpr = window.devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
    canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr);
  }
  const g = canvas.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  return { g, w, h };
}

const PAD_L = 64, PAD_R = 14;
const xOf = (t, w) => PAD_L + (t - state.from) / Math.max(1, state.to - state.from) * (w - PAD_L - PAD_R);
const tOf = (x, w) => state.from + (x - PAD_L) / (w - PAD_L - PAD_R) * (state.to - state.from);

function timeTicks(w) {
  const span = state.to - state.from;
  const steps = [60e3, 300e3, 900e3, 1800e3, HOUR, 3 * HOUR, 6 * HOUR, 12 * HOUR, 24 * HOUR];
  const step = steps.find((s) => span / s <= (w - PAD_L) / 90) || 24 * HOUR;
  const ticks = [];
  for (let t = Math.ceil((state.from + zoneOffset) / step) * step - zoneOffset; t <= state.to; t += step) ticks.push(t);
  return { ticks, withDate: step >= 24 * HOUR || span > 24 * HOUR };
}

function drawStrip() {
  const { g, w, h } = fitCanvas($('#strip'));
  g.clearRect(0, 0, w, h);
  g.font = '10px Consolas, monospace';
  const { ticks, withDate } = timeTicks(w);
  g.fillStyle = '#56606e';
  for (const t of ticks) {
    const x = xOf(t, w);
    g.fillRect(x, h - 12, 1, 4);
    const label = withDate ? fmt(t).slice(5, 16) : fmt(t, false).slice(0, 5);
    g.fillText(label, x + 2, h - 2);
  }
  g.fillStyle = '#8b95a5';
  g.fillText('все', 6, 16);
  if (state.data) g.fillText('выбранный', 6, 30);
  const mine = new Set(subjectAlerts().map((a) => a.t + a.kind + a.entity));
  for (const a of state.anomalies) {
    const x = xOf(a.t, w);
    g.fillStyle = SEV_COLOR[severity(a.kind)];
    g.fillRect(x - 1, 6, 3, 14);
    if (mine.has(a.t + a.kind + a.entity)) g.fillRect(x - 1, 22, 3, 12);
  }
  drawSelection(g, w, 0, h - 12);
  drawCursor(g, w, 0, h - 12);
}

// a click on the strip: the anomaly under the mouse, if there is one
function stripClick(x, w) {
  let best = null, bestD = 8;
  for (const a of state.anomalies) {
    const d = Math.abs(xOf(a.t, w) - x);
    if (d < bestD) { best = a; bestD = d; }
  }
  if (!best) { setCursor(tOf(x, w)); return; }
  state.selAlert = best;
  setCursor(best.t);
  $('#anomalyKind').value = '';
  showTab('anomalies');
  renderAnomalies();
  const el = $('#anomalies .item.sel');
  if (el) el.scrollIntoView({ block: 'center' });
}

// the selected range and the moment, on either canvas
function drawSelection(g, w, top, bottom) {
  if (!selection) return;
  const a = Math.max(PAD_L, xOf(selection[0], w)), b = Math.min(w - PAD_R, xOf(selection[1], w));
  g.fillStyle = 'rgba(95,179,249,0.18)';
  g.fillRect(a, top, b - a, bottom - top);
  g.fillStyle = '#5fb3f9';
  g.fillRect(a, top, 1, bottom - top);
  g.fillRect(b - 1, top, 1, bottom - top);
}
function drawCursor(g, w, top, bottom) {
  if (!state.cursor || state.cursor < state.from || state.cursor > state.to) return;
  const x = xOf(state.cursor, w);
  g.fillStyle = '#5fb3f9';
  g.fillRect(x - 1, top, 2, bottom - top);
  g.beginPath();
  g.moveTo(x - 5, top); g.lineTo(x + 5, top); g.lineTo(x, top + 7);
  g.fill();
}

// ------------------------------------------------------------------ the subject's ledger

async function loadLedger() {
  status('Загрузка…');
  const d = await api('ledger', { kind: state.kind, id: state.id, from: state.from, to: state.to });
  if (d.id !== state.id) return;
  state.data = d;
  Object.assign(state.names, d.names);
  if (!state.shown.size) topItems();
  if (!state.cursor || state.cursor < state.from || state.cursor > state.to) state.cursor = Math.min(state.to, Date.now());
  renderSubject();
  renderLegend();
  renderSources();
  renderChanges();
  renderAnomalies();
  drawChart(); drawStrip();
  loadHoldings();
  status('');
}

// the items that changed most (by how much of what they started or ended with), at most 8
function ranked() {
  const d = state.data;
  const items = Object.keys(d.series);
  const change = (i) => Math.abs((d.end[i] || 0) - (d.start[i] || 0));
  const flow = (i) => Object.values(d.sources[i] || {}).reduce((s, v) => s + Math.abs(v), 0);
  const bad = (i) => d.unexplained[i] || (d.sources[i] && Object.keys(d.sources[i]).some((k) => kindClass(k) === 'bad')) ? 1 : 0;
  return items.sort((a, b) => bad(b) - bad(a) || change(b) - change(a) || flow(b) - flow(a) || (d.end[b] || 0) - (d.end[a] || 0));
}
function topItems() {
  state.shown = new Set(ranked().slice(0, 8));
}

function renderSubject() {
  const d = state.data;
  const kindName = { player: 'Игрок', grid: 'Грид', entity: 'Инвентарь' }[d.kind];
  // an inventory nothing names: a block's by its number, a character's as a body
  const name = d.name || (d.kind === 'entity' ? (d.grids[d.id] ? `блок ${d.id}` : `тело ${d.id}`) : d.id);
  const bad = subjectAlerts().filter((a) => severity(a.kind) === 3).length;
  const up = d.kind === 'entity' && d.grids[d.id] ? `<a data-grid="${d.grids[d.id]}">грид «${esc(d.names[d.grids[d.id]] || d.grids[d.id])}»</a>` : '';
  $('#subject').innerHTML = `<h2>${kindName}: ${esc(name)}</h2>
    <div class="meta">${d.inventories} инвентарей · ${Object.keys(d.series).length} видов предметов · ${d.changes.length} изменений ·
      аномалий: ${d.alerts.length}${up}</div>
    ${bad ? `<div class="warn">Серьёзных аномалий: ${bad} — отмечены красным на графике и в списке справа.</div>` : ''}
    ${Object.keys(d.unexplained).length ? `<div class="warn">Есть изменения, не сходящиеся с учётом: ${esc(Object.keys(d.unexplained).map(itemName).join(', '))}.</div>` : ''}`;
  const g = $('#subject a[data-grid]');
  if (g) g.addEventListener('click', () => choose('grid', g.dataset.grid));
  $('#chartWrap').hidden = false;
  $('#sourcesWrap').hidden = false;
}

const PALETTE = ['#5fb3f9', '#58d68d', '#f5b041', '#af7ac5', '#ec7063', '#48c9b0', '#f4d03f', '#eb984e', '#85c1e9', '#d98880', '#7dcea0', '#bb8fce'];
const colorOf = (item) => PALETTE[Math.abs([...item].reduce((h, c) => (h * 31 + c.charCodeAt(0)) | 0, 7)) % PALETTE.length];

function renderLegend() {
  const d = state.data;
  const q = $('#itemFilter').value.trim().toLowerCase();
  const items = ranked().filter((i) => !q || i.toLowerCase().includes(q));
  $('#legend').innerHTML = items.map((i) => {
    const delta = (d.end[i] || 0) - (d.start[i] || 0);
    return `<label class="${state.shown.has(i) ? 'on' : ''}" title="${esc(i)}: было ${num(d.start[i] || 0, decimalsFor((d.end[i] || 0) - (d.start[i] || 0)))}, стало ${num(d.end[i] || 0, decimalsFor((d.end[i] || 0) - (d.start[i] || 0)))}">
      <input type="checkbox" data-item="${esc(i)}"${state.shown.has(i) ? ' checked' : ''}>
      <span class="sw" style="background:${state.shown.has(i) ? colorOf(i) : 'transparent'};border:1px solid ${colorOf(i)}"></span>
      ${esc(itemName(i))} <span class="d ${delta > 0 ? 'plus' : delta < 0 ? 'minus' : ''}">${delta ? signed(delta) : ''}</span></label>`;
  }).join('');
  for (const label of $('#legend').querySelectorAll('label')) {
    label.addEventListener('click', (e) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      // Ctrl+click: only this item shown; again on the only one shown - all the listed items back
      e.preventDefault();
      const item = label.querySelector('input').dataset.item;
      const alone = state.shown.size === 1 && state.shown.has(item);
      state.shown.clear();
      if (alone) items.forEach((i) => state.shown.add(i)); else state.shown.add(item);
      renderLegend(); drawChart();
    });
  }
  for (const box of $('#legend').querySelectorAll('input')) {
    box.addEventListener('change', () => {
      if (box.checked) state.shown.add(box.dataset.item); else state.shown.delete(box.dataset.item);
      renderLegend(); drawChart();
    });
  }
}

function renderSources() {
  const d = state.data;
  const names = { ...state.names, ...d.names };
  const rows = ranked().filter((i) => (d.start[i] || 0) !== (d.end[i] || 0) || d.sources[i] || d.unexplained[i]);
  if (!rows.length) { $('#sources').innerHTML = '<tr><td class="dim">За интервал ничего не менялось.</td></tr>'; return; }
  $('#sources').innerHTML = '<tr><th>Предмет</th><th>Было</th><th>Стало</th><th>Изменение</th><th>Из чего сложилось</th></tr>' +
    rows.map((i) => {
      const a = d.start[i] || 0, b = d.end[i] || 0;
      const chips = Object.entries(d.sources[i] || {}).sort((x, y) => Math.abs(y[1]) - Math.abs(x[1]))
        .map(([k, v]) => `<span class="chip ${kindClass(k)}" title="${esc(k)}">${esc(kindLabel(k, names))} <b class="${v > 0 ? 'plus' : 'minus'}">${signed(v)}</b></span>`);
      if (d.unexplained[i]) chips.unshift(`<span class="chip bad" title="Изменение, для которого нет записей учёта (например, часть истории до записи или потерянная запись)">Не сходится <b>${signed(d.unexplained[i])}</b></span>`);
      const bad = d.unexplained[i] || Object.keys(d.sources[i] || {}).some((k) => kindClass(k) === 'bad');
      return `<tr class="${bad ? 'bad' : ''}"><td>${esc(itemName(i))}</td><td class="n">${num(a, decimalsFor(b - a))}</td><td class="n">${num(b, decimalsFor(b - a))}</td>
        <td class="n ${b > a ? 'plus' : b < a ? 'minus' : ''}">${b !== a ? signed(b - a) : ''}</td><td>${chips.join('')}</td></tr>`;
    }).join('');
}

// An inventory as a person reads it: the block's name and which of its inventories, counted from 1
// ("Survival Kit[2]"); a character's own inventory as the player's suit - the body it has now, or one it
// left behind (after a respawn), or just a body when that is not known.
function invLabel(entity, inv, names, body, owner) {
  // a body nothing names goes by its player's name; a block goes by its name, or its type/subtype when it
  // has none - only a block of an old record whose grid is long gone is left with its number
  const name = names[entity] || (body && owner ? names[owner] : null);
  let who;
  if (body === 'current') who = `Скафандр «${name || '?'}»`;
  else if (body === 'old') who = `Брошенное тело «${name || '?'}»`;
  else if (body === 'body') who = `Тело «${name || '?'}»`;
  else who = name || `блок ${entity}`;
  return `${who}[${(Number(inv) || 0) + 1}]`;
}

function renderChanges(near = null) {
  const d = state.data;
  if (!d) return;
  const names = { ...state.names, ...d.names };
  let list = d.changes;
  if (near) {
    // the 40 closest to the moment, newest first
    list = [...list].sort((a, b) => Math.abs(a.t - near) - Math.abs(b.t - near)).slice(0, 40).sort((a, b) => b.t - a.t);
  }
  if (!list.length) { $('#changes').innerHTML = '<div class="more">Изменений нет.</div>'; return; }
  $('#changes').innerHTML = (near ? `<div class="head">Рядом с ${fmt(near)} · <a id="allChanges" href="#">все</a></div>` : '') +
    list.slice(0, 600).map((c) => {
      const grid = d.grids[c.entity];
      const where = invLabel(c.entity, c.inv, names, grid ? null : 'body') + (grid && grid !== c.entity ? ' · ' + (names[grid] || grid) : '');
      const delta = Object.entries(c.delta).map(([i, v]) => `<span class="d ${v > 0 ? 'plus' : 'minus'}">${esc(itemName(i))} ${signed(v)}</span>`).join(', ');
      const flows = c.flows.sort((x, y) => Math.abs(y[2]) - Math.abs(x[2])).slice(0, 12)
        .map(([k, i, v]) => `<span class="chip ${kindClass(k)}">${esc(kindLabel(k, names))}: ${esc(itemName(i))} <b class="${v > 0 ? 'plus' : 'minus'}">${signed(v)}</b></span>`).join('');
      return `<div class="item change" data-t="${c.t}" data-entity="${c.entity}">
        <span class="when">${fmt(c.t)}</span> <span class="who">${esc(where)}</span>
        <div>${delta || '<span class="dim">без изменения итога</span>'}</div><div class="flows">${flows}</div></div>`;
    }).join('');
  const all = $('#allChanges');
  if (all) all.addEventListener('click', (e) => { e.preventDefault(); renderChanges(); });
  for (const el of $('#changes').querySelectorAll('.item')) {
    el.addEventListener('click', () => setCursor(Number(el.dataset.t)));
    el.addEventListener('dblclick', () => choose('entity', el.dataset.entity, Number(el.dataset.t)));
    el.title = 'Клик — отметить момент на графике; двойной клик — только этот инвентарь';
  }
}

function showChangesNear(t) {
  showTab('changes');
  renderChanges(t);
}

// ------------------------------------------------------------------ the chart

// amounts at time t: each point holds the totals after a change, so the value holds until the next point
function valueAt(series, times, t) {
  let lo = 0, hi = times.length - 1, idx = 0;
  while (lo <= hi) { const mid = (lo + hi) >> 1; if (times[mid] <= t) { idx = mid; lo = mid + 1; } else hi = mid - 1; }
  return series[idx];
}

function drawChart() {
  const canvas = $('#chart');
  if (!state.data || $('#chartWrap').hidden) return;
  const { g, w, h } = fitCanvas(canvas);
  const d = state.data;
  const top = 10, bottom = h - 22;
  g.clearRect(0, 0, w, h);
  const items = [...state.shown].filter((i) => d.series[i]);
  let max = 1;
  for (const i of items) for (const v of d.series[i]) max = Math.max(max, v);
  const log = $('#logScale').checked;
  const yOf = (v) => log ? bottom - Math.log10(1 + Math.max(0, v)) / Math.log10(1 + max) * (bottom - top) : bottom - v / max * (bottom - top);

  // grid and scale
  g.font = '10px Consolas, monospace';
  g.strokeStyle = '#262c35'; g.fillStyle = '#6b7584';
  const levels = log ? [...Array(Math.ceil(Math.log10(max + 1)) + 1).keys()].map((p) => 10 ** p).filter((v) => v <= max * 1.01) : [0, 0.25, 0.5, 0.75, 1].map((f) => f * max);
  for (const v of levels) {
    const y = yOf(v);
    g.beginPath(); g.moveTo(PAD_L, y); g.lineTo(w - PAD_R, y); g.stroke();
    g.fillText(num(v, v < 10 ? 2 : 0), 4, y + 3);
  }
  const { ticks, withDate } = timeTicks(w);
  for (const t of ticks) {
    const x = xOf(t, w);
    g.beginPath(); g.moveTo(x, top); g.lineTo(x, bottom); g.stroke();
    g.fillText(withDate ? fmt(t).slice(5, 16) : fmt(t, false).slice(0, 5), x + 2, h - 6);
  }

  // what lies outside the range (while a zoom loads) stays out of the frame
  g.save();
  g.beginPath();
  g.rect(PAD_L, 0, w - PAD_L - PAD_R, h);
  g.clip();

  // anomalies of the subject
  for (const a of subjectAlerts()) {
    const x = xOf(a.t, w);
    g.fillStyle = SEV_COLOR[severity(a.kind)] + '55';
    g.fillRect(x - 1, top, 3, bottom - top);
    g.fillStyle = SEV_COLOR[severity(a.kind)];
    g.beginPath(); g.moveTo(x - 5, top); g.lineTo(x + 5, top); g.lineTo(x, top + 7); g.fill();
  }

  // the items: steps, a value holds until the next change
  for (const i of items) {
    const s = d.series[i];
    g.strokeStyle = colorOf(i); g.lineWidth = 1.6;
    g.beginPath();
    for (let k = 0; k < s.length; k++) {
      const x = xOf(d.times[k], w), y = yOf(s[k]);
      if (k === 0) g.moveTo(x, y);
      else { g.lineTo(x, yOf(s[k - 1])); g.lineTo(x, y); }
    }
    g.lineTo(xOf(state.to, w), yOf(s[s.length - 1]));
    g.stroke();
  }
  g.lineWidth = 1;

  drawSelection(g, w, top, bottom);
  if (state.hover) {
    g.strokeStyle = '#ffffff66';
    const x = xOf(state.hover, w);
    g.beginPath(); g.moveTo(x, top); g.lineTo(x, bottom); g.stroke();
  }
  drawCursor(g, w, 0, bottom);
  g.restore();
}

const tip = $('#tip');
$('#chart').addEventListener('mousemove', (e) => {
  if (!state.data) return;
  const r = e.target.getBoundingClientRect();
  const t = tOf(e.clientX - r.left, r.width);
  if (t < state.from || t > state.to) { tip.hidden = true; state.hover = null; drawChart(); return; }
  state.hover = t;
  drawChart();
  const d = state.data;
  const rows = [...state.shown].filter((i) => d.series[i]).map((i) => [i, valueAt(d.series[i], d.times, t)]).sort((a, b) => b[1] - a[1]);
  const near = subjectAlerts().filter((a) => Math.abs(xOf(a.t, r.width) - (e.clientX - r.left)) < 6);
  tip.innerHTML = `<b>${fmt(t)}</b><br>` + rows.map(([i, v]) => `<span style="color:${colorOf(i)}">■</span> ${esc(itemName(i))}: ${amt(v)}`).join('<br>') +
    near.map((a) => `<br><span style="color:${SEV_COLOR[severity(a.kind)]}">▲ ${esc(alertLabel(a.kind))}</span>: ${esc(a.detail)}`).join('');
  tip.hidden = false;
  tip.style.left = Math.min(e.clientX + 14, innerWidth - tip.offsetWidth - 8) + 'px';
  tip.style.top = Math.min(e.clientY + 14, innerHeight - tip.offsetHeight - 8) + 'px';
});
$('#chart').addEventListener('mouseleave', () => { tip.hidden = true; state.hover = null; drawChart(); });

// a click on the chart: the moment (an anomaly's, when one is under the mouse) and the changes near it
function chartClick(x, w) {
  const near = subjectAlerts().find((a) => Math.abs(xOf(a.t, w) - x) < 6);
  if (near) { state.selAlert = near; renderAnomalies(); }
  setCursor(near ? near.t : tOf(x, w));
  if (state.data) showChangesNear(state.cursor);
}

// ------------------------------------------------------------------ the time axis, as on the map: the wheel zooms around the
// mouse, the pressed wheel pans, a drag selects a range to open, a click sets the moment, the moment's handle drags

const MIN_SPAN = 60_000, MAX_SPAN = 7 * 24 * HOUR;
let selection = null;           // [from, to] while a range is selected
let axisDrag = null;
const reloadSoon = debounce(() => {
  const last = rangeHistory[rangeHistory.length - 1];
  if (last) last[2] = null;             // the next turn of the wheel is a step of its own for ↩
  saveHash();
  loadAll();
}, 350);

// a new range drawn at once with what is loaded, and loaded when the wheel or the pan stops
function moveRange(from, to) {
  const span = Math.min(MAX_SPAN, Math.max(MIN_SPAN, to - from));
  const middle = (from + to) / 2;
  state.from = Math.round(middle - span / 2);
  state.to = Math.round(middle + span / 2);
  $('#from').value = toInput(state.from);
  $('#to').value = toInput(state.to);
  drawStrip(); drawChart();
  reloadSoon();
}

function setCursor(t) {
  state.cursor = Math.max(state.from, Math.min(state.to, t));
  drawChart(); drawStrip();
  loadHoldings();
}

function attachAxis(canvas, onClick) {
  canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    const r = canvas.getBoundingClientRect();
    const t = tOf(e.clientX - r.left, r.width);
    const k = e.deltaY > 0 ? 1.25 : 0.8;
    const span = Math.min(MAX_SPAN, Math.max(MIN_SPAN, (state.to - state.from) * k));
    const f = (t - state.from) / Math.max(1, state.to - state.from);
    if (!rangeHistory.length || rangeHistory[rangeHistory.length - 1][2] !== 'wheel') {
      rangeHistory.push([state.from, state.to, 'wheel']);
      $('#rangeBack').hidden = false;
    }
    moveRange(t - span * f, t - span * f + span);
  }, { passive: false });
  canvas.addEventListener('mousedown', (e) => {
    const r = canvas.getBoundingClientRect();
    const x = e.clientX - r.left;
    if (e.button === 1) {
      e.preventDefault();
      rangeHistory.push([state.from, state.to]);
      $('#rangeBack').hidden = false;
      axisDrag = { mode: 'pan', x: e.clientX, from: state.from, to: state.to, w: r.width };
      return;
    }
    if (e.button !== 0) return;
    const onHandle = state.cursor && Math.abs(xOf(state.cursor, r.width) - x) <= 6;
    axisDrag = { mode: onHandle ? 'scrub' : 'click', x: e.clientX, t: tOf(x, r.width), w: r.width, left: r.left, onClick };
  });
}
window.addEventListener('mousemove', (e) => {
  const d = axisDrag;
  if (!d) return;
  const t = Math.max(state.from, Math.min(state.to, tOf(e.clientX - d.left, d.w)));
  if (d.mode === 'pan') {
    const shift = -(e.clientX - d.x) / (d.w - PAD_L - PAD_R) * (d.to - d.from);
    moveRange(d.from + shift, d.to + shift);
  } else if (d.mode === 'scrub') {
    state.cursor = t;
    drawChart(); drawStrip();
    loadHoldings();
  } else if (d.mode === 'click' && Math.abs(e.clientX - d.x) > 4) {
    d.mode = 'select';
  }
  if (d.mode === 'select') {
    selection = [Math.min(d.t, t), Math.max(d.t, t)];
    drawChart(); drawStrip();
  }
});
window.addEventListener('mouseup', (e) => {
  const d = axisDrag;
  if (!d) return;
  axisDrag = null;
  if (d.mode === 'click') {
    selection = null;
    $('#rangeBar').hidden = true;
    d.onClick(e.clientX - d.left, d.w);
  } else if (d.mode === 'select' && selection && selection[1] - selection[0] > 1000) {
    showRangeBar();
  }
});
window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && selection) { selection = null; $('#rangeBar').hidden = true; drawChart(); drawStrip(); }
});

function showRangeBar() {
  const bar = $('#rangeBar');
  const w = $('#chart').clientWidth;
  const [a, b] = selection;
  $('#rangeZoom').textContent = `Показать ${fmt(a, b - a > 20 * HOUR)} – ${fmt(b, b - a > 20 * HOUR)}`;
  bar.hidden = false;
  bar.style.left = Math.max(120, Math.min(w - 120, (xOf(a, w) + xOf(b, w)) / 2)) + 'px';
}
$('#rangeZoom').addEventListener('click', () => {
  const [a, b] = selection;
  selection = null;
  $('#rangeBar').hidden = true;
  if (state.cursor < a || state.cursor > b) state.cursor = b;
  setRange(a, b);
});
$('#rangeCancel').addEventListener('click', () => { selection = null; $('#rangeBar').hidden = true; drawChart(); drawStrip(); });

attachAxis($('#strip'), stripClick);
attachAxis($('#chart'), chartClick);

// ------------------------------------------------------------------ what each inventory held at the moment

let holdingsRequest = 0;
let holdings = null;
const loadHoldings = debounce(async () => {
  if (!state.id || !state.cursor) return;
  const request = ++holdingsRequest;
  const data = await api('holdings', { kind: state.kind, id: state.id, t: Math.round(state.cursor) });
  if (request !== holdingsRequest) return;
  holdings = data;
  Object.assign(state.names, data.names);
  renderHoldings();
}, 120);

function renderHoldings() {
  const wrap = $('#holdingsWrap');
  if (!holdings || !state.data) { wrap.hidden = true; return; }
  wrap.hidden = false;
  const names = { ...state.names, ...holdings.names };
  const q = $('#holdingFilter').value.trim().toLowerCase();
  const showEmpty = $('#holdingEmpty').checked;
  const all = holdings.inventories;
  const empty = all.filter((i) => !Object.keys(i.items).length && !Object.keys(i.prev || {}).length).length;
  $('#holdingsAt').textContent = `на ${fmt(holdings.at)}`;
  const cards = [];
  for (const inv of all) {
    const name = invLabel(inv.entity, inv.inv, names, inv.body, inv.owner);
    const gridName = inv.grid ? names[inv.grid] || inv.grid : '';
    const items = new Set([...Object.keys(inv.items), ...Object.keys(inv.prev || {})]);
    if (!showEmpty && !Object.keys(inv.items).length && !Object.keys(inv.prev || {}).length) continue;
    const blockMatches = !q || name.toLowerCase().includes(q) || gridName.toLowerCase().includes(q);
    const rows = [...items].filter((i) => blockMatches || i.toLowerCase().includes(q))
      .sort((a, b) => (inv.items[b] || 0) - (inv.items[a] || 0))
      .map((i) => {
        const now = inv.items[i] || 0, was = inv.prev ? inv.prev[i] || 0 : now;
        const d = now - was;
        return `<tr class="${now ? d ? 'changed' : '' : 'gone'}"><td>${esc(itemName(i))}</td><td class="n">${num(now, decimalsFor(now - was))}</td>
          <td class="n ${d > 0 ? 'plus' : d < 0 ? 'minus' : ''}">${d ? signed(d) : ''}</td></tr>`;
      });
    if (q && !rows.length) continue;
    const fill = inv.max > 0 ? Math.min(100, inv.volume / inv.max * 100) : 0;
    const why = inv.flows.sort((a, b) => Math.abs(b[2]) - Math.abs(a[2])).slice(0, 6)
      .map(([k, i, v]) => `<span class="chip ${kindClass(k)}" title="${esc(k)}">${esc(kindLabel(k, names))}: ${esc(itemName(i))} <b class="${v > 0 ? 'plus' : 'minus'}">${signed(v)}</b></span>`).join('');
    cards.push(`<div class="card">
      <h4><a data-entity="${inv.entity}" title="Только этот инвентарь">${esc(name)}</a>
        <span class="meta" title="Когда записано это состояние${inv.prevT ? '; прошлая запись — ' + fmt(inv.prevT, false) : ''}">${fmt(inv.t, false)}</span></h4>
      ${state.kind === 'player' && inv.grid ? `<div class="grid">на <a data-grid="${inv.grid}" title="Инвентари этого грида">${esc(gridName)}</a></div>` : ''}
      <div class="fill" title="Заполнено ${num(fill, 0)}%"><div style="width:${fill}%"></div></div>
      ${rows.length ? `<table>${rows.join('')}</table>` : '<div class="empty">пусто</div>'}
      ${why ? `<div class="why">${why}</div>` : ''}</div>`);
  }
  $('#holdingsNote').textContent = `${all.length} инвентарей${empty ? `, пустых ${empty}` : ''}; изменение — с прошлой записи инвентаря`;
  $('#holdings').innerHTML = cards.join('') || `<div class="dim">${!all.length ? 'В этот момент инвентарей нет.'
    : q ? 'Нет инвентарей под фильтр.' : 'Все инвентари в этот момент пусты — «показывать пустые», чтобы увидеть их.'}</div>`;
  for (const a of $('#holdings').querySelectorAll('a[data-entity]')) a.addEventListener('click', () => choose('entity', a.dataset.entity, state.cursor));
  for (const a of $('#holdings').querySelectorAll('a[data-grid]')) a.addEventListener('click', () => choose('grid', a.dataset.grid, state.cursor));
}
$('#holdingFilter').addEventListener('input', debounce(renderHoldings, 150));
$('#holdingEmpty').addEventListener('change', renderHoldings);

$('#logScale').addEventListener('change', drawChart);
$('#itemsTop').addEventListener('click', () => { topItems(); renderLegend(); drawChart(); });
$('#itemsNone').addEventListener('click', () => { state.shown.clear(); renderLegend(); drawChart(); });
$('#itemFilter').addEventListener('input', debounce(renderLegend, 150));
$('#anomalyKind').addEventListener('change', renderAnomalies);
$('#onlySubject').addEventListener('change', renderAnomalies);
window.addEventListener('resize', () => { drawStrip(); drawChart(); });

// ------------------------------------------------------------------ tabs

function showTab(name) {
  for (const b of document.querySelectorAll('.tabs button')) b.classList.toggle('active', b.dataset.tab === name);
  for (const s of document.querySelectorAll('.tab')) s.classList.toggle('active', s.id === 'tab-' + name);
}
for (const b of document.querySelectorAll('.tabs button')) b.addEventListener('click', () => {
  showTab(b.dataset.tab);
  if (b.dataset.tab === 'changes') renderChanges();
});

// ------------------------------------------------------------------ search

let options = [];
let active = -1;
async function openDropdown() {
  const q = $('#search').value.trim();
  const all = await api('objects', { from: state.from, to: state.to, q });
  if ($('#search').value.trim() !== q) return;
  options = [...all.players.map((o) => ({ kind: 'player', id: o.id, name: o.name })), ...all.grids.map((o) => ({ kind: 'grid', id: o.id, name: o.name }))];
  if (q.length >= 2) {
    const found = await api('search', { q, from: state.from - 7 * 24 * HOUR, to: state.to });
    const known = new Set(options.map((o) => o.kind + ':' + o.id));
    for (const o of found) if ((o.kind === 'player' || o.kind === 'grid') && !known.has(o.kind + ':' + o.id)) options.push({ kind: o.kind, id: o.id, name: o.name });
  }
  if (/^\d{6,}$/.test(q)) options.push({ kind: 'entity', id: q, name: 'инвентарь с id ' + q });
  active = -1;
  const label = { player: 'игрок', grid: 'грид', entity: 'блок' };
  $('#results').innerHTML = options.length
    ? options.slice(0, 300).map((o, i) => `<div class="opt" data-i="${i}"><span class="n">${esc(o.name || o.id)}</span><span class="c">${label[o.kind]}</span></div>`).join('')
    : '<div class="none">Ничего не нашлось.</div>';
  $('#results').hidden = false;
  for (const el of $('#results').querySelectorAll('.opt')) el.addEventListener('mousedown', (e) => { e.preventDefault(); pick(Number(el.dataset.i)); });
}
function pick(i) {
  const o = options[i];
  if (!o) return;
  $('#results').hidden = true;
  $('#search').value = o.name || o.id;
  choose(o.kind, o.id);
}
$('#search').addEventListener('input', debounce(openDropdown, 200));
$('#search').addEventListener('focus', openDropdown);
$('#search').addEventListener('blur', () => setTimeout(() => { $('#results').hidden = true; }, 150));
$('#searchOpen').addEventListener('click', () => { $('#search').focus(); openDropdown(); });
$('#search').addEventListener('keydown', (e) => {
  const els = $('#results').querySelectorAll('.opt');
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault();
    active = Math.max(0, Math.min(els.length - 1, active + (e.key === 'ArrowDown' ? 1 : -1)));
    els.forEach((el, i) => el.classList.toggle('active', i === active));
    els[active]?.scrollIntoView({ block: 'nearest' });
  } else if (e.key === 'Enter') pick(active >= 0 ? active : 0);
  else if (e.key === 'Escape') $('#results').hidden = true;
});

// ------------------------------------------------------------------ range buttons and start

$('#reload').addEventListener('click', () => { readRange(); saveHash(); loadAll(); });
$('#lastHour').addEventListener('click', async () => { const { now } = await api('now'); setRange(now - HOUR, now); });
$('#lastDay').addEventListener('click', async () => { const { now } = await api('now'); setRange(now - 24 * HOUR, now); });
$('#lastWeek').addEventListener('click', async () => { const { now } = await api('now'); setRange(now - 7 * 24 * HOUR, now); });

async function start() {
  const clock = await api('now');
  zoneOffset = (clock.offsetMinutes || 0) * 60_000;
  $('#zone').textContent = clock.zone || 'UTC';
  const h = new URLSearchParams(location.hash.slice(1));
  const to = Number(h.get('to')) || clock.now;
  const from = Number(h.get('from')) || to - 24 * HOUR;
  state.from = from; state.to = to;
  $('#from').value = toInput(from);
  $('#to').value = toInput(to);
  if (h.get('id')) { state.kind = h.get('kind') || 'player'; state.id = h.get('id'); }
  saveHash();
  await loadAll();
  if (!state.anomalies.length && !state.id) status('За интервал аномалий нет. Выберите игрока или грид, чтобы посмотреть его инвентари.');
}
window.__ledger = { state };
start();
