const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const ctx = {Intl, Date, console};
vm.createContext(ctx);
const file = require('node:path').join(__dirname, '../SentisWatcher/Web/hydrogen.js');
test('hydrogen charts handle empty/zero points and escape entity names', () => {
  assert.ok(fs.existsSync(file), 'hydrogen UI module is missing');
  vm.runInContext(fs.readFileSync(file, 'utf8'), ctx);
  const ui = ctx.WatcherHydrogen;
  assert.match(ui.render({series:[]}, 1), /Нет записей/);
  const series = [{id:'9007199254740993', name:'<img onerror=x>', latest:[], points:[{t:1000, stored:0, buffer:0, capacity:0, produced:0, consumed:0, tankIn:0, tankOut:0, frozen:false}]}];
  const html = ui.render({series}, 1);
  assert.ok(!html.includes('<img'));
  assert.ok(!html.includes('NaN'));
  assert.ok(html.includes('&lt;img'));
  assert.match(html, /л\/с/);
});
test('history charts preserve real time spacing and break missing-data gaps', () => {
  const html = ctx.WatcherHydrogen.chart([{t:0,v:1},{t:10000,v:2},{t:100000,v:3}], [['v','Stock','#fff']], 0, 100000, 'л');
  assert.ok((html.match(/<polyline/g)||[]).length >= 2);
  assert.match(html, /<title>/);
  assert.ok(!html.includes('NaN'));
});
