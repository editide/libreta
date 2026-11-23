const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

function press(pointerType) {
  const calls = { prevented: false, selected: false };
  const app = loadApp();
  app.validCellRegion = () => true;
  app.validCellCoordinate = () => false;
  app.selectInGrid = () => { calls.selected = true; };
  app.syncGridSelection = () => {};
  app.enterInspector = () => {};
  app.beginCellRangePointer({
    pointerType, button: 0, isPrimary: true, pointerId: 1, clientX: 0, clientY: 0,
    ctrlKey: false, metaKey: false, shiftKey: false,
    preventDefault() { calls.prevented = true; },
    currentTarget: { setPointerCapture() {}, addEventListener() {} }
  }, 0, 0);
  return calls;
}

test('a touch leaves the gesture to the browser so the table scrolls', () => {
  const calls = press('touch');

  assert.equal(calls.prevented, false);
  assert.equal(calls.selected, false);
});

test('a mouse press still starts a range selection', () => {
  const calls = press('mouse');

  assert.equal(calls.prevented, true);
  assert.equal(calls.selected, true);
});
