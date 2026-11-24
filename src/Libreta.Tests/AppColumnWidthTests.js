const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const app = loadApp();

test('a column widens to fit its widest number', () => {
  // Arrange
  const rows = [
    { cells: [{ display: '13,513,513' }] },
    { cells: [{ display: '13,210,422,593' }] }
  ];

  // Act
  const [width] = app.valueColumnWidths(1, rows);

  // Assert
  assert.equal(width, 'max(var(--period-width), calc(8.11rem + 2 * var(--space-5)))');
});

test('a column of text or no values keeps the period width', () => {
  // Arrange
  const rows = [
    { kind: 'header' },
    { cells: [{ display: 'A long sentence of prose', text: true }, { display: '' }] }
  ];

  // Act
  const widths = app.valueColumnWidths(2, rows);

  // Assert
  assert.deepEqual([...widths], ['var(--period-width)', 'var(--period-width)']);
});
