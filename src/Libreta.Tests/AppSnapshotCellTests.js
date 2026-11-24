const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const app = loadApp();

const lines = {
  'historical.revenue': { view: 'income', columns: { 2026: 'forecast' } }
};

test('a fragment naming no line asks for no cell', () => {
  assert.equal(app.snapshotCell('#view=income', lines), null);
  assert.equal(app.snapshotCell('', lines), null);
});

test('a cell opens in the first view presenting its line', () => {
  assert.deepEqual({ ...app.snapshotCell('#line=historical.revenue&column=2025', lines) },
    { view: 'income', line: 'historical.revenue', column: '2025' });
});

test('a column another view presents first opens there', () => {
  assert.equal(app.snapshotCell('#line=historical.revenue&column=2026', lines).view, 'forecast');
});

test('a view named in the fragment wins', () => {
  assert.equal(app.snapshotCell('#view=summary&line=historical.revenue&column=2026', lines).view, 'summary');
});

test('a line is named as the command line names it', () => {
  assert.equal(app.snapshotCell('#line=historical/revenue', lines).line, 'historical.revenue');
});

test('a line no view presents names no view', () => {
  assert.equal(app.snapshotCell('#line=historical.margin&column=2025', lines).view, null);
});
