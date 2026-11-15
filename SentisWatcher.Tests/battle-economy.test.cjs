const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../SentisWatcher/Web/battle-economy.js'), 'utf8');
const modulePromise = import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const names = JSON.parse(fs.readFileSync(path.join(__dirname, '../SentisWatcher/Web/component-names.json'), 'utf8'));
const component = (id='SteelPlate', lost=2, price=13) => ({id,lost,unitPrice:price,unpriced:price===null?lost:0,priceStatus:price===null?'unavailable':'ok',knownCredits:price===null?'0':String(lost*price),credits:price===null?null:String(lost*price)});
const data = () => ({status:'ok', affectedBlocks:2,damagedBlocks:1,destroyedBlocks:1,lostComponents:2,unpricedComponents:0,knownCredits:'26',credits:'26',sides:[{id:'l1',name:'[AAA] Alpha',kind:'faction',affectedBlocks:2,damagedBlocks:1,destroyedBlocks:1,lostComponents:2,unpricedComponents:0,credits:'26',knownCredits:'26',components:[component()]}]});

test('credit strings preserve values beyond Number.MAX_SAFE_INTEGER and reject arbitrary text', async () => {
  const {creditsText} = await modulePromise;
  assert.equal(creditsText('10000000000000000000').replace(/\D/g,''), '10000000000000000000');
  assert.equal(creditsText('0'),'0 SC');
  for (const value of [0, null, '1e20', '-1', '1<script>', '1\n', '9'.repeat(30)]) assert.equal(creditsText(value),'—');
});
test('component names use official RU/EN translations and unknown mods are explicit', async () => {
  const {componentLabel,economyHtml} = await modulePromise;
  assert.equal(componentLabel('SteelPlate','ru',names),'Стальная пластина');
  assert.equal(componentLabel('SteelPlate','en',names),'Steel Plate');
  assert.match(componentLabel('UnknownMod','ru',names),/Неизвестный компонент \(UnknownMod\)/);
  assert.match(economyHtml(data(),{names}),/Стальная пластина/);
  assert.match(economyHtml(data(),{names,lang:'en'}),/Steel Plate/);
});
test('one matrix has factions as columns, component quantities as rows and credits as the final row', async () => {
  const {economyHtml} = await modulePromise; const e=data();
  e.sides.push({...e.sides[0],id:'l2',name:'Beta',credits:'99',components:[component('SteelPlate',3,20),component('Computer',1,39)]});
  const html=economyHtml(e,{names});
  assert.equal((html.match(/<table\b/g)||[]).length,1);
  assert.match(html,/<thead><tr><th scope="col">Компонент<\/th><th scope="col">\[AAA\] Alpha<\/th><th scope="col">Beta<\/th>/);
  assert.match(html,/Стальная пластина<\/th><td>2<\/td><td>3<\/td>/);
  assert.match(html,/Компьютер<\/th><td>0<\/td><td>1<\/td>/);
  assert.match(html,/<tfoot><tr><th scope="row">Потеряно в кредитах, SC<\/th><td>26 SC<\/td><td>99 SC<\/td><\/tr><\/tfoot>/);
  assert.doesNotMatch(html,/<select|economy-summary|economy-components|Повреждено, не разрушено/);
  assert.match(economyHtml(e,{lang:'en'}),/Credits lost, SC/);
});
test('missing telemetry is not displayed as zero cost, and unknown prices/coverage give lower bounds', async () => {
  const {economyHtml} = await modulePromise;
  for(const e of [null,{status:'unavailable'}]) { const html=economyHtml(e); assert.match(html,/Нет достаточной телеметрии/); assert.doesNotMatch(html,/0 SC/); }
  const e=data(); e.status='partial'; e.credits=null;
  assert.match(economyHtml(e),/≥ 26 SC/); assert.match(economyHtml(e),/Учёт неполный/);
  e.status='ok'; e.unpricedComponents=2; e.sides[0].credits=null; e.sides[0].components=[component('Mod',2,null)];
  const html=economyHtml(e); assert.match(html,/Цена неизвестна/); assert.match(html,/не подменена нулём/); assert.match(html,/≥ 26 SC/);
});
test('an absent component means zero only with complete coverage and an untruncated list', async () => {
  const {economyHtml}=await modulePromise; const e=data();
  e.sides.push({...e.sides[0],id:'l2',name:'Beta',components:[]});
  assert.match(economyHtml(e),/SteelPlate\)<\/th><td>2<\/td><td>0<\/td>/);
  e.status='partial'; assert.match(economyHtml(e),/SteelPlate\)<\/th><td>2<\/td><td>—<\/td>/);
  e.status='ok'; e.truncated=true; assert.match(economyHtml(e),/SteelPlate\)<\/th><td>2<\/td><td>—<\/td>/);
});
test('all-side helper sums integer money exactly and retains mixed price information for callers', async () => {
  const {componentTotals} = await modulePromise; const e=data();
  e.sides.push({...e.sides[0],id:'l2',name:'Beta',components:[component('SteelPlate',3,20),component('Mod',1,null)]});
  const total=componentTotals(e.sides).find(c=>c.id==='SteelPlate');
  assert.equal(total.lost,5); assert.equal(total.credits,'86'); assert.equal(total.priceStatus,'mixed');
});
test('names and component codes never inject HTML', async () => {
  const {economyHtml}=await modulePromise; const e=data();
  e.sides[0].id='"><img src=x>'; e.sides[0].name='<img src=x onerror=alert(1)>';
  e.sides[0].components=[component('<svg/onload=x>')];
  const html=economyHtml(e); assert.doesNotMatch(html,/<img|<svg/); assert.match(html,/&lt;img/); assert.match(html,/&lt;svg/);
});
test('recording interruptions remain explicit without inventing block counters', async () => {
  const {economyHtml}=await modulePromise; const e=data();
  e.status='partial'; e.recordingInterrupted=true; e.damagedBlocks=null; e.destroyedBlocks=null;
  const html=economyHtml(e); assert.match(html,/Запись прерывалась/); assert.match(html,/≥ 26 SC/);
});
test('matrix row limit is explicit and does not recompute the recorded credit footer from omitted rows', async () => {
  const {economyHtml}=await modulePromise; const e=data();
  e.sides[0].components=Array.from({length:257},(_,i)=>component('Mod'+i));
  const html=economyHtml(e); assert.equal((html.match(/<th scope="row" title=/g)||[]).length,256);
  assert.match(html,/Показаны первые 256/); assert.match(html,/<tfoot>.*26 SC/);
});
