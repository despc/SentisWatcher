// SentisWatcher: the structures page. What stands and flies in the world right now - not from the records, read from
// the game on request: /api/structures (every structure, briefly) and /api/structure (one in full: its blocks by
// type and its electricity - what makes it, what takes it, which blocks go without).

const $ = (s) => document.querySelector(s);
const state = { list: [], sort: 'blocks', desc: true, selected: null, detail: null };

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

const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const int = (n) => Math.round(n).toLocaleString('ru-RU');

// power as the game counts it, MW: shown in the unit that reads best
function power(mw) {
  const a = Math.abs(mw);
  if (a === 0) return '0';
  if (a >= 1000) return (mw / 1000).toLocaleString('ru-RU', { maximumFractionDigits: 2 }) + ' ГВт';
  if (a >= 1) return mw.toLocaleString('ru-RU', { maximumFractionDigits: 2 }) + ' МВт';
  if (a >= 0.001) return (mw * 1000).toLocaleString('ru-RU', { maximumFractionDigits: 1 }) + ' кВт';
  return (mw * 1e6).toLocaleString('ru-RU', { maximumFractionDigits: 0 }) + ' Вт';
}
const energy = (mwh) => power(mwh) + '·ч';
// hours as a time a person reads
function duration(hours) {
  if (!isFinite(hours)) return '—';
  if (hours < 1 / 60) return 'меньше минуты';
  if (hours < 1) return Math.round(hours * 60) + ' мин';
  if (hours < 48) return hours.toLocaleString('ru-RU', { maximumFractionDigits: 1 }) + ' ч';
  if (hours < 24 * 365) return (hours / 24).toLocaleString('ru-RU', { maximumFractionDigits: 1 }) + ' сут';
  return (hours / 24 / 365).toLocaleString('ru-RU', { maximumFractionDigits: 1 }) + ' г';
}
const kg = (n) => n.toLocaleString('ru-RU', { maximumFractionDigits: n < 10 ? 2 : 0 }) + ' кг';

// How long the structure lasts on what it has, at the present load: the batteries' charge over what they give
// more than they take; the reactors' fuel over what they burn.
function autonomy(p) {
  const lines = [];
  if (p.storage > 0) {
    const drain = p.batteryOut - p.batteryIn;
    if (drain > 1e-7) lines.push(`Батарей хватит на <b>${duration(p.stored / drain)}</b>: в них ${energy(p.stored)}, отдают ${power(drain)}${p.batteryIn > 1e-7 ? ' сверх заряда' : ''}.`);
    else if (drain < -1e-7) lines.push(`Батареи заряжаются: получают ${power(-drain)}, до полного заряда осталось ${energy(p.storage - p.stored)}${p.storage > p.stored ? ' (около ' + duration((p.storage - p.stored) / -drain) + ')' : ''}.`);
    else lines.push(`Батареи не расходуются: в них ${energy(p.stored)} из ${energy(p.storage)}.`);
  }
  const f = p.fuel;
  if (f) {
    const all = f.inReactors + f.stored;
    const where = `в реакторах ${kg(f.inReactors)}` + (f.stored > 0 ? `, ещё ${kg(f.stored)} в других инвентарях структуры` : '');
    if (all <= 0) lines.push(`Топлива для реакторов (${esc(f.name)}) нет.`);
    else if (f.burn > 0) lines.push(`Топлива реакторов (${esc(f.name)}) хватит на <b>${duration(all / f.burn)}</b>: ${where}; расход ${kg(f.burn)} в час` +
      (f.stored > 0 ? `, только того, что в реакторах, — на ${duration(f.inReactors / f.burn)}` : '') + '.');
    else lines.push(`Реакторы топливо (${esc(f.name)}) сейчас не расходуют: ${where}.`);
  }
  return lines.length ? `<div class="autonomy">${lines.map((l) => `<div>${l}</div>`).join('')}<div class="dim">При нынешней нагрузке.</div></div>` : '';
}

// the distributor's state of a structure
const STATES = {
  Ok: ['ok', 'хватает'],
  OverloadAdaptible: ['warn', 'перегрузка'],
  OverloadBlackout: ['bad', 'отключения'],
  NoPower: ['none', 'нет энергии'],
};
function stateChip(s) {
  const [cls, text] = STATES[s] || ['none', s || '—'];
  return `<span class="state ${cls}">${text}</span>`;
}
const isBad = (s) => s.state === 'OverloadAdaptible' || s.state === 'OverloadBlackout';

// ------------------------------------------------------------------ the list

