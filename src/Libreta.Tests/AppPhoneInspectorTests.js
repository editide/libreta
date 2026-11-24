const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

test('on a phone, a tap that opens the inspector over the table takes a history entry back closes', () => {
  const app = loadApp();

  const opening = app.inspectorTakesEntry(true, false);
  const moving = app.inspectorTakesEntry(true, true);
  const wide = app.inspectorTakesEntry(false, false);

  assert.deepEqual([opening, moving, wide], [true, false, false]);
});
