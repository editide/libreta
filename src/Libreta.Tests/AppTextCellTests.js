const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const app = loadApp();
app.set({ payload: { units: { '': { scale: 1, prefix: '', suffix: '' } } } });

const markup = '<img src=x onerror="alert(1)"> R&D';
const textCell = { kind: 'fact', text: true, display: markup, exact: markup };

test('a text cell in the table shows its markup as text', () => {
  const html = app.tableCellHtml(textCell, { kind: 'period' }, 0);

  assert.match(html, />&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt; R&amp;D<\/td>$/);
  assert.doesNotMatch(html, /<img/);
});

test('a text cell in the inspector\'s values shows its markup as text', () => {
  const html = app.valuesRowHtml({ label: 'FY2025' }, textCell);

  assert.match(html, /<td>&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt; R&amp;D<\/td>/);
  assert.doesNotMatch(html, /<img/);
});

test('a range sums numbers but not text that reads as one', () => {
  const cells = [
    { exact: 5, units: '' },
    { exact: '2024', text: true, units: '' }
  ];
  app.selectedRangeCells = () => cells;
  app.selectedCoordinates = () => cells;

  const summary = app.selectedRangeSummary();

  assert.equal(summary.cellCount, 2);
  assert.equal(summary.numericCount, 1);
  assert.deepEqual([...summary.groups.get('')], [5]);
});
