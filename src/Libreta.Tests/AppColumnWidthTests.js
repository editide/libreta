const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.js'), 'utf8');
const extract = (name) => app.match(new RegExp(`function ${name}\\([\\s\\S]*?\\n\\}`))?.[0];
const constant = (name) => app.match(new RegExp(`const ${name} = [^;]*;`))?.[0];

function load() {
  const context = vm.createContext({});
  vm.runInContext(`${constant('DISPLAY_GLYPH_EMS')}\n${extract('displayEms')}\n${extract('valueColumnWidths')}`,
    context);
  return vm.runInContext('valueColumnWidths', context);
}

test('a column widens to fit its widest number', () => {
  // Arrange
  const valueColumnWidths = load();
  const rows = [
    { cells: [{ display: '13,513,513' }] },
    { cells: [{ display: '13,210,422,593' }] },
  ];

  // Act
  const [width] = valueColumnWidths(1, rows);

  // Assert
  assert.equal(width, 'max(var(--period-width), calc(8.11rem + 2 * var(--space-5)))');
});

test('a column of text or no values keeps the period width', () => {
  // Arrange
  const valueColumnWidths = load();
  const rows = [
    { kind: 'header' },
    { cells: [{ display: 'A long sentence of prose', text: true }, { display: '' }] },
  ];

  // Act
  const widths = valueColumnWidths(2, rows);

  // Assert
  assert.deepEqual([...widths], ['var(--period-width)', 'var(--period-width)']);
});