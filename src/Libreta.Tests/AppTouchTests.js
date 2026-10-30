const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.js'), 'utf8');
const pointerSource = app.match(/function beginCellRangePointer\([\s\S]*?\n\}/)?.[0];
assert.ok(pointerSource, 'app.js must define beginCellRangePointer');

function press(pointerType) {
  const calls = { prevented: false, selected: false };
  const context = {
    COLUMN_AXIS: -1,
    cellRanges: [],
    validCellRegion: () => true,
    validCellCoordinate: () => false,
    lastCellRegion: () => null,
    selectInGrid: () => { calls.selected = true; },
    selectionFor: () => null,
    syncGridSelection: () => {},
    enterInspector: () => {},
    requestAnimationFrame: () => 0,
    document: { body: { classList: { add() {} } } }
  };
  const beginCellRangePointer = vm.runInNewContext(`(${pointerSource})`, context);
  beginCellRangePointer({
    pointerType, button: 0, isPrimary: true, pointerId: 1, clientX: 0, clientY: 0,
    ctrlKey: false, metaKey: false, shiftKey: false,
    preventDefault() { calls.prevented = true; },
    currentTarget: { setPointerCapture() {}, addEventListener() {} }
  }, 0, 0);
  return calls;
}

test('a touch leaves the gesture to the browser so the table scrolls', () => {
  // Act
  const calls = press('touch');

  // Assert
  assert.equal(calls.prevented, false);
  assert.equal(calls.selected, false);
});

test('a mouse press still starts a range selection', () => {
  // Act
  const calls = press('mouse');

  // Assert
  assert.equal(calls.prevented, true);
  assert.equal(calls.selected, true);
});