const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.js'), 'utf8');
const renderSourceText = app.match(/function renderSource\([\s\S]*?\n\}/)?.[0];
assert.ok(renderSourceText, 'app.js must define renderSource');
const renderSource = vm.runInNewContext(`(${renderSourceText})`);
const escapeHtmlText = app.match(/function escapeHtml\([\s\S]*?\n\}/)?.[0];
assert.ok(escapeHtmlText, 'app.js must define escapeHtml');
const escapeHtml = vm.runInNewContext(`(${escapeHtmlText})`);
const renderReferenceText = app.match(/function renderReference\([\s\S]*?\n\}/)?.[0];
assert.ok(renderReferenceText, 'app.js must define renderReference');
const referenceLabelText = app.match(/function referenceLabel\([\s\S]*?\n\}/)?.[0];
assert.ok(referenceLabelText, 'app.js must define referenceLabel');
const referenceContext = vm.createContext({
  escapeHtml,
  formulaExpansions: new Map(),
  formulaLabels: false,
  payload: { formulas: {} }
});
vm.runInContext(`${referenceLabelText}\n${renderReferenceText}`, referenceContext);
const renderReference = vm.runInContext('renderReference', referenceContext);

test('the inspector omits an empty source', () => {
  assert.equal(renderSource(null), '');
});

test('a linked formula reference stays green without a navigation target', () => {
  const html = renderReference('inputs.revenue["2026"]', {
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
  referenceContext.formulaLabels = true;
  const html = renderReference('historical.segments.revenue["2025"]', {
    line: 'historical.segments.revenue',
    column: '2025',
    label: 'Segment revenue',
    kind: 'fact',
    linked: false,
    sourceView: null
  });
  referenceContext.formulaLabels = false;

  assert.match(html, />Segment revenue\[&quot;2025&quot;\]<\/button>/);
  assert.match(html, /title="Open historical\.segments\.revenue\[&quot;2025&quot;\]"/);
});

test('an expanded alias is written out in place of its own name, with nothing to collapse back to', () => {
  const renderFormulaText = app.match(/function renderFormula\([\s\S]*?\n\}/)?.[0];
  assert.ok(renderFormulaText, 'app.js must define renderFormula');
  const context = vm.createContext({
    escapeHtml,
    formulaExpansions: new Map(),
    formulaLabels: false,
    payload: { formulas: { ltm: { text: 'self["FY2025"] - self["H1 2025"]', references: [] } } }
  });
  vm.runInContext(`${renderFormulaText}\n${referenceLabelText}\n${renderReferenceText}`, context);

  const html = vm.runInContext('renderReference', context)('inputs.revenue["LTM"]', {
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
  const renderDependentsText = app.match(/function renderDependents\([\s\S]*?\n\}/)?.[0];
  assert.ok(renderDependentsText, 'app.js must define renderDependents');
  const renderDependents = vm.runInNewContext(`(${renderDependentsText})`, { escapeHtml, LINE_VIEW: 'line:' });

  const html = renderDependents([
    { line: 'a.doubled', column: '2023', label: 'Doubled', columnLabel: '2023', value: '200', kind: 'formula',
      sourceView: 'summary', hidden: false },
    { line: 'a.helper', column: '2023', label: 'Helper', columnLabel: '2023', value: '100', kind: 'formula',
      sourceView: null, hidden: true }
  ]);

  assert.equal(renderDependents([]), '');
  assert.equal(renderDependents(undefined), '');
  assert.match(html, /<h3>Used by<\/h3>/);
  assert.match(html, /<button type="button" class="dep" data-name="a\.doubled" data-column-key="2023" data-source-view="summary"/);
  assert.match(html, /<button type="button" class="dep dependent-hidden" data-name="a\.helper" data-column-key="2023" data-source-view="line:a\.helper"/);
});

test('a date cell whose exact value is its display reads as matching', () => {
  const stableNumberText = app.match(/function stableNumber\([\s\S]*?\n\}/)?.[0];
  const displayMatchesExactText = app.match(/function displayMatchesExact\([\s\S]*?\n\}/)?.[0];
  const displayMatchesExact = vm.runInNewContext(`${stableNumberText}\n(${displayMatchesExactText})`);

  assert.equal(displayMatchesExact('2026-06-30', '2026-06-30', 'date'), true);
});
