const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp, makeElement } = require('./helpers/appHarness');

test('views cycle in the order the navigation tree shows them', () => {
  const app = loadApp();
  const views = ['dcf/assumptions', 'dcf/discounted-cash-flow/segments', 'dcf/discounted-cash-flow', 'dcf/terminal-value']
    .map((id) => ({ id }));

  const order = app.treeOrder(app.viewTree(views));

  assert.deepEqual([...order], [
    'dcf/assumptions', 'dcf/discounted-cash-flow', 'dcf/discounted-cash-flow/segments', 'dcf/terminal-value'
  ]);
});

test('cycling across either end reveals the active navigation entry and focuses the table', async () => {
  const views = ['first', 'middle', 'last'].map((id) => ({ id }));
  const revealed = [];
  let tableFocusCount = 0;
  const app = loadApp();
  app.set({ activeView: 'first', catalog: { views }, navigationVisible: true });
  app.openView = async (id) => { app.set({ activeView: id }); };
  app.elements.set('view-tree', makeElement({
    querySelector: () => makeElement({
      scrollIntoView: (options) => revealed.push([app.eval('activeView'), options.block])
    })
  }));
  app.focusTable = () => { tableFocusCount++; };

  await app.cycleView(-1);
  await app.cycleView(1);

  assert.deepEqual(revealed, [['last', 'nearest'], ['first', 'nearest']]);
  assert.equal(tableFocusCount, 2);
});
