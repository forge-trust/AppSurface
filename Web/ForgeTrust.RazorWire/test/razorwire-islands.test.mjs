import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const islandsPath = new URL('../wwwroot/razorwire/razorwire.islands.js', import.meta.url);
const mountFixture = './test/fixtures/razorwire-test-island.mjs';
const missingMountFixture = './test/fixtures/razorwire-missing-mount.mjs';

test('load strategy hydrates generated output and passes parsed props', async () => {
  const island = new FakeElement('div');
  island.setAttribute('data-rw-module', 'test-load');
  island.setAttribute('data-rw-props', '{"attributeName":"data-mounted-props","attributeValue":"42"}');

  loadIslands([island], {
    islandModules: {
      'test-load': mountFixture
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
  island.setAttribute('data-rw-props', '{"recordChildCountAttribute":"data-child-count"}');

  loadIslands([island], {
    islandModules: {
      'test-only': mountFixture
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
      'test-missing-mount': missingMountFixture
    }
  });
  await waitForAttributeValue(island, 'data-rw-hydrated', 'failed');
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
      'test-unknown': mountFixture
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
  island.setAttribute('data-rw-props', '{"attributeName":"data-mounted","attributeValue":"idle"}');
  const idleCallbacks = [];

  loadIslands([island], {
    islandModules: {
      'test-idle': mountFixture
    },
    requestIdleCallback: callback => {
      idleCallbacks.push(callback);
    }
  });

  await waitForCondition(() => idleCallbacks.length === 1);
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
  island.setAttribute('data-rw-props', '{"attributeName":"data-mounted","attributeValue":"visible"}');
  const observers = [];

  loadIslands([island], {
    islandModules: {
      'test-visible': mountFixture
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
  await waitForCondition(() => observers.length === 1);
  observers[0].callback([{ isIntersecting: true }]);
  await flushHydration();

  assert.equal(island.getAttribute('data-rw-hydrated'), 'true');
  assert.equal(island.getAttribute('data-mounted'), 'visible');
});

test('allowed direct module specifiers pass validation before strategy handling', async () => {
  const cases = [
    './test/fixtures/razorwire-test-island.mjs',
    '/js/island.js',
    'http://example.test/js/island.js',
    'https://cdn.example.test/island.js',
    'test-import-map-island'
  ];

  for (const moduleSpecifier of cases) {
    const island = new FakeElement('div');
    island.setAttribute('data-rw-module', moduleSpecifier);
    island.setAttribute('data-rw-strategy', 'manual');

    const { warnings } = loadIslands([island]);
    await flushHydration();

    assert.equal(island.hasAttribute('data-rw-hydrated'), false);
    assert.equal(warnings.warns.some(entry => entry[0] === 'Unknown island strategy: manual'), true, moduleSpecifier);
    assert.equal(warnings.warns.some(entry => entry[0].startsWith('RazorWire island module')), false, moduleSpecifier);
  }
});

test('allowed manifest module specifiers pass validation before strategy handling', async () => {
  const cases = [
    './test/fixtures/razorwire-test-island.mjs',
    '/js/island.js',
    'http://example.test/js/island.js',
    'https://cdn.example.test/island.js',
    'test-import-map-island'
  ];

  for (const moduleSpecifier of cases) {
    const island = new FakeElement('div');
    island.setAttribute('data-rw-module', 'test-manifest-entry');
    island.setAttribute('data-rw-strategy', 'manual');

    const { warnings } = loadIslands([island], {
      islandModules: {
        'test-manifest-entry': moduleSpecifier
      }
    });
    await flushHydration();

    assert.equal(island.hasAttribute('data-rw-hydrated'), false);
    assert.equal(warnings.warns.some(entry => entry[0] === 'Unknown island strategy: manual'), true, moduleSpecifier);
    assert.equal(warnings.warns.some(entry => entry[0].startsWith('RazorWire island module')), false, moduleSpecifier);
  }
});

test('blocked direct module specifiers are rejected before dynamic import', async () => {
  const cases = [
    'data:text/javascript,export function mount() {}',
    'data:text/javascript;base64,ZXhwb3J0IGZ1bmN0aW9uIG1vdW50KCkge30=',
    'data:application/json,{}',
    'javascript:alert(1)',
    'blob:https://example.test/module',
    'file:///tmp/island.js',
    '//cdn.example.test/island.js',
    'http://other.example.test/island.js',
    'ftp://example.test/island.js',
    '',
    '   '
  ];

  for (const moduleSpecifier of cases) {
    const island = new FakeElement('div');
    island.setAttribute('data-rw-module', moduleSpecifier);
    island.setAttribute('data-rw-strategy', 'manual');

    const { warnings } = loadIslands([island]);
    await flushHydration();

    assert.equal(island.hasAttribute('data-rw-hydrated'), false);
    assert.equal(warnings.warns.some(entry => entry[0] === 'Unknown island strategy: manual'), false, moduleSpecifier);
    assert.equal(warnings.warns.some(entry => blockedWarningMatches(entry, 'data-rw-module')), true, moduleSpecifier);
  }
});

test('blocked manifest module specifiers are rejected before dynamic import', async () => {
  const cases = [
    'data:text/javascript,export function mount() {}',
    'data:text/javascript;base64,ZXhwb3J0IGZ1bmN0aW9uIG1vdW50KCkge30=',
    'data:application/json,{}',
    'javascript:alert(1)',
    'blob:https://example.test/module',
    'file:///tmp/island.js',
    '//cdn.example.test/island.js',
    'http://other.example.test/island.js',
    'ftp://example.test/island.js',
    '   '
  ];

  for (const moduleSpecifier of cases) {
    const island = new FakeElement('div');
    island.setAttribute('data-rw-module', 'blocked-mapping');
    island.setAttribute('data-rw-strategy', 'manual');

    const { warnings } = loadIslands([island], {
      islandModules: {
        'blocked-mapping': moduleSpecifier
      }
    });
    await flushHydration();

    assert.equal(island.hasAttribute('data-rw-hydrated'), false);
    assert.equal(warnings.warns.some(entry => entry[0] === 'Unknown island strategy: manual'), false, moduleSpecifier);
    assert.equal(warnings.warns.some(entry => blockedWarningMatches(entry, 'window.RazorWireIslandModules')), true, moduleSpecifier);
  }
});

test('non-string manifest module mappings are skipped without throwing', async () => {
  const island = new FakeElement('div');
  island.setAttribute('data-rw-module', 'bad-mapping');
  const mappingValue = { path: mountFixture };

  const { warnings } = loadIslands([island], {
    islandModules: {
      'bad-mapping': mappingValue
    }
  });
  await flushHydration();

  assert.equal(island.hasAttribute('data-rw-hydrated'), false);
  const warning = warnings.warns.find(entry => blockedWarningMatches(entry, 'window.RazorWireIslandModules'));
  assert.ok(warning);
  assert.equal(warning[1], mappingValue);
});

function loadIslands(islands, overrides = {}) {
  const document = new FakeDocument(islands);
  const warnings = { errors: [], warns: [] };
  const window = {
    RazorWireIslandsInitialized: false,
    RazorWireIslandModules: overrides.islandModules,
    location: { origin: 'http://example.test' }
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
    URL,
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

async function waitForAttributeValue(element, name, value) {
  await waitForCondition(() => element.getAttribute(name) === value);
}

async function waitForCondition(predicate) {
  const maxAttempts = 20;
  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    if (predicate()) {
      return;
    }

    await flushHydration();
  }

  assert.fail(`waitForCondition timed out after ${maxAttempts} attempts`);
}

function blockedWarningMatches(entry, source) {
  return entry[0].startsWith('RazorWire island module')
    && entry[0].includes(`from ${source}`)
    && entry[0].includes('Use a relative, root-relative, same-origin, explicit HTTPS, or bare import-map module specifier.');
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
