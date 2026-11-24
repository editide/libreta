const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

test('the inspector omits an empty source', () => {
  const app = loadApp();
  assert.equal(app.renderSource(null), '');
});

test('a linked formula reference stays green without a navigation target', () => {
  const app = loadApp();
  app.set({ payload: { formulas: {} } });
  const html = app.renderReference('inputs.revenue["2026"]', {
    line: 'inputs.revenue',
    column: '2026',
    kind: 'fact',
    linked: true,
    sourceView: null
  });

  assert.match(html, /class="formula-reference linked"/);
  assert.match(html, /data-source-view=""/);
});

test('formula labels replace the name but keep the written subscript, with the name as the tooltip', () => {
  const app = loadApp();
  app.set({ payload: { formulas: {} }, formulaLabels: true });
  const html = app.renderReference('historical.segments.revenue["2025"]', {
    line: 'historical.segments.revenue',
    column: '2025',
    label: 'Segment revenue',
    kind: 'fact',
    linked: false,
    sourceView: null
  });

  assert.match(html, />Segment revenue\[&quot;2025&quot;\]<\/button>/);
  assert.match(html, /title="Open historical\.segments\.revenue\[&quot;2025&quot;\]"/);
});

test('an expanded alias is written out in place of its own name, with nothing to collapse back to', () => {
  const app = loadApp();
  app.set({ payload: { formulas: { ltm: { text: 'self["FY2025"] - self["H1 2025"]', references: [] } } } });

  const html = app.renderReference('inputs.revenue["LTM"]', {
    line: 'inputs.revenue',
    column: 'LTM',
    kind: 'fact',
    linked: true,
    sourceView: 'inputs',
    formulaId: 'ltm'
  }, true);

  assert.match(html, /self\[&quot;FY2025&quot;\] - self\[&quot;H1 2025&quot;\]/);
  assert.doesNotMatch(html, /formula-expand/);
});

test('the inspector lists what reads a cell, opening one no table shows on a view of its own', () => {
  const app = loadApp();

  const html = app.renderDependents([
    { line: 'a.doubled', column: '2023', label: 'Doubled', columnLabel: '2023', value: '200', kind: 'formula',
      sourceView: 'summary', hidden: false },
    { line: 'a.helper', column: '2023', label: 'Helper', columnLabel: '2023', value: '100', kind: 'formula',
      sourceView: null, hidden: true }
  ]);

  assert.equal(app.renderDependents([]), '');
  assert.equal(app.renderDependents(undefined), '');
  assert.match(html, /<h3>Used by<\/h3>/);
  assert.match(html, /<button type="button" class="dep" data-name="a\.doubled" data-column-key="2023" data-source-view="summary"/);
  assert.match(html, /<button type="button" class="dep dependent-hidden" data-name="a\.helper" data-column-key="2023" data-source-view="line:a\.helper"/);
});

test('check equations relabel qualified references from the server formula payload', () => {
  const app = loadApp();

  const text = app.formatCheckEquation({
    name: 'total',
    formula: 'historical.revenue - cost - total',
    expression: {
      text: 'historical.revenue - cost - total',
      references: [
        { start: 0, length: 18, line: 'historical.revenue', label: 'Revenue' },
        { start: 21, length: 4, line: 'cost', label: 'Cost - Direct' },
        { start: 28, length: 5, line: 'total', label: 'Gross Profit' }
      ]
    }
  });

  assert.equal(text, 'Revenue − Cost - Direct = Gross Profit');
});
