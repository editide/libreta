const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const app = loadApp();

function outline(node, depth = 0) {
  const lines = [];
  for (const child of node.children.values()) {
    const label = child.view?.id ?? (child.heading ? `# ${child.heading}` : `${child.segment}/`);
    lines.push(`${'  '.repeat(depth)}${label}`);
    lines.push(...outline(child, depth + 1));
  }
  return lines;
}

const views = [
  { id: 'operations', headings: [{ name: 'Statements', depth: 0 }] },
  { id: 'balance', headings: [{ name: 'Statements', depth: 0 }, { name: 'Detail', depth: 0 }] },
  { id: 'derived/summary', headings: [{ name: 'Analysis', depth: 0 }] },
  { id: 'derived/costs', headings: [{ name: 'Costs', depth: 0 }] },
  { id: 'other/summary', headings: [{ name: 'Analysis', depth: 0 }] },
  { id: 'parent', headings: [] },
  { id: 'parent/child', headings: [{ name: 'Breakdown', depth: 1 }] },
  { id: 'loose' }
];

test('headings group views in place of the folder labels beneath them', () => {
  const tree = app.viewTree(views);

  assert.deepEqual(outline(tree), [
    '# Statements',
    '  operations',
    '  # Detail',
    '    balance',
    '# Analysis',
    '  derived/summary',
    '  other/summary',
    '# Costs',
    '  derived/costs',
    'parent',
    '  # Breakdown',
    '    parent/child',
    'loose'
  ]);
  assert.deepEqual([...app.treeOrder(tree)], [
    'operations', 'balance', 'derived/summary', 'other/summary', 'derived/costs', 'parent', 'parent/child', 'loose'
  ]);
});

test('a view under headings expands every heading and parent view above it', () => {
  const viewIds = new Set(views.map((view) => view.id));

  const keys = app.treeKeys(views[6], viewIds);

  assert.deepEqual([...keys], ['parent', 'parent/#Breakdown', 'parent/#Breakdown/parent/child']);
});

test('a heading keeps the folder of an included book beneath it', () => {
  const books = ['alpha', 'beta'].map((book) => ({
    id: `${book}/analysis`,
    book,
    headings: [{ name: 'Companies', depth: 0 }, { name: 'Statements', depth: 1 }]
  }));

  const tree = app.viewTree(books);

  assert.deepEqual(outline(tree), [
    '# Companies',
    '  alpha/',
    '    # Statements',
    '      alpha/analysis',
    '  beta/',
    '    # Statements',
    '      beta/analysis'
  ]);
});

test('a view without headings keeps the keys its folders always had', () => {
  const view = { id: 'derived/summary' };

  const keys = app.treeKeys(view, new Set([view.id]));

  assert.deepEqual([...keys], ['derived', 'derived/summary']);
});

test('every heading, folder, and parent view with entries beneath it is a branch', () => {
  const keys = app.branchKeys(app.viewTree(views));

  assert.deepEqual([...keys], ['#Statements', '#Statements/#Detail', '#Analysis', '#Costs', 'parent', 'parent/#Breakdown']);
});

test('the expand-all toggle reads a top-level group as open until the reader closes it', () => {
  const instance = loadApp();
  const states = instance.eval('branchStates');
  const tree = instance.viewTree(views);

  const initially = instance.anyBranchExpanded(tree);
  for (const key of ['#Statements', '#Analysis', '#Costs']) states.set(key, false);
  const headingsClosed = instance.anyBranchExpanded(tree);
  states.set('parent', true);
  const parentOpened = instance.anyBranchExpanded(tree);

  assert.equal(initially, true);
  assert.equal(headingsClosed, false);
  assert.equal(parentOpened, true);
});
