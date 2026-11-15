const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const num = n => Number(n || 0).toLocaleString('ru-RU', { maximumFractionDigits: 0 });

export function outcomeHtml(outcome) {
  if (!outcome) return '<p class="dim">Результат ещё не рассчитан.</p>';
  const sides = outcome.sides || [];
  const leaders = sides.filter(s => (outcome.leaders || []).includes(s.id));
  const label = outcome.status === 'winner' ? `Победитель: ${leaders.map(s => esc(s.name)).join(', ')}`
    : outcome.status === 'draw' ? `Ничья: ${leaders.map(s => esc(s.name)).join(', ')}`
      : outcome.status === 'provisional' ? 'Предварительный результат — бой продолжается или границы неполны'
        : 'Победитель неизвестен: не установлен участник с нанесённым боевым уроном';
  const hint = outcome.economyIncluded === false ? '<p class="dim">Экономика неполна или недоступна: результат рассчитан только по урону (70% нанесённый, 30% штраф за полученный).</p>' : '';
  return `<h3 title="35% — нанесённый боевой урон; 15% — штраф за полученный; 50% — штраф за стоимость собственных потерь. Сравниваются доли от общих показателей боя.">${label}</h3>${hint}<div class="table-scroll"><table class="load"><thead><tr><th>Сторона</th><th class="n">Нанесла</th><th class="n">Получила</th></tr></thead><tbody>${sides.map(s => `<tr><td>${esc(s.name)}${s.kind === 'player' ? ' · без фракции' : s.kind === 'unknown' ? ' · фракция неизвестна' : ''}</td><td class="n">${num(s.dealt)}</td><td class="n">${num(s.taken)}</td></tr>`).join('')}</tbody></table></div>`;
}

export function mvpHtml(mvp) {
  if (!mvp || mvp.status === 'none' || mvp.status === 'provisional' && !(mvp.players || []).length) return '<p class="dim">MVP: не установлен участник с нанесённым боевым уроном.</p>';
  const names = (mvp.players || []).map(p => esc(p.name)).join(', ');
  const label = mvp.status === 'provisional' ? 'Кандидаты в MVP' : mvp.status === 'tie' ? 'MVP — равные результаты' : mvp.status === 'insufficient' ? 'Кандидаты в MVP — не хватает данных попаданий для разрешения равенства' : 'MVP';
  return `<p><b>${label}: ${names}</b></p>`;
}

export function accuracyText(player) {
  return player.accuracyStatus === 'ok' && Number.isFinite(player.accuracy)
    ? `${player.accuracy.toLocaleString('ru-RU', { maximumFractionDigits: 1 })}%` : '—';
}

export function accuracyDescription(player) {
  const labels = { unavailable: 'Нет точной телеметрии в старых записях или запись прервалась', ambiguous: 'Стрелявший грид участвовал одновременно в нескольких боях', incomplete: 'Неполная запись запусков снарядов', no_shots: 'Нет зарегистрированных снарядов', ok: 'Доля снарядов с прямым попаданием во врага/нейтрала; один снаряд считается один раз' };
  return labels[player.accuracyStatus] || 'Недостаточно данных';
}
