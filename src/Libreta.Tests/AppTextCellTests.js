const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.js'), 'utf8');
const names = ['escapeHtml', 'columnStyle', 'tableCellHtml', 'valuesRowHtml', 'exactDiffersFromDisplay',
  'selectedRangeSummary'];
const source = names.map((name) => {
  const text = app.match(new RegExp(`function ${name}\\([\\s\\S]*?\\n\\}`))?.[0];
  assert.ok(text, `app.js must define ${name}`);
  return text;
}).join('\n');
const context = vm.createContext({
  payload: { units: { '': { scale: 1, prefix: '', suffix: '' } } },
  rangeCells: [],
  selectedRangeCells: () => context.rangeCells,
  selectedCoordinates: () => context.rangeCells,
  displayedExactValue: (cell) => cell.exact
});
vm.runInContext(source, context);
const tableCellHtml = vm.runInContext('tableCellHtml', context);
const valuesRowHtml = vm.runInContext('valuesRowHtml', context);
const selectedRangeSummary = vm.runInContext('selectedRangeSummary', context);

const markup = '<img src=x onerror="alert(1)"> R&D';
const textCell = { kind: 'fact', text: true, display: markup, exact: markup };

test('a text cell in the table shows its markup as text', () => {
  // Act
  const html = tableCellHtml(textCell, { kind: 'period' }, 0);

  // Assert
  assert.match(html, />&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt; R&amp;D<\/td>$/);
  assert.doesNotMatch(html, /<img/);
});

test('a text cell in the inspector\'s values shows its markup as text', () => {
  // Act
  const html = valuesRowHtml({ label: 'FY2025' }, textCell);

  // Assert
  assert.match(html, /<td>&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt; R&amp;D<\/td>/);
  assert.doesNotMatch(html, /<img/);
});

test('a range sums numbers but not text that reads as one', () => {
  // Arrange
  context.rangeCells = [
    { exact: 5, units: '' },
    { exact: '2024', text: true, units: '' }
  ];

  // Act
  const summary = selectedRangeSummary();

  // Assert
  assert.equal(summary.cellCount, 2);
  assert.equal(summary.numericCount, 1);
  assert.deepEqual([...summary.groups.get('')], [5]);
});