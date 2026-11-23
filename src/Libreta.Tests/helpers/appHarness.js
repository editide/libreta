const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const appPath = path.join(__dirname, '..', '..', 'Libreta', 'wwwroot', 'app.js');
const appScript = new vm.Script(fs.readFileSync(appPath, 'utf8'), { filename: 'app.js' });

function makeElement(overrides = {}) {
  const classes = new Set();
  const attributes = new Map();
  const styles = new Map();
  const element = {
    hidden: false,
    open: false,
    value: '',
    innerHTML: '',
    textContent: '',
    scrollLeft: 0,
    scrollTop: 0,
    clientWidth: 800,
    clientHeight: 600,
    offsetWidth: 800,
    offsetHeight: 600,
    tabIndex: -1,
    dataset: {},
    classList: {
      add: (...names) => names.forEach((name) => classes.add(name)),
      remove: (...names) => names.forEach((name) => classes.delete(name)),
      toggle: (name, force) => {
        const next = force === undefined ? !classes.has(name) : Boolean(force);
        if (next) classes.add(name);
        else classes.delete(name);
        return next;
      },
      contains: (name) => classes.has(name)
    },
    style: {
      setProperty: (name, value) => styles.set(name, String(value)),
      removeProperty: (name) => styles.delete(name),
      getPropertyValue: (name) => styles.get(name) ?? ''
    },
    setAttribute: (name, value) => attributes.set(name, String(value)),
    getAttribute: (name) => attributes.get(name) ?? null,
    removeAttribute: (name) => attributes.delete(name),
    hasAttribute: (name) => attributes.has(name),
    addEventListener: () => {},
    removeEventListener: () => {},
    querySelector: () => makeElement(),
    querySelectorAll: () => [],
    closest: () => null,
    matches: () => false,
    contains: () => false,
    appendChild: (child) => child,
    insertBefore: (child) => child,
    focus: () => {},
    blur: () => {},
    scrollIntoView: () => {},
    getBoundingClientRect: () => ({ left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 }),
    showModal: () => { element.open = true; },
    close: () => { element.open = false; },
    hidePopover: () => {},
    ...overrides
  };
  return element;
}

function makeStorage() {
  const store = new Map();
  return {
    getItem: (key) => store.has(key) ? store.get(key) : null,
    setItem: (key, value) => store.set(key, String(value)),
    removeItem: (key) => store.delete(key)
  };
}

function loadApp(overrides = {}) {
  const elements = new Map();
  const queried = new Map();
  const byId = (id) => {
    if (id === 'libreta-snapshot' && !elements.has(id)) return null;
    if (!elements.has(id)) elements.set(id, makeElement({ id }));
    return elements.get(id);
  };
  const bySelector = (selector) => {
    if (!queried.has(selector)) queried.set(selector, makeElement());
    return queried.get(selector);
  };
  const matchMedia = () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} });
  const document = {
    title: '',
    activeElement: null,
    documentElement: makeElement(),
    body: makeElement(),
    getElementById: byId,
    querySelector: bySelector,
    querySelectorAll: () => [],
    createElement: () => makeElement(),
    elementFromPoint: () => null,
    addEventListener: () => {},
    removeEventListener: () => {}
  };
  const context = {
    console,
    URL,
    URLSearchParams,
    structuredClone,
    setTimeout,
    clearTimeout,
    document,
    matchMedia,
    location: { href: 'http://localhost/', hash: '', search: '', reload: () => {} },
    history: { state: null, pushState: () => {}, replaceState: () => {}, back: () => {} },
    localStorage: makeStorage(),
    sessionStorage: makeStorage(),
    navigator: { clipboard: { writeText: async () => {} } },
    EventSource: class { addEventListener() {} close() {} },
    fetch: () => new Promise(() => {}),
    getComputedStyle: () => ({ getPropertyValue: () => '' }),
    requestAnimationFrame: () => 0,
    cancelAnimationFrame: () => {},
    innerWidth: 1280,
    innerHeight: 800,
    addEventListener: () => {},
    removeEventListener: () => {},
    focus: () => {},
    elements,
    makeElement,
    ...overrides
  };
  context.window = context;
  context.parent = overrides.parent ?? context;
  vm.createContext(context);
  appScript.runInContext(context);
  context.eval = (code) => vm.runInContext(code, context);
  context.set = (values) => {
    context.__values = values;
    for (const key of Object.keys(values)) {
      vm.runInContext(`${key} = __values[${JSON.stringify(key)}]`, context);
    }
    delete context.__values;
  };
  return context;
}

module.exports = { loadApp, makeElement };
