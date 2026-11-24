const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const app = loadApp();

function key(name, modifiers = {}) {
  return { key: name, ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, ...modifiers };
}

test('escape leaves the book, even with a cell selected', () => {
  assert.equal(app.leavesEmbeddedBook(key('Escape'), false), true);
});

test('escape closes an open dialog or menu first', () => {
  assert.equal(app.leavesEmbeddedBook(key('Escape'), true), false);
});

test('other keys and modified escapes stay in the book', () => {
  assert.equal(app.leavesEmbeddedBook(key('ArrowLeft'), false), false);
  assert.equal(app.leavesEmbeddedBook(key('Escape', { ctrlKey: true }), false), false);
});