// the owners to choose from: those of the structures now, with how many each has; the choice survives a refresh
const ALL = '*all', NOBODY = '*nobody';
function drawOwners() {
  const counts = new Map();
  for (const s of state.list) counts.set(s.owner || NOBODY, (counts.get(s.owner || NOBODY) || 0) + 1);
  const chosen = $('#owner').value || ALL;
  const names = [...counts.keys()].filter((k) => k !== NOBODY).sort((a, b) => a.localeCompare(b, 'ru'));
  const tag = new Map(state.list.filter((s) => s.owner && s.faction).map((s) => [s.owner, s.faction]));
  $('#owner').innerHTML = `<option value="${ALL}">все (${state.list.length})</option>` +
    (counts.has(NOBODY) ? `<option value="${NOBODY}">без владельца (${counts.get(NOBODY)})</option>` : '') +
    names.map((n) => `<option value="${esc(n)}">${esc(n)}${tag.has(n) ? ' [' + esc(tag.get(n)) + ']' : ''} (${counts.get(n)})</option>`).join('');
  $('#owner').value = chosen === ALL || counts.has(chosen) ? chosen : ALL;
}

const COLUMNS = [
  ['name', 'Структура', false],
  ['owner', 'Владелец', false],
  ['grids', 'Гридов', true],
  ['blocks', 'Блоков', true],
  ['pcu', 'PCU', true],
  ['required', 'Нужно', true],
  ['available', 'Доступно', true],
  ['state', 'Энергия', false],
];

function drawList() {
  const q = $('#q').value.trim().toLowerCase();
  const onlyBad = $('#onlyBad').checked;
  const owner = $('#owner').value;
  let rows = state.list.filter((s) => (!onlyBad || isBad(s)) &&
    (owner === ALL || (s.owner || NOBODY) === owner) &&
    (!q || (s.name || '').toLowerCase().includes(q)));
  const key = state.sort, dir = state.desc ? -1 : 1;
  rows.sort((a, b) => {
    const x = a[key] ?? '', y = b[key] ?? '';
    return (typeof x === 'number' ? x - y : String(x).localeCompare(String(y), 'ru')) * dir || b.blocks - a.blocks;
  });
  $('#count').textContent = `структур: ${rows.length}` + (rows.length !== state.list.length ? ` из ${state.list.length}` : '') +
    `, блоков: ${int(rows.reduce((n, s) => n + s.blocks, 0))}`;
  const shown = rows.slice(0, 1000);
  $('#structures').innerHTML = '<tr>' + COLUMNS.map(([k, title, n]) =>
    `<th data-k="${k}" class="${n ? 'n' : ''} ${k === key ? 'sorted' : ''}">${title}${k === key ? (state.desc ? ' ▼' : ' ▲') : ''}</th>`).join('') + '</tr>' +
    shown.map((s) => `<tr class="st ${s.id === state.selected ? 'sel' : ''}" data-id="${s.id}">
      <td class="name">${esc(s.name)} <span class="dim">${s.static ? 'станция' : s.large ? 'большой' : 'малый'}</span></td>
      <td>${s.faction ? '<span class="dim">[' + esc(s.faction) + ']</span> ' : ''}${esc(s.owner || '—')}</td>
      <td class="n">${s.grids}</td><td class="n">${int(s.blocks)}</td><td class="n">${int(s.pcu)}</td>
      <td class="n">${power(s.required)}</td><td class="n">${power(s.available)}</td>
      <td>${stateChip(s.state)}</td></tr>`).join('') +
    (rows.length > shown.length ? `<tr><td colspan="8" class="dim">показаны первые ${shown.length}: уточните отбор</td></tr>` : '');
}

$('#structures').addEventListener('click', (e) => {
  const th = e.target.closest('th');
  if (th) {
    const k = th.dataset.k;
    if (state.sort === k) state.desc = !state.desc;
    else { state.sort = k; state.desc = COLUMNS.find((c) => c[0] === k)[2]; }
    drawList();
    return;
  }
  const row = e.target.closest('tr.st');
  if (row) select(row.dataset.id);
});

// ------------------------------------------------------------------ one structure

function table(head, rows, empty) {
  if (!rows.length) return `<div class="dim">${empty}</div>`;
  return `<table class="load"><tr>${head.map(([t, n]) => `<th class="${n ? 'n' : ''}">${t}</th>`).join('')}</tr>${rows.join('')}</table>`;
}

function bar(share, cls = '') {
  return `<div class="bar"><i class="${cls}" style="width:${Math.max(0, Math.min(100, share * 100)).toFixed(1)}%"></i></div>`;
}

