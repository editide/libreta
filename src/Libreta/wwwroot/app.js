const SOURCE_IMAGE_ZOOM_MIN = 100;
const SOURCE_IMAGE_ZOOM_MAX = 500;
const SOURCE_IMAGE_ZOOM_STEP = 50;

class RequestCoordinator {
  constructor() {
    this.viewSequence = 0;
    this.catalogSequence = 0;
  }

  beginView(view) {
    return { sequence: ++this.viewSequence, view };
  }

  beginCatalog() {
    ++this.viewSequence;
    return ++this.catalogSequence;
  }

  acceptView(request, activeView) {
    return request.sequence === this.viewSequence && request.view === activeView;
  }

  acceptCatalog(sequence) {
    if (sequence !== this.catalogSequence) return false;
    ++this.viewSequence;
    return true;
  }
}

// An exported page carries the catalog, every view and the source images in place of the server.
const snapshot = readSnapshot();
// A page in a frame, such as a slide deck tracing a number, starts on the table alone, live or exported.
const embedded = window.parent !== window;
const el = (id) => document.getElementById(id);
// Held by reference: render() rebuilds the header row and re-appends it.
const labelResizer = el('label-resizer');
const sourceImageViewport = el('source-image-dialog').querySelector('.source-image-dialog-viewport');
let payload = null;
let catalog = null;
let activeView = null;
let selection = null;
let cellRanges = [];
let sensitivitySelection = null;
let calculationHighlights = true;
let formulaLabels = false;
let pendingShortcut = null;
let shortcutTimer = null;
let inspectorDock = stored('inspectorDock') === 'bottom' ? 'bottom' : 'right';
let uiScale = Number(stored('uiScale')) || 100;
let theme = stored('theme') || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
let navigationVisible = !embedded && stored('navigationVisible') !== 'false';
let checksExpanded = null;
const requests = new RequestCoordinator();
const branchStates = new Map();
const formulaExpansions = new Map();
// The values stepped into from the selected cell's calculation, outermost first.
let calculationTrail = { cell: null, steps: [] };
// Expansion ids join line and column with a NUL, which HTML attributes cannot carry, so buttons index into this.
let calculationTargets = [];
let revealedInitialView = false;
const historyStates = readHistoryStates();
let historyEntry = history.state?.entry ?? newHistoryEntry();
let sourceImageZoom = SOURCE_IMAGE_ZOOM_MIN;
let sourceImageFitWidth = 0;
let sourceImageFitHeight = 0;
const compactLayout = window.matchMedia('(max-width: 52rem)');
// Kinds of an axis entry that names a line, as opposed to a book or a period.
const LINE_KINDS = new Set(['fact', 'formula', 'missing', 'metric']);
// Selection row index meaning "the whole column".
const COLUMN_AXIS = -1;
const KIND_LABELS = { fact: 'Fact', formula: 'Calculated', book: 'Book', column: 'Column', metric: 'Line' };
// Keyed by coordinate, not position, so highlighting survives a transpose.
const cellNodes = new Map();
let suppressCellClick = false;

// The middle column has a 36rem minimum, so every panel is capped against the window as well as a constant.
const navWidth = createSize({
  key: 'navWidth', cssVar: '--nav-width-user', fallback: 224, min: 160, max: 480, fraction: .4, axis: 'x',
  sync: () => syncResizerRange(el('nav-resizer'), navWidth)
});
const labelWidth = createSize({
  key: 'labelWidth', cssVar: '--label-width-user', fallback: 352, min: 128, max: 720, fraction: .45, axis: 'x',
  sync: () => syncResizerRange(labelResizer, labelWidth)
});
const inspectorWidth = createSize({
  key: 'inspectorWidth', cssVar: '--inspector-width-user', fallback: 400, min: 280, max: 720, fraction: .45,
  axis: 'x', sync: syncInspectorResizer
});
const inspectorHeight = createSize({
  key: 'inspectorHeight', cssVar: '--inspector-height-user', fallback: 304, min: 160, max: 900, fraction: .7,
  axis: 'y', sync: syncInspectorResizer
});
const sizes = [navWidth, labelWidth, inspectorWidth, inspectorHeight];

function stored(key) {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function store(key, value) {
  try {
    localStorage.setItem(key, String(value));
  } catch {}
}

function setUiScale(percent, persist = true) {
  uiScale = Math.min(140, Math.max(70, Math.round(percent / 5) * 5));
  document.documentElement.style.setProperty('--ui-scale', String(uiScale / 100));
  el('scale').value = String(uiScale);
  el('scale-value').textContent = `${uiScale}%`;
  if (persist) store('uiScale', uiScale);
}

function setTheme(next, persist = true) {
  theme = next === 'dark' ? 'dark' : 'light';
  document.documentElement.dataset.theme = theme;
  const label = theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme';
  const button = el('theme-toggle');
  button.title = label;
  button.setAttribute('aria-label', label);
  if (persist) store('theme', theme);
}

function setNavigationVisible(visible, persist = !embedded) {
  navigationVisible = visible;
  document.querySelector('.layout').classList.toggle('nav-hidden', !visible);
  const toggle = el('nav-toggle');
  const label = visible ? 'Hide navigation' : 'Show navigation';
  toggle.setAttribute('aria-expanded', String(visible));
  toggle.setAttribute('aria-label', label);
  toggle.title = label;
  if (persist) store('navigationVisible', visible);
}

function createSize({ key, cssVar, fallback, min, max, fraction, axis, sync }) {
  const clamp = (px) => {
    const room = Math.round((axis === 'x' ? window.innerWidth : window.innerHeight) * fraction);
    return Math.round(Math.min(Math.max(min, Math.min(max, room)), Math.max(min, px)));
  };
  const saved = Number(stored(key));
  const size = {
    fallback, min, max, axis,
    value: clamp(saved > 0 ? saved : fallback),
    set(px, persist = true) {
      size.preference = px;
      size.value = clamp(px);
      document.documentElement.style.setProperty(cssVar, `${size.value}px`);
      sync();
      if (persist) store(key, size.value);
      return size.value;
    }
  };
  size.preference = size.value;
  return size;
}

function syncResizerRange(resizer, size) {
  resizer.setAttribute('aria-valuenow', String(size.value));
  resizer.setAttribute('aria-valuemin', String(size.min));
  resizer.setAttribute('aria-valuemax', String(size.max));
}

function effectiveDock() {
  return compactLayout.matches ? 'bottom' : inspectorDock;
}

function inspectorSize() {
  return effectiveDock() === 'bottom' ? inspectorHeight : inspectorWidth;
}

function syncInspectorResizer() {
  const resizer = el('inspector-resizer');
  const bottom = effectiveDock() === 'bottom';
  resizer.setAttribute('aria-orientation', bottom ? 'horizontal' : 'vertical');
  resizer.setAttribute('aria-label', bottom ? 'Inspector height' : 'Inspector width');
  syncResizerRange(resizer, inspectorSize());
}

// `sign` is 1 when the handle sits on the panel's growing edge and -1 when dragging away from it widens the panel.
function beginEdgeResize(event, { resizer, axis, sign, start, apply, settled }) {
  if (event.button !== 0) return;
  event.preventDefault();
  const origin = axis === 'x' ? event.clientX : event.clientY;
  let current = start;
  resizer.setPointerCapture(event.pointerId);
  document.body.classList.add('resizing', axis === 'x' ? 'resize-col' : 'resize-row');

  const move = (moved) => {
    const delta = (axis === 'x' ? moved.clientX : moved.clientY) - origin;
    current = apply(start + sign * delta, false);
  };
  const end = () => {
    resizer.removeEventListener('pointermove', move);
    resizer.removeEventListener('pointerup', end);
    resizer.removeEventListener('pointercancel', end);
    document.body.classList.remove('resizing', 'resize-col', 'resize-row');
    apply(current);
    settled();
  };

  resizer.addEventListener('pointermove', move);
  resizer.addEventListener('pointerup', end);
  resizer.addEventListener('pointercancel', end);
}

function resizeByKey(event, size, sign) {
  const directions = size.axis === 'x' ? { ArrowLeft: -1, ArrowRight: 1 } : { ArrowUp: -1, ArrowDown: 1 };
  if (event.altKey) return false;
  if (directions[event.key]) size.set(size.value + sign * directions[event.key] * (event.shiftKey ? 48 : 16));
  else if (event.key === 'Home') size.set(size.fallback);
  else return false;
  event.preventDefault();
  return true;
}

function wireResizer(resizer, sizeFor, sign, settled = () => {}) {
  resizer.addEventListener('pointerdown', (event) => {
    const size = sizeFor();
    beginEdgeResize(event, { resizer, axis: size.axis, sign, start: size.value, apply: size.set, settled });
  });
  resizer.addEventListener('keydown', (event) => {
    if (resizeByKey(event, sizeFor(), sign)) settled();
  });
  resizer.addEventListener('dblclick', () => {
    sizeFor().set(sizeFor().fallback);
    settled();
  });
}

function repositionSourceImage() {
  requestAnimationFrame(() => {
    const sourceImage = el('panel').querySelector('.source-image.has-source-region');
    if (sourceImage) positionSourceImage(sourceImage);
  });
}

function setInspectorDock(dock, persist = true) {
  if (persist) {
    inspectorDock = dock;
    store('inspectorDock', dock);
  }
  const dockNow = effectiveDock();
  document.querySelector('.layout').classList.toggle('inspector-bottom', dockNow === 'bottom');
  el('dock-right').setAttribute('aria-pressed', String(dockNow === 'right'));
  el('dock-bottom').setAttribute('aria-pressed', String(dockNow === 'bottom'));
  el('dock-right').disabled = compactLayout.matches;
  el('dock-right').title = compactLayout.matches
    ? 'Inspector docks below at this window size'
    : 'Dock inspector right';
  syncInspectorResizer();
  repositionSourceImage();
}

function readSnapshot() {
  const node = document.getElementById('libreta-snapshot');
  return node ? JSON.parse(node.textContent) : null;
}

async function fetchView(id) {
  if (snapshot) return snapshot.views[id] ?? { error: `No view '${id}' in this export.` };
  const response = await fetch(`/api/view?id=${encodeURIComponent(id)}`);
  if (!response.ok) throw new Error(`View request failed (${response.status}).`);
  return response.json();
}

async function fetchCatalog() {
  if (snapshot) return snapshot.catalog;
  const response = await fetch('/api/catalog');
  if (!response.ok) throw new Error(`Catalog request failed (${response.status}).`);
  return response.json();
}

function sourceImageUrl(path) {
  return snapshot ? snapshot.images[path] ?? ''
    : `/api/source-image?path=${encodeURIComponent(path)}&version=${payload.version}`;
}

async function load() {
  const request = requests.beginView(activeView);
  let nextPayload;
  try {
    nextPayload = await fetchView(request.view);
  } catch (error) {
    // A failed view still renders, so focus is never stranded.
    nextPayload = { error: error.message };
  }
  if (!requests.acceptView(request, activeView)) return false;
  payload = nextPayload;
  const activeSensitivity = (payload.sensitivities ?? []).find((item) => item.name === sensitivitySelection?.name);
  if (!activeSensitivity || !activeSensitivity.cells[sensitivitySelection.row]?.[sensitivitySelection.column]) {
    sensitivitySelection = null;
  }
  if (selection?.periodIndex >= (payload.columns?.length ?? 0)) {
    if (selection.index === COLUMN_AXIS) selection = null;
    else selection.periodIndex = null;
  }
  cellRanges = cellRanges.filter(validCellRegion);
  render();
  return true;
}

async function refresh() {
  const sequence = requests.beginCatalog();
  try {
    const nextCatalog = await fetchCatalog();
    if (!requests.acceptCatalog(sequence)) return;
    catalog = nextCatalog;
    if (catalog.error) {
      payload = { error: catalog.error };
      renderNavigation();
      render();
      return;
    }
    if (activeView === null) activeView = requestedView();
    const previousView = activeView;
    if (!knownView(activeView)) {
      activeView = catalog.views[0]?.id ?? null;
    }
    if (activeView !== previousView) clearViewState();
    let restored = null;
    let cell = null;
    if (!revealedInitialView) {
      revealedInitialView = true;
      expandAncestors(activeView);
      cell = takeRequestedCell();
      restored = cell ? null : savedViewState(activeView);
      if (restored) restoreViewState(restored);
    }
    if (activeView === null) {
      payload = { error: 'No view.yaml files were found beneath this book root.' };
      renderNavigation();
      render();
      return;
    }
    updateUrl(activeView, true);
    renderNavigation();
    const loaded = await load();
    if (loaded && restored) revealViewState(restored);
    if (loaded && cell) openCoordinate(cell.line, cell.column);
  } catch (error) {
    if (!requests.acceptCatalog(sequence)) return;
    catalog = { name: '', views: [] };
    payload = {
      error: `${error.message} The server may need to be rebuilt and restarted after a C# change.`
    };
    renderNavigation();
    render();
  }
}

function renderNavigation() {
  const name = catalog?.name ?? '';
  const shortName = catalog?.shortName ?? '';
  document.title = shortName ? `Libreta | ${shortName}` : 'Libreta';
  el('book-short-name').textContent = shortName;
  el('book-short-name').title = name !== shortName ? name : '';
  const container = el('view-tree');
  const navigation = document.querySelector('.navigation');
  const scrollTop = navigation.scrollTop;
  container.innerHTML = '';
  renderTreeChildren(container, viewTree(catalog?.views ?? []));
  navigation.scrollTop = scrollTop;
}

function viewTree(views) {
  const root = { children: new Map(), view: null, key: '' };
  for (const view of views) {
    let node = root;
    for (const segment of view.id.split('/')) {
      const key = node.key ? `${node.key}/${segment}` : segment;
      if (!node.children.has(segment)) {
        node.children.set(segment, { children: new Map(), view: null, segment, key });
      }
      node = node.children.get(segment);
    }
    node.view = view;
  }
  return root;
}

function treeOrder(node) {
  const ids = node.view ? [node.view.id] : [];
  for (const child of node.children.values()) ids.push(...treeOrder(child));
  return ids;
}

function renderTreeChildren(container, node) {
  const list = document.createElement('ul');
  list.setAttribute('role', node.key ? 'group' : 'tree');
  for (const child of node.children.values()) {
    const item = document.createElement('li');
    item.setAttribute('role', 'none');
    const row = document.createElement('div');
    row.className = 'tree-entry';
    const title = child.view?.title ?? labelFor(child.segment);
    if (child.view) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'tree-view';
      button.textContent = child.view.title;
      button.title = child.view.title;
      button.dataset.view = child.view.id;
      button.setAttribute('role', 'treeitem');
      const active = child.view.id === activeView;
      button.classList.toggle('active', active);
      if (active) button.setAttribute('aria-current', 'page');
      button.setAttribute('aria-selected', String(active));
      button.tabIndex = active ? 0 : -1;
      button.addEventListener('click', async () => {
        await openView(child.view.id);
      });
      row.appendChild(button);
    } else {
      const label = document.createElement('div');
      label.className = 'tree-label';
      label.textContent = title;
      row.appendChild(label);
    }
    // A folder without a view starts open: its label cannot be clicked open.
    const expanded = branchStates.get(child.key) ?? !child.view;
    if (child.children.size) {
      const toggle = document.createElement('button');
      toggle.type = 'button';
      toggle.className = 'tree-toggle';
      toggle.textContent = expanded ? '⌄' : '›';
      toggle.tabIndex = -1;
      toggle.setAttribute('aria-label', `${expanded ? 'Collapse' : 'Expand'} ${title}`);
      toggle.setAttribute('aria-expanded', String(expanded));
      row.querySelector('.tree-view')?.setAttribute('aria-expanded', String(expanded));
      toggle.addEventListener('click', () => setBranchExpanded(child.key, !expanded));
      row.appendChild(toggle);
    }
    item.appendChild(row);
    if (child.children.size && expanded) renderTreeChildren(item, child);
    list.appendChild(item);
  }
  container.appendChild(list);
}

