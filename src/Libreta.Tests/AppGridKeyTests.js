const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const line = { cells: [{}, {}, {}] };
const rows = [{ kind: 'header' }, line, line, line, { kind: 'spacer' }, line, line, { kind: 'spacer' }];

function pageTarget(index, direction, pageHeight = 60) {
  const app = loadApp();
  app.set({ payload: { rows } });
  return app.pageCellTarget(index, direction, (row) => row * 20, pageHeight);
}

function pressOnCell(key, index, periodIndex, modifiers = {}) {
  const calls = [];
  const app = loadApp();
  app.set({ payload: { columns: [{}, {}, {}], rows } });
  app.handleHeaderKey = () => calls.push(['header']);
  app.extendCellRangeByKey = () => false;
  app.selectRow = (row) => calls.push(['row', row]);
  app.inspectCell = (row, period) => calls.push(['cell', row, period]);
  app.pageSelection = (...args) => calls.push(['page', ...args]);
  app.moveSelection = () => calls.push(['move']);
  app.movePeriod = () => calls.push(['period']);
  app.handleGridKey({ key, ctrlKey: false, shiftKey: false, preventDefault() {}, ...modifiers }, index, periodIndex);
  return calls;
}

test('a page moves to the furthest line that starts within one page', () => {
  assert.equal(pageTarget(1, 1), 3);
  assert.equal(pageTarget(3, 1), 6);
  assert.equal(pageTarget(6, -1), 3);
});

test('a page shorter than a line still moves one line', () => {
  assert.equal(pageTarget(1, 1, 10), 2);
});

test('a page stops at the first and last lines', () => {
  assert.equal(pageTarget(6, 1), 6);
  assert.equal(pageTarget(1, -1), 1);
});

test('page down and page up page the selection, extending it with shift', () => {
  assert.deepEqual(pressOnCell('PageDown', 2, 1), [['page', 2, 1, 1, false]]);
  assert.deepEqual(pressOnCell('PageUp', 2, -1, { shiftKey: true }), [['page', 2, -1, -1, true]]);
});

test('ctrl+home goes to the first line label and ctrl+end to the last line in the last period', () => {
  assert.deepEqual(pressOnCell('Home', 3, 1, { ctrlKey: true }), [['row', 1]]);
  assert.deepEqual(pressOnCell('End', 2, -1, { ctrlKey: true }), [['cell', 6, 2]]);
});

test('home and end without ctrl stay on the line', () => {
  assert.deepEqual(pressOnCell('Home', 3, 1), [['row', 3]]);
  assert.deepEqual(pressOnCell('End', 3, -1), [['cell', 3, 2]]);
});

test('alt with an arrow is left to the browser for back and forward', () => {
  let prevented = false;
  const preventDefault = () => { prevented = true; };

  const calls = [...pressOnCell('ArrowLeft', 2, 1, { altKey: true, preventDefault }),
    ...pressOnCell('ArrowRight', -1, 1, { altKey: true, preventDefault })];

  assert.deepEqual(calls, []);
  assert.equal(prevented, false);
});

test('arrow keys move focus and refresh the inspector without rebuilding the table', () => {
  const focused = [];
  let panelUpdates = 0;
  const app = loadApp();
  app.set({
    payload: { columns: [{}, {}, {}], rows },
    selection: { index: 1, periodIndex: 0 },
    cellRanges: [],
    sensitivitySelection: null,
    checksExpanded: false
  });
  app.render = () => assert.fail('moving focus rebuilt the table');
  app.syncGridSelection = () => {};
  app.highlightTableCalculation = () => {};
  app.renderPanel = () => { panelUpdates++; };
  app.focusGridCell = () => {
    const current = app.eval('selection');
    focused.push([current.index, current.periodIndex]);
  };
  for (const key of ['ArrowRight', 'ArrowDown']) {
    const { index, periodIndex } = app.eval('selection');
    app.handleGridKey({ key, preventDefault() {} }, index, periodIndex);
  }
  assert.deepEqual(focused, [[1, 1], [2, 1]]);
  assert.equal(panelUpdates, 2);
});