function drawDetail() {
  const d = state.detail;
  if (!d) return;
  const p = d.power;
  const blocks = d.grids.reduce((n, g) => n + g.blocks, 0);
  const mapLink = `index.html#tracks=grid:${d.id}&sel=grid:${d.id}`;

  // does every block get its electricity
  let verdict;
  if (p.consumers === 0 && p.capacity === 0) verdict = ['none', 'В структуре нет ни источников, ни потребителей электричества.'];
  else if (p.unpoweredCount === 0) verdict = ['ok', `Электричества хватает всем: ${p.consumers} потребителей получают своё.`];
  else verdict = ['bad', `Электричества не хватает: без питания ${p.unpoweredCount} из ${p.consumers} потребителей.`];
  const load = p.capacity > 0 ? p.required / p.capacity : (p.required > 0 ? 2 : 0);
  // the batteries' charge against what the structure takes over what the others give
  const charging = d.power.sinks.filter((s) => /batter|аккумулятор|батаре/i.test(s.name)).reduce((n, s) => n + s.now, 0);

  $('#detail').innerHTML = `
    <h2>${esc(d.name)}</h2>
    <div class="sub">${d.faction ? '[' + esc(d.faction) + '] ' : ''}${esc(d.owner || 'без владельца')} ·
      гридов ${d.grids.length}, блоков ${int(blocks)}, PCU ${int(d.grids.reduce((n, g) => n + g.pcu, 0))} ·
      <a href="${mapLink}" title="${d.x.toFixed(0)}, ${d.y.toFixed(0)}, ${d.z.toFixed(0)}">на карте</a></div>

    <h3>Энергобаланс ${stateChip(p.state)}</h3>
    <div class="verdict ${verdict[0]}">${verdict[1]}</div>
    ${autonomy(p)}
    <div class="summary">
      <div class="card"><div class="k">производится</div><div class="v">${power(p.produced)}</div><div class="s">из ${power(p.capacity)} возможных</div></div>
      <div class="card"><div class="k">потребляется</div><div class="v">${power(p.consumed)}</div><div class="s">запрошено ${power(p.required)}</div></div>
      <div class="card"><div class="k">запас мощности</div><div class="v ${p.capacity < p.required ? 'bad' : ''}">${power(p.capacity - p.required)}</div><div class="s">возможное минус запрошенное</div></div>
      ${p.storage > 0 ? `<div class="card"><div class="k">в батареях</div><div class="v">${energy(p.stored)}</div><div class="s">из ${energy(p.storage)}${charging > 0 ? ', заряд ' + power(charging) : ''}</div></div>` : ''}
    </div>
    <div class="dim">Загрузка источников: ${p.capacity > 0 ? (load * 100).toFixed(0) + '%' : 'источников нет'}</div>
    ${bar(load, load > 1 ? 'over' : '')}
    ${p.storage > 0 ? `<div class="dim">Заряд батарей: ${(p.stored / p.storage * 100).toFixed(0)}%</div>${bar(p.stored / p.storage, 'charge')}` : ''}

    <div class="cols">
      <div><h3>Чем производится</h3>
        ${table([['Источник'], ['Блоков', 1], ['Работает', 1], ['Сейчас', 1], ['Максимум', 1], ['Доля', 1]],
          p.sources.map((s) => `<tr class="${s.working === 0 ? 'off' : ''}"><td class="name">${esc(s.name)}</td><td class="n">${s.count}</td><td class="n">${s.working}</td>
            <td class="n">${power(s.now)}</td><td class="n">${power(s.max)}</td><td class="n">${p.produced > 0 ? (s.now / p.produced * 100).toFixed(0) + '%' : '—'}</td></tr>`),
          'Источников электричества нет.')}
      </div>
      <div><h3>Чем потребляется</h3>
        ${table([['Потребитель'], ['Блоков', 1], ['Просят', 1], ['Без питания', 1], ['Запрошено', 1], ['Получают', 1]],
          p.sinks.filter((s) => s.max > 0 || s.now > 0).map((s) => `<tr class="${s.unpowered ? 'unpowered' : ''}"><td class="name">${esc(s.name)}</td><td class="n">${s.count}</td><td class="n">${s.working}</td>
            <td class="n">${s.unpowered || '—'}</td><td class="n">${power(s.max)}</td><td class="n">${power(s.now)}</td></tr>`),
          'Сейчас электричество никто не запрашивает.')}
        ${(() => { const idle = p.sinks.filter((s) => !(s.max > 0 || s.now > 0)); return idle.length ?
          `<div class="dim" title="${esc(idle.map((s) => s.name + ' ×' + s.count).join(', '))}">Ещё ${idle.reduce((n, s) => n + s.count, 0)} блоков умеют потреблять, но сейчас ничего не просят (выключены или простаивают).</div>` : ''; })()}
      </div>
    </div>

    ${p.unpowered.length ? `<h3>Блоки без питания <span class="dim">— просят электричество и не получают${p.unpoweredCount > p.unpowered.length ? `; показаны ${p.unpowered.length} из ${p.unpoweredCount}` : ''}</span></h3>
      ${table([['Блок'], ['Тип'], ['Грид'], ['Нужно', 1], ['Получает', 1]],
        p.unpowered.map((b) => `<tr class="unpowered"><td class="name">${esc(b.name)}</td><td>${esc(b.type)}</td><td>${esc(b.grid)}</td>
          <td class="n">${power(b.required)}</td><td class="n">${(b.supplied * 100).toFixed(0)}%</td></tr>`), '')}` : ''}

    <div class="cols">
      <div><h3>Гриды</h3>
        ${table([['Грид'], ['Вид'], ['Блоков', 1], ['PCU', 1], ['Масса, т', 1]],
          d.grids.map((g) => `<tr><td class="name">${esc(g.name)}</td><td>${g.static ? 'станция' : g.large ? 'большой' : 'малый'}</td>
            <td class="n">${int(g.blocks)}</td><td class="n">${int(g.pcu)}</td><td class="n">${g.mass > 0 ? (g.mass / 1000).toLocaleString('ru-RU', { maximumFractionDigits: 1 }) : '—'}</td></tr>`), '')}
      </div>
      <div><h3>Блоки <span class="dim">— ${d.blocks.length} видов</span></h3>
        ${table([['Блок'], ['Штук', 1], ['Доля', 1]],
          d.blocks.map((b) => `<tr><td class="name">${esc(b.name)}</td><td class="n">${int(b.count)}</td><td class="n">${(b.count / blocks * 100).toFixed(1)}%</td></tr>`), '')}
      </div>
    </div>`;
}