// Only ever expands, so the reader's own expansions survive navigation.
function expandAncestors(id) {
  if (!id) return;
  const segments = id.split('/');
  for (let depth = 1; depth <= segments.length; depth += 1) {
    branchStates.set(segments.slice(0, depth).join('/'), true);
  }
}

// A line no table shows has a view of its own, which the server builds on request and an export carries.
const LINE_VIEW = 'line:';

function knownView(id) {
  if (id?.startsWith(LINE_VIEW)) return !snapshot || Boolean(snapshot.views[id]);
  return catalog?.views.some((view) => view.id === id) ?? false;
}

function viewTitle(id) {
  return catalog?.views.find((view) => view.id === id)?.title;
}

function labelFor(segment) {
  return segment.split('-').map((word) => word.charAt(0).toUpperCase() + word.slice(1)).join(' ');
}

async function openView(id, replace = false, restored = null) {
  if (catalog?.error) return;
  const sameView = id === activeView && payload !== null;
  if (sameView && !restored) return;
  if (!replace) rememberHistoryEntry();
  activeView = id;
  if (restored) restoreViewState(restored);
  else clearViewState();
  expandAncestors(id);
  updateUrl(id, replace);
  renderNavigation();
  if (sameView) render();
  else if (!await load()) return;
  if (restored) revealViewState(restored);
}

// A followed reference is a step back undoes, even when it stays in the same view.
async function followReference(view, line, column, replace = false) {
  if (view && view !== activeView) {
    await openView(view, replace);
    return openCoordinate(line, column);
  }
  if (locate(line, column || null) === null) return false;
  if (!replace) rememberHistoryEntry();
  updateUrl(activeView, replace);
  return openCoordinate(line, column);
}

// An export keeps the view in the fragment: browsers disagree on letting a file: URL change its query.
function clearViewState() {
  selection = null;
  cellRanges = [];
  sensitivitySelection = null;
  checksExpanded = null;
}

function requestedView() {
  const url = new URL(location.href);
  if (!snapshot) return url.searchParams.get('view');
  return snapshotCell(url.hash, snapshot.lines)?.view ?? new URLSearchParams(url.hash.slice(1)).get('view');
}

// A page linking into an export names a cell in the fragment, which has no server to ask which view presents it.
function snapshotCell(hash, lines) {
  const params = new URLSearchParams(hash.slice(1));
  const name = params.get('line');
  if (!name) return null;
  const line = name.replaceAll('/', '.').replaceAll('-', '_');
  const column = params.get('column');
  const presented = lines?.[line];
  return { view: params.get('view') ?? presented?.columns?.[column] ?? presented?.view ?? null, line, column };
}

// A tool opening the book at a cell names it once in the URL, and a reload keeps only the view.
function takeRequestedCell() {
  if (snapshot) return snapshotCell(location.hash, snapshot.lines);
  const url = new URL(location.href);
  const line = url.searchParams.get('line');
  if (!line) return null;
  const cell = { line, column: url.searchParams.get('column') };
  url.searchParams.delete('line');
  url.searchParams.delete('column');
  history.replaceState(history.state, '', url);
  return cell;
}

function updateUrl(id, replace) {
  const url = new URL(location.href);
  if (snapshot) url.hash = new URLSearchParams({ view: id }).toString();
  else url.searchParams.set('view', id);
  if (!replace) historyEntry = newHistoryEntry();
  history[replace ? 'replaceState' : 'pushState']({ entry: historyEntry }, '', url);
}

// Each history entry keeps the selection and scroll it was left with, so back and forward return to them.
// They live in memory rather than history.state, which browsers throttle too hard to write on every arrow key.
function newHistoryEntry() {
  return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function readHistoryStates() {
  try {
    return new Map(JSON.parse(sessionStorage.getItem('historyStates')) ?? []);
  } catch {
    return new Map();
  }
}

function rememberHistoryEntry() {
  if (activeView === null || payload === null) return;
  const viewport = document.querySelector('.table-viewport');
  historyStates.delete(historyEntry);
  historyStates.set(historyEntry, JSON.parse(JSON.stringify({
    view: activeView, selection, cellRanges, sensitivitySelection, checksExpanded, calculationTrail,
    scroll: { left: viewport.scrollLeft, top: viewport.scrollTop }
  })));
  while (historyStates.size > 100) historyStates.delete(historyStates.keys().next().value);
}

function persistHistoryStates() {
  rememberHistoryEntry();
  try {
    sessionStorage.setItem('historyStates', JSON.stringify([...historyStates]));
  } catch {}
}

function savedViewState(id) {
  const state = historyStates.get(historyEntry);
  return state?.view === id ? state : null;
}

function restoreViewState(state) {
  selection = state.selection;
  cellRanges = state.cellRanges;
  sensitivitySelection = state.sensitivitySelection;
  checksExpanded = state.checksExpanded;
  calculationTrail = state.calculationTrail;
}

function revealViewState(state) {
  if (selection !== null) focusGridCell();
  else el('sensitivities').querySelector('td.selected')?.focus({ preventScroll: true });
  const viewport = document.querySelector('.table-viewport');
  viewport.scrollLeft = state.scroll.left;
  viewport.scrollTop = state.scroll.top;
}

function render() {
  const error = el('error');
  error.hidden = !payload.error;
  error.textContent = payload.error ?? '';

  el('title').textContent = payload.title ?? '';
  el('subtitle').textContent = payload.subtitle ?? '';

  const head = el('head');
  const columns = payload.columns ?? [];
  const tableWidth = `calc(var(--label-width) + ${columns.length} * var(--period-width))`;
  document.querySelector('.table-viewport').style.setProperty('--table-width', tableWidth);
  head.innerHTML = '<th></th>' + columns.map((column, index) =>
    `<th class="${columnStyle(column)}" data-column="${index}" ` +
    `title="${escapeHtml(column.label)}">${escapeHtml(column.label)}</th>`).join('');
  head.firstElementChild.appendChild(labelResizer);
  head.querySelectorAll('[data-column]').forEach((cell) => {
    const periodIndex = Number(cell.dataset.column);
    cell.setAttribute('aria-label', `${columns[periodIndex].label} overview`);
    cell.addEventListener('pointerdown', (event) => beginCellRangePointer(event, COLUMN_AXIS, periodIndex));
    cell.addEventListener('click', () => {
      if (suppressCellClick) suppressCellClick = false;
      else selectColumn(periodIndex, true);
    });
    cell.addEventListener('keydown', (event) => handleGridKey(event, COLUMN_AXIS, periodIndex));
  });

  const body = el('body');
  body.innerHTML = '';
  cellNodes.clear();
  for (const [index, row] of (payload.rows ?? []).entries()) {
    const tr = document.createElement('tr');
    const sourceView = knownView(row.sourceView) ? row.sourceView : null;
    tr.className = [row.style, row.kind, sourceView ? 'linked-row' : ''].filter(Boolean).join(' ');
    if (row.kind === 'spacer') {
      // One cell per column, so a selection spanning the gap can shade it.
      tr.innerHTML = `<td></td>${columns.map(() => '<td></td>').join('')}`;
      tr.dataset.spacerIndex = index;
      tr.setAttribute('aria-hidden', 'true');
      body.appendChild(tr);
      continue;
    }
    if (row.kind === 'header') {
      tr.innerHTML = `<th colspan="${columns.length + 1}"><span>${escapeHtml(row.label)}</span></th>`;
      body.appendChild(tr);
      continue;
    }
    tr.innerHTML = `<th data-column="-1" title="${escapeHtml(row.label)}">${escapeHtml(row.label)}</th>` +
      row.cells.map((cell, index) => `<td class="${[cell.kind, cell.unresolved ? 'unresolved' : '',
        cell.linked ? 'linked' : '', columnStyle(columns[index])].filter(Boolean).join(' ')}" ` +
        `data-column="${index}">${cell.display}</td>`).join('');
    tr.dataset.name = row.name;
    tr.dataset.rowIndex = index;
    tr.style.setProperty('--indent-level', row.indent ?? 0);
    tr.querySelectorAll('[data-column]').forEach((cell) => {
      const periodIndex = Number(cell.dataset.column);
      const isRowLabel = periodIndex === -1;
      if (!isRowLabel) cellNodes.set(coordinateKey(row.cells[periodIndex].line, row.cells[periodIndex].column), cell);
      cell.setAttribute('aria-label', isRowLabel
        ? `${row.label} overview`
        : `${row.label}, ${columns[periodIndex].label}, ${row.cells[periodIndex].display}`);
      cell.addEventListener('pointerdown', (event) => beginCellRangePointer(event, index, periodIndex));
      cell.addEventListener('click', (event) => {
        if (suppressCellClick) suppressCellClick = false;
        else if (isRowLabel) selectRow(index, true);
        else if (event.shiftKey) extendCellRange(index, periodIndex, true);
        else inspectCell(index, periodIndex);
      });
      cell.addEventListener('keydown', (event) => handleGridKey(event, index, periodIndex));
    });
    body.appendChild(tr);
  }

  syncGridSelection();
  renderChecks();
  renderSensitivities();
  renderPanel();
  highlightTableCalculation();
}

function renderSensitivities() {
  const container = el('sensitivities');
  container.innerHTML = '';
  for (const sensitivity of payload.sensitivities ?? []) {
    const section = document.createElement('section');
    section.className = 'sensitivity';
    section.dataset.sensitivity = sensitivity.name;
    const horizontal = sensitivity.inputs[0];
    const vertical = sensitivity.inputs[1] ?? null;
    const rows = sensitivity.cells;
    const heading = document.createElement('div');
    heading.className = 'sensitivity-heading';
    heading.innerHTML = sensitivityHeadingHtml(sensitivity);
    section.appendChild(heading);

    const table = document.createElement('table');
    table.className = 'sensitivity-table';
    table.setAttribute('role', 'grid');
    table.innerHTML = `<thead><tr><th class="sensitivity-output" scope="col" rowspan="2" ` +
      `colspan="${vertical ? 2 : 1}">${sensitivityCoordinateLink(sensitivity.outputLabel, sensitivity.outputLine, sensitivity.outputColumn, sensitivity.outputSourceView)}</th>` +
      `<th class="sensitivity-column-axis" scope="colgroup" colspan="${horizontal.displays.length}">` +
      `${sensitivityCoordinateLink(horizontal.label, horizontal.line, horizontal.column, horizontal.sourceView)}</th></tr><tr>` +
      horizontal.displays.map((display, columnIndex) =>
        `<th scope="col" data-column="${columnIndex}">${escapeHtml(display)}</th>`).join('') +
      '</tr></thead><tbody></tbody>';
    const body = table.querySelector('tbody');
    rows.forEach((cells, rowIndex) => {
      const row = document.createElement('tr');
      const rowLabel = vertical ? vertical.displays[rowIndex] : sensitivity.outputLabel;
      row.innerHTML = (vertical && rowIndex === 0
        ? `<th class="sensitivity-row-axis" scope="rowgroup" rowspan="${rows.length}">` +
          `<span>${sensitivityCoordinateLink(vertical.label, vertical.line, vertical.column, vertical.sourceView)}</span></th>`
        : '') + `<th scope="row" data-row="${rowIndex}">${escapeHtml(rowLabel)}</th>`;
      cells.forEach((cell, columnIndex) => {
        const td = document.createElement('td');
        if (!cell.valid) td.className = 'invalid';
        td.textContent = cell.display;
        td.tabIndex = rowIndex === 0 && columnIndex === 0 ? 0 : -1;
        td.dataset.row = String(rowIndex);
        td.dataset.column = String(columnIndex);
        const assumptions = sensitivity.inputs.map((input, inputIndex) =>
          `${input.label}: ${input.displays[inputIndex === 0 ? columnIndex : rowIndex]}`).join('; ');
        const exactOutput = cell.exact == null ? cell.display : groupExactNumber(cell.exact);
        td.setAttribute('aria-label', `${assumptions}; ${sensitivity.outputLabel}: ${cell.display}`);
        td.setAttribute('aria-selected', 'false');
        td.title = `${assumptions}\n${sensitivity.outputLabel}: ${exactOutput}`;
        td.addEventListener('click', () => selectSensitivityCell(sensitivity, section, rowIndex, columnIndex));
        td.addEventListener('keydown', (event) =>
          handleSensitivityKey(event, sensitivity, section, rowIndex, columnIndex));
        row.appendChild(td);
      });
      body.appendChild(row);
    });
    const tableScroll = document.createElement('div');
    tableScroll.className = 'sensitivity-table-scroll';
    tableScroll.tabIndex = -1;
    tableScroll.appendChild(table);
    section.appendChild(tableScroll);
    bindSensitivityLinks(section);
    if (sensitivitySelection?.name === sensitivity.name) {
      updateSensitivitySelection(section, sensitivitySelection.row, sensitivitySelection.column);
    }
    container.appendChild(section);
  }
}

function sensitivityCoordinateLink(label, line, column, sourceView) {
  if (!sourceView) return escapeHtml(label);
  return `<button type="button" class="sensitivity-coordinate-link" data-name="${escapeHtml(line)}" ` +
    `data-column-key="${escapeHtml(column)}" data-source-view="${escapeHtml(sourceView)}" ` +
    `title="Open ${escapeHtml(label)} cell">${escapeHtml(label)}</button>`;
}

function bindSensitivityLinks(container) {
  container.querySelectorAll('.sensitivity-coordinate-link').forEach((node) => {
    node.addEventListener('click', () => followReference(node.dataset.sourceView, node.dataset.name, node.dataset.columnKey));
  });
}

function sensitivityHeadingHtml(sensitivity) {
  const subtitle = sensitivity.outputUnits ? `<p>${escapeHtml(unitLabel(sensitivity.outputUnits))}</p>` : '';
  return `<div><h2>${escapeHtml(sensitivity.title)}</h2>${subtitle}</div>`;
}

function selectSensitivityCell(sensitivity, section, row, column, focus = true) {
  selection = null;
  cellRanges = [];
  checksExpanded = false;
  sensitivitySelection = { name: sensitivity.name, row, column };
  clearSelection();
  updateSensitivitySelection(section, row, column);
  if (focus) {
    const cell = section.querySelector(`td[data-row="${row}"][data-column="${column}"]`);
    cell?.focus({ preventScroll: true });
    scrollSensitivityCellIntoView(cell);
  }
}

function scrollSensitivityCellIntoView(cell) {
  const scroller = cell?.closest('.sensitivity-table-scroll');
  if (!scroller) return;
  const scrollerRect = scroller.getBoundingClientRect();
  const cellRect = cell.getBoundingClientRect();
  const frozenHeader = scroller.querySelector('tbody tr:first-child > th:last-of-type');
  const leftEdge = Math.max(scrollerRect.left, frozenHeader?.getBoundingClientRect().right ?? scrollerRect.left);
  if (cellRect.right > scrollerRect.right) {
    scroller.scrollLeft += cellRect.right - scrollerRect.right;
  } else if (cellRect.left < leftEdge) {
    scroller.scrollLeft -= leftEdge - cellRect.left;
  }
}

function updateSensitivitySelection(section, row, column) {
  document.querySelectorAll('.sensitivity').forEach((otherSection) => {
    otherSection.querySelectorAll('.selected, .cross-selected').forEach((node) => {
      node.classList.remove('selected', 'cross-selected');
      node.setAttribute('aria-selected', 'false');
    });
  });
  section.querySelectorAll(`[data-row="${row}"], [data-column="${column}"]`).forEach((node) => {
    node.classList.add('cross-selected');
  });
  section.querySelectorAll('td[data-row]').forEach((node) => {
    const selected = Number(node.dataset.row) === row && Number(node.dataset.column) === column;
    node.classList.toggle('selected', selected);
    node.tabIndex = selected ? 0 : -1;
    node.setAttribute('aria-selected', String(selected));
  });
}

function handleSensitivityKey(event, sensitivity, section, row, column) {
  if (event.altKey) return;
  let nextRow = row;
  let nextColumn = column;
  if (event.key === 'ArrowLeft') nextColumn -= 1;
  else if (event.key === 'ArrowRight') nextColumn += 1;
  else if (event.key === 'ArrowUp') nextRow -= 1;
  else if (event.key === 'ArrowDown') nextRow += 1;
  else if (event.key === 'Home') nextColumn = 0;
  else if (event.key === 'End') nextColumn = sensitivity.cells[row].length - 1;
  else if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault();
    selectSensitivityCell(sensitivity, section, row, column, false);
    return;
  } else if (event.key === 'Escape') {
    event.preventDefault();
    sensitivitySelection = null;
    renderSensitivities();
    renderPanel();
    el('sensitivities').querySelector('td[data-row="0"][data-column="0"]')?.focus();
    return;
  } else {
    return;
  }
  nextRow = Math.max(0, Math.min(sensitivity.cells.length - 1, nextRow));
  nextColumn = Math.max(0, Math.min(sensitivity.cells[nextRow].length - 1, nextColumn));
  event.preventDefault();
  selectSensitivityCell(sensitivity, section, nextRow, nextColumn);
}

