import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const helperSource = readFileSync(new URL('../Assets/Pwa/pwa-register.js', import.meta.url), 'utf8');

function runHelper({ window = {}, navigator = {}, worker = '/tenant/service-worker.js', scope = '/tenant/', setup } = {}) {
  const errors = [];
  const context = {
    window,
    navigator,
    document: { currentScript: { dataset: { appsurfacePwaWorker: worker, appsurfacePwaScope: scope } } },
    console: { error: value => errors.push(value) },
    DOMException,
    Error,
    Symbol,
    Promise
  };
  vm.createContext(context);
  if (setup) vm.runInContext(setup, context);
  assert.doesNotThrow(() => vm.runInContext(helperSource, context));
  return { context, errors };
}

test('helper load is inert and register passes the captured immutable arguments', async () => {
  const calls = [];
  const navigator = {
    serviceWorker: {
      async register(worker, options) { calls.push({ worker, options }); return { active: true }; }
    },
    Notification: { requestPermission() { calls.push('permission'); } },
    PushManager: { subscribe() { calls.push('subscription'); } }
  };
  const result = runHelper({ navigator });
  assert.deepEqual(calls, []);
  result.context.document.currentScript = null;
  const registration = await result.context.window.AppSurface.Pwa.register();
  assert.deepEqual(registration, { active: true });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].worker, '/tenant/service-worker.js');
  assert.equal(calls[0].options.scope, '/tenant/');
  assert.equal(calls[0].options.updateViaCache, 'none');
  assert.deepEqual(result.errors, []);
});

test('helper preserves a valid non-directory raw-prefix scope', async () => {
  const calls = [];
  const result = runHelper({
    scope: '/tenant/app',
    navigator: {
      serviceWorker: {
        async register(worker, options) { calls.push({ worker, options }); return {}; }
      }
    }
  });

  await result.context.window.AppSurface.Pwa.register();

  assert.deepEqual(JSON.parse(JSON.stringify(calls)), [{
    worker: '/tenant/service-worker.js',
    options: { scope: '/tenant/app', updateViaCache: 'none' }
  }]);
});

test('unsupported service workers resolve null without touching permission or subscription APIs', async () => {
  const calls = [];
  const result = runHelper({
    navigator: {
      Notification: { requestPermission() { calls.push('permission'); } },
      PushManager: { subscribe() { calls.push('subscription'); } }
    }
  });
  assert.equal(await result.context.window.AppSurface.Pwa.register(), null);
  assert.deepEqual(calls, []);
});

test('invalid metadata and registration failures use stable sanitized InvalidStateError values', async () => {
  const invalid = runHelper({ navigator: { serviceWorker: {} }, worker: '../worker.js' });
  await assert.rejects(invalid.context.window.AppSurface.Pwa.register(), error =>
    error.name === 'InvalidStateError' && error.message === 'ASPWAJS001');

  const rejected = runHelper({
    navigator: { serviceWorker: { register: async () => { throw new Error('secret endpoint'); } } }
  });
  await assert.rejects(rejected.context.window.AppSurface.Pwa.register(), error =>
    error.name === 'InvalidStateError' && error.message === 'ASPWAJS003' && !error.message.includes('secret'));

  const hostileNavigator = runHelper({
    navigator: new Proxy({}, { has() { throw new Error('navigator secret'); } })
  });
  await assert.rejects(hostileNavigator.context.window.AppSurface.Pwa.register(), error =>
    error.name === 'InvalidStateError' && error.message === 'ASPWAJS003');
});

test('identical duplicate helper loading is a no-op and conflicting configuration is rejected', () => {
  const navigator = { serviceWorker: { register: async () => ({}) } };
  const first = runHelper({ navigator });
  const original = first.context.window.AppSurface.Pwa.register;

  first.context.document.currentScript = {
    dataset: { appsurfacePwaWorker: '/tenant/service-worker.js', appsurfacePwaScope: '/tenant/' }
  };
  vm.runInContext(helperSource, first.context);
  assert.equal(first.context.window.AppSurface.Pwa.register, original);
  assert.deepEqual(first.errors, []);

  first.context.document.currentScript.dataset.appsurfacePwaScope = '/other/';
  vm.runInContext(helperSource, first.context);
  assert.equal(first.context.window.AppSurface.Pwa.register, original);
  assert.deepEqual(first.errors, ['ASPWAJS002']);
});

