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

test('an unresolved cell, which carries no exact value, neither differs from its display nor flips sign', () => {
  const unresolved = { display: '', units: '', contra: true };
  assert.equal(app.exactDiffersFromDisplay(unresolved), false);
  assert.equal(app.displayedExactValue(unresolved), undefined);
});