function renderChecks() {
  const checks = payload.checks ?? [];
  if (!checks.length) {
    el('checks').classList.remove('expanded');
    el('checks').innerHTML = '';
    return;
  }
  const failed = checks.filter((c) => !c.passed).length;
  if (checksExpanded === null) checksExpanded = failed > 0;
  const container = el('checks');
  container.classList.toggle('expanded', checksExpanded);
  container.innerHTML = `<details${checksExpanded ? ' open' : ''}>
    <summary>
      <span>Checks</span>
      <span class="check-count ${failed ? 'fail' : 'pass'}">${checks.length - failed}/${checks.length} pass</span>
    </summary>
    <div class="check-list">${checks.map((c) => `<details class="check ${c.passed ? 'pass' : 'fail'}">
      <summary>
        <span class="badge">${c.passed ? 'PASS' : 'FAIL'}</span>
        <span>${escapeHtml(c.name)}</span>
      </summary>
      <div class="check-detail">
        <span class="check-label">Equation</span>
        <code>${escapeHtml(formatCheckEquation(c))}</code>
        ${c.note ? `<span class="check-note">${escapeHtml(c.note)}</span>` : ''}
        ${c.passed ? '' : `<span class="check-deltas">${escapeHtml(formatCheckDeltas(c))}</span>`}
      </div>
    </details>`).join('')}</div>
  </details>`;
  container.querySelector(':scope > details').addEventListener('toggle', (event) => {
    checksExpanded = event.currentTarget.open;
    container.classList.toggle('expanded', checksExpanded);
    if (!checksExpanded || selection === null) return;
    clearSelection();
  });
}

function formatCheckDeltas(check) {
  if (!check.periods.length) return 'No period carries its facts';
  return check.periods.map((p, i) => `${p} off by ${check.deltas[i]}`).join(', ');
}

function formatCheckEquation(check) {
  const suffix = ` - ${check.name}`;
  const equation = check.formula.endsWith(suffix)
    ? `${check.formula.slice(0, -suffix.length)} = ${check.name}`
    : `${check.formula} = 0`;
  const labels = new Map([...(payload.rows ?? []), ...(payload.columns ?? [])]
    .map((entry) => [entry.name, entry.label]));
  return equation
    .replace(/\b[a-zA-Z_][a-zA-Z0-9_]*\b/g, (name) => labels.get(name) ?? name.replaceAll('_', ' '))
    .replaceAll(' - ', ' − ')
    .replaceAll(' * ', ' × ')
    .replaceAll(' / ', ' ÷ ');
}

// Held by index, not name: a view may list the same line twice.
function selectedEntry() {
  if (selection === null) return null;
  return selection.index === COLUMN_AXIS
    ? (payload.columns ?? [])[selection.periodIndex] ?? null
    : (payload.rows ?? [])[selection.index] ?? null;
}

function selectedCell() {
  if (selection === null || selection.index === COLUMN_AXIS || selection.periodIndex === null) return null;
  return (payload.rows ?? [])[selection.index]?.cells?.[selection.periodIndex] ?? null;
}

// The axis entry naming a cell's line: its row, or its column when the view is transposed.
function lineEntry(rowIndex, periodIndex) {
  const row = (payload.rows ?? [])[rowIndex];
  if (row && LINE_KINDS.has(row.kind)) return row;
  return (payload.columns ?? [])[periodIndex] ?? row ?? null;
}

function contextEntry(rowIndex, periodIndex) {
  const row = (payload.rows ?? [])[rowIndex];
  const column = (payload.columns ?? [])[periodIndex] ?? null;
  return row && LINE_KINDS.has(row.kind) ? column : row ?? null;
}

// Shared by cellNodes and calculation references, so one finds the other.
function coordinateKey(line, column) {
  return `${line}\u0000${column ?? ''}`;
}

function columnStyle(column) {
  return column?.style ? `col-${column.style}` : '';
}

// A whole row or column matches only when that axis entry is the line itself.
function locate(line, column) {
  const columns = payload.columns ?? [];
  for (const [index, row] of (payload.rows ?? []).entries()) {
    for (const [periodIndex, cell] of (row.cells ?? []).entries()) {
      if (cell.line !== line) continue;
      if (column) {
        if (cell.column === column) return { index, periodIndex };
        continue;
      }
      if (row.name === line) return { index, periodIndex: null };
      if (columns[periodIndex]?.name === line) return { index: COLUMN_AXIS, periodIndex };
      return { index, periodIndex };
    }
  }
  return null;
}

function selectInGrid(nextSelection, ranges) {
  checksExpanded = false;
  sensitivitySelection = null;
  selection = nextSelection;
  cellRanges = ranges;
}

function selectRow(index, focus = false) {
  selectInGrid({ index, periodIndex: null }, []);
  render();
  if (focus) focusGridCell(index, null);
}

function selectColumn(periodIndex, focus = false) {
  selectInGrid({ index: COLUMN_AXIS, periodIndex }, []);
  render();
  if (focus) focusGridCell(COLUMN_AXIS, periodIndex);
}

function inspectCell(index, periodIndex) {
  selectInGrid({ index, periodIndex }, [{ anchor: { index, periodIndex }, active: { index, periodIndex } }]);
  render();
  focusGridCell(index, periodIndex);
}

function validCellCoordinate(coordinate) {
  if (!coordinate || !Number.isInteger(coordinate.index) || !Number.isInteger(coordinate.periodIndex)) return false;
  const columns = payload?.columns ?? [];
  if (coordinate.periodIndex < -1 || coordinate.periodIndex >= columns.length) return false;
  if (coordinate.index === COLUMN_AXIS) return true;
  const row = (payload?.rows ?? [])[coordinate.index];
  if (!row?.cells?.length) return false;
  return coordinate.periodIndex === -1 || Boolean(row.cells[coordinate.periodIndex]);
}

function validCellRegion(region) {
  return validCellCoordinate(region?.anchor) && validCellCoordinate(region?.active);
}

function regionBounds(region) {
  if (!validCellRegion(region)) return null;
  return {
    firstRow: Math.min(region.anchor.index, region.active.index),
    lastRow: Math.max(region.anchor.index, region.active.index),
    firstColumn: Math.min(region.anchor.periodIndex, region.active.periodIndex),
    lastColumn: Math.max(region.anchor.periodIndex, region.active.periodIndex)
  };
}

function cellRangeBounds() {
  return cellRanges.map(regionBounds).filter(Boolean);
}

// Header and spacer rows carry no cells, so a range skips them.
function regionRowIndexes(bounds) {
  const indexes = bounds.firstRow === COLUMN_AXIS ? [COLUMN_AXIS] : [];
  (payload?.rows ?? []).forEach((row, index) => {
    if (row.cells?.length && index >= bounds.firstRow && index <= bounds.lastRow) indexes.push(index);
  });
  return indexes;
}

function gridKey(index, periodIndex) {
  return `${index}:${periodIndex}`;
}

function selectedCoordinates() {
  const seen = new Set();
  const coordinates = [];
  for (const bounds of cellRangeBounds()) {
    for (const index of regionRowIndexes(bounds)) {
      for (let column = bounds.firstColumn; column <= bounds.lastColumn; column++) {
        const key = gridKey(index, column);
        if (seen.has(key)) continue;
        seen.add(key);
        coordinates.push({ index, periodIndex: column });
      }
    }
  }
  return coordinates;
}

function selectedRangeCells() {
  return selectedCoordinates()
    .filter(({ index, periodIndex }) => index >= 0 && periodIndex >= 0)
    .map(({ index, periodIndex }) => payload.rows[index].cells[periodIndex]);
}

function isMultiCellRange() {
  return selectedCoordinates().length > 1;
}

function lastCellRegion() {
  return cellRanges[cellRanges.length - 1] ?? null;
}

function gridCellNode(index, periodIndex) {
  if (index === COLUMN_AXIS) {
    const head = el('head');
    return periodIndex === -1 ? head.firstElementChild : head.querySelector(`[data-column="${periodIndex}"]`);
  }
  return el('body').querySelector(`tr[data-row-index="${index}"] [data-column="${periodIndex}"]`);
}

function syncGridSelection() {
  const head = el('head');
  const body = el('body');
  [head, body].forEach((section) => {
    section.querySelectorAll('.selected, .cell-selected, .selected-cell, .selected-column, .range-selected, ' +
      '.range-top, .range-right, .range-bottom, .range-left')
      .forEach((node) => node.classList.remove('selected', 'cell-selected', 'selected-cell', 'selected-column',
        'range-selected', 'range-top', 'range-right', 'range-bottom', 'range-left'));
    section.querySelectorAll('[data-column]').forEach((cell) => {
      cell.tabIndex = -1;
      cell.removeAttribute('aria-selected');
    });
  });

  // Edges go where a neighbour is unselected, so touching regions read as one outline.
  const coordinates = selectedCoordinates();
  const keys = new Set(coordinates.map(({ index, periodIndex }) => gridKey(index, periodIndex)));
  const rowOrder = [COLUMN_AXIS, ...(payload?.rows ?? []).map((row, index) => (row.cells?.length ? index : null))
    .filter((index) => index !== null)];
  for (const { index, periodIndex } of coordinates) {
    const cell = gridCellNode(index, periodIndex);
    if (!cell) continue;
    const position = rowOrder.indexOf(index);
    const above = rowOrder[position - 1];
    const below = rowOrder[position + 1];
    cell.classList.add('range-selected');
    if (position <= 0 || !keys.has(gridKey(above, periodIndex))) cell.classList.add('range-top');
    if (below === undefined || !keys.has(gridKey(below, periodIndex))) cell.classList.add('range-bottom');
    if (!keys.has(gridKey(index, periodIndex - 1))) cell.classList.add('range-left');
    if (!keys.has(gridKey(index, periodIndex + 1))) cell.classList.add('range-right');
    cell.setAttribute('aria-selected', 'true');
  }

  const columnCount = (payload?.columns ?? []).length;
  body.querySelectorAll('tr.spacer[data-spacer-index]').forEach((tr) => {
    const index = Number(tr.dataset.spacerIndex);
    const above = rowOrder.filter((row) => row >= 0 && row < index).pop();
    const below = rowOrder.find((row) => row > index);
    const spans = (column) => above !== undefined && below !== undefined
      && keys.has(gridKey(above, column)) && keys.has(gridKey(below, column));
    for (let column = -1; column < columnCount; column++) {
      if (!spans(column)) continue;
      const cell = tr.children[column + 1];
      cell.classList.add('range-selected');
      if (!spans(column - 1)) cell.classList.add('range-left');
      if (!spans(column + 1)) cell.classList.add('range-right');
    }
  });

  if (selection?.index === COLUMN_AXIS) {
    head.querySelector(`[data-column="${selection.periodIndex}"]`)?.classList.add('selected');
    body.querySelectorAll(`td[data-column="${selection.periodIndex}"]`)
      .forEach((cell) => cell.classList.add('selected-column'));
  } else if (selection?.periodIndex === null) {
    body.querySelector(`tr[data-row-index="${selection.index}"]`)?.classList.add('selected');
  } else if (validCellCoordinate(selection)) {
    const tr = body.querySelector(`tr[data-row-index="${selection.index}"]`);
    tr?.classList.add('cell-selected');
    tr?.querySelector(`td[data-column="${selection.periodIndex}"]`)?.classList.add('selected-cell');
    head.querySelector(`[data-column="${selection.periodIndex}"]`)?.classList.add('cell-selected');
  }

  const active = selection?.index === COLUMN_AXIS ? head : body.querySelector(`tr[data-row-index="${selection?.index}"]`);
  const activeCell = active?.querySelector(`[data-column="${selection?.periodIndex ?? -1}"]`);
  if (activeCell) {
    activeCell.tabIndex = 0;
    activeCell.setAttribute('aria-selected', 'true');
  } else if (selection === null) {
    const fallback = gridCellNode(lastCellRegion()?.active?.index ?? NaN, lastCellRegion()?.active?.periodIndex ?? NaN)
      ?? body.querySelector('tr[data-name] [data-column="-1"]');
    fallback?.setAttribute('tabindex', '0');
  }
}