test('helper merges plain namespaces and refuses conflicting values without overwriting them', () => {
  const merged = runHelper({ setup: 'window.AppSurface = { kept: true };' });
  const existing = merged.context.window.AppSurface;
  assert.equal(merged.context.window.AppSurface, existing);
  assert.equal(existing.kept, true);
  assert.equal(typeof existing.Pwa.register, 'function');

  for (const AppSurface of [null, 'occupied', [], () => {}]) {
    const result = runHelper({ window: { AppSurface } });
    assert.equal(result.context.window.AppSurface, AppSurface);
    assert.deepEqual(result.errors, ['ASPWAJS002']);
  }

  const register = () => 'existing';
  const occupied = runHelper({ window: { AppSurface: { Pwa: { register } } } });
  assert.equal(occupied.context.window.AppSurface.Pwa.register, register);
  assert.deepEqual(occupied.errors, ['ASPWAJS002']);
});

test('frozen objects, proxies, throwing accessors, and forged brands never escape or get overwritten', () => {
  const cases = [
    { AppSurface: Object.freeze({}) },
    { AppSurface: new Proxy({}, { getPrototypeOf() { throw new Error('trap'); } }) },
    { get AppSurface() { throw new Error('getter'); } }
  ];
  for (const window of cases) {
    const result = runHelper({ window });
    assert.deepEqual(result.errors, ['ASPWAJS002']);
  }

  const brandKey = Symbol.for('ForgeTrust.AppSurface.Pwa.register');
  const forged = () => {};
  Object.defineProperty(forged, brandKey, { value: new Proxy({}, { get() { throw new Error('brand trap'); } }) });
  const result = runHelper({ window: { AppSurface: { Pwa: { register: forged } } } });
  assert.equal(result.context.window.AppSurface.Pwa.register, forged);
  assert.deepEqual(result.errors, ['ASPWAJS002']);

  const accessorPwa = {};
  const accessorRegister = () => 'accessor';
  Object.defineProperty(accessorPwa, 'register', { configurable: true, get: () => accessorRegister });
  const accessor = runHelper({ window: { AppSurface: { Pwa: accessorPwa } } });
  assert.equal(accessor.context.window.AppSurface.Pwa.register, accessorRegister);
  assert.deepEqual(accessor.errors, ['ASPWAJS002']);

  const customAppSurface = Object.create(Object.create(null));
  const customRoot = runHelper({ window: { AppSurface: customAppSurface } });
  assert.equal(customRoot.context.window.AppSurface, customAppSurface);
  assert.deepEqual(customRoot.errors, ['ASPWAJS002']);

  const customPwa = Object.create(Object.create(null));
  const customNested = runHelper({ window: { AppSurface: { Pwa: customPwa } } });
  assert.equal(customNested.context.window.AppSurface.Pwa, customPwa);
  assert.deepEqual(customNested.errors, ['ASPWAJS002']);

  const forgedPrototype = Object.create(null);
  forgedPrototype.hasOwnProperty = () => false;
  forgedPrototype.toString = () => '[object Object]';
  const forgedRoot = Object.create(forgedPrototype);
  const forgedPrototypeResult = runHelper({ window: { AppSurface: forgedRoot } });
  assert.equal(forgedPrototypeResult.context.window.AppSurface, forgedRoot);
  assert.deepEqual(forgedPrototypeResult.errors, ['ASPWAJS002']);

  const accessorValue = {};
  const accessorWindow = {};
  Object.defineProperty(accessorWindow, 'AppSurface', { get: () => accessorValue });
  const rootAccessor = runHelper({ window: accessorWindow });
  assert.equal(accessorValue.Pwa, undefined);
  assert.deepEqual(rootAccessor.errors, ['ASPWAJS002']);

  const inheritedValue = {};
  const inheritedWindow = Object.create({ AppSurface: inheritedValue });
  const inherited = runHelper({ window: inheritedWindow });
  assert.equal(inheritedValue.Pwa, undefined);
  assert.deepEqual(inherited.errors, ['ASPWAJS002']);
});

test('helper rejects recursively encoded traversal metadata', async () => {
  const result = runHelper({
    worker: '/tenant/%252e%252e/service-worker.js',
    scope: '/tenant/',
    navigator: { serviceWorker: {} }
  });
  await assert.rejects(
    result.context.window.AppSurface.Pwa.register(),
    error => error.name === 'InvalidStateError' && error.message === 'ASPWAJS001');
});

test('helper brand is frozen and non-enumerable', () => {
  const result = runHelper();
  const register = result.context.window.AppSurface.Pwa.register;
  const key = Symbol.for('ForgeTrust.AppSurface.Pwa.register');
  const descriptor = Object.getOwnPropertyDescriptor(register, key);
  assert.equal(descriptor.enumerable, false);
  assert.equal(Object.isFrozen(descriptor.value), true);
  assert.deepEqual(Object.keys(descriptor.value), ['version', 'workerPath', 'scope']);
});
