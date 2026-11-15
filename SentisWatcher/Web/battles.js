import { mountBattleTimeline } from './battle-timeline.js';
import { economyHtml } from './battle-economy.js';
import { outcomeHtml, mvpHtml, accuracyText, accuracyDescription } from './battle-results.js';

const $ = s => document.querySelector(s);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const num = n => Number(n || 0).toLocaleString('ru-RU', { maximumFractionDigits: 0 });
const relations = { enemy: 'враг', neutral: 'нейтрал', ally: 'союзник', own: 'своё', nobody: 'ничьё', unknown: 'неизвестно' };
const kinds = { player: 'игрок', bot: 'бот', npc: 'NPC', none: 'неизвестно' };
let offset = 0, data = null, report = null, selected = '', request = 0, detailRequest = 0, params = {};
let disposeTimeline = null, componentNames = {};
fetch('component-names.json').then(r => r.ok ? r.json() : {}).then(names => { componentNames = names; renderEconomy(); }).catch(() => {});
function renderEconomy() {
  const container = $('#economy');
  if (container) container.innerHTML = economyHtml(report?.economy, {names:componentNames});
}
const fmt = t => new Date(t + offset).toISOString().slice(0, 19).replace('T', ' ');
const input = t => fmt(t).replace(' ', 'T');
const parse = v => Date.parse(v + 'Z') - offset;
const duration = ms => `${Math.floor(ms / 60000)} мин ${Math.floor(ms / 1000) % 60} с`;
function status(text, error = false) { $('#status').textContent = text; $('#status').classList.toggle('error', error); }
async function api(call, p = {}) {
  const r = await fetch('/api/' + call + '?' + new URLSearchParams(p));
  const body = await r.json();
  if (!r.ok || body.error) throw new Error(body.error || r.statusText);
  return body;
}
function range(from, to) { $('#from').value = input(from); $('#to').value = input(to); }
function drawList() {
  const q = $('#search').value.trim().toLocaleLowerCase();
  const list = (data?.battles || []).filter(b => [b.title, ...b.participants, ...b.factions, ...b.weapons].join(' ').toLocaleLowerCase().includes(q));
  list.sort($('#sort').value === 'time' ? (a, b) => b.start - a.start : (a, b) => b.combatDamage - a.combatDamage);
  $('#count').textContent = `${list.length} / ${data?.battles.length || 0}`;
  $('#battleList').innerHTML = list.length ? list.map(b => `<button class="battle ${b.id === selected ? 'selected' : ''}" data-id="${esc(b.id)}" aria-pressed="${b.id === selected}">
    <b>${esc(b.title)}</b><span>${fmt(b.start)} — ${fmt(b.end).slice(11)}</span><span>${duration(b.duration)} · ${b.ongoing ? 'бой продолжается' : b.endedAt ? 'завершён' : 'граница не найдена'} · урон ${num(b.combatDamage)}</span>
    <span>${b.participants.map(esc).join(', ') || 'Неизвестные участники'}</span><small>${b.factions.map(esc).join(' / ')}</small>
    <span>Блоков: ${num(b.destroyed)} · убито: ${num(b.kills)}${b.clippedStart || b.clippedEnd ? ' · ⚠ предел поиска' : ''}</span></button>`).join('') : '<p class="dim">Сражений не найдено. Выберите день БД или измените интервал.</p>';
}
const person = p => `${esc(p.name)}<small>${esc(p.faction)}</small>`;
function drawReport() {
  disposeTimeline?.(); disposeTimeline = null;
  if (!report) { $('#report').innerHTML = '<h2>Итоги боя</h2><p class="dim">Выберите сражение слева.</p>'; return; }
  const b = report.summary;
  $('#report').innerHTML = `<h2>Итоги боя · ${esc(b.title)}</h2>
    <p>${fmt(b.start)} — ${fmt(b.end)} · ${duration(b.duration)} · последний урон ${fmt(b.end)}<br>${b.ongoing ? '<b>Бой продолжается — ещё не прошло 2 минуты без урона</b>' : b.endedAt ? `Завершён ${fmt(b.endedAt)} после 2 минут тишины` : 'Окончание не подтверждено'} ${b.clippedStart || b.clippedEnd ? '<span class="warn">· Достигнут защитный предел поиска границ</span>' : ''}</p>
    <div class="summary">${[['Боевой урон', b.combatDamage], ['Весь урон с контекстом', b.damage], ['Событий урона', b.hits], ['Убито с контекстом', b.kills], ['Блоков с контекстом', b.destroyed]].map(([k, v]) => `<div class="card"><div class="k">${k}</div><div class="v">${num(v)}</div></div>`).join('')}</div>
    ${outcomeHtml(b.outcome)}${mvpHtml(report.mvp)}
    <section id="economy" aria-label="Экономические потери"></section>
    <div class="report-tools"><a href="index.html#from=${b.start - 60000}&to=${b.end + 60000}&t=${b.start}">На карте</a><button id="export">Скачать JSON</button></div>
    <h3>Урон и прыжки по времени</h3>
    <div class="battle-chart-legend"><span><i class="combat"></i>Боевой урон</span><span><i class="context"></i>Прочий урон</span><span><i class="jump"></i>Прыжки: ${(report.jumps || []).length}</span></div>
    <div id="battleChart" class="battle-chart">
      <svg id="battleTimeline" role="group" aria-label="Таймлайн урона и прыжков" preserveAspectRatio="none"></svg>
      <div class="battle-chart-tip" role="tooltip" hidden></div>
    </div>
    <p class="dim">${fmt(report.timelineFrom ?? b.start)} → ${fmt(report.timelineTo ?? b.end)} · наведите на столбец или ромб прыжка для подробностей</p>
    <h3>Участники (с контекстом) · нажмите строку для отчёта по полученному урону</h3>
    <div class="table-scroll"><table class="load"><thead><tr><th>Кто / фракция на момент боя</th><th class="n">Нанёс</th><th class="n">Получил</th><th class="n">Попал / снарядов</th><th class="n">Меткость</th><th class="n">Прыжков</th><th class="n">Убийства / смерти</th><th class="n">Блоки уничтожил / потерял</th></tr></thead>
    <tbody>${report.participants.map((p, i) => `<tr class="person" data-person="${i}" tabindex="0" role="button" aria-label="Полученный урон: ${esc(p.person.name)}"><td>${person(p.person)}${report.mvp?.players?.some(m => m.id === p.person.id) ? '<small>★ MVP / кандидат</small>' : ''}</td><td class="n">${num(p.dealt)}</td><td class="n">${num(p.taken)}</td><td class="n">${['ok', 'no_shots'].includes(p.accuracyStatus) ? `${num(p.hitShots)} / ${num(p.shots)}` : '—'}</td><td class="n" title="${esc(accuracyDescription(p))}">${accuracyText(p)}</td><td class="n">${num(p.jumpCount)}</td><td class="n">${num(p.kills)} / ${num(p.deaths)}</td><td class="n">${num(p.destroyed)} / ${num(p.lost)}</td></tr>`).join('')}</tbody></table></div>
    <h3>Кто → кому → чем · отчёт по полученному урону</h3>
    <div class="report-tools"><label>Кому <select id="victim"><option value="">всем</option>${report.participants.map((p, i) => `<option value="${i}">${esc(p.person.name)} · ${esc(p.person.faction)}</option>`).join('')}</select></label>
    <label>Чем <select id="weapon"><option value="">всё</option>${[...new Set(report.breakdown.map(r => r.weapon))].sort().map(w => `<option value="${esc(w)}">${esc(w)}</option>`).join('')}</select></label>
    <label>Отношение <select id="relation"><option value="">все</option>${Object.entries(relations).map(([k, n]) => `<option value="${k}">${n}</option>`).join('')}</select></label><span id="filteredTotal"></span></div>
    <div class="table-scroll"><table class="load"><thead><tr><th>Получатель / фракция</th><th>Кто / фракция</th><th>Источник → цель</th><th>Чем</th><th>Отношение</th><th class="n">Урон</th><th class="n">Событий урона</th><th class="n">Убито</th><th class="n">Блоков</th></tr></thead><tbody id="breakdown"></tbody></table></div>
    <p class="dim">Неизвестный источник не считается отдельным игроком. Фракции старых записей не восстанавливаются из текущего мира. В старой записи нельзя восстановить пилота/строителя: MVP следует сохранённой атрибуции. Уничтоженные блоки не означают уничтоженные корабли. Меткость — прямые попадания снарядами, а не число повреждённых блоков; взрыв рядом без прямого контакта не считается попаданием. Окно выстрелов включает 2 минуты до первого урона и до завершения боя. «—» означает, что точных данных нет. Прыжки считаются с первого урона до завершения, включая пассажиров, без повторов одного физического прыжка.</p>`;
  renderEconomy();
  disposeTimeline = mountBattleTimeline($('#battleChart'), report, fmt);
  for (const id of ['victim', 'weapon', 'relation']) $('#' + id).addEventListener('change', drawBreakdown);
  $('#export').onclick = () => {
    const url = URL.createObjectURL(new Blob([JSON.stringify({ parameters: params, report }, null, 2)], { type: 'application/json' }));
    const a = document.createElement('a'); a.href = url; a.download = `battle-${b.start}.json`; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
  };
  function choosePerson(e) {
    const tr = e.target.closest('[data-person]');
    if (!tr) return;
    $('#victim').value = tr.dataset.person; drawBreakdown();
    $('#victim').scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  }
  $('#report').onclick = choosePerson;
  $('#report').onkeydown = e => { if ((e.key === 'Enter' || e.key === ' ') && e.target.matches('[data-person]')) { e.preventDefault(); choosePerson(e); } };
  drawBreakdown();
}
function drawBreakdown() {
  const p = $('#victim').value === '' ? null : report.participants[Number($('#victim').value)].person;
  const rows = report.breakdown.filter(r => (!p || r.victim.id === p.id && r.victim.factionKey === p.factionKey) &&
    (!$('#weapon').value || r.weapon === $('#weapon').value) && (!$('#relation').value || r.relation === $('#relation').value));
  $('#filteredTotal').textContent = `Получено: ${num(rows.reduce((s, r) => s + r.damage, 0))}`;
  $('#breakdown').innerHTML = rows.length ? rows.map(r => `<tr><td>${person(r.victim)}</td><td>${person(r.attacker)}<small>${esc(kinds[r.attackerKind] || r.attackerKind)}</small></td>
    <td>${esc(r.source)} → ${esc(r.target)}<small>${esc(r.targetKind === 'block' ? 'грид' : r.targetKind)}</small></td><td>${esc(r.weapon)}</td><td>${esc(relations[r.relation] || r.relation)}</td>
    <td class="n">${num(r.damage)}</td><td class="n">${num(r.hits)}</td><td class="n">${num(r.kills)}</td><td class="n">${num(r.destroyed)}</td></tr>`).join('') : '<tr><td colspan="9">Нет событий по фильтру.</td></tr>';
}
async function selectBattle(id) {
  disposeTimeline?.(); disposeTimeline = null;
  const version = ++detailRequest;
  selected = id; report = null; drawList();
  $('#report').innerHTML = '<h2>Итоги боя</h2><p>Загружаю…</p>';
  try {
    const result = await api('battles', { ...params, battle: id });
    if (version !== detailRequest) return;
    report = result.report;
    if (!report) throw new Error('Бой изменился или не найден. Обновите список.');
    const hash = new URLSearchParams({ ...params, battle: id });
    history.replaceState(null, '', '#' + hash);
    drawReport();
  } catch (e) { if (version === detailRequest) { $('#report').textContent = e.message; status(e.message, true); } }
}
async function load(preferred = '') {
  const version = ++request; ++detailRequest;
  const from = parse($('#from').value), to = parse($('#to').value);
  if (!Number.isFinite(from) || !Number.isFinite(to) || to < from || to - from > 7 * 86400000) { status('Задайте корректный интервал не более 7 суток.', true); return; }
  params = { from, to };
  selected = ''; report = null; data = null; drawList(); drawReport(); status('Ищу сражения…');
  try {
    const result = await api('battles', params);
    if (version !== request) return;
    data = result; drawList();
    status(`Сражений: ${data.battles.length} · границы найдены автоматически · проверено записей: ${num(data.rows)}`);
    const id = data.battles.find(b => b.id === preferred)?.id || data.battles[0]?.id;
    if (id) await selectBattle(id);
  } catch (e) { if (version === request) status(e.message, true); }
}
$('#battleList').onclick = e => { const b = e.target.closest('[data-id]'); if (b) selectBattle(b.dataset.id); };
$('#search').oninput = drawList;
$('#sort').onchange = drawList;
$('#reload').onclick = () => load();
$('#lastDay').onclick = () => { range(Date.now() - 86400000, Date.now()); load(); };
$('#day').onchange = () => { if (!$('#day').value) return; const from = Date.parse($('#day').value + 'T00:00:00Z'); range(from, from + 86400000 - 1); load(); };
(async () => {
  const [clock, days] = await Promise.all([api('now'), api('days')]);
  offset = (clock.offsetMinutes || 0) * 60000; $('#zone').textContent = clock.zone;
  $('#day').innerHTML += days.map(d => `<option value="${esc(d)}">${esc(d)} (UTC)</option>`).join('');
  const hash = new URLSearchParams(location.hash.slice(1));
  if (hash.has('from') && hash.has('to') && Number.isFinite(Number(hash.get('from'))) && Number.isFinite(Number(hash.get('to')))) range(Number(hash.get('from')), Number(hash.get('to')));
  else if (days.length) { const from = Date.parse(days[0] + 'T00:00:00Z'); range(from, from + 86400000 - 1); $('#day').value = days[0]; }
  else range(clock.now - 86400000, clock.now);
  await load(hash.get('battle') || '');
})().catch(e => status(e.message, true));
setInterval(() => { if ($('#live').checked && !document.hidden) load(selected); }, 10000);