// Labels and period headers are selectable but have nothing to inspect.
function selectionFor(coordinate) {
  return coordinate.index >= 0 && coordinate.periodIndex >= 0 ? { ...coordinate } : null;
}

function extendCellRange(index, periodIndex, focus = false) {
  const previous = lastCellRegion();
  const anchor = validCellCoordinate(previous?.anchor)
    ? previous.anchor
    : validCellCoordinate(selection) ? selection : { index, periodIndex };
  selectInGrid(selectionFor({ index, periodIndex }),
    [...cellRanges.slice(0, -1), { anchor: { ...anchor }, active: { index, periodIndex } }]);
  render();
  if (focus) focusGridCell(index, periodIndex);
}

function cellAtPoint(x, y) {
  const cell = document.elementFromPoint(x, y)?.closest('.book-table [data-column]');
  if (!cell) return null;
  const inHead = Boolean(cell.closest('thead'));
  const index = inHead ? COLUMN_AXIS : Number(cell.closest('tr')?.dataset.rowIndex);
  const periodIndex = Number(cell.dataset.column);
  return validCellCoordinate({ index, periodIndex }) ? { index, periodIndex } : null;
}

function beginCellRangePointer(event, index, periodIndex) {
  // A finger drag is a scroll; the click that follows a tap selects the cell instead.
  if (event.button !== 0 || !event.isPrimary || event.pointerType === 'touch') return;
  event.preventDefault();
  const cell = event.currentTarget;
  const additive = event.ctrlKey || event.metaKey;
  const previous = cellRanges.filter(validCellRegion)
    .map((region) => ({ anchor: { ...region.anchor }, active: { ...region.active } }));
  const base = additive ? previous : event.shiftKey ? previous.slice(0, -1) : [];
  const anchor = event.shiftKey && !additive && validCellCoordinate(lastCellRegion()?.anchor)
    ? { ...lastCellRegion().anchor }
    : { index, periodIndex };
  const region = { anchor, active: { index, periodIndex } };
  // A plain press on a label or period header stays a click (select the row or column) until it drags.
  const axis = index === COLUMN_AXIS || periodIndex === -1;
  let committed = false;
  let pointer = { x: event.clientX, y: event.clientY };
  let frame = null;
  let ended = false;

  const commit = () => {
    committed = true;
    selectInGrid(selectionFor(region.active), [...base, region]);
    syncGridSelection();
  };
  if (!axis || additive || event.shiftKey) commit();
  document.body.classList.add('selecting-cells');
  cell.setPointerCapture(event.pointerId);

  const update = () => {
    if (ended) return;
    const viewport = document.querySelector('.table-viewport');
    const viewportRect = viewport.getBoundingClientRect();
    const threshold = 32;
    const vertical = pointer.y < viewportRect.top + threshold ? -12
      : pointer.y > viewportRect.bottom - threshold ? 12 : 0;
    const horizontal = pointer.x < viewportRect.left + threshold ? -12
      : pointer.x > viewportRect.right - threshold ? 12 : 0;
    if (vertical) viewport.scrollTop += vertical;
    if (horizontal) viewport.scrollLeft += horizontal;
    const x = Math.max(viewportRect.left + 2, Math.min(viewportRect.right - 2, pointer.x));
    const y = Math.max(viewportRect.top + 2, Math.min(viewportRect.bottom - 2, pointer.y));
    const coordinate = cellAtPoint(x, y);
    if (coordinate && (coordinate.index !== region.active.index
      || coordinate.periodIndex !== region.active.periodIndex)) {
      region.active = coordinate;
      if (committed) {
        selection = selectionFor(coordinate);
        cellRanges = [...base, region];
        syncGridSelection();
      } else {
        commit();
      }
    }
    frame = requestAnimationFrame(update);
  };
  const move = (moved) => {
    pointer = { x: moved.clientX, y: moved.clientY };
  };
  const end = () => {
    ended = true;
    if (frame !== null) cancelAnimationFrame(frame);
    cell.removeEventListener('pointermove', move);
    cell.removeEventListener('pointerup', end);
    cell.removeEventListener('pointercancel', end);
    document.body.classList.remove('selecting-cells');
    if (!committed) return;
    suppressCellClick = true;
    setTimeout(() => { suppressCellClick = false; });
    renderPanel();
    highlightTableCalculation();
    focusGridCell();
  };
  cell.addEventListener('pointermove', move);
  cell.addEventListener('pointerup', end);
  cell.addEventListener('pointercancel', end);
  frame = requestAnimationFrame(update);
}

function clearSelection() {
  selection = null;
  cellRanges = [];
  syncGridSelection();
  renderPanel();
  highlightTableCalculation();
}

function focusGridCell(index = selection?.index, periodIndex = selection?.periodIndex ?? null) {
  const row = index === COLUMN_AXIS ? el('head') : el('body').querySelector(`tr[data-row-index="${index}"]`);
  const cell = row?.querySelector(`[data-column="${periodIndex ?? -1}"]`);
  if (!cell) return;
  cell.focus({ preventScroll: true });
  scrollGridCellIntoView(cell);
}

function scrollGridCellIntoView(cell) {
  const viewport = cell.closest('.table-viewport');
  const table = cell.closest('.book-table');
  const bounds = viewport.getBoundingClientRect();
  const style = getComputedStyle(viewport);
  // the content box: the padding holds the scrollbars clear of the cards, so a cell must not come to rest in it
  const viewportRect = {
    top: bounds.top + viewport.clientTop,
    left: bounds.left + viewport.clientLeft,
    bottom: bounds.top + viewport.clientTop + viewport.clientHeight - parseFloat(style.paddingBottom),
    right: bounds.left + viewport.clientLeft + viewport.clientWidth - parseFloat(style.paddingRight)
  };
  const cellRect = cell.getBoundingClientRect();
  const stickyWidth = table.querySelector('thead th:first-child')?.getBoundingClientRect().width ?? 0;
  const headerHeight = table.tHead?.getBoundingClientRect().height ?? 0;
  let left = viewport.scrollLeft;
  let top = viewport.scrollTop;

  if (cell.dataset.column === '-1') {
    left = 0;
  } else if (Number(cell.dataset.column) === payload.columns.length - 1) {
    left = viewport.scrollWidth;
  } else if (cellRect.left < viewportRect.left + stickyWidth) {
    left -= viewportRect.left + stickyWidth - cellRect.left;
  } else if (cellRect.right > viewportRect.right) {
    left += cellRect.right - viewportRect.right;
  }

  if (Number(cell.closest('tr').dataset.rowIndex) === (payload.rows ?? []).findIndex(hasCells)) {
    top = 0;
  } else if (cellRect.top < viewportRect.top + headerHeight) {
    top -= viewportRect.top + headerHeight - cellRect.top;
  } else if (cellRect.bottom > viewportRect.bottom) {
    top += cellRect.bottom - viewportRect.bottom;
  }

  viewport.scrollTo({ left, top });
}

function moveSelection(index, direction, edge = false) {
  const next = verticalCellTarget(index, direction, edge);
  if (next === index) return;
  const periodIndex = selection?.periodIndex ?? null;
  if (next === COLUMN_AXIS) {
    // Leaving the table upward lands on the column header.
    if (periodIndex !== null) selectColumn(periodIndex, true);
    return;
  }
  const target = { index: next, periodIndex };
  selectInGrid(target, periodIndex === null ? [] : [{ anchor: { ...target }, active: { ...target } }]);
  render();
  focusGridCell();
}

function movePeriod(index, direction) {
  const current = selection?.index === index ? selection.periodIndex ?? -1 : -1;
  const next = Math.max(-1, Math.min(payload.columns.length - 1, current + direction));
  if (next === current) return;
  if (next === -1) selectRow(index, true);
  else inspectCell(index, next);
}

function hasCells(row) {
  return Boolean(row?.cells?.length);
}

function selectTableCorner(key) {
  const rows = payload.rows ?? [];
  if (key === 'Home') selectRow(rows.findIndex(hasCells), true);
  else inspectCell(rows.findLastIndex(hasCells), payload.columns.length - 1);
}

function pageSelection(index, periodIndex, direction, extend) {
  const viewport = document.querySelector('.table-viewport');
  const rowTop = (row) => el('body').querySelector(`tr[data-row-index="${row}"]`).offsetTop;
  const pageHeight = viewport.clientHeight - parseFloat(getComputedStyle(viewport).paddingBottom)
    - el('head').offsetHeight;
  const next = pageCellTarget(index, direction, rowTop, pageHeight);
  if (next === index) return;
  // Scrolling by the distance moved keeps the selection where it sat on screen, as a spreadsheet does.
  viewport.scrollTop += rowTop(next) - rowTop(index);
  if (extend) extendCellRange(next, periodIndex, true);
  else if (periodIndex === -1) selectRow(next, true);
  else inspectCell(next, periodIndex);
}

function pageCellTarget(index, direction, rowTop, pageHeight) {
  const rows = payload.rows ?? [];
  const limit = rowTop(index) + direction * pageHeight;
  let next = index;
  for (let candidate = index + direction; rows[candidate]; candidate += direction) {
    if (!hasCells(rows[candidate])) continue;
    if (next !== index && direction * (rowTop(candidate) - limit) > 0) break;
    next = candidate;
  }
  return next;
}

function verticalCellTarget(index, direction, edge) {
  const rows = payload.rows ?? [];
  let next = index;
  if (edge) {
    while (rows[next + direction]?.cells?.length) next += direction;
    if (next !== index) return next;
  }
  let candidate = index + direction;
  while (rows[candidate] && !rows[candidate].cells?.length) candidate += direction;
  if (rows[candidate]?.cells?.length) return candidate;
  return direction === -1 && candidate < 0 ? COLUMN_AXIS : index;
}

function extendCellRangeByKey(event, index, periodIndex) {
  if (!event.shiftKey || periodIndex < -1) return false;
  const lastColumn = (payload.columns ?? []).length - 1;
  let nextIndex = index;
  let nextColumn = periodIndex;
  if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
    const direction = event.key === 'ArrowRight' ? 1 : -1;
    nextColumn = event.ctrlKey ? (direction === 1 ? lastColumn : -1)
      : Math.max(-1, Math.min(lastColumn, periodIndex + direction));
  } else if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
    const direction = event.key === 'ArrowDown' ? 1 : -1;
    nextIndex = verticalCellTarget(index, direction, event.ctrlKey);
  } else if (event.key === 'Home' || event.key === 'End') {
    nextColumn = event.key === 'Home' ? -1 : lastColumn;
  } else {
    return false;
  }
  event.preventDefault();
  extendCellRange(nextIndex, nextColumn, true);
  return true;
}

function handleGridKey(event, index, periodIndex) {
  if (event.altKey) return;
  if (index === COLUMN_AXIS) {
    handleHeaderKey(event, periodIndex);
    return;
  }
  const key = event.key;
  if (extendCellRangeByKey(event, index, periodIndex)) return;
  const syncSelection = () => {
    if (selection?.index === index && (selection.periodIndex ?? -1) === periodIndex) return;
    if (periodIndex === -1) selectRow(index, true);
    else inspectCell(index, periodIndex);
  };
  if (key === 'ArrowLeft' || key === 'ArrowRight') {
    event.preventDefault();
    syncSelection();
    if (event.ctrlKey) {
      if (key === 'ArrowLeft') selectRow(index, true);
      else inspectCell(index, payload.columns.length - 1);
    } else {
      movePeriod(index, key === 'ArrowRight' ? 1 : -1);
    }
    return;
  }
  if (key === 'ArrowUp' || key === 'ArrowDown') {
    event.preventDefault();
    syncSelection();
    const direction = key === 'ArrowDown' ? 1 : -1;
    moveSelection(index, direction, event.ctrlKey);
    return;
  }
  if (key === 'PageUp' || key === 'PageDown') {
    event.preventDefault();
    pageSelection(index, periodIndex, key === 'PageDown' ? 1 : -1, event.shiftKey);
    return;
  }
  if (key === 'Home' || key === 'End') {
    event.preventDefault();
    if (event.ctrlKey) selectTableCorner(key);
    else if (key === 'Home') selectRow(index, true);
    else inspectCell(index, payload.columns.length - 1);
    return;
  }
  if (key === 'Enter' || key === ' ') {
    event.preventDefault();
    syncSelection();
    return;
  }
  if (key === 'Escape') {
    event.preventDefault();
    clearSelection();
    el('body').querySelector('[data-column="-1"]')?.focus();
  }
}

function handleHeaderKey(event, periodIndex) {
  const key = event.key;
  const last = (payload.columns ?? []).length - 1;
  if (extendCellRangeByKey(event, COLUMN_AXIS, periodIndex)) return;
  if (key === 'ArrowLeft' || key === 'ArrowRight') {
    event.preventDefault();
    const direction = key === 'ArrowRight' ? 1 : -1;
    const next = event.ctrlKey ? (direction === 1 ? last : 0) : periodIndex + direction;
    selectColumn(Math.max(0, Math.min(last, next)), true);
    return;
  }
  if (key === 'ArrowDown') {
    event.preventDefault();
    const index = (payload.rows ?? []).findIndex((row) => row.kind !== 'header' && row.kind !== 'spacer');
    if (index >= 0) inspectCell(index, periodIndex);
    return;
  }
  if (key === 'Home' || key === 'End') {
    event.preventDefault();
    if (event.ctrlKey) selectTableCorner(key);
    else selectColumn(key === 'Home' ? 0 : last, true);
    return;
  }
  if (key === 'Enter' || key === ' ') {
    event.preventDefault();
    selectColumn(periodIndex, true);
    return;
  }
  if (key === 'Escape') {
    event.preventDefault();
    clearSelection();
    el('body').querySelector('[data-column="-1"]')?.focus();
  }
}

function selectedSensitivity() {
  if (sensitivitySelection === null) return null;
  return (payload.sensitivities ?? []).find((item) => item.name === sensitivitySelection.name) ?? null;
}

function sensitivityChartSeries(sensitivity, row, column) {
  const horizontal = sensitivity.inputs[0];
  const vertical = sensitivity.inputs[1] ?? null;
  const values = (cells) => cells.map((cell) => {
    const value = cell.valid && cell.exact != null ? Number(cell.exact) : NaN;
    return Number.isFinite(value) ? value : null;
  });
  const rowCells = sensitivity.cells[row];
  const rowSeries = {
    axisLabel: horizontal.label,
    title: vertical ? `${vertical.label} ${vertical.displays[row]}` : sensitivity.outputLabel,
    labels: horizontal.displays,
    displays: rowCells.map((cell) => cell.display),
    values: values(rowCells),
    selectedIndex: column
  };
  if (!vertical) return { row: rowSeries, column: null };
  const columnCells = sensitivity.cells.map((cells) => cells[column]);
  return {
    row: rowSeries,
    column: {
      axisLabel: vertical.label,
      title: `${horizontal.label} ${horizontal.displays[column]}`,
      labels: vertical.displays,
      displays: columnCells.map((cell) => cell.display),
      values: values(columnCells),
      selectedIndex: row
    }
  };
}

