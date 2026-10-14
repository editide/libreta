const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.js'), 'utf8');
const source = app.match(/function leavesEmbeddedBook\(event, overlayOpen\) \{[\s\S]*?\n\}/)?.[0];
assert.ok(source, 'app.js must define leavesEmbeddedBook');
const leavesEmbeddedBook = vm.runInNewContext(`(${source})`);

function key(name, modifiers = {}) {
  return { key: name, ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, ...modifiers };
}

test('escape leaves the book, even with a cell selected', () => {
  assert.equal(leavesEmbeddedBook(key('Escape'), false), true);
});

test('escape closes an open dialog or menu first', () => {
  assert.equal(leavesEmbeddedBook(key('Escape'), true), false);
});

test('other keys and modified escapes stay in the book', () => {
  assert.equal(leavesEmbeddedBook(key('ArrowLeft'), false), false);
  assert.equal(leavesEmbeddedBook(key('Escape', { ctrlKey: true }), false), false);
});
