const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ctx = {console, Intl, Date};
vm.createContext(ctx);
const code = fs.readFileSync(path.join(__dirname, '../SentisWatcher/Web/battle-timeline.js'), 'utf8');
vm.runInContext(code.replace(/^export /gm, ''), ctx);
const formatTime = t => new Date(t).toISOString().slice(0, 19).replace('T', ' ');
const report = (points = [], jumps = []) => ({summary: {start: 0, end: 10000}, timelineFrom: 0, timelineTo: 120000, timeline: points, jumps});

test('empty, zero and single-hit timelines have finite coordinates and usable height', () => {
  for (const points of [[], [{t:0,width:2000,damage:0,combatDamage:0}], [{t:0,width:2000,damage:30,combatDamage:30}]]) {
    const chart = ctx.timelineSvg(report(points), 800, formatTime);
    assert.doesNotMatch(chart.svg, /NaN|Infinity/);
    assert.equal(chart.layout.height, 240);
    assert.match(chart.svg, /Прыжки/);
    if (points[0]?.damage) assert.ok(chart.layout.bars[0].height > 100);
  }
});

test('bars preserve true time spacing, stay in the plot and show small nonzero damage', () => {
  const r = report([
    {t:0,width:2000,damage:1000000000,combatDamage:900000000},
    {t:100000,width:30000,damage:1,combatDamage:0},
  ]);
  const l = ctx.timelineLayout(r, 380);
  assert.ok(l.bars[1].x > l.bars[0].x + 150);
  assert.ok(l.bars[1].height >= 1);
  for (const b of l.bars) {
    assert.ok(b.x >= l.left && b.x + b.width <= l.right);
    assert.ok(b.y >= l.top && b.y + b.height <= l.bottom + 0.001);
  }
  assert.equal(l.bars[1].combatHeight, 0);
});

test('jump tooltip identifies pilot, passengers, grid, time and distance; close markers retain all jumps', () => {
  const first = {t:1000,grid:'Raven',direction:'departure',distance:20000,actors:[{name:'Pilot',role:'pilot'},{name:'Passenger',role:'passenger'}]};
  const second = {t:1001,grid:'Scout',direction:'arrival',distance:10000,actors:[{name:'Other pilot',role:'pilot'}]};
  const l = ctx.timelineLayout(report([], [first, second]), 800);
  assert.equal(l.clusters.length, 1);
  assert.equal(l.clusters[0].jumps.length, 2);
  const label = ctx.jumpDescription(first, formatTime);
  for (const expected of ['уход','Кто: Pilot','Пассажиры: Passenger','Грид: Raven','20 км','00:00:01']) assert.ok(label.includes(expected), expected);
  assert.match(ctx.timelineSvg(report([], [first, second]), 800, formatTime).svg, /Other pilot/);
});

test('jump and player names are escaped in SVG and missing actors are not guessed', () => {
  const jump = {t:1000,grid:'<img onerror="bad">',distance:0,actors:[{name:'<script>bad</script>',role:'pilot'}]};
  const svg = ctx.timelineSvg(report([], [jump]), 800, formatTime).svg;
  assert.ok(!svg.includes('<img') && !svg.includes('<script>'));
  assert.match(svg, /&lt;img/);
  assert.match(ctx.jumpDescription({t:0,gridId:'999',distance:0}, formatTime), /Кто: не записано/);
});