// Bars grow from zero, so the domain always includes it.
function chartDomain(values) {
  const finite = values.filter(Number.isFinite);
  const minimum = Math.min(0, ...finite);
  const maximum = Math.max(0, ...finite);
  return minimum === maximum ? [minimum, minimum + 1] : [minimum, maximum];
}

function chartScale([minimum, maximum], top, bottom) {
  return (value) => top + ((maximum - value) / (maximum - minimum)) * (bottom - top);
}

function renderSensitivityChart(series, outputLabel, domain) {
  const width = 360;
  const y = chartScale(domain, 8, 126);
  const baseline = y(0);
  const band = width / Math.max(series.values.length, 1);
  const barWidth = Math.min(34, band * .62);
  const bars = series.values.map((value, index) => {
    const center = band * (index + .5);
    const label = escapeHtml(series.labels[index]);
    if (value === null) {
      const selected = index === series.selectedIndex ? ' selected' : '';
      return `<g class="sensitivity-chart-missing${selected}"><title>${label}: unavailable</title>` +
        `<line x1="${center - 5}" y1="${baseline - 5}" x2="${center + 5}" y2="${baseline + 5}"></line>` +
        `<line x1="${center + 5}" y1="${baseline - 5}" x2="${center - 5}" y2="${baseline + 5}"></line></g>`;
    }
    const valueY = y(value);
    const top = Math.min(valueY, baseline);
    const height = Math.max(1, Math.abs(baseline - valueY));
    const selected = index === series.selectedIndex ? ' selected' : '';
    return `<rect class="sensitivity-chart-bar${selected}" x="${center - barWidth / 2}" y="${top}" ` +
      `width="${barWidth}" height="${height}" rx="3"><title>${label}: ` +
      `${escapeHtml(series.displays[index])}</title></rect>`;
  }).join('');
  const labels = series.labels.map((label, index) =>
    `<text x="${band * (index + .5)}" y="148">${escapeHtml(label)}</text>`).join('');
  const chartLabel = `${outputLabel} by ${series.axisLabel}, ${series.title}`;
  return `<figure class="sensitivity-chart"><figcaption><strong>${escapeHtml(series.axisLabel)}</strong>` +
    `<span>${escapeHtml(series.title)}</span></figcaption>` +
    `<svg viewBox="0 0 ${width} 158" role="img" aria-label="${escapeHtml(chartLabel)}">` +
    `<line class="sensitivity-chart-baseline" x1="0" y1="${baseline}" x2="${width}" y2="${baseline}"></line>` +
    `${bars}<g class="sensitivity-chart-labels">${labels}</g></svg></figure>`;
}

function renderSensitivityPanel(panel, sensitivity) {
  const { row, column } = sensitivitySelection;
  const cell = sensitivity.cells[row][column];
  const assumptions = sensitivity.inputs.map((input, index) => {
    const displayIndex = index === 0 ? column : row;
    return `<div class="sensitivity-assumption"><dt>${sensitivityCoordinateLink(input.label, input.line, input.column, input.sourceView)}</dt>` +
      `<dd><strong>${escapeHtml(input.displays[displayIndex])}</strong>` +
      `<span class="sensitivity-exact">${exactNumberHtml(String(cell.inputs[index]), input.units)}</span></dd></div>`;
  }).join('');
  const summary = cell.valid
    ? `<div class="selection-summary"><strong class="selection-value">${escapeHtml(cell.display)}</strong>` +
      `<span class="selection-exact">${exactNumberHtml(cell.exact, sensitivity.outputUnits)}</span></div>`
    : '<div class="selection-summary"><strong class="selection-value">Unavailable</strong></div>';
  const unavailable = cell.valid ? ''
    : `<section class="sensitivity-result"><h3>Result</h3><p class="note">` +
      `${escapeHtml(cell.unavailableReason ?? 'The output is unavailable for this trial.')}</p></section>`;
  const series = sensitivityChartSeries(sensitivity, row, column);
  const chartSeries = [series.row, series.column].filter(Boolean);
  const domain = chartDomain(chartSeries.flatMap((chart) => chart.values));
  const charts = chartSeries.map((chart) => renderSensitivityChart(chart, sensitivity.outputLabel, domain)).join('');
  panel.innerHTML = `<div class="sensitivity-inspector"><div class="inspector-header sensitivity-overview">` +
    `<div class="inspector-title"><div class="inspector-eyebrow">` +
    `<div class="kind metric">Sensitivity</div></div><h2>${sensitivityCoordinateLink(sensitivity.outputLabel, sensitivity.outputLine, sensitivity.outputColumn, sensitivity.outputSourceView)}` +
    `</h2></div>${summary}</div><section class="sensitivity-inputs"><h3>Assumptions</h3>` +
    `<dl class="sensitivity-assumptions">${assumptions}</dl></section>${unavailable}` +
    `<section class="sensitivity-charts" aria-label="Sensitivity charts">${charts}</section></div>`;
  bindSensitivityLinks(panel);
}

function cleanClipboardValue(value) {
  return String(value ?? '').replace(/[\t\r\n]+/g, ' ');
}

function displayedExactValue(cell) {
  if (cell.exact == null || !cell.contra || Number(cell.exact) === 0) return cell.exact;
  return cell.exact.startsWith('-') ? cell.exact.slice(1) : `-${cell.exact}`;
}

function coordinateText(coordinate, exact) {
  const { index, periodIndex } = coordinate;
  if (index === COLUMN_AXIS) return periodIndex === -1 ? '' : payload.columns[periodIndex]?.label ?? '';
  const row = payload.rows[index];
  if (periodIndex === -1) return row.label ?? '';
  const cell = row.cells[periodIndex];
  return exact ? displayedExactValue(cell) : cell.display;
}

function selectedRangeTsv(exact = false) {
  return cellRangeBounds().map((bounds) => regionRowIndexes(bounds).map((index) => {
    const line = [];
    for (let column = bounds.firstColumn; column <= bounds.lastColumn; column++) {
      line.push(cleanClipboardValue(coordinateText({ index, periodIndex: column }, exact)));
    }
    return line.join('\t');
  }).join('\n')).join('\n\n');
}

// A selected line or period header has no cell range, so its label stands in.
function selectedCopyText() {
  if (selectedCoordinates().length) return selectedRangeTsv();
  if (selection === null) return null;
  const periodIndex = selection.index === COLUMN_AXIS ? selection.periodIndex : -1;
  return cleanClipboardValue(coordinateText({ index: selection.index, periodIndex }));
}

function handleGridCopy(event) {
  const text = selectedCopyText();
  if (text === null) return;
  event.preventDefault();
  event.clipboardData.setData('text/plain', text);
}

// With no text selected the browser may never raise a copy event on the grid.
function handleGridCopyKey(event) {
  if (event.key.toLowerCase() !== 'c' || !(event.ctrlKey || event.metaKey) || event.altKey) return;
  const text = selectedCopyText();
  if (text === null) return;
  event.preventDefault();
  navigator.clipboard?.writeText(text);
}

async function copySelectedRange(exact, button) {
  const label = button.querySelector('span');
  const original = label.textContent;
  try {
    await navigator.clipboard.writeText(selectedRangeTsv(exact));
    label.textContent = 'Copied';
  } catch {
    label.textContent = 'Copy failed';
  }
  setTimeout(() => { label.textContent = original; }, 1200);
}

