import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import vm from 'node:vm';

const script = await readFile(new URL('../wwwroot/js/pwa-badging-proof.js', import.meta.url), 'utf8');
const view = await readFile(new URL('../Views/Home/Index.cshtml', import.meta.url), 'utf8');

class FakeElement {
  constructor({ value = '', disabled = false, textContent = '' } = {}) {
    this.attributes = new Map();
    this.dataset = {};
    this.disabled = disabled;
    this.focused = false;
    this.listeners = new Map();
    this.textContent = textContent;
    this.value = value;
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  async dispatch(type) {
    for (const listener of this.listeners.get(type) ?? []) listener({ type, target: this });
    await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
  }

  focus() { this.focused = true; }
  removeAttribute(name) { this.attributes.delete(name); }
  setAttribute(name, value) { this.attributes.set(name, value); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
}

function createHarness({ badging, readyState = 'complete', helperAccessError = false } = {}) {
  const elements = {
    'badge-count': new FakeElement({ value: '3' }),
    'badge-count-error': new FakeElement(),
    'set-badge': new FakeElement({ disabled: true }),
    'clear-badge': new FakeElement({ disabled: true }),
    'attention-count': new FakeElement({ textContent: '3 items need attention' }),
    'badging-status': new FakeElement({ textContent: 'Preparing the AppSurface badging helper…' })
  };
  const documentListeners = new Map();
  const document = {
    readyState,
    getElementById(id) { return elements[id] ?? null; },
    addEventListener(type, listener) {
      const listeners = documentListeners.get(type) ?? [];
      listeners.push(listener);
      documentListeners.set(type, listeners);
    },
    async dispatch(type) {
      for (const listener of documentListeners.get(type) ?? []) listener({ type });
      await new Promise(resolve => setImmediate(resolve));
    }
  };
  const window = {};
  if (helperAccessError) {
    Object.defineProperty(window, 'AppSurface', { get() { throw new Error('secret'); } });
  } else if (badging !== null) {
    window.AppSurface = { Pwa: { badging: badging ?? {
      async set() { return 'accepted'; },
      async clear() { return 'accepted'; }
    } } };
  }

  vm.runInNewContext(script, { document, window, Number, Promise }, { filename: 'pwa-badging-proof.js' });
  return { document, elements, window };
}

test('view exposes an independent accessible proof without push or permission controls', () => {
  assert.match(view, /id="badging-heading">Application badge/u);
  assert.match(view, /id="attention-count">3 items need attention/u);
  assert.match(view, /aria-live="polite" aria-atomic="true"/u);
  assert.match(view, /pwa-badging-proof\.js/u);
  assert.doesNotMatch(view, /badgeCount|request notification permission|push payload composer/iu);
});

test('initialization waits for DOM readiness and enables both independent actions', async () => {
  const harness = createHarness({ readyState: 'loading' });
  assert.equal(harness.elements['set-badge'].disabled, true);

  await harness.document.dispatch('DOMContentLoaded');

  assert.equal(harness.elements['badging-status'].dataset.state, 'ready');
  assert.equal(harness.elements['set-badge'].disabled, false);
  assert.equal(harness.elements['clear-badge'].disabled, false);
});

test('valid set updates canonical state and reports accepted or unsupported truthfully', async t => {
  await t.test('accepted', async () => {
    const calls = [];
    const harness = createHarness({ badging: {
      async set(count) { calls.push(count); return 'accepted'; },
      async clear() { return 'accepted'; }
    } });
    harness.elements['badge-count'].value = '4';

    await harness.elements['set-badge'].dispatch('click');

    assert.deepEqual(calls, [4]);
    assert.equal(harness.elements['attention-count'].textContent, '4 items need attention');
    assert.equal(harness.elements['badging-status'].dataset.state, 'accepted-set');
    assert.match(harness.elements['badging-status'].textContent, /may hide or change/u);
  });

  await t.test('unsupported', async () => {
    const harness = createHarness({ badging: {
      async set() { return 'unsupported'; },
      async clear() { return 'unsupported'; }
    } });
    await harness.elements['set-badge'].dispatch('click');
    assert.equal(harness.elements['badging-status'].dataset.state, 'unsupported');
    assert.match(harness.elements['badging-status'].textContent, /in-app attention state remains available/u);
  });
});

test('pending request locks both actions until the helper settles', async () => {
  let resolveSet;
  const harness = createHarness({ badging: {
    set() { return new Promise(resolve => { resolveSet = resolve; }); },
    async clear() { return 'accepted'; }
  } });

  const pending = harness.elements['set-badge'].dispatch('click');
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(harness.elements['badging-status'].dataset.state, 'setting');
  assert.equal(harness.elements['set-badge'].disabled, true);
  assert.equal(harness.elements['clear-badge'].disabled, true);

  resolveSet('accepted');
  await pending;
  assert.equal(harness.elements['set-badge'].disabled, false);
  assert.equal(harness.elements['clear-badge'].disabled, false);
});

test('unexpected outcome and hostile error access fail closed without false acceptance', async () => {
  const unexpected = createHarness({ badging: {
    async set() { return 'visible'; },
    async clear() { return 'accepted'; }
  } });
  await unexpected.elements['set-badge'].dispatch('click');
  assert.equal(unexpected.elements['badging-status'].dataset.state, 'helper-conflict');
  assert.equal(unexpected.elements['set-badge'].disabled, true);
  assert.equal(unexpected.elements['clear-badge'].disabled, true);
  assert.doesNotMatch(unexpected.elements['badging-status'].textContent, /accepted for/u);

  const hostile = createHarness({ badging: {
    async set() {
      const error = {};
      Object.defineProperty(error, 'message', { get() { throw new Error('secret'); } });
      throw error;
    },
    async clear() { return 'accepted'; }
  } });
  await hostile.elements['set-badge'].dispatch('click');
  assert.equal(hostile.elements['badging-status'].dataset.state, 'helper-conflict');
  assert.equal(hostile.elements['set-badge'].disabled, true);
  assert.equal(hostile.elements['clear-badge'].disabled, true);
  assert.doesNotMatch(hostile.elements['badging-status'].textContent, /secret/u);
});

test('invalid input never calls the helper and returns focus with bounded guidance', async () => {
  let calls = 0;
  const harness = createHarness({ badging: {
    async set() { calls += 1; return 'accepted'; },
    async clear() { calls += 1; return 'accepted'; }
  } });
  harness.elements['badge-count'].value = '1e3';

  await harness.elements['set-badge'].dispatch('click');

  assert.equal(calls, 0);
  assert.equal(harness.elements['badge-count'].getAttribute('aria-invalid'), 'true');
  assert.equal(harness.elements['badge-count'].focused, true);
  assert.equal(harness.elements['badging-status'].dataset.state, 'invalid');
});

test('set and clear rejection preserve the newly authoritative in-app state', async () => {
  const harness = createHarness({ badging: {
    async set() { throw new DOMException('ASPWAJS041', 'InvalidStateError'); },
    async clear() { throw new DOMException('ASPWAJS042', 'InvalidStateError'); }
  } });
  harness.elements['badge-count'].value = '7';

  await harness.elements['set-badge'].dispatch('click');
  assert.equal(harness.elements['attention-count'].textContent, '7 items need attention');
  assert.match(harness.elements['badging-status'].textContent, /remains 7/u);

  await harness.elements['clear-badge'].dispatch('click');
  assert.equal(harness.elements['attention-count'].textContent, '0 items need attention');
  assert.match(harness.elements['badging-status'].textContent, /remains 0/u);
});

test('missing or hostile helper stays disabled with value-free recovery guidance', () => {
  for (const options of [{ badging: null }, { helperAccessError: true }]) {
    const harness = createHarness(options);
    assert.equal(harness.elements['badging-status'].dataset.state, 'helper-conflict');
    assert.equal(harness.elements['set-badge'].disabled, true);
    assert.equal(harness.elements['clear-badge'].disabled, true);
    assert.doesNotMatch(harness.elements['badging-status'].textContent, /secret/u);
  }
});
