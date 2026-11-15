// Shared with Watcher's private report. Money is decimal text, never a lossy JS Number.
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const validMoney = value => typeof value === 'string' && /^[0-9]{1,29}$/.test(value);
export function creditsText(value, locale = 'ru-RU') {
  return validMoney(value) ? BigInt(value).toLocaleString(locale) + ' SC' : '—';
}
export function componentLabel(id, lang = 'ru', names = {}) {
  const entry = Object.hasOwn(names, id) ? names[id] : null;
  return entry?.[lang] || entry?.en || (lang === 'en' ? 'Unknown component' : 'Неизвестный компонент') + ` (${id})`;
}
export function componentTotals(sides) {
  const totals = new Map();
  for (const side of sides) for (const c of side.components || []) {
    if (!totals.has(c.id)) totals.set(c.id, {id:c.id, lost:0, unpriced:0, known:0n, complete:true, prices:new Set()});
    const t = totals.get(c.id);
    t.lost += c.lost || 0; t.unpriced += c.unpriced || 0;
    if (validMoney(c.knownCredits)) t.known += BigInt(c.knownCredits);
    t.complete &&= validMoney(c.credits);
    if (c.priceStatus === 'mixed') t.prices.add('mixed');
    if (Number.isSafeInteger(c.unitPrice)) t.prices.add(c.unitPrice);
  }
  return [...totals.values()].map(t => ({id:t.id, lost:t.lost, unpriced:t.unpriced,
    knownCredits:t.known.toString(), credits:t.complete ? t.known.toString() : null,
    unitPrice:t.prices.size === 1 && !t.prices.has('mixed') && !t.unpriced ? [...t.prices][0] : null,
    priceStatus:t.unpriced ? 'unavailable' : t.prices.size > 1 || t.prices.has('mixed') ? 'mixed' : 'ok'}));
}
const words = {
  ru: {interrupted:'Запись прерывалась. Число уникальных блоков между сеансами неизвестно и не подменено нулём.',title:'Экономические потери',side:'Фракция / сторона',damaged:'Повреждено, не разрушено',destroyed:'Разрушено',lost:'Утрачено компонентов',cost:'Ущерб, SC',component:'Компонент',quantity:'Количество потерь',price:'Цена за единицу, SC',all:'Все стороны',components:'Потери компонентов',total:'Всего',unknown:'Цена неизвестна',mixed:'Разные цены',unavailable:'Нет достаточной телеметрии компонентов. Для старых боёв потери задним числом не восстанавливаются.',none:'Урон по блокам не зарегистрирован. Разборка не считается уничтожением компонентов.',partial:'Учёт неполный: показаны только подтверждённые потери. Знак ≥ обозначает нижнюю границу стоимости, а не полный ущерб.',unpriced:'Есть компоненты без подтверждённой цены: их стоимость не подменена нулём.',provisional:'Предварительная оценка: бой продолжается или границы неполны.',truncated:'Список компонентов сокращён. Итоги сторон могут включать не показанные строки; полный состав по этой таблице не пересчитывайте.',limit:'Показаны первые 256 типов компонентов.',hint:'Компоненты утерянные в следствии повреждения/разрушения блоков',blocks:'Уникальные блоки, получившие урон за бой; повторные попадания не увеличивают счётчик.',owner:'При смене владельца один блок может учитываться у нескольких сторон; общий итог удаляет такие повторы.',noFaction:'Без фракции',unknownFaction:'Фракция не установлена',empty:'Утрата смонтированных компонентов не зарегистрирована.'},
  en: {interrupted:'Recording was interrupted. Unique block counts across sessions are unknown, not zero.',title:'Economic losses',side:'Faction / side',damaged:'Damaged, not destroyed',destroyed:'Destroyed',lost:'Components lost',cost:'Loss, SC',component:'Component',quantity:'Quantity lost',price:'Unit price, SC',all:'All sides',components:'Component losses',total:'Total',unknown:'Price unknown',mixed:'Multiple prices',unavailable:'Insufficient component telemetry. Losses cannot be reconstructed for older battles.',none:'No block damage recorded. Dismantling is not counted as component destruction.',partial:'Incomplete recording: confirmed losses only. ≥ indicates a lower bound, not the full loss.',unpriced:'Some components have no confirmed price; their value is not replaced with zero.',provisional:'Provisional estimate: battle ongoing or boundaries incomplete.',truncated:'Component list shortened. Side totals may include omitted rows; do not recalculate full totals from this table.',limit:'Showing the first 256 component types.',hint:'Components lost due to block damage/destruction',blocks:'Unique blocks damaged during the battle; repeated hits do not increase this count.',owner:'A block changing ownership may appear under multiple sides; the overall total deduplicates these blocks.',noFaction:'Factionless',unknownFaction:'Faction undetermined',empty:'No loss of mounted components recorded.'},
};
export function economyHtml(economy, {lang = 'ru', names = {}} = {}) {
  const w = words[lang === 'en' ? 'en' : 'ru'], locale = lang === 'en' ? 'en-GB' : 'ru-RU';
  const n = value => Number.isSafeInteger(value) && value >= 0 ? value.toLocaleString(locale) : '—';
  const heading = `<h3>${w.title}</h3>`;
  if (!economy || economy.status === 'unavailable') return heading + `<p class="muted dim small">${w.unavailable}</p>`;
  if (economy.status === 'not_applicable') return heading + `<p class="muted dim small">${w.none}</p>`;
  const partial = economy.status !== 'ok', sides = economy.sides || [];
  const cost = (row, incomplete = partial) => !incomplete && validMoney(row.credits) ? creditsText(row.credits, locale)
    : validMoney(row.knownCredits) ? '≥ ' + creditsText(row.knownCredits, locale) : '—';
  const sideName = s => esc(s.name) + (s.kind === 'player' ? ` <small>· ${w.noFaction}</small>` : s.kind === 'unknown' ? ` <small>· ${w.unknownFaction}</small>` : '');
  const components = componentTotals(sides).sort((a,b) => componentLabel(a.id,lang,names).localeCompare(componentLabel(b.id,lang,names),locale));
  const columns = sides.map(s => new Map((s.components || []).map(c => [c.id, c])));
  const quantity = (column, id) => column.has(id) ? n(column.get(id).lost) : partial || economy.truncated ? '—' : '0';
  const creditsLabel = lang === 'en' ? 'Credits lost, SC' : 'Потеряно в кредитах, SC';
  const rowLimit = lang === 'en' ? 'Showing the first 256 component types. Side totals may include omitted rows.' : 'Показаны первые 256 типов компонентов. Итоги сторон могут включать непоказанные строки.';
  return heading +
    (economy.recordingInterrupted ? `<p class="notice warn">${w.interrupted}</p>` : '') +
    (economy.provisional ? `<p class="notice warn">${w.provisional}</p>` : '') +
    (partial ? `<p class="notice warn">${w.partial}</p>` : '') +
    (economy.unpricedComponents > 0 ? `<p class="muted dim small">${w.unknown}. ${w.unpriced}</p>` : '') +
    (economy.truncated ? `<p class="notice warn">${w.truncated}</p>` : '') +
    `<div class="table-scroll"><table class="load economy-matrix"><thead><tr><th scope="col">${w.component}</th>${sides.map(s => `<th scope="col">${sideName(s)}</th>`).join('')}</tr></thead><tbody>${components.slice(0,256).map(c => `<tr><th scope="row" title="${esc(c.id)}">${esc(componentLabel(c.id,lang,names))}</th>${columns.map(column => `<td>${quantity(column,c.id)}</td>`).join('')}</tr>`).join('') || `<tr><td colspan="${sides.length+1}">${w.empty}</td></tr>`}</tbody><tfoot><tr><th scope="row">${creditsLabel}</th>${sides.map(s => `<td>${cost(s)}</td>`).join('')}</tr></tfoot></table></div>` +
    (components.length > 256 ? `<p class="muted dim small">${rowLimit}</p>` : '') +
    `<p class="muted dim small">${w.hint}</p>`;
}