function unitLabel(units) {
  if (!units) return 'Number';
  const words = units.replaceAll('_', ' ').replaceAll('-', ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

function stableNumber(value) {
  return Number(value.toPrecision(15));
}

function selectedRangeSummary() {
  const cells = selectedRangeCells();
  const groups = new Map();
  let numericCount = 0;
  for (const cell of cells) {
    const value = Number(displayedExactValue(cell));
    if (cell.exact == null) continue;
    if (!Number.isFinite(value)) continue;
    numericCount++;
    const units = cell.units ?? '';
    if (!groups.has(units)) groups.set(units, []);
    groups.get(units).push(value);
  }
  return { cellCount: selectedCoordinates().length, numericCount, groups };
}

function rangeHeadline() {
  const regions = cellRangeBounds();
  const cells = selectedCoordinates().length;
  if (regions.length !== 1) return `${regions.length} regions, ${cells} cells`;
  const rows = regionRowIndexes(regions[0]).length;
  const columns = regions[0].lastColumn - regions[0].firstColumn + 1;
  return `${rows} × ${columns} cells`;
}

function renderRangePanel(panel) {
  const summary = selectedRangeSummary();
  const rows = Array.from(summary.groups, ([units, values]) => {
    const sum = stableNumber(values.reduce((total, value) => total + value, 0));
    const average = stableNumber(sum / values.length);
    return `<div class="range-unit"><span class="range-unit-name">${escapeHtml(unitLabel(units))}</span>` +
      `<span class="range-figure">${exactNumberHtml(String(sum), units)}</span>` +
      `<span class="range-figure">${exactNumberHtml(String(average), units)}</span>` +
      `<span class="range-count">${values.length}</span></div>`;
  }).join('');
  const units = rows === ''
    ? '<p class="range-empty">No numeric values selected.</p>'
    : `<div class="range-units"><div class="range-unit-headings"><span></span><span>Sum</span>` +
      `<span>Average</span><span>Count</span></div>${rows}</div>`;
  const copyIcon = '<svg class="icon" viewBox="0 0 16 16" aria-hidden="true"><rect x="5.5" y="5.5" width="7" height="7" rx="1"/>' +
    '<path d="M10.5 5.5v-2a1 1 0 0 0-1-1h-6a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2"/></svg>';
  panel.innerHTML = `<div class="range-panel"><div class="inspector-header range-heading"><div class="inspector-title">` +
    `<div class="inspector-eyebrow"><div class="kind metric">Selection</div></div>` +
    `<h2>${rangeHeadline()}</h2></div></div><div class="range-figures">${units}` +
    `<div class="range-totals"><div class="range-total"><dl><dt>Total cells</dt><dd>${summary.cellCount}</dd></dl>` +
    `<button type="button" class="link-button" data-copy-range="display">${copyIcon}<span>Copy</span></button></div>` +
    `<div class="range-total"><dl><dt>Numeric values</dt><dd>${summary.numericCount}</dd></dl>` +
    `<button type="button" class="link-button" data-copy-range="exact">${copyIcon}<span>Copy exact</span></button></div>` +
    `</div></div></div>`;
  panel.querySelector('[data-copy-range="display"]')
    ?.addEventListener('click', (event) => copySelectedRange(false, event.currentTarget));
  panel.querySelector('[data-copy-range="exact"]')
    ?.addEventListener('click', (event) => copySelectedRange(true, event.currentTarget));
}

function renderPanel() {
  formulaExpansions.clear();
  const panel = el('panel');
  const entry = selectedEntry();
  const sensitivity = selectedSensitivity();
  const rangeSelected = isMultiCellRange();
  const inspector = panel.closest('.inspector');
  const showInspector = Boolean(entry || sensitivity || rangeSelected) && !checksExpanded;
  inspector.hidden = !showInspector;
  document.querySelector('.layout').classList.toggle('inspector-hidden', !showInspector);
  if (sensitivity) {
    renderSensitivityPanel(panel, sensitivity);
    return;
  }
  if (rangeSelected) {
    renderRangePanel(panel);
    return;
  }
  if (!entry) {
    panel.innerHTML = '';
    return;
  }

  const cell = selectedCell();
  const alongColumn = selection.index === COLUMN_AXIS;
  const headers = alongColumn
    ? (payload.rows ?? []).filter((line) => line.cells?.length)
    : (payload.columns ?? []);
  const cells = alongColumn ? headers.map((line) => line.cells[selection.periodIndex]) : entry.cells ?? [];
  const heading = cell === null ? entry : lineEntry(selection.index, selection.periodIndex);
  const context = cell === null ? null : contextEntry(selection.index, selection.periodIndex);
  const kind = cell?.kind ?? entry.kind;
  const candidateSourceView = cell?.sourceView ?? entry.sourceView;
  const sourceView = knownView(candidateSourceView) ? candidateSourceView : null;
  const linked = cell?.linked ?? Boolean(sourceView);
  // A linked cell is one reference; inspect its target.
  const linkedReference = linked && cell?.formula?.references?.length === 1 ? cell.formula.references[0] : null;
  const linkedFact = linkedReference !== null && linkedReference.kind === 'fact'
    && !(linkedReference.formulaId && payload.formulas?.[linkedReference.formulaId]);
  const sourceTitle = viewTitle(sourceView) ?? sourceView;
  const contextLabel = context === null ? ''
    : `<span class="selection-period">${escapeHtml(context.label)}</span>`;
  const selectionValue = cell === null ? ''
    : `<strong class="selection-value">${escapeHtml(cell.display)}</strong>`;
  const selectionExact = cell === null || cell.exact == null ? ''
    : `<span class="selection-exact">${exactNumberHtml(cell.exact, cell.units)}</span>`;
  const selectionSummary = cell === null ? ''
    : `<div class="selection-summary">${selectionValue}${selectionExact}</div>`;
  const kindLabel = linked ? 'Linked' : KIND_LABELS[kind] ?? 'Unavailable';
  const kindPill = `<div class="kind ${linked ? 'reference' : kind}">${kindLabel}</div>`;
  const parts = [`<div class="inspector-header"><div class="inspector-title">` +
    `<div class="inspector-eyebrow">${kindPill}</div>` +
    `<h2>${escapeHtml(heading?.label ?? '')}<wbr>${contextLabel}</h2></div>`];
  parts.push(selectionSummary);
  if (sourceView) {
    parts.push(`<button type="button" class="link-button source-view-link" aria-label="Open source table: ${escapeHtml(sourceTitle)}">` +
      `<span>${escapeHtml(sourceTitle)}</span><svg class="icon" viewBox="0 0 16 16" aria-hidden="true">` +
      '<path d="M6 4h6v6M12 4l-7 7"/></svg></button>');
  }
  parts.push('</div><div class="inspector-body">');

  if (kind === 'formula' && !linkedFact) {
    if (cell === null) {
      parts.push(formulaHeading());
      parts.push(`<div class="${formulaBoxClass()}">${renderFormula(entry.formula)}</div>`);
    } else {
      parts.push(`<div class="inspector-detail-grid"><section>${formulaHeading()}`);
      parts.push(`<div class="${formulaBoxClass()}">` +
        `${renderFormula(cell.formula ?? entry.formula, linkedReference !== null)}</div></section>`);
      parts.push(`<section>${renderCalculation(cell, heading?.label ?? '')}</section></div>`);
    }
  }

  const source = kind === 'fact' || linkedFact
    ? renderSource(cell === null ? entry.source : cell.source) : '';
  const note = renderNote(cell === null ? entry.note : cell.note);
  if (cell === null) {
    const chart = LINE_KINDS.has(entry.kind) ? renderChart(entry, cells, headers) : '';
    parts.push(`<div class="${chart ? 'overview-grid' : 'overview-single'}"><div class="values"><h3>Values</h3><table><tbody>`);
    for (let i = 0; i < cells.length; i++) {
      const exact = exactDiffersFromDisplay(cells[i])
        ? `<td>${exactNumberHtml(cells[i].exact, cells[i].units)}</td>` : '<td></td>';
      parts.push(`<tr><th>${escapeHtml(headers[i].label)}</th><td>${cells[i].display}</td>${exact}</tr>`);
    }
    parts.push('</tbody></table></div>');
    parts.push(source, note, chart);
    parts.push('</div>');
  } else {
    parts.push(source, note);
  }
  parts.push('</div>');
  if (cell !== null) parts.push(renderDependents(cell.dependents));

  panel.innerHTML = parts.join('');
  panel.querySelector('.source-view-link')?.addEventListener('click', () => {
    const line = linkedReference?.line ?? cell?.line ?? entry.name;
    const column = linkedReference?.column ?? cell?.column ?? null;
    followReference(sourceView, line, column);
  });
  const sourceImage = panel.querySelector('.source-image');
  sourceImage?.addEventListener('click', (event) => {
    openSourceImage(event.currentTarget);
  });
  if (sourceImage?.classList.contains('has-source-region')) {
    const image = sourceImage.querySelector('img');
    if (image.complete) positionSourceImage(sourceImage);
    else image.addEventListener('load', () => positionSourceImage(sourceImage), { once: true });
  }
  panel.querySelector('.calc-highlight-toggle')?.addEventListener('click', () => toggleCalculationHighlights(true));
  panel.querySelector('.formula-label-toggle')?.addEventListener('click', () => toggleFormulaLabels(true));
  bindInspectorInputs(panel);
}

// A dependent no table shows opens on a view of its own.
function renderDependents(dependents) {
  if (!dependents?.length) return '';
  const rows = dependents.map((dependent) => {
    const label = escapeHtml(dependent.label);
    const view = dependent.hidden ? `${LINE_VIEW}${dependent.line}` : dependent.sourceView ?? '';
    const name = `<button type="button" class="dep${dependent.hidden ? ' dependent-hidden' : ''}" ` +
      `data-name="${escapeHtml(dependent.line)}" data-column-key="${escapeHtml(dependent.column)}" ` +
      `data-source-view="${escapeHtml(view)}" title="Open ${label} · ${escapeHtml(dependent.columnLabel)}` +
      `${dependent.hidden ? ', which no table shows' : ''}">${label}</button>`;
    return `<tr><th>${name}</th><td class="dependent-column">${escapeHtml(dependent.columnLabel)}</td>` +
      `<td class="${escapeHtml(dependent.kind)}">${escapeHtml(dependent.value)}</td></tr>`;
  }).join('');
  return `<section class="used-by"><h3>Used by</h3><table class="dependents"><tbody>${rows}</tbody></table></section>`;
}

function bindInspectorInputs(container) {
  container.querySelectorAll('.dep, .formula-reference:not(.formula-expand)').forEach((node) => {
    node.addEventListener('click', async () => {
      await openDependency(node);
    });
  });
  container.querySelectorAll('.formula-expand, .formula-collapse').forEach((node) => {
    node.addEventListener('click', () => toggleFormulaExpansion(node));
  });
  container.querySelectorAll('.calc-expand').forEach((node) => {
    node.addEventListener('click', () => stepIntoCalculation(node));
  });
  container.querySelectorAll('[data-calculation-step]').forEach((node) => {
    node.addEventListener('click', () => stepOutOfCalculation(Number(node.dataset.calculationStep)));
  });
  container.querySelectorAll('.calc-input').forEach((node) => {
    node.addEventListener('mouseenter', () => highlightInput(node, true));
    node.addEventListener('mouseleave', () => highlightInput(node, false));
    node.addEventListener('focus', () => highlightInput(node, true));
    node.addEventListener('blur', () => highlightInput(node, false));
  });
}

function toggleCalculationHighlights(focusToggle = false) {
  calculationHighlights = !calculationHighlights;
  renderPanel();
  highlightTableCalculation();
  if (focusToggle) el('panel').querySelector('.calc-highlight-toggle')?.focus();
}

function toggleFormulaLabels(focusToggle = false) {
  formulaLabels = !formulaLabels;
  renderPanel();
  if (focusToggle) el('panel').querySelector('.formula-label-toggle')?.focus();
}

function formulaHeading() {
  const toggle = `<button type="button" class="inspector-toggle formula-label-toggle" ` +
    `aria-pressed="${formulaLabels}" aria-label="Readable names in formulas" ` +
    `title="${formulaLabels ? 'Show formula variables' : 'Show readable names'}">` +
    `<span class="formula-glyphs" aria-hidden="true"><i>x</i><i>Aa</i></span></button>`;
  return `<div class="section-heading"><h3>Formula</h3>${toggle}</div>`;
}

function formulaBoxClass() {
  return formulaLabels ? 'formula-box labels' : 'formula-box';
}

function exactDiffersFromDisplay(cell) {
  return cell.exact != null && !displayMatchesExact(cell.display, cell.exact, cell.units);
}

function renderFormula(formula, expand = false) {
  if (!formula) return '<span class="formula-expression"></span>';
  let rendered = '';
  let end = 0;
  for (const reference of formula.references) {
    rendered += escapeHtml(formula.text.slice(end, reference.start));
    end = reference.start + reference.length;
    rendered += renderReference(formula.text.slice(reference.start, end), reference, expand);
  }
  rendered += escapeHtml(formula.text.slice(end));
  return `<span class="formula-expression">${rendered}</span>`;
}

function renderReference(text, reference, expand = false) {
  const expansion = reference.formulaId ? payload.formulas?.[reference.formulaId] : null;
  const style = expansion || reference.kind === 'formula' ? 'calculated'
    : reference.linked ? 'linked' : 'fact';
  // Expanded aliases render inline with no collapse control.
  if (expansion && expand) return renderFormula(expansion);
  const shown = formulaLabels ? referenceLabel(text, reference) : text;
  const alternate = formulaLabels ? text : referenceLabel(text, reference);
  if (!expansion) {
    return `<button type="button" class="formula-reference ${style}" data-name="${escapeHtml(reference.line)}" ` +
      `data-column-key="${escapeHtml(reference.column ?? '')}" ` +
      `data-source-view="${escapeHtml(reference.sourceView ?? '')}" ` +
      `title="Open ${escapeHtml(alternate)}">${escapeHtml(shown)}</button>`;
  }
  const expansionId = `formula-expansion-${formulaExpansions.size}`;
  formulaExpansions.set(expansionId, { expansion, text });
  return `<span class="formula-toggle"><button type="button" ` +
    `class="formula-reference ${style} formula-expand" data-formula-expansion-id="${expansionId}" ` +
    `aria-expanded="false" title="Expand ${escapeHtml(alternate)}">${escapeHtml(shown)}</button>` +
    `<span class="formula-inline-expansion" id="${expansionId}" hidden></span></span>`;
}

// The written subscript stays, so a label reads as the same coordinate the name did.
function referenceLabel(text, reference) {
  const subscript = text.indexOf('[');
  return (reference.label || reference.line) + (subscript === -1 ? '' : text.slice(subscript));
}

function toggleFormulaExpansion(source) {
  const expansion = document.getElementById(source.dataset.formulaExpansionId);
  if (!expansion) return;
  const deferred = formulaExpansions.get(expansion.id);
  if (deferred && expansion.innerHTML === '') {
    expansion.innerHTML = renderFormula(deferred.expansion) +
      `<button type="button" class="formula-collapse" data-formula-expansion-id="${expansion.id}" ` +
      `title="Collapse to ${escapeHtml(deferred.text)}">Collapse</button>`;
    bindInspectorInputs(expansion);
  }
  const collapsed = source.closest('.formula-toggle')?.querySelector('.formula-expand');
  const open = expansion.hidden;
  expansion.hidden = !open;
  if (collapsed) {
    collapsed.hidden = open;
    collapsed.setAttribute('aria-expanded', String(open));
  }
}

async function openDependency(node) {
  const name = node.dataset.name;
  if (await followReference(node.dataset.sourceView, name, node.dataset.columnKey)) return;
  const expansion = Array.from(el('panel').querySelectorAll('.calc-expand'))
    .find((candidate) => candidate.dataset.name === name);
  if (expansion) stepIntoCalculation(expansion);
}

// Select by coordinate: the target view may lay it out differently.
function openCoordinate(line, column) {
  const target = locate(line, column || null);
  if (target === null) return false;
  if (target.index === COLUMN_AXIS) selectColumn(target.periodIndex, true);
  else if (target.periodIndex === null) selectRow(target.index, true);
  else inspectCell(target.index, target.periodIndex);
  return true;
}

function renderCalculation(cell, label) {
  const toggle = `<button type="button" class="inspector-toggle calc-highlight-toggle" ` +
    `aria-pressed="${calculationHighlights}" title="Toggle calculation colors">` +
    `<span class="highlight-swatches" aria-hidden="true"><i></i><i></i></span>Colors</button>`;
  const heading = `<div class="section-heading"><h3>Calculation</h3>${toggle}</div>`;
  const current = currentCalculation(cell);
  if (!current) {
    return `${heading}<div class="calculation unavailable">Unavailable because an input is missing.</div>`;
  }

  const steps = calculationSteps(cell);
  const trail = steps.length === 0 ? '' : `<nav class="calculation-trail" aria-label="Calculation steps">` +
    [label, ...steps.map((step) => step.title)].map((title, index) => index === steps.length
      ? `<span aria-current="step">${escapeHtml(title)}</span>`
      : `<button type="button" data-calculation-step="${index}">${escapeHtml(title)}</button>`)
      .join('<span class="calculation-trail-separator" aria-hidden="true">›</span>') + '</nav>';
  const references = calculationReferences(current.calculation);
  calculationTargets = [];
  const expression = current.calculation.tokens.map((token) => {
    if (!token.dependency) return escapeHtml(token.text);
    const title = calculationTokenTitle(token);
    const color = references.get(coordinateKey(token.dependency, token.column));
    const colorAttribute = calculationHighlights && color !== undefined ? ` data-calc-color="${color}"` : '';
    if (calculationExpansion(token)) {
      return `<button type="button" class="calc-input calc-expand" data-name="${escapeHtml(token.dependency)}" ` +
        `data-column-key="${escapeHtml(token.column ?? '')}"${colorAttribute} ` +
        `data-target="${calculationTargets.push({ token, title }) - 1}" ` +
        `title="Step into ${escapeHtml(title)}">${escapeHtml(token.text)}</button>`;
    }
    return `<button type="button" class="calc-input dep" data-name="${escapeHtml(token.dependency)}" ` +
      `data-column-key="${escapeHtml(token.column ?? '')}" ` +
      `data-source-view="${escapeHtml(token.sourceView ?? '')}"${colorAttribute} ` +
      `title="Open ${escapeHtml(title)}">${escapeHtml(token.text)}</button>`;
  }).join('');
  return `${heading}${trail}<div class="calculation"><div class="calculation-expression">${expression}` +
    `<span class="calculation-result"><span class="calculation-equals">=</span>` +
    `<strong>${current.result}</strong></span></div></div>`;
}

function calculationTokenTitle(token) {
  const label = token.label ?? token.dependency;
  return token.columnLabel ? `${label} · ${token.columnLabel}` : label;
}

// The trail belongs to one cell; selecting another starts again at its own calculation.
function calculationSteps(cell) {
  const key = coordinateKey(cell.line, cell.column);
  if (calculationTrail.cell !== key) calculationTrail = { cell: key, steps: [] };
  return calculationTrail.steps;
}

function currentCalculation(cell) {
  if (!cell?.calculation || cell.exact == null) return null;
  const step = calculationSteps(cell).at(-1);
  if (step) {
    const calculation = payload.calculations?.[step.id];
    return calculation ? { calculation, result: escapeHtml(step.text) } : null;
  }
  // A linked cell is one reference; show the calculation it points to.
  const aliased = cell.linked && cell.formula?.references?.length === 1 && cell.calculation.tokens?.length === 1
    ? calculationExpansion(cell.calculation.tokens[0])
    : null;
  return { calculation: aliased ?? cell.calculation, result: exactNumberHtml(cell.exact, cell.units) };
}

function stepIntoCalculation(source) {
  const cell = selectedCell();
  if (!cell) return;
  const target = calculationTargets[Number(source.dataset.target)];
  if (!target) return;
  calculationSteps(cell).push({ id: target.token.expansionId, text: target.token.text, title: target.title });
  showCalculationStep();
  const panel = el('panel');
  (panel.querySelector('.calculation .calc-input') ?? panel.querySelector('[data-calculation-step]:last-of-type'))
    ?.focus();
}

function stepOutOfCalculation(depth) {
  const cell = selectedCell();
  if (!cell) return;
  const steps = calculationSteps(cell);
  const left = steps[depth];
  steps.length = depth;
  showCalculationStep();
  Array.from(el('panel').querySelectorAll('.calc-expand'))
    .find((candidate) => calculationTargets[Number(candidate.dataset.target)]?.token.expansionId === left?.id)
    ?.focus();
}

function showCalculationStep() {
  renderPanel();
  highlightTableCalculation();
}

function groupExactNumber(value) {
  return String(value).replace(/^(-?)(\d+)(\.\d+)?$/, (match, sign, integer, fraction = '') =>
    `${sign}${integer.replace(/\B(?=(\d{3})+(?!\d))/g, ',')}${fraction}`);
}

function shortenExactNumber(value) {
  const number = Number(value);
  const fraction = String(value).split('.')[1] ?? '';
  if (!Number.isFinite(number) || fraction.length <= 2) return groupExactNumber(value);
  // Below a hundredth, two decimals would read as zero, so keep two significant digits instead.
  const rounded = Math.abs(number) >= .01 ? number.toFixed(2) : String(Number(number.toPrecision(2)));
  return groupExactNumber(rounded);
}

function unitFormat(units) {
  return payload?.units?.[units] ?? { scale: 1, prefix: '', suffix: '' };
}

// A scaled unit shows its exact value in the same terms as the display, such as 12.5% rather than 0.125.
function exactNumberHtml(value, units = '') {
  const number = Number(value);
  const { scale, suffix } = unitFormat(units);
  const scaling = scale !== 1 && Number.isFinite(number);
  const scaled = scaling ? stableNumber(number * scale) : number;
  const full = scaling ? `${groupExactNumber(String(scaled))}${suffix}` : groupExactNumber(value);
  const short = scaling ? `${groupExactNumber(scaled.toFixed(2))}${suffix}` : shortenExactNumber(value);
  const rounded = scaling ? scaled !== Number(scaled.toFixed(2)) : short !== full;
  return rounded
    ? `<span class="exact-rounded" title="${escapeHtml(full)}">${escapeHtml(short)}</span>`
    : escapeHtml(short);
}

function calculationExpansion(token) {
  return payload.calculations?.[token.expansionId] ?? null;
}

// A value used twice keeps one color, so the table and the calculation agree on which cell is which.
function calculationReferences(calculation) {
  const references = new Map();
  for (const token of calculation?.tokens ?? []) {
    if (!token.dependency) continue;
    const key = coordinateKey(token.dependency, token.column);
    if (!references.has(key)) references.set(key, references.size % 6);
  }
  return references;
}

function highlightTableCalculation() {
  el('body').querySelectorAll('.calculation-source').forEach((cell) => {
    cell.classList.remove('calculation-source', 'emphasized');
    delete cell.dataset.calcColor;
  });
  el('body').querySelectorAll('.calculation-result').forEach((cell) => {
    cell.classList.remove('calculation-result');
    cell.removeAttribute('title');
  });

  const selected = selectedCell();
  if (!selected || selected.kind !== 'formula' || !selected.calculation) return;

  cellNodes.get(coordinateKey(selected.line, selected.column))?.classList.add('calculation-result');

  if (!calculationHighlights) return;

  for (const [key, color] of calculationReferences(currentCalculation(selected)?.calculation)) {
    const cell = cellNodes.get(key);
    if (!cell) continue;
    cell.classList.add('calculation-source');
    cell.dataset.calcColor = color;
  }
}

function displayMatchesExact(display, exact, units = '') {
  if (display === exact) return true;
  const { scale, prefix, suffix } = unitFormat(units);
  let text = display.replaceAll(',', '');
  const parenthesized = text.startsWith('(') && text.endsWith(')');
  const negative = parenthesized || text.startsWith('-');
  if (negative) text = parenthesized ? text.slice(1, -1) : text.slice(1);
  if (prefix && text.startsWith(prefix)) text = text.slice(prefix.length);
  if (suffix && text.endsWith(suffix)) text = text.slice(0, -suffix.length);
  return Number(negative ? `-${text}` : text) === stableNumber(Number(exact) * scale);
}

function highlightInput(source, active) {
  cellNodes.get(coordinateKey(source.dataset.name, source.dataset.columnKey))
    ?.classList.toggle('emphasized', active);
}

function renderNote(note) {
  if (!note) return '';
  const paragraphs = note.split('\n\n').map((paragraph) => `<p class="note">${escapeHtml(paragraph)}</p>`);
  return `<section class="annotation"><h3>Note</h3>${paragraphs.join('')}</section>`;
}

function renderSource(cellSource) {
  const source = cellSource ?? {};
  const details = Object.entries(source.details ?? {});
  const notes = source.note ? `<p class="note">${escapeHtml(source.note)}</p>` : '';
  if (!details.length && !source.image && !source.url) {
    return notes ? `<section class="source"><h3>Source</h3>${notes}</section>` : '';
  }
  const parts = ['<section class="source"><h3>Source</h3><div class="source-body">'];
  if (source.image) {
    const image = sourceImageUrl(source.image);
    const description = source.table || 'source table';
    const region = source.region;
    const regionClass = region ? ' has-source-region' : '';
    const regionData = region ? ` data-source-region-x="${region.x}" data-source-region-y="${region.y}" ` +
      `data-source-region-width="${region.width}" data-source-region-height="${region.height}"` : '';
    const highlight = region ? `<svg class="source-image-highlight" aria-hidden="true" hidden>` +
      `<rect x="${region.x}" y="${region.y}" width="${region.width}" height="${region.height}"></rect></svg>` : '';
    parts.push(`<button type="button" class="source-image${regionClass}" data-source-image="${escapeHtml(image)}" ` +
      `data-source-image-description="${escapeHtml(description)}"${regionData} aria-label="Open source image">` +
      `<img src="${escapeHtml(image)}" alt="${escapeHtml(description)}">${highlight}</button>`);
  }
  parts.push('<div class="source-details">');
  if (details.length) {
    parts.push('<dl>');
    for (const [label, value] of details) {
      parts.push(`<dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd>`);
    }
    parts.push('</dl>');
  }
  if (source.url) {
    parts.push(`<p class="note"><a href="${escapeHtml(source.url)}" target="_blank" rel="noopener">` +
      'Open source URL</a></p>');
  }
  parts.push(notes, '</div></div></section>');
  return parts.join('');
}

function sourceImageRegion(element) {
  if (!element.classList.contains('has-source-region')) return null;
  return {
    x: Number(element.dataset.sourceRegionX),
    y: Number(element.dataset.sourceRegionY),
    width: Number(element.dataset.sourceRegionWidth),
    height: Number(element.dataset.sourceRegionHeight)
  };
}

function positionSourceImage(button) {
  const image = button.querySelector('img');
  const region = sourceImageRegion(button);
  if (!region || image.naturalWidth === 0 || image.naturalHeight === 0) return;
  const placement = sourceImagePlacement(image.naturalWidth, image.naturalHeight, region);
  button.style.setProperty('--source-image-width', `${placement.width}%`);
  button.style.setProperty('--source-image-left', `${placement.left}%`);
  button.style.setProperty('--source-image-top', `${placement.top}%`);
  const highlight = button.querySelector('.source-image-highlight');
  highlight.setAttribute('viewBox', placement.viewBox);
  highlight.removeAttribute('hidden');
}

function sourceImageViewBox(width, height, region) {
  let cropWidth = Math.min(width, Math.max(width / 2, region.width * 6));
  let cropHeight = cropWidth / 2;
  if (cropHeight > height) {
    cropHeight = height;
    cropWidth = Math.min(width, height * 2);
  }
  const centerX = region.x + region.width / 2;
  const centerY = region.y + region.height / 2;
  const x = Math.min(Math.max(centerX - cropWidth / 2, 0), width - cropWidth);
  const y = Math.min(Math.max(centerY - cropHeight / 2, 0), height - cropHeight);
  return `${x} ${y} ${cropWidth} ${cropHeight}`;
}

function sourceImagePlacement(width, height, region) {
  const viewBox = sourceImageViewBox(width, height, region);
  const [cropX, cropY, cropWidth, cropHeight] = viewBox.split(' ').map(Number);
  return {
    viewBox,
    width: width / cropWidth * 100,
    left: -cropX / cropWidth * 100,
    top: -cropY / cropHeight * 100
  };
}

function renderChart(entry, cells, headers) {
  const periodColumns = payload.periodColumns ?? [];
  const points = cells
    .map((cell, index) => ({ cell, label: headers[index]?.label ?? cell.column }))
    .filter((entry) => periodColumns.includes(entry.cell.column))
    .map((entry) => ({
      label: entry.label,
      display: entry.cell.display,
      value: entry.cell.exact == null ? NaN : Number(entry.cell.exact)
    }));
  const values = points.filter((point) => Number.isFinite(point.value));
  if (values.length < 2) return '';

  const width = 320;
  const height = 154;
  const y = chartScale(chartDomain(values.map((point) => point.value)), 12, 146);
  const baseline = y(0);
  const slot = (width - 32) / points.length;
  const barWidth = Math.min(38, slot * .52);

  const bars = points.map((point, index) => {
    if (!Number.isFinite(point.value)) return '';
    const barY = point.value >= 0 ? y(point.value) : baseline;
    const barHeight = Math.max(2, Math.abs(y(point.value) - baseline));
    const barX = 16 + index * slot + (slot - barWidth) / 2;
    return `<g data-tooltip="${escapeHtml(point.label)}: ${escapeHtml(point.display)}">
      <rect x="${barX}" y="${barY}" width="${barWidth}" height="${barHeight}" rx="3"></rect>
    </g>`;
  }).join('');
  const labels = points.map((point) => `<span>${escapeHtml(point.label)}</span>`).join('');

  return `<div class="trend"><h3>Trend</h3><figure class="trend-chart ${entry.kind}">
    <svg viewBox="0 0 ${width} ${height}" preserveAspectRatio="none" role="img" aria-label="${escapeHtml(entry.label)} trend by period">
      <line x1="16" y1="${baseline}" x2="${width - 16}" y2="${baseline}"></line>
      ${bars}
    </svg><div class="trend-axis" style="--trend-columns: ${points.length}">${labels}</div>
  </figure></div>`;
}

const TOOLTIP_DELAY = 200;
let tooltipLayer = null;
let tooltipHost = null;
let tooltipTimer = 0;
let tooltipPointer = null;

function tooltipFor(target) {
  return target.closest?.('[data-tooltip], [title], [data-tooltip-title]') ?? null;
}

function tooltipText(host) {
  return host.dataset.tooltip ?? host.dataset.tooltipTitle ?? host.getAttribute('title') ?? '';
}

function showTooltip(host) {
  if (host === tooltipHost) return;
  hideTooltip();
  const text = tooltipText(host);
  if (!text.trim()) return;
  const native = host.getAttribute('title');
  if (native !== null) {
    // Held aside while ours shows and restored after, so it stays in the accessible name.
    host.dataset.tooltipTitle = native;
    host.removeAttribute('title');
  }
  if (tooltipLayer === null) {
    tooltipLayer = document.createElement('div');
    tooltipLayer.className = 'tooltip';
    tooltipLayer.setAttribute('role', 'presentation');
    document.body.appendChild(tooltipLayer);
  }
  tooltipHost = host;
  tooltipLayer.textContent = text;
  tooltipLayer.hidden = false;
  placeTooltip(host);
}

function pointerInside(host) {
  if (tooltipPointer === null) return false;
  const bounds = host.getBoundingClientRect();
  return tooltipPointer.x >= bounds.left && tooltipPointer.x <= bounds.right
    && tooltipPointer.y >= bounds.top && tooltipPointer.y <= bounds.bottom;
}

function placeTooltip(host) {
  const margin = 8;
  const box = tooltipLayer.getBoundingClientRect();
  // Follow the cursor, or the element under keyboard focus.
  const anchor = pointerInside(host)
    ? { left: tooltipPointer.x, right: tooltipPointer.x, top: tooltipPointer.y - 6, bottom: tooltipPointer.y + 14 }
    : host.getBoundingClientRect();
  let top = anchor.top - box.height - margin;
  let placement = 'top';
  if (top < margin) {
    top = anchor.bottom + margin;
    placement = 'bottom';
  }
  const left = Math.min(Math.max((anchor.left + anchor.right) / 2 - box.width / 2, margin),
    Math.max(margin, window.innerWidth - box.width - margin));
  tooltipLayer.dataset.placement = placement;
  tooltipLayer.style.top = `${top}px`;
  tooltipLayer.style.left = `${left}px`;
}

function hideTooltip() {
  clearTimeout(tooltipTimer);
  if (tooltipHost !== null) {
    const held = tooltipHost.dataset.tooltipTitle;
    if (held !== undefined) {
      tooltipHost.setAttribute('title', held);
      delete tooltipHost.dataset.tooltipTitle;
    }
    tooltipHost = null;
  }
  if (tooltipLayer !== null) tooltipLayer.hidden = true;
}

function setupTooltips() {
  document.addEventListener('pointermove', (event) => {
    tooltipPointer = { x: event.clientX, y: event.clientY };
    if (tooltipHost !== null) placeTooltip(tooltipHost);
  }, { passive: true });
  document.addEventListener('pointerover', (event) => {
    const host = tooltipFor(event.target);
    if (host === null) {
      hideTooltip();
      return;
    }
    if (host === tooltipHost) return;
    hideTooltip();
    tooltipTimer = setTimeout(() => showTooltip(host), TOOLTIP_DELAY);
  });
  // Pointer-only, like the native title: focus dismisses a tooltip but never raises one.
  document.addEventListener('focusin', hideTooltip);
  document.addEventListener('pointerdown', hideTooltip);
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') hideTooltip();
  }, true);
  document.addEventListener('scroll', hideTooltip, true);
  window.addEventListener('blur', hideTooltip);
}

