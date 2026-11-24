const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

function copy(selection, cellRanges = []) {
  const app = loadApp();
  app.set({
    payload: {
      columns: [{ label: 'FY2024' }, { label: 'FY2025' }],
      rows: [{ label: 'Revenue', cells: [{ display: '1,200' }, { display: '1,500' }] }]
    }
  });
  app.eval('viewState').selection = selection;
  app.selectedCoordinates = () => cellRanges;
  app.selectedRangeTsv = () => 'range';
  let copied = null;
  let prevented = false;
  app.handleGridCopy({
    preventDefault: () => { prevented = true; },
    clipboardData: { setData: (_, text) => { copied = text; } }
  });
  return prevented ? copied : null;
}

test('copying a single cell copies its display value', () => {
  const cell = { index: 0, periodIndex: 1 };
  assert.equal(copy(cell, [cell]), 'range');
});

test('copying a selected line copies its label', () => {
  assert.equal(copy({ index: 0, periodIndex: null }), 'Revenue');
});

test('copying a selected period header copies its label', () => {
  assert.equal(copy({ index: -1, periodIndex: 1 }), 'FY2025');
});

test('copying with nothing selected leaves the browser alone', () => {
  assert.equal(copy(null), null);
});
