const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const app = loadApp();
app.set({
  payload: {
    units: {
      '': { scale: 1, prefix: '', suffix: '' },
      basis_points: { scale: 10000, prefix: '~', suffix: ' bp' },
      share: { scale: 100, prefix: '', suffix: '' }
    }
  }
});

test('an exact value is shown in its unit\'s scale with the unit\'s suffix', () => {
  assert.equal(app.exactNumberHtml('0.0125', 'basis_points'), '125.00 bp');
  assert.equal(app.exactNumberHtml('0.125', 'share'), '12.50');
});

test('an exact value in an unscaled unit is shown as it is', () => {
  assert.equal(app.exactNumberHtml('1234.5', ''), '1,234.5');
});

test('a cell shows its exact value only when the server marks it rounded', () => {
  assert.equal(app.exactDiffersFromDisplay({ display: '~125 bp', exact: '0.01251', rounded: true }), true);
  assert.equal(app.exactDiffersFromDisplay({ display: '-', exact: '0', rounded: false }), false);
  assert.equal(app.exactDiffersFromDisplay({ display: '2026-06-30', exact: '2026-06-30', rounded: false }), false);
});

test('an unresolved cell carries no exact value and does not differ from its display', () => {
  const unresolved = { display: '', units: '', contra: true };
  assert.equal(app.exactDiffersFromDisplay(unresolved), false);
});

test('copy exact and range summary use displayExact from the server when present', () => {
  const cells = [
    { display: '100', exact: '100', units: '' },
    { display: '(40)', exact: '40', displayExact: '-40', contra: true, units: '' }
  ];
  app.set({
    payload: {
      units: { '': { scale: 1, prefix: '', suffix: '' } },
      columns: [{ label: '2025' }],
      rows: [
        { label: 'Revenue', cells: [cells[0]] },
        { label: 'Expense', cells: [cells[1]] }
      ]
    }
  });
  app.selectedRangeCells = () => cells;
  app.selectedCoordinates = () => cells;

  assert.equal(app.coordinateText({ index: 0, periodIndex: 0 }, true), '100');
  assert.equal(app.coordinateText({ index: 1, periodIndex: 0 }, true), '-40');
  const summary = app.selectedRangeSummary();
  assert.deepEqual([...summary.groups.get('')], [100, -40]);
});
