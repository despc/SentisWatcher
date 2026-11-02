// No third-party chart dependency. All entity text is escaped; time spacing is the recorded UTC timestamp.
(function(root) {
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const num = n => Number(n || 0).toLocaleString('ru-RU', {maximumFractionDigits:2});
  const liters = n => num(n) + ' л';
  const rate = n => num(n) + ' л/с';
  const time = t => new Date(t).toLocaleString('ru-RU');
  const roles = {tank:'Бак',engine:'Водородный двигатель',generator:'Выработка',consumer:'Потребитель',thrusters:'Движители'};
  function chart(points, lines, from, to, unit) {
    if (!points.length) return '<div class="dim">Нет записей</div>';
    from = from ?? points[0].t; to = Math.max(from + 1, to ?? points[points.length-1].t);
    let max = 1;
    for (const p of points) for (const [key] of lines) max = Math.max(max, Number(p[key]) || 0);
    const x = p => 68 + 710 * Math.max(0, Math.min(1, (p.t-from)/(to-from)));
    const tick = t => to-from >= 86400000 ? new Date(t).toLocaleString('ru-RU', {day:'2-digit',month:'2-digit',hour:'2-digit',minute:'2-digit'}) : new Date(t).toLocaleTimeString('ru-RU');
    const y = v => 132 - 112 * (Number(v)||0) / max;
    let paths = '';
    for (const [key,label,color] of lines) {
      let part = [], previous = null;
      const flush = () => { if (part.length) paths += `<polyline fill="none" stroke="${color}" stroke-width="1.8" points="${part.join(' ')}"/>`; part=[]; };
      // A long scan can take >10 s. Do not draw invented continuity through a recording outage.
      const deltas = points.slice(1).map((p,i)=>p.t-points[i].t).filter(d=>d>0).sort((a,b)=>a-b);
      const interval = deltas.length ? deltas[Math.floor((deltas.length-1)/2)] : 10000;
      for (const p of points) {
        if (previous != null && p.t-previous > Math.max(30000, interval*3)) flush();
        const px=x(p).toFixed(2), py=y(p[key]).toFixed(2);
        part.push(`${px},${py}`);
        paths += `<circle cx="${px}" cy="${py}" r="2.6" fill="${color}"><title>${esc(time(p.t)+' · '+label+': '+num(p[key])+' '+unit+(p.frozen?' · заморожен':''))}</title></circle>`;
        previous=p.t;
      }
      flush();
    }
    return `<div class="h2-legend">${lines.map(([,label,color])=>`<span style="color:${color}">● ${esc(label)}</span>`).join('')}</div><svg class="h2-chart" viewBox="0 0 800 165" role="img" aria-label="${esc(lines.map(l=>l[1]).join(', '))}">
      <path d="M68 20V132H778" fill="none" stroke="var(--line)"/><path d="M68 76H778" stroke="var(--line)" stroke-dasharray="3 5"/>
      <text x="63" y="24" text-anchor="end">${esc(num(max))}</text><text x="63" y="135" text-anchor="end">0</text><text x="68" y="12">${esc(unit)}</text>
      ${paths}<text x="68" y="155">${esc(tick(from))}</text><text x="778" y="155" text-anchor="end">${esc(tick(to))}</text></svg>`;
  }
  function render(data, hours, error='') {
    const controls = `<div class="h2-heading"><h3>Водород</h3><label>период <select id="h2range">${[[1,'1 час'],[6,'6 часов'],[24,'24 часа'],[168,'7 дней']].map(([h,t])=>`<option value="${h}" ${Number(hours)===h?'selected':''}>${t}</option>`).join('')}</select></label></div>`;
    if (error) return controls + `<div class="state bad">${esc(error)}</div>`;
    if (!data?.series?.length) return controls + '<div class="dim">Нет записей водорода за выбранный период.</div>';
    return controls + data.series.map(s => {
      const p=s.points[s.points.length-1];
      const previous=s.points.length>1?s.points[s.points.length-2]:null;
      const delta=previous ? p.stored+p.buffer-previous.stored-previous.buffer : null;
      return `<section class="h2-grid"><h4>${esc(s.name)} ${p.frozen?'<span class="state none">заморожен</span>':''}</h4>
        <div class="h2-stats"><span>Баки <b>${liters(p.stored)}</b> / ${liters(p.capacity)}</span><span>Двигатели <b>${liters(p.buffer)}</b></span><span class="h2-produced">Выработка <b>${rate(p.produced)}</b></span><span class="h2-consumed">Расход <b>${rate(p.consumed)}</b></span></div>
        <div class="dim">${esc(time(p.t))}${delta!==null?' · Δ запас '+(delta>0?'+':'')+liters(delta)+' за '+num((p.t-previous.t)/1000)+' с':''}</div>
        ${chart(s.points, [['stored','В баках','#6ab5ff'],['buffer','В двигателях','#bd9bff']],data.from,data.to,'л')}
        ${chart(s.points, [['produced','Выработка','#59d99b'],['consumed','Сжигание / потребление','#ff8c82'],['tankIn','Вход в баки','#6ab5ff'],['tankOut','Выход из баков','#e7bd63']],data.from,data.to,'л/с')}
        <div class="h2-blocks"><table class="load"><tr><th>Блок</th><th>Роль</th><th class="n">Запас, л</th><th class="n">Выработка, л/с</th><th class="n">Расход, л/с</th><th class="n">Вход, л/с</th><th class="n">Выход, л/с</th></tr>
        ${(s.latest||[]).map(b=>`<tr><td class="name" title="${esc(b.id)}">${esc(b.name)}</td><td>${esc(roles[b.role]||b.role)}</td><td class="n">${num(b.stored+b.buffer)}</td><td class="n">${num(b.produced)}</td><td class="n" title="${b.role==='engine'?'По текущей электрической мощности двигателя':''}">${num(b.consumed)}</td><td class="n">${num(b.input)}</td><td class="n">${num(b.output)}</td></tr>`).join('')}</table></div></section>`;
    }).join('') + '<div class="dim h2-note">л/с симуляции — мгновенные показания, не накопленные счётчики. Вход/выход баков — перекачка, не сжигание. Скачок запаса сам по себе не определяет причину списания. Движители сгруппированы по общему газовому входу.</div>';
  }
  root.WatcherHydrogen={chart,render,liters};
})(globalThis);
