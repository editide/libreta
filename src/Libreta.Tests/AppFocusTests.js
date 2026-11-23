const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const app = loadApp();

function target(matchingSelector = null) {
  return { closest(selector) { return selector === matchingSelector ? this : null; } };
}

test('plain page chrome restores focus to the selected grid cell', () => {
  assert.equal(app.shouldRestoreGridFocus(target(), true, false), true);
});

test('interactive elements keep their own focus', () => {
  const interactiveSelector = 'a, button, input, select, textarea, summary, [contenteditable], ' +
    '[tabindex]:not([tabindex="-1"]), [data-column]';
  assert.equal(app.shouldRestoreGridFocus(target(interactiveSelector), true, false), false);
});

test('focus is not restored without a selection or while a dialog is open', () => {
  assert.equal(app.shouldRestoreGridFocus(target(), false, false), false);
  assert.equal(app.shouldRestoreGridFocus(target(), true, true), false);
});
