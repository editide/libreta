const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

function pressOnHeader(key, periodIndex, ctrlKey = false) {
  const selected = [];
  const app = loadApp();
  app.set({
    payload: { columns: [{}, {}, {}, {}, {}], rows: [{}, { cells: [{}] }, { cells: [{}] }, {}] }
  });
  app.extendCellRangeByKey = () => false;
  app.selectColumn = (index) => selected.push(index);
  app.selectRow = (row) => selected.push(['row', row]);
  app.inspectCell = (row, period) => selected.push(['cell', row, period]);
  app.handleHeaderKey({ key, ctrlKey, metaKey: false, shiftKey: false, preventDefault() {} }, periodIndex);
  return selected;
}

test('ctrl+arrow on a column header jumps to the first or last column', () => {
  assert.deepEqual(pressOnHeader('ArrowLeft', 2, true), [0]);
  assert.deepEqual(pressOnHeader('ArrowRight', 2, true), [4]);
});

test('a plain arrow on a column header moves one column', () => {
  assert.deepEqual(pressOnHeader('ArrowLeft', 2), [1]);
  assert.deepEqual(pressOnHeader('ArrowRight', 2), [3]);
});

test('ctrl+home on a column header goes to the first line label and ctrl+end to the last line in the last period', () => {
  assert.deepEqual(pressOnHeader('Home', 2, true), [['row', 1]]);
  assert.deepEqual(pressOnHeader('End', 2, true), [['cell', 2, 4]]);
});
