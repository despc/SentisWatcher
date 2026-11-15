const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const modulePromise = import('data:text/javascript;base64,' + fs.readFileSync(path.join(__dirname, '../SentisWatcher/Web/battle-results.js')).toString('base64'));

test('winner and factionless side names are escaped and the balance column is absent', async () => {
  const { outcomeHtml } = await modulePromise;
  const html = outcomeHtml({ status: 'winner', leaders: ['p'], sides: [{ id: 'p', name: '<script>evil</script>', kind: 'player', dealt: 200, taken: 30, balance: 170 }] });
  assert.match(html, /Победитель:/);
  assert.doesNotMatch(html, /Победитель по урону/);
  assert.match(html, /без фракции/);
  assert.match(html, /200/); assert.match(html, /30/);
  assert.doesNotMatch(html, /Баланс|170/);
  assert.ok(!html.includes('<script>'));
});

test('draws, incomplete boundaries and unknown factions are not displayed as a confirmed winner', async () => {
  const { outcomeHtml } = await modulePromise;
  assert.match(outcomeHtml({ status: 'draw', leaders: [], sides: [] }), /Ничья/);
  assert.match(outcomeHtml({ status: 'provisional', leaders: [], sides: [] }), /Предварительный/);
  assert.match(outcomeHtml({ status: 'unknown', leaders: [], sides: [] }), /Победитель неизвестен/);
});

test('MVP preserves real ties and unavailable hit tie-breaks instead of choosing an arbitrary player', async () => {
  const { mvpHtml } = await modulePromise;
  assert.match(mvpHtml({ status: 'tie', players: [{ name: '<b>A</b>' }, { name: 'B' }] }), /равные результаты/);
  assert.ok(!mvpHtml({ status: 'winner', players: [{ name: '<b>A</b>' }] }).includes('<b>A</b>'));
  assert.match(mvpHtml({ status: 'insufficient', players: [] }), /не хватает данных/);
});

test('incomplete economics is explicitly marked instead of pretending unknown losses are zero', async () => {
  const { outcomeHtml } = await modulePromise;
  assert.match(outcomeHtml({status:'winner',economyIncluded:false,sides:[]}), /Экономика неполна.*только по урону/);
  assert.doesNotMatch(outcomeHtml({status:'winner',economyIncluded:true,sides:[]}), /Экономика неполна/);
});

test('unknown accuracy is a dash, real zero accuracy is 0%, and physical-round semantics are explicit', async () => {
  const { accuracyText, accuracyDescription } = await modulePromise;
  assert.equal(accuracyText({ accuracy: null, accuracyStatus: 'unavailable' }), '—');
  assert.equal(accuracyText({ accuracy: null, accuracyStatus: 'ambiguous' }), '—');
  assert.equal(accuracyText({ accuracy: 0, accuracyStatus: 'ok' }), '0%');
  assert.equal(accuracyText({ accuracy: 25, accuracyStatus: 'ok' }), '25%');
  assert.match(accuracyDescription({ accuracyStatus: 'ok' }), /один снаряд считается один раз/);
});
