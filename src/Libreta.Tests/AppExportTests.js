const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const app = loadApp();

test('an export takes the name the server gives it', () => {
  assert.equal(app.downloadName("attachment; filename=spacex.xlsx; filename*=UTF-8''spacex.xlsx", 'xlsx'), 'spacex.xlsx');
});

test('an encoded name is decoded', () => {
  assert.equal(app.downloadName("attachment; filename=\"a b.xlsx\"; filename*=UTF-8''a%20b.xlsx", 'xlsx'), 'a b.xlsx');
});

test('a plain name is read without its quotes', () => {
  assert.equal(app.downloadName('attachment; filename="book one.html"', 'html'), 'book one.html');
});

test('a response without a name still downloads in its format', () => {
  assert.equal(app.downloadName(null, 'html'), 'book.html');
});
