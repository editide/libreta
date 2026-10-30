const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.js'), 'utf8');
const extract = (name) => app.match(new RegExp(`function ${name}\\([\\s\\S]*?\\n\\}`))?.[0];

function load() {
  const context = vm.createContext({});
  vm.runInContext(extract('inspectorTakesEntry'), context);
  return context;
}

test('on a phone, a tap that opens the inspector over the table takes a history entry back closes', () => {
  // Arrange
  const { inspectorTakesEntry } = load();

  // Act
  const opening = inspectorTakesEntry(true, false);
  const moving = inspectorTakesEntry(true, true);
  const wide = inspectorTakesEntry(false, false);

  // Assert
  assert.deepEqual([opening, moving, wide], [true, false, false]);
});
