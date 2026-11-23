const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const app = loadApp();

const sensitivity = {
  inputs: [
    { label: 'Growth', displays: ['2%', '3%', '4%'] },
    { label: 'WACC', displays: ['8%', '9%'] }
  ],
  cells: [
    [{ exact: '10', valid: true }, { exact: '20', valid: true }, { valid: false }],
    [{ exact: '-5', valid: true }, { exact: '30', valid: true }, { exact: '40', valid: true }]
  ]
};

test('sensitivity chart series cross the selected cell', () => {
  const series = app.sensitivityChartSeries(sensitivity, 1, 1);

  assert.deepEqual(Array.from(series.row.labels), ['2%', '3%', '4%']);
  assert.deepEqual(Array.from(series.row.values), [-5, 30, 40]);
  assert.equal(series.row.selectedIndex, 1);
  assert.equal(series.row.title, 'WACC 9%');
  assert.deepEqual(Array.from(series.column.labels), ['8%', '9%']);
  assert.deepEqual(Array.from(series.column.values), [20, 30]);
  assert.equal(series.column.selectedIndex, 1);
  assert.equal(series.column.title, 'Growth 3%');
});

test('one-input sensitivities omit the redundant column chart', () => {
  const oneInput = {
    inputs: [sensitivity.inputs[0]],
    cells: [sensitivity.cells[0]]
  };

  const series = app.sensitivityChartSeries(oneInput, 0, 1);

  assert.equal(series.column, null);
});

test('the selected result is the accented chart column', () => {
  const html = app.renderSensitivityChart({
    axisLabel: 'Growth',
    title: 'WACC 9%',
    labels: ['2%', '3%', '4%'],
    displays: ['$10', '$20', '$30'],
    values: [10, 20, 30],
    selectedIndex: 1
  }, 'Equity value', [0, 30]);

  assert.equal((html.match(/sensitivity-chart-bar selected/g) ?? []).length, 1);
  assert.match(html, /sensitivity-chart-bar selected[^>]+><title>3%: \$20<\/title>/);
});

test('paired sensitivity charts share a zero-based vertical domain', () => {
  const domain = app.chartDomain([10, 20, null, -5, 40]);

  assert.deepEqual(Array.from(domain), [-5, 40]);
});

test('sensitivity coordinate labels link only when a view presents the cell', () => {
  const linked = app.sensitivityCoordinateLink('WACC', 'dcf.wacc.wacc', 'Value', 'dcf.wacc');

  assert.match(linked, /data-name="dcf\.wacc\.wacc" data-column-key="Value" data-source-view="dcf\.wacc"/);
  assert.equal(app.sensitivityCoordinateLink('Hidden', 'dcf.hidden', 'Value', null), 'Hidden');
  assert.equal(app.sensitivityCoordinateLink('<Growth>', 'dcf.growth', 'Value', null), '&lt;Growth&gt;');
});

test('the sensitivity subtitle spells out its output units', () => {
  const html = app.sensitivityHeadingHtml({ title: 'Value per share', outputUnits: 'dollars_per_share' });

  assert.equal(html, '<div><h2>Value per share</h2><p>Dollars per share</p></div>');
});

test('a sensitivity without output units has no subtitle', () => {
  const html = app.sensitivityHeadingHtml({ title: 'Value per share', outputUnits: '' });

  assert.equal(html, '<div><h2>Value per share</h2></div>');
});