function escapeHtml(text) {
  return String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#039;');
}

function toggleNavigation() {
  const hiding = navigationVisible;
  if (hiding && el('view-tree').contains(document.activeElement)) focusTable();
  setNavigationVisible(!navigationVisible);
}

function focusNavigation() {
  if (!navigationVisible) setNavigationVisible(true);
  const active = el('view-tree').querySelector('button.active');
  focusNavigationView((active ?? el('view-tree').querySelector('.tree-view'))?.dataset.view);
}

function focusNavigationView(id) {
  if (!id) return;
  const button = el('view-tree').querySelector(`.tree-view[data-view="${CSS.escape(id)}"]`);
  if (!button) return;
  button.focus({ preventScroll: true });
  button.scrollIntoView({ block: 'nearest' });
}

async function selectNavigationView(button) {
  if (!button) return;
  await openView(button.dataset.view);
  focusNavigationView(button.dataset.view);
}

function setBranchExpanded(key, expanded) {
  branchStates.set(key, expanded);
  renderNavigation();
  focusNavigationView(key);
}

function focusTable() {
  if (selection !== null) focusGridCell();
  else el('body').querySelector('[tabindex="0"]')?.focus();
}

function shouldRestoreGridFocus(target, hasSelection, dialogOpen) {
  if (!hasSelection || dialogOpen) return false;
  const focusTarget = 'a, button, input, select, textarea, summary, [contenteditable], ' +
    '[tabindex]:not([tabindex="-1"]), [data-column]';
  return target.closest(focusTarget) === null;
}

function restoreGridFocusAfterClick(event) {
  if (!shouldRestoreGridFocus(event.target, selection !== null,
    document.querySelector('dialog[open]') !== null)) return;
  focusGridCell();
}

function cycleView(direction) {
  const ids = treeOrder(viewTree(catalog?.views ?? []));
  const current = ids.indexOf(activeView);
  if (current < 0 || ids.length < 2) return;
  const next = (current + direction + ids.length) % ids.length;
  openView(ids[next]).then(focusTable);
}

function openShortcuts() {
  const dialog = el('shortcuts-dialog');
  if (dialog.open) return;
  dialog.showModal();
  dialog.querySelector('button')?.focus();
}

function setSourceImageHighlight(highlight, region, width, height) {
  highlight.setAttribute('hidden', '');
  if (!region || width === 0 || height === 0) return;
  highlight.setAttribute('viewBox', `0 0 ${width} ${height}`);
  const rectangle = highlight.querySelector('rect');
  rectangle.setAttribute('x', region.x);
  rectangle.setAttribute('y', region.y);
  rectangle.setAttribute('width', region.width);
  rectangle.setAttribute('height', region.height);
  highlight.removeAttribute('hidden');
}

