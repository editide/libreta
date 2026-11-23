const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.js'), 'utf8');
const treeStepsSource = app.match(/function treeSteps\([\s\S]*?\n\}/)?.[0];
const viewTreeSource = app.match(/function viewTree\([\s\S]*?\n\}/)?.[0];
const treeOrderSource = app.match(/function treeOrder\([\s\S]*?\n\}/)?.[0];
assert.ok(viewTreeSource, 'app.js must define viewTree');
assert.ok(treeOrderSource, 'app.js must define treeOrder');
const context = vm.createContext({});
vm.runInContext(`${treeStepsSource}\n${viewTreeSource}\n${treeOrderSource}`, context);
const viewOrder = (views) => vm.runInContext('treeOrder', context)(vm.runInContext('viewTree', context)(views));

test('views cycle in the order the navigation tree shows them', () => {
  // Arrange
  const views = ['dcf/assumptions', 'dcf/discounted-cash-flow/segments', 'dcf/discounted-cash-flow', 'dcf/terminal-value']
    .map((id) => ({ id }));

  // Act
  const order = viewOrder(views);

  // Assert
  assert.deepEqual([...order], [
    'dcf/assumptions', 'dcf/discounted-cash-flow', 'dcf/discounted-cash-flow/segments', 'dcf/terminal-value'
  ]);
});

test('cycling across either end reveals the active navigation entry and focuses the table', async () => {
  const views = ['first', 'middle', 'last'].map((id) => ({ id }));
  const revealed = [];
  let tableFocusCount = 0;
  const cycleContext = vm.createContext({
    activeView: 'first',
    catalog: { views },
    navigationVisible: true,
    openView: async (id) => { cycleContext.activeView = id; },
    el: (id) => {
      assert.equal(id, 'view-tree');
      return { querySelector: () => ({
        scrollIntoView: (options) => revealed.push([cycleContext.activeView, options.block])
      }) };
    },
    focusTable: () => { tableFocusCount++; }
  });
  const cycleSource = app.match(/(?:async )?function cycleView\([\s\S]*?\n\}/)[0];
  const cycle = vm.runInContext(`${treeStepsSource}\n${viewTreeSource}\n${treeOrderSource}\n${cycleSource}\ncycleView`, cycleContext);

  await cycle(-1);
  await cycle(1);

  assert.deepEqual(revealed, [['last', 'nearest'], ['first', 'nearest']]);
  assert.equal(tableFocusCount, 2);
});
