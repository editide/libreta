const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp, makeElement } = require('./helpers/appHarness');

test('clearViewState resets every view-scoped field including calculationTrail', () => {
  const app = loadApp();
  const state = app.eval('viewState');
  state.selection = { index: 1, periodIndex: 0 };
  state.cellRanges = [{ anchor: { index: 1, periodIndex: 0 }, active: { index: 2, periodIndex: 1 } }];
  state.sensitivitySelection = { name: 'dcf', row: 0, column: 1 };
  state.checksExpanded = true;
  state.openChecks.add('check\u0000formula');
  state.calculationTrail = { cell: 'rev\u00002024', steps: [{ id: 'a', text: '1', title: 'A' }] };

  app.clearViewState();

  const cleared = app.eval('viewState');
  assert.equal(cleared.selection, null);
  assert.equal(cleared.cellRanges.length, 0);
  assert.equal(cleared.sensitivitySelection, null);
  assert.equal(cleared.checksExpanded, null);
  assert.equal(cleared.openChecks.size, 0);
  assert.equal(cleared.calculationTrail.cell, null);
  assert.equal(cleared.calculationTrail.steps.length, 0);
});

test('rememberHistoryEntry and restoreViewState preserve view state without aliasing', () => {
  const app = loadApp();
  app.set({ activeView: 'income', payload: { columns: [], rows: [] } });
  const state = app.eval('viewState');
  state.selection = { index: 2, periodIndex: 1 };
  state.cellRanges = [{ anchor: { index: 2, periodIndex: 1 }, active: { index: 2, periodIndex: 1 } }];
  state.checksExpanded = false;
  state.openChecks.add('margin\u0000gp / rev');
  state.calculationTrail = { cell: 'gp\u00002024', steps: [{ id: 'x', text: '10', title: 'GP' }] };

  app.rememberHistoryEntry();
  app.persistHistoryStates();
  app.clearViewState();

  const saved = app.savedViewState('income');
  assert.ok(saved);
  app.restoreViewState(saved);

  const restored = app.eval('viewState');
  assert.deepEqual(JSON.parse(JSON.stringify(restored.selection)), { index: 2, periodIndex: 1 });
  assert.deepEqual(JSON.parse(JSON.stringify(restored.cellRanges)),
    [{ anchor: { index: 2, periodIndex: 1 }, active: { index: 2, periodIndex: 1 } }]);
  assert.equal(restored.checksExpanded, false);
  assert.equal(restored.openChecks.has('margin\u0000gp / rev'), true);
  assert.deepEqual(JSON.parse(JSON.stringify(restored.calculationTrail)),
    { cell: 'gp\u00002024', steps: [{ id: 'x', text: '10', title: 'GP' }] });

  // Mutating the restored state must not mutate the saved history snapshot.
  restored.selection.index = 9;
  restored.openChecks.delete('margin\u0000gp / rev');
  app.restoreViewState(saved);
  assert.equal(app.eval('viewState.selection.index'), 2);
  assert.equal(app.eval('viewState.openChecks.has("margin\\u0000gp / rev")'), true);

  // Persisted JSON in sessionStorage also round-trips openChecks as a Set.
  const persisted = app.readHistoryStates().get(app.eval('historyEntry'));
  app.clearViewState();
  app.restoreViewState(persisted);
  assert.equal(app.eval('viewState.openChecks.has("margin\\u0000gp / rev")'), true);
});

test('syncGridSelection highlights a cell range from cached gridNodes without per-cell DOM queries', () => {
  const app = loadApp();
  const payload = {
    columns: [{ label: '2024' }, { label: '2025' }],
    rows: [
      { cells: [{ display: '1' }, { display: '2' }] },
      { cells: [{ display: '3' }, { display: '4' }] }
    ]
  };
  app.set({ payload });

  const cells = new Map();
  const gridNodes = app.eval('gridNodes');
  for (const row of [0, 1]) {
    for (const col of [0, 1]) {
      const node = makeElement();
      cells.set(`${row}:${col}`, node);
      gridNodes.set(`${row}:${col}`, node);
    }
  }

  let bodyQueries = 0;
  app.elements.set('head', makeElement());
  app.elements.set('body', makeElement({
    querySelector: () => {
      bodyQueries++;
      return null;
    }
  }));

  const state = app.eval('viewState');
  state.selection = { index: 1, periodIndex: 1 };
  state.cellRanges = [{ anchor: { index: 0, periodIndex: 0 }, active: { index: 1, periodIndex: 1 } }];

  app.syncGridSelection();

  assert.equal(cells.get('0:0').classList.contains('range-selected'), true);
  assert.equal(cells.get('0:0').classList.contains('range-top'), true);
  assert.equal(cells.get('0:0').classList.contains('range-left'), true);
  assert.equal(cells.get('0:0').classList.contains('range-right'), false);
  assert.equal(cells.get('0:0').classList.contains('range-bottom'), false);
  assert.equal(cells.get('1:1').classList.contains('range-bottom'), true);
  assert.equal(cells.get('1:1').classList.contains('range-right'), true);
  assert.equal(cells.get('1:1').classList.contains('selected-cell'), true);
  assert.equal(cells.get('1:1').tabIndex, 0);
  // Only the single active row highlight queries body; range highlighting itself does zero DOM queries.
  assert.equal(bodyQueries, 1);
});