async function select(id) {
  state.selected = id;
  history.replaceState(null, '', '#' + id);
  drawList();
  await loadDetail();
}

async function loadDetail() {
  if (!state.selected) return;
  try {
    state.detail = await api('structure', { id: state.selected });
    // the same structure under its largest grid's id, if that changed
    if (state.detail.id !== state.selected) { state.selected = state.detail.id; drawList(); }
    drawDetail();
  } catch (e) {
    $('#detail').innerHTML = `<div class="dim hint">Структуры больше нет в мире или игра не ответила: ${esc(e.message)}</div>`;
  }
}

async function load() {
  try {
    state.list = await api('structures');
    drawOwners();
    drawList();
    await loadDetail();
    status('обновлено ' + new Date().toLocaleTimeString('ru-RU'));
  } catch (e) {
    status('Игра не ответила: ' + e.message, true);
  }
}

// The bar between the list and the structure: dragged, it sets the list's width (a share of the page, kept in this
// browser); a double click puts it back.
const SPLIT_KEY = 'watcher.structures.left';
function setLeft(share) {
  const main = document.querySelector('main.split');
  if (share == null) main.style.removeProperty('--left');
  else main.style.setProperty('--left', (Math.max(0.15, Math.min(0.85, share)) * 100).toFixed(2) + '%');
}
try { const kept = Number(localStorage.getItem(SPLIT_KEY)); if (kept > 0) setLeft(kept); } catch (e) { /* no storage: the default */ }
$('#splitter').addEventListener('pointerdown', (e) => {
  e.preventDefault();
  const bar = e.currentTarget, main = document.querySelector('main.split');
  bar.setPointerCapture(e.pointerId);
  document.body.classList.add('resizing');
  let share = null;
  const move = (m) => {
    const box = main.getBoundingClientRect();
    share = Math.max(0.15, Math.min(0.85, (m.clientX - box.left) / box.width));
    setLeft(share);
  };
  const up = () => {
    bar.removeEventListener('pointermove', move);
    document.body.classList.remove('resizing');
    if (share != null) try { localStorage.setItem(SPLIT_KEY, String(share)); } catch (err) { /* not kept */ }
  };
  bar.addEventListener('pointermove', move);
  bar.addEventListener('pointerup', up, { once: true });
  bar.addEventListener('pointercancel', up, { once: true });
});
$('#splitter').addEventListener('dblclick', () => {
  setLeft(null);
  try { localStorage.removeItem(SPLIT_KEY); } catch (err) { /* nothing kept */ }
});

$('#q').addEventListener('input', drawList);
$('#onlyBad').addEventListener('change', drawList);
$('#owner').addEventListener('change', drawList);
$('#reload').addEventListener('click', load);
setInterval(() => { if ($('#live').checked && !document.hidden) load(); }, 10_000);
if (/^#\d+$/.test(location.hash)) state.selected = location.hash.slice(1);
load();
