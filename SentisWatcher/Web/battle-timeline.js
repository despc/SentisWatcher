const escapeText = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const number = value => Number(value || 0).toLocaleString('ru-RU', { maximumFractionDigits: 1 });
const shortNumber = value => value >= 1e6 ? number(value / 1e6) + ' млн' : value >= 1e3 ? number(value / 1e3) + ' тыс' : number(value);

export function jumpDescription(jump, formatTime) {
  const pilots = (jump.actors || []).filter(a => a.role === 'pilot').map(a => a.name);
  const passengers = (jump.actors || []).filter(a => a.role !== 'pilot').map(a => a.name);
  const direction = { arrival: 'прибытие', departure: 'уход', jump: 'прыжок' }[jump.direction] || 'прыжок';
  return `${formatTime(jump.t)} · ${direction}\nКто: ${pilots.join(', ') || (passengers.length ? passengers.join(', ') + ' (пассажиры)' : 'не записано')}` +
    (pilots.length && passengers.length ? '\nПассажиры: ' + passengers.join(', ') : '') +
    `\nГрид: ${jump.grid || jump.gridId || 'неизвестно'}\nДальность: ${number(jump.distance / 1000)} км`;
}

export function timelineLayout(report, width = 800) {
  const left = 76, right = Math.max(320, width) - 20, top = 20, bottom = 148, lane = 182, axis = 215;
  const points = report.timeline || [], jumps = report.jumps || [];
  const from = Number(report.timelineFrom ?? report.summary.start);
  const to = Math.max(from + 2000, Number(report.timelineTo ?? report.summary.end + 2000));
  const x = t => left + Math.min(1, Math.max(0, (t - from) / (to - from))) * (right - left);
  const max = Math.max(1, ...points.map(p => Number(p.damage) || 0));
  const magnitude = 10 ** Math.floor(Math.log10(max));
  const ceiling = Math.ceil(max / magnitude) * magnitude;
  const bars = points.map((p, index) => {
    const total = Math.max(0, Number(p.damage) || 0), combat = Math.min(total, Math.max(0, Number(p.combatDamage) || 0));
    const h = total > 0 ? Math.max(1, total / ceiling * (bottom - top)) : 0;
    const x0 = x(p.t), x1 = x(p.t + p.width), barWidth = Math.max(0, x1 - x0 - Math.min(1, (x1 - x0) / 4));
    return { index, x: x0, width: barWidth, y: bottom - h, height: h, combatHeight: total > 0 ? h * combat / total : 0 };
  });
  // Multiple jumps within a few pixels share one hit target and one tooltip: no events hidden under other markers.
  const clusters = new Map();
  for (const jump of jumps) {
    const position = x(jump.t), key = Math.round((position - left) / 14);
    if (!clusters.has(key)) clusters.set(key, { x: position, jumps: [] });
    clusters.get(key).jumps.push(jump);
  }
  return { width: Math.max(320, width), height: 240, left, right, top, bottom, lane, axis, from, to, ceiling, bars, clusters: [...clusters.values()] };
}