function sizeSourceImageToFit() {
  const image = el('source-image-full');
  const viewport = sourceImageViewport;
  if (image.naturalWidth === 0 || image.naturalHeight === 0) return;
  const styles = getComputedStyle(viewport);
  const availableWidth = viewport.clientWidth - parseFloat(styles.paddingLeft) - parseFloat(styles.paddingRight);
  const availableHeight = viewport.clientHeight - parseFloat(styles.paddingTop) - parseFloat(styles.paddingBottom);
  const scale = Math.min(1, availableWidth / image.naturalWidth, availableHeight / image.naturalHeight);
  sourceImageFitWidth = image.naturalWidth * scale;
  sourceImageFitHeight = image.naturalHeight * scale;
  setSourceImageZoom(sourceImageZoom);
}

function setSourceImageZoom(percent, anchor) {
  const viewport = sourceImageViewport;
  const canvas = viewport.querySelector('.source-image-dialog-canvas');
  const oldRect = canvas.getBoundingClientRect();
  const viewportRect = viewport.getBoundingClientRect();
  const focus = anchor ?? {
    x: viewportRect.left + viewport.clientWidth / 2,
    y: viewportRect.top + viewport.clientHeight / 2
  };
  const relativeX = oldRect.width === 0 ? .5 : Math.min(1, Math.max(0, (focus.x - oldRect.left) / oldRect.width));
  const relativeY = oldRect.height === 0 ? .5 : Math.min(1, Math.max(0, (focus.y - oldRect.top) / oldRect.height));
  sourceImageZoom = Math.min(SOURCE_IMAGE_ZOOM_MAX,
    Math.max(SOURCE_IMAGE_ZOOM_MIN, Math.round(percent / SOURCE_IMAGE_ZOOM_STEP) * SOURCE_IMAGE_ZOOM_STEP));
  canvas.style.width = `${sourceImageFitWidth * sourceImageZoom / 100}px`;
  canvas.style.height = `${sourceImageFitHeight * sourceImageZoom / 100}px`;
  el('source-image-zoom-value').textContent = `${sourceImageZoom}%`;
  el('source-image-zoom-out').disabled = sourceImageZoom === SOURCE_IMAGE_ZOOM_MIN;
  el('source-image-zoom-in').disabled = sourceImageZoom === SOURCE_IMAGE_ZOOM_MAX;
  el('source-image-zoom-fit').disabled = sourceImageZoom === SOURCE_IMAGE_ZOOM_MIN;
  canvas.classList.toggle('zoom-limit', sourceImageZoom === SOURCE_IMAGE_ZOOM_MAX);
  requestAnimationFrame(() => {
    const newRect = canvas.getBoundingClientRect();
    viewport.scrollBy({
      left: newRect.left + newRect.width * relativeX - focus.x,
      top: newRect.top + newRect.height * relativeY - focus.y
    });
  });
}

function handleSourceImageZoomKeydown(event) {
  const zoomKeys = ['+', '=', '-', '_', '0'];
  if (!zoomKeys.includes(event.key)) return;
  event.preventDefault();
  if (event.key === '0') setSourceImageZoom(SOURCE_IMAGE_ZOOM_MIN);
  else setSourceImageZoom(sourceImageZoom + (event.key === '+' || event.key === '='
    ? SOURCE_IMAGE_ZOOM_STEP : -SOURCE_IMAGE_ZOOM_STEP));
}

function handleSourceImageWheel(event) {
  if (!event.ctrlKey && !event.metaKey) return;
  event.preventDefault();
  setSourceImageZoom(sourceImageZoom + (event.deltaY < 0 ? SOURCE_IMAGE_ZOOM_STEP : -SOURCE_IMAGE_ZOOM_STEP),
    { x: event.clientX, y: event.clientY });
}

function openSourceImage(button) {
  const dialog = el('source-image-dialog');
  const image = el('source-image-full');
  const highlight = el('source-image-full-highlight');
  const region = sourceImageRegion(button);
  const showHighlight = () => {
    setSourceImageHighlight(highlight, region, image.naturalWidth, image.naturalHeight);
    sizeSourceImageToFit();
  };
  sourceImageZoom = SOURCE_IMAGE_ZOOM_MIN;
  setSourceImageHighlight(highlight, null, 0, 0);
  el('source-image-title').textContent = button.dataset.sourceImageDescription;
  dialog.showModal();
  sourceImageViewport.focus();
  image.onload = showHighlight;
  image.src = button.dataset.sourceImage;
  image.alt = button.dataset.sourceImageDescription;
  if (image.complete) {
    image.onload = null;
    showHighlight();
  }
}

function isTypingTarget(target) {
  return target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement
    || target instanceof HTMLSelectElement || target.isContentEditable;
}

// A page showing an export in a frame, such as a slide deck tracing a number, takes Escape back unless a dialog or
// menu claims it; the selected cell is what that page opened the book at, so clearing it is no reason to stay.
function leavesEmbeddedBook(event, overlayOpen) {
  return event.key === 'Escape' && !overlayOpen && !event.ctrlKey && !event.metaKey && !event.altKey;
}

function handleGlobalKeydown(event) {
  if (event.defaultPrevented || isTypingTarget(event.target)) return;
  if (el('shortcuts-dialog').open || el('source-image-dialog').open) return;
  if (el('export-menu').matches(':popover-open')) return;

  const key = event.key.toLowerCase();
  if (pendingShortcut === 'g') {
    pendingShortcut = null;
    clearTimeout(shortcutTimer);
    const shortcuts = { n: focusNavigation, t: focusTable, c: toggleCalculationHighlights, l: toggleFormulaLabels };
    if (shortcuts[key]) {
      event.preventDefault();
      shortcuts[key]();
    }
    return;
  }

  if (event.ctrlKey || event.metaKey || event.altKey) return;
  if (key === 'g') {
    event.preventDefault();
    pendingShortcut = 'g';
    clearTimeout(shortcutTimer);
    shortcutTimer = setTimeout(() => { pendingShortcut = null; }, 1500);
    return;
  }
  if (event.key === '?') {
    event.preventDefault();
    openShortcuts();
    return;
  }
  if (event.key === '\\') {
    event.preventDefault();
    toggleNavigation();
    return;
  }
  if (event.key === '[' || event.key === ']') {
    event.preventDefault();
    cycleView(event.key === ']' ? 1 : -1);
  }
}

/** The file name the server gives an export, from its Content-Disposition header. */
function downloadName(disposition, format) {
  const encoded = disposition?.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
  if (encoded) return decodeURIComponent(encoded);
  return disposition?.match(/filename="?([^";]+)"?/i)?.[1] ?? `book.${format}`;
}

// Fetched rather than linked, so a book that fails to export shows why instead of a failed download.
async function downloadExport(format) {
  const button = el('export-button');
  const error = el('error');
  button.disabled = true;
  // A failed export's message stands until the next attempt; the view's own error, if any, returns in its place.
  error.hidden = !payload?.error;
  error.textContent = payload?.error ?? '';
  try {
    const response = await fetch(`/api/export/${format}`);
    if (!response.ok) throw new Error(await response.text() || `The book failed to export (${response.status}).`);
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement('a');
    link.href = url;
    link.download = downloadName(response.headers.get('Content-Disposition'), format);
    link.click();
    // Revoking at once can cancel the download before the browser reads the file.
    setTimeout(() => URL.revokeObjectURL(url), 10000);
  } catch (failure) {
    error.hidden = false;
    error.textContent = failure.message;
  } finally {
    button.disabled = false;
    // Disabling the button dropped its focus; return it unless the reader has moved on meanwhile.
    if (document.activeElement === document.body) button.focus();
  }
}

function exportMenuItems() {
  return Array.from(el('export-menu').querySelectorAll('[role="menuitem"]'));
}

// The browser opens and dismisses the popover; placing it above the button and moving focus is left to the page.
function handleExportMenuToggle(event) {
  const open = event.newState === 'open';
  el('export-button').setAttribute('aria-expanded', String(open));
  if (!open) return;
  const anchor = el('export-button').getBoundingClientRect();
  const menu = el('export-menu');
  menu.style.bottom = `${window.innerHeight - anchor.top + 4}px`;
  menu.style.left = `${Math.max(8, Math.min(anchor.left, window.innerWidth - menu.offsetWidth - 8))}px`;
  exportMenuItems()[0].focus();
}

function handleExportMenuKeydown(event) {
  const items = exportMenuItems();
  const current = items.indexOf(document.activeElement);
  const moves = { ArrowDown: current + 1, ArrowUp: current - 1, Home: 0, End: items.length - 1 };
  if (!(event.key in moves)) return;
  event.preventDefault();
  items[(moves[event.key] + items.length) % items.length].focus();
}

function chooseExport(event) {
  const format = event.target.closest('[data-format]')?.dataset.format;
  if (!format) return;
  el('export-menu').hidePopover();
  downloadExport(format);
}

function connect() {
  if (snapshot) {
    el('dot').className = 'status-indicator';
    el('status').textContent = `Exported ${new Date(snapshot.exported).toLocaleDateString()}`;
    return;
  }
  const source = new EventSource('/api/events');
  source.onmessage = () => {
    el('dot').className = 'status-indicator live';
    el('status').textContent = 'Live';
    refresh();
  };
  source.addEventListener('assets', () => location.reload());
  source.addEventListener('cell', (event) => {
    const { view, line, column } = JSON.parse(event.data);
    // A frame's history is its page's too, where back should close the frame rather than step through cells sent to it.
    followReference(view, line, column, embedded);
    // A frame may be hidden over a page that keeps its keys, so only a page of its own takes focus.
    if (!embedded) window.focus();
  });
  source.onerror = () => {
    el('dot').className = 'status-indicator down';
    el('status').textContent = 'Disconnected';
  };
}

window.addEventListener('popstate', async (event) => {
  rememberHistoryEntry();
  historyEntry = event.state?.entry ?? newHistoryEntry();
  const id = requestedView();
  if (!knownView(id)) return;
  const cell = snapshot ? takeRequestedCell() : null;
  if (!cell) {
    openView(id, true, savedViewState(id));
    return;
  }
  await openView(id, true);
  updateUrl(activeView, true);
  openCoordinate(cell.line, cell.column);
});
window.addEventListener('pagehide', persistHistoryStates);

el('view-tree').addEventListener('keydown', (event) => {
  if (event.altKey) return;
  if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'Enter', 'Escape'].includes(event.key)) return;
  if (event.key === 'Escape' || event.key === 'Enter') {
    event.preventDefault();
    focusTable();
    return;
  }
  const buttons = Array.from(el('view-tree').querySelectorAll('.tree-view'));
  const current = buttons.indexOf(document.activeElement);
  if (current < 0) return;
  event.preventDefault();
  const button = buttons[current];
  if (event.key === 'ArrowRight') {
    if (button.getAttribute('aria-expanded') === 'false') {
      setBranchExpanded(button.dataset.view, true);
      return;
    }
    if (button.getAttribute('aria-expanded') === 'true') {
      selectNavigationView(buttons[current + 1]);
    }
    return;
  }
  if (event.key === 'ArrowLeft') {
    if (button.getAttribute('aria-expanded') === 'true') {
      setBranchExpanded(button.dataset.view, false);
      return;
    }
    const parent = button.closest('ul')?.parentElement?.querySelector(':scope > .tree-entry > .tree-view');
    if (parent) selectNavigationView(parent);
    return;
  }
  const next = event.key === 'Home' ? 0
    : event.key === 'End' ? buttons.length - 1
      : Math.max(0, Math.min(buttons.length - 1, current + (event.key === 'ArrowDown' ? 1 : -1)));
  if (next !== current) selectNavigationView(buttons[next]);
});

// An exported page has no server to build an export.
el('export-button').hidden = Boolean(snapshot);
el('export-button').setAttribute('aria-expanded', 'false');
el('export-menu').addEventListener('toggle', handleExportMenuToggle);
el('export-menu').addEventListener('keydown', handleExportMenuKeydown);
el('export-menu').addEventListener('click', chooseExport);
el('shortcuts-button').addEventListener('click', openShortcuts);
el('shortcuts-close').addEventListener('click', () => el('shortcuts-dialog').close());
el('source-image-close').addEventListener('click', () => el('source-image-dialog').close());
el('source-image-zoom-out').addEventListener('click', () => setSourceImageZoom(sourceImageZoom - SOURCE_IMAGE_ZOOM_STEP));
el('source-image-zoom-in').addEventListener('click', () => setSourceImageZoom(sourceImageZoom + SOURCE_IMAGE_ZOOM_STEP));
el('source-image-zoom-fit').addEventListener('click', () => setSourceImageZoom(SOURCE_IMAGE_ZOOM_MIN));
el('source-image-dialog').addEventListener('click', (event) => {
  if (event.target === event.currentTarget) event.currentTarget.close();
});
el('source-image-dialog').addEventListener('close', () => {
  el('source-image-full').removeAttribute('src');
  el('source-image-full-highlight').hidden = true;
  focusGridCell();
});
el('source-image-dialog').addEventListener('keydown', handleSourceImageZoomKeydown);
sourceImageViewport.addEventListener('wheel', handleSourceImageWheel, { passive: false });
sourceImageViewport.querySelector('.source-image-dialog-canvas').addEventListener('click', (event) => {
  setSourceImageZoom(sourceImageZoom + SOURCE_IMAGE_ZOOM_STEP, { x: event.clientX, y: event.clientY });
});
el('dock-right').addEventListener('click', () => setInspectorDock('right'));
el('dock-bottom').addEventListener('click', () => setInspectorDock('bottom'));
compactLayout.addEventListener('change', () => setInspectorDock(inspectorDock, false));
el('scale').addEventListener('input', (event) => setUiScale(Number(event.target.value)));
el('scale-reset').addEventListener('click', () => setUiScale(100));
el('theme-toggle').addEventListener('click', () => setTheme(theme === 'dark' ? 'light' : 'dark'));
el('nav-toggle').addEventListener('click', toggleNavigation);
wireResizer(el('nav-resizer'), () => navWidth, 1);
wireResizer(labelResizer, () => labelWidth, 1);
wireResizer(el('inspector-resizer'), inspectorSize, -1, repositionSourceImage);
document.querySelector('.book-table').addEventListener('copy', handleGridCopy);
document.querySelector('.book-table').addEventListener('keydown', handleGridCopyKey);
window.addEventListener('resize', () => {
  sizes.forEach((size) => size.set(size.preference, false));
  if (el('source-image-dialog').open) sizeSourceImageToFit();
});
document.addEventListener('keydown', handleGlobalKeydown);
if (embedded) {
  document.addEventListener('keydown', (event) => {
    const overlayOpen = el('shortcuts-dialog').open || el('source-image-dialog').open ||
      el('export-menu').matches(':popover-open');
    if (leavesEmbeddedBook(event, overlayOpen)) window.parent.postMessage({ libreta: 'escape' }, '*');
  }, true);
}
document.addEventListener('click', restoreGridFocusAfterClick);

setupTooltips();
setInspectorDock(inspectorDock, false);
setUiScale(uiScale, false);
setTheme(theme, false);
sizes.forEach((size) => size.set(size.value, false));
setNavigationVisible(navigationVisible, false);
refresh();
connect();
