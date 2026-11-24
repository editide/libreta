const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp, makeElement } = require('./helpers/appHarness');

function makeCheckContainer() {
  let outerToggle = null;
  const itemToggles = [];
  const outer = makeElement({
    isConnected: true,
    open: true,
    addEventListener: (event, fn) => {
      if (event === 'toggle') outerToggle = fn;
    }
  });
  const items = [];
  const container = makeElement({
    querySelector: (selector) => selector === ':scope > details' ? outer : null,
    querySelectorAll: (selector) => {
      if (selector !== '.check') return [];
      items.length = 0;
      itemToggles.length = 0;
      const count = (container.innerHTML.match(/<details class="check /g) ?? []).length;
      for (let i = 0; i < count; i++) {
        const item = makeElement({
          isConnected: true,
          open: false,
          addEventListener: (event, fn) => {
            if (event === 'toggle') itemToggles[i] = fn;
          }
        });
        items.push(item);
      }
      return items;
    }
  });
  return {
    container,
    outer,
    items,
    fireOuterToggle: (open, isConnected = true) => {
      outer.open = open;
      outer.isConnected = isConnected;
      outerToggle({ currentTarget: outer });
    },
    fireItemToggle: (index, open, isConnected = true) => {
      items[index].open = open;
      items[index].isConnected = isConnected;
      itemToggles[index]({ currentTarget: items[index] });
    }
  };
}

test('an open check stays open across reloads until the reader closes it', () => {
  const app = loadApp();
  const { container, fireItemToggle } = makeCheckContainer();
  app.elements.set('checks', container);
  app.eval(`payload = {
    checks: [
      { name: 'gross_profit', formula: 'revenue - cost - gross_profit', periods: ['2024'], deltas: ['1'], passed: false },
      { name: 'margin', formula: 'gross_profit / revenue', periods: ['2024'], deltas: ['0'], passed: true }
    ]
  }`);

  app.renderChecks();
  assert.doesNotMatch(container.innerHTML, /<details class="check fail" open>/);

  fireItemToggle(0, true);
  app.renderChecks();
  assert.match(container.innerHTML, /<details class="check fail" open>/);
  assert.doesNotMatch(container.innerHTML, /<details class="check pass" open>/);

  fireItemToggle(0, false);
  app.renderChecks();
  assert.doesNotMatch(container.innerHTML, /<details class="check fail" open>/);
});

test('a toggle event on a detached checks element does not clear the current selection', () => {
  const app = loadApp();
  const { container, fireOuterToggle } = makeCheckContainer();
  app.elements.set('checks', container);
  let cleared = false;
  app.clearSelection = () => { cleared = true; };
  app.eval(`
    payload = { checks: [{ name: 'gross_profit', formula: 'revenue - gross_profit', periods: ['2024'], deltas: ['1'], passed: false }] };
    viewState.selection = { index: 0, periodIndex: 0 };
    viewState.checksExpanded = false;
  `);

  app.renderChecks();
  fireOuterToggle(true, false);

  assert.equal(cleared, false);
  assert.equal(app.eval('viewState.checksExpanded'), false);
});