export function timelineSvg(report, width, formatTime) {
  const l = timelineLayout(report, width), lines = [], labels = [];
  for (let i = 0; i <= 4; i++) {
    const y = l.bottom - (l.bottom - l.top) * i / 4;
    lines.push(`<line x1="${l.left}" x2="${l.right}" y1="${y}" y2="${y}" stroke="#354151"/>`);
    labels.push(`<text x="${l.left - 8}" y="${y + 4}" text-anchor="end">${escapeText(shortNumber(l.ceiling * i / 4))}</text>`);
  }
  const ticks = l.width < 550 ? 2 : 4;
  for (let i = 0; i <= ticks; i++) {
    const x = l.left + (l.right - l.left) * i / ticks, t = l.from + (l.to - l.from) * i / ticks;
    labels.push(`<text x="${x}" y="${l.axis + 15}" text-anchor="${i === 0 ? 'start' : i === ticks ? 'end' : 'middle'}">${escapeText(formatTime(t).slice(11))}</text>`);
  }
  const bars = l.bars.map(b => `<g data-bucket="${b.index}" tabindex="0" role="img" aria-label="${escapeText(formatTime(report.timeline[b.index].t) + ' · урон ' + number(report.timeline[b.index].damage))}">
    <rect x="${b.x}" y="${b.y}" width="${b.width}" height="${b.height}" fill="#687d91"/>
    <rect x="${b.x}" y="${l.bottom - b.combatHeight}" width="${b.width}" height="${b.combatHeight}" fill="#5fb3f9"/>
    <rect x="${b.x}" y="${l.top}" width="${b.width}" height="${l.bottom - l.top}" fill="transparent"/>
  </g>`).join('');
  const markers = l.clusters.map((c, i) => {
    const description = c.jumps.map(j => jumpDescription(j, formatTime)).join('\n\n');
    return `<g class="battle-jump" data-jump="${i}" tabindex="0" role="img" aria-label="${escapeText(description)}">
      <line x1="${c.x}" x2="${c.x}" y1="${l.top}" y2="${l.lane}" stroke="#c39bd3" stroke-dasharray="3 4" opacity="0.6"/>
      <path d="M ${c.x} ${l.lane - 7} l 7 7 l -7 7 l -7 -7 Z" fill="#c39bd3"/>
      <rect x="${c.x - 9}" y="${l.lane - 12}" width="18" height="24" fill="transparent"/>
      <title>${escapeText(description)}</title></g>`;
  }).join('');
  return { layout: l, svg: lines.join('') + bars + `<line x1="${l.left}" x2="${l.right}" y1="${l.lane}" y2="${l.lane}" stroke="#4b3b59"/>` +
    `<text x="${l.left - 8}" y="${l.lane + 4}" text-anchor="end">Прыжки</text>` + markers + labels.join('') +
    (!report.timeline?.some(p => p.damage > 0) ? `<text x="${(l.left + l.right) / 2}" y="80" text-anchor="middle">Нет урона</text>` : '') };
}

export function mountBattleTimeline(host, report, formatTime) {
  const svg = host.querySelector('svg'), tooltip = host.querySelector('[role="tooltip"]');
  let layout, disposed = false;
  function hide() { tooltip.hidden = true; }
  function show(text, x, y) {
    tooltip.textContent = text; tooltip.hidden = false;
    const r = tooltip.getBoundingClientRect();
    tooltip.style.left = Math.max(8, Math.min(innerWidth - r.width - 8, x + 14)) + 'px';
    tooltip.style.top = Math.max(8, Math.min(innerHeight - r.height - 8, y + 14)) + 'px';
  }
  function redraw() {
    if (disposed) return;
    const rendered = timelineSvg(report, host.clientWidth || 800, formatTime);
    layout = rendered.layout;
    svg.setAttribute('viewBox', `0 0 ${layout.width} ${layout.height}`);
    svg.innerHTML = rendered.svg;
    hide();
  }
  function describeBucket(index) {
    const p = report.timeline[index];
    return `${formatTime(p.t)} — ${formatTime(p.t + p.width).slice(11)}\nВесь урон: ${number(p.damage)}\nБоевой: ${number(p.combatDamage)}\nПрочий: ${number(p.damage - p.combatDamage)}`;
  }
  function handle(event) {
    const target = event.target.closest('[data-jump], [data-bucket]');
    const r = svg.getBoundingClientRect();
    let text;
    if (target?.hasAttribute('data-jump')) text = layout.clusters[Number(target.dataset.jump)].jumps.map(j => jumpDescription(j, formatTime)).join('\n\n');
    else {
      let index = target?.hasAttribute('data-bucket') ? Number(target.dataset.bucket) : -1;
      if (index < 0 && event.type === 'pointermove') {
        const x = (event.clientX - r.left) / r.width * layout.width;
        const time = layout.from + (x - layout.left) / (layout.right - layout.left) * (layout.to - layout.from);
        index = (report.timeline || []).findIndex(p => time >= p.t && time < p.t + p.width);
      }
      if (index < 0) { hide(); return; }
      text = describeBucket(index);
    }
    const rect = target?.getBoundingClientRect();
    show(text, event.clientX ?? rect?.x ?? r.x, event.clientY ?? rect?.y ?? r.y);
  }
  redraw();
  const observer = new ResizeObserver(redraw); observer.observe(host);
  svg.addEventListener('pointermove', handle); svg.addEventListener('focusin', handle);
  svg.addEventListener('pointerleave', hide); svg.addEventListener('focusout', hide);
  window.addEventListener('scroll', hide, true);
  return () => {
    disposed = true; observer.disconnect(); window.removeEventListener('scroll', hide, true); hide();
  };
}
