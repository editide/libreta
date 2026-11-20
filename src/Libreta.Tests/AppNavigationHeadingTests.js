const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.js'), 'utf8');
const sources = ['treeSteps', 'viewTree', 'treeOrder', 'treeKeys'].map((name) => {
  const source = app.match(new RegExp(`function ${name}\\([\\s\\S]*?\\n\\}`))?.[0];
  assert.ok(source, `app.js must define ${name}`);
  return source;
});
const context = vm.createContext({});
vm.runInContext(sources.join('\n'), context);
const viewTree = vm.runInContext('viewTree', context);
const treeOrder = vm.runInContext('treeOrder', context);
const treeKeys = vm.runInContext('treeKeys', context);

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
  // Arrange
  // Act
  const tree = viewTree(views);

  // Assert
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
  assert.deepEqual([...treeOrder(tree)], [
    'operations', 'balance', 'derived/summary', 'other/summary', 'derived/costs', 'parent', 'parent/child', 'loose'
  ]);
});

test('a view under headings expands every heading and parent view above it', () => {
  // Arrange
  const viewIds = new Set(views.map((view) => view.id));

  // Act
  const keys = treeKeys(views[6], viewIds);

  // Assert
  assert.deepEqual([...keys], ['parent', 'parent/#Breakdown', 'parent/#Breakdown/parent/child']);
});

test('a heading keeps the folder of an included book beneath it', () => {
  // Arrange
  const books = ['alpha', 'beta'].map((book) => ({
    id: `${book}/analysis`,
    book,
    headings: [{ name: 'Companies', depth: 0 }, { name: 'Statements', depth: 1 }]
  }));

  // Act
  const tree = viewTree(books);

  // Assert
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
  // Arrange
  const view = { id: 'derived/summary' };

  // Act
  const keys = treeKeys(view, new Set([view.id]));

  // Assert
  assert.deepEqual([...keys], ['derived', 'derived/summary']);
});

test('every heading, folder, and parent view with entries beneath it is a branch', () => {
  // Arrange
  const branchKeys = vm.runInContext(`${app.match(/function branchKeys\([\s\S]*?\n\}/)[0]}\nbranchKeys`, context);

  // Act
  const keys = branchKeys(viewTree(views));

  // Assert
  assert.deepEqual([...keys], ['#Statements', '#Statements/#Detail', '#Analysis', '#Costs', 'parent', 'parent/#Breakdown']);
});

test('the expand-all toggle reads a top-level group as open until the reader closes it', () => {
  // Arrange
  const states = new Map();
  const groups = vm.createContext({ branchStates: states });
  const anyBranchExpanded = vm.runInContext(
    `${app.match(/function anyBranchExpanded\([\s\S]*?\n\}/)[0]}\nanyBranchExpanded`, groups);
  const tree = viewTree(views);

  // Act
  const initially = anyBranchExpanded(tree);
  for (const key of ['#Statements', '#Analysis', '#Costs']) states.set(key, false);
  const headingsClosed = anyBranchExpanded(tree);
  states.set('parent', true);
  const parentOpened = anyBranchExpanded(tree);

  // Assert
  assert.equal(initially, true);
  assert.equal(headingsClosed, false);
  assert.equal(parentOpened, true);
});
