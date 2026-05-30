import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const islandsPath = new URL('../wwwroot/razorwire/razorwire.islands.js', import.meta.url);

test('load strategy hydrates generated output and passes parsed props', async () => {
  const island = new FakeElement('div');
  island.setAttribute('data-rw-module', 'test-load');
  island.setAttribute('data-rw-props', '{"answer":"42"}');

  loadIslands([island], {
    islandModules: {
      'test-load': moduleWithMount('root.setAttribute("data-mounted-props", props.answer);')
    }
  });
  await flushHydration();

  assert.equal(island.getAttribute('data-rw-hydrated'), 'true');
  assert.equal(island.getAttribute('data-mounted-props'), '42');
});

test('only strategy clears server content before mounting', async () => {
  const island = new FakeElement('div');
  island.innerHTML = '<p>server content</p>';
  island.setAttribute('data-rw-module', 'test-only');
  island.setAttribute('data-rw-strategy', 'only');

  loadIslands([island], {
    islandModules: {
      'test-only': moduleWithMount('root.setAttribute("data-child-count", String(root.children.length));')
    }
  });
  await flushHydration();

  assert.equal(island.getAttribute('data-rw-hydrated'), 'true');
  assert.equal(island.getAttribute('data-child-count'), '0');
  assert.equal(island.children.length, 0);
});

test('missing mount marks island failed and prevents duplicate hydration', async () => {
  const island = new FakeElement('div');
  island.setAttribute('data-rw-module', 'test-missing-mount');

  const { document } = loadIslands([island], {
    islandModules: {
      'test-missing-mount': 'data:text/javascript,export const value = 1;'
    }
  });
  await flushHydration();
  document.dispatchEvent({ type: 'turbo:load' });
  await flushHydration();

  assert.equal(island.getAttribute('data-rw-hydrated'), 'failed');
});

test('import failure leaves island retryable', async () => {
  const island = new FakeElement('div');
  island.setAttribute('data-rw-module', './missing-island-module.js');

  const { warnings } = loadIslands([island]);
  await flushHydration();

  assert.equal(island.hasAttribute('data-rw-hydrated'), false);
  assert.equal(warnings.errors.some(entry => entry[0].startsWith('Failed to mount island:')), true);
});

test('unknown strategy does not hydrate and reports the invalid strategy', async () => {
  const island = new FakeElement('div');
  island.setAttribute('data-rw-module', 'test-unknown');
  island.setAttribute('data-rw-strategy', 'immediate');

  const { warnings } = loadIslands([island], {
    islandModules: {
      'test-unknown': moduleWithMount('root.setAttribute("data-mounted", "true");')
    }
  });
  await flushHydration();

  assert.equal(island.hasAttribute('data-rw-hydrated'), false);
  assert.equal(island.hasAttribute('data-mounted'), false);
  assert.equal(warnings.warns.some(entry => entry[0] === 'Unknown island strategy: immediate'), true);
});

test('idle strategy uses requestIdleCallback when available', async () => {
  const island = new FakeElement('div');
  island.setAttribute('data-rw-module', 'test-idle');
  island.setAttribute('data-rw-strategy', 'idle');
  const idleCallbacks = [];

  loadIslands([island], {
    islandModules: {
      'test-idle': moduleWithMount('root.setAttribute("data-mounted", "idle");')
    },
    requestIdleCallback: callback => {
      idleCallbacks.push(callback);
    }
  });

  assert.equal(idleCallbacks.length, 1);
  idleCallbacks[0]();
  await flushHydration();

  assert.equal(island.getAttribute('data-rw-hydrated'), 'true');
  assert.equal(island.getAttribute('data-mounted'), 'idle');
});

test('visible strategy waits for intersection before hydrating', async () => {
  const island = new FakeElement('div');
  island.setAttribute('data-rw-module', 'test-visible');
  island.setAttribute('data-rw-strategy', 'visible');
  const observers = [];

  loadIslands([island], {
    islandModules: {
      'test-visible': moduleWithMount('root.setAttribute("data-mounted", "visible");')
    },
    IntersectionObserver: class {
      constructor(callback) {
        this.callback = callback;
        observers.push(this);
      }

      observe(target) {
        this.target = target;
      }

      unobserve() {}
    }
  });

  assert.equal(island.hasAttribute('data-rw-hydrated'), false);
  observers[0].callback([{ isIntersecting: true }]);
  await flushHydration();

  assert.equal(island.getAttribute('data-rw-hydrated'), 'true');
  assert.equal(island.getAttribute('data-mounted'), 'visible');
});

test('dangerous direct module schemes are rejected before dynamic import', async () => {
  const island = new FakeElement('div');
  island.setAttribute('data-rw-module', 'javascript:alert(1)');

  const { warnings } = loadIslands([island]);
  await flushHydration();

  assert.equal(island.hasAttribute('data-rw-hydrated'), false);
  assert.equal(warnings.warns.some(entry => entry[0].startsWith('RazorWire island module')), true);
});

test('non-string manifest module mappings are skipped without throwing', async () => {
  const island = new FakeElement('div');
  island.setAttribute('data-rw-module', 'bad-mapping');

  const { warnings } = loadIslands([island], {
    islandModules: {
      'bad-mapping': { path: moduleWithMount('root.setAttribute("data-mounted", "true");') }
    }
  });
  await flushHydration();

  assert.equal(island.hasAttribute('data-rw-hydrated'), false);
  assert.equal(warnings.warns.some(entry => entry[0] === 'RazorWire island module "bad-mapping" resolved to an empty module specifier.'), true);
});

function loadIslands(islands, overrides = {}) {
  const document = new FakeDocument(islands);
  const warnings = { errors: [], warns: [] };
  const window = {
    RazorWireIslandsInitialized: false,
    RazorWireIslandModules: overrides.islandModules
  };
  if (overrides.requestIdleCallback) {
    window.requestIdleCallback = overrides.requestIdleCallback;
  }
  if (overrides.IntersectionObserver) {
    window.IntersectionObserver = overrides.IntersectionObserver;
  }

  const context = {
    document,
    window,
    console: {
      error: (...args) => warnings.errors.push(args),
      warn: (...args) => warnings.warns.push(args)
    },
    setTimeout: callback => callback(),
    IntersectionObserver: overrides.IntersectionObserver,
    globalThis: null
  };
  context.globalThis = context;
  vm.createContext(context);
  vm.runInContext(readFileSync(islandsPath, 'utf8'), context, {
    importModuleDynamically: vm.constants.USE_MAIN_CONTEXT_DEFAULT_LOADER
  });

  return { document, warnings };
}

async function flushHydration() {
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
}

function moduleWithMount(body) {
  return `data:text/javascript,${encodeURIComponent(`export async function mount(root, props) { ${body} }`)}`;
}

class FakeDocument {
  constructor(islands) {
    this.readyState = 'complete';
    this.listeners = new Map();
    this.islands = islands;
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) || [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  dispatchEvent(event) {
    for (const listener of this.listeners.get(event.type) || []) {
      listener(event);
    }
  }

  querySelectorAll(selector) {
    if (selector === '[data-rw-module]:not([data-rw-hydrated])') {
      return this.islands.filter(island => island.hasAttribute('data-rw-module') && !island.hasAttribute('data-rw-hydrated'));
    }

    return [];
  }
}

class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.attributes = new Map();
    this.children = [];
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.get(name) ?? null;
  }

  hasAttribute(name) {
    return this.attributes.has(name);
  }

  set innerHTML(value) {
    this.children = value ? [new FakeElement('p')] : [];
  }
}
