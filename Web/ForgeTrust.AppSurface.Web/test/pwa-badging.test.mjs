import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const asset = name => readFileSync(new URL(`../Assets/Pwa/${name}`, import.meta.url), 'utf8');
const factorySource = asset('pwa-badging-factory.js').trim();
const registrationSource = asset('pwa-register.js');

function install({
  realm = 'page',
  nativeTarget = {},
  root = {},
  setup,
  DOMExceptionValue = DOMException,
  ErrorValue = Error,
  TypeErrorValue = TypeError
} = {}) {
  const errors = [];
  const context = {
    root,
    nativeTarget,
    DOMException: DOMExceptionValue,
    Error: ErrorValue,
    TypeError: TypeErrorValue,
    console: { error: value => errors.push(value) }
  };
  if (realm === 'page') {
    root.navigator = nativeTarget;
    context.window = root;
    context.navigator = nativeTarget;
  } else {
    context.self = root;
    root.navigator = nativeTarget;
  }
  vm.createContext(context);
  if (setup) vm.runInContext(setup, context);
  const rootName = realm === 'page' ? 'window' : 'self';
  assert.doesNotThrow(() => vm.runInContext(
    `const installBadging = ${factorySource}; installBadging(${rootName}, ${rootName}.navigator, () => console.error("ASPWAJS002"));`,
    context));
  let api;
  try {
    api = root.AppSurface?.Pwa?.badging;
  } catch {
    api = undefined;
  }
  return { context, root, nativeTarget, errors, api };
}

for (const realm of ['page', 'worker']) {
  test(`${realm} adapter validates, sets, clears, and preserves native receiver`, async () => {
    const calls = [];
    const nativeTarget = {
      async setAppBadge(value) { calls.push({ method: 'set', value, receiver: this }); },
      async clearAppBadge() { calls.push({ method: 'clear', receiver: this }); }
    };
    const result = install({ realm, nativeTarget });

    assert.equal(await result.api.set(4), 'accepted');
    assert.equal(await result.api.set(0), 'accepted');
    assert.equal(await result.api.clear(), 'accepted');
    assert.deepEqual(calls.map(call => [call.method, call.value]), [['set', 4], ['clear', undefined], ['clear', undefined]]);
    assert.ok(calls.every(call => call.receiver === nativeTarget));
    assert.deepEqual(result.errors, []);
  });

  test(`${realm} adapter returns unsupported and uses set-zero clear fallback`, async () => {
    const unsupported = install({ realm });
    assert.equal(await unsupported.api.set(2), 'unsupported');
    assert.equal(await unsupported.api.clear(), 'unsupported');

    const calls = [];
    const fallback = install({ realm, nativeTarget: { async setAppBadge(value) { calls.push(value); } } });
    assert.equal(await fallback.api.clear(), 'accepted');
    assert.deepEqual(calls, [0]);
  });

  test(`${realm} adapter rejects every invalid count before native access`, async () => {
    let nativeReads = 0;
    const nativeTarget = {};
    Object.defineProperty(nativeTarget, 'setAppBadge', { get() { nativeReads += 1; return async () => {}; } });
    const result = install({ realm, nativeTarget });

    for (const value of [-1, 1.5, Number.NaN, Number.POSITIVE_INFINITY, Number.MAX_SAFE_INTEGER + 1, '4', 4n]) {
      await assert.rejects(result.api.set(value), error => error instanceof TypeError && error.message === 'ASPWAJS040');
    }
    assert.equal(nativeReads, 0);
  });

  test(`${realm} adapter sanitizes native set and clear failures without fallback`, async () => {
    let setCalls = 0;
    const nativeTarget = {
      async setAppBadge() { setCalls += 1; throw new Error('secret count and permission'); },
      async clearAppBadge() { throw new Error('secret clear state'); }
    };
    const result = install({ realm, nativeTarget });

    await assert.rejects(result.api.set(3), error => error.name === 'InvalidStateError' && error.message === 'ASPWAJS041');
    await assert.rejects(result.api.clear(), error => error.name === 'InvalidStateError' && error.message === 'ASPWAJS042');
    assert.equal(setCalls, 1);
    assert.equal(result.errors.join(' ').includes('secret'), false);
  });
}

test('hostile native getters and error constructors remain value-free', async () => {
  const nativeTarget = {};
  Object.defineProperty(nativeTarget, 'setAppBadge', { get() { throw new Error('native getter secret'); } });
  const result = install({
    nativeTarget,
    DOMExceptionValue: function HostileDOMException() { throw new Error('DOMException constructor secret'); },
    ErrorValue: function HostileError() { throw new TypeError('Error constructor secret'); }
  });

  await assert.rejects(
    result.api.set(9),
    error => error.name === 'InvalidStateError' && error.message === 'ASPWAJS041' && !String(error).includes('secret'));
});

test('hostile TypeError constructor cannot expose invalid input', async () => {
  const result = install({ setup: 'TypeError = function HostileTypeError() { throw new Error("input secret"); };' });

  await assert.rejects(
    result.api.set(-1),
    error => error.name === 'TypeError' && error.message === 'ASPWAJS040' && !String(error).includes('secret'));
});

test('hostile error constructors cannot return matching unsanitized replacements', async () => {
  const nativeTarget = {};
  Object.defineProperty(nativeTarget, 'setAppBadge', { get() { throw new Error('native getter secret'); } });
  const invalidState = install({
    nativeTarget,
    DOMExceptionValue: function HostileDOMException() {
      this.secret = 'DOMException replacement secret';
    },
    ErrorValue: function HostileError() { return new Error('Error replacement secret'); }
  });
  const invalidCount = install({
    TypeErrorValue: function HostileTypeError() {
      this.secret = 'TypeError replacement secret';
    },
    ErrorValue: function HostileError() { return new Error('Error replacement secret'); }
  });
  invalidState.context.DOMException.prototype.name = 'InvalidStateError';
  invalidState.context.DOMException.prototype.message = 'ASPWAJS041';
  invalidCount.context.TypeError.prototype.name = 'TypeError';
  invalidCount.context.TypeError.prototype.message = 'ASPWAJS040';

  await assert.rejects(
    invalidState.api.set(9),
    error => error.name === 'InvalidStateError' && error.message === 'ASPWAJS041' && !String(error).includes('secret'));
  await assert.rejects(
    invalidCount.api.set(-1),
    error => error.name === 'TypeError' && error.message === 'ASPWAJS040' && !String(error).includes('secret'));
});

test('mutable Number global cannot influence count validation', async () => {
  const calls = [];
  const result = install({ nativeTarget: { async setAppBadge(value) { calls.push(value); } } });
  vm.runInContext('Number = { isSafeInteger() { throw new Error("number global secret"); } };', result.context);

  assert.equal(await result.api.set(4), 'accepted');
  await assert.rejects(
    result.api.set(-1),
    error => error.name === 'TypeError' && error.message === 'ASPWAJS040' && !String(error).includes('secret'));
  assert.deepEqual(calls, [4]);
});

test('API is frozen, branded, non-enumerable, and compatible duplicate load is a no-op', () => {
  const first = install();
  const original = first.api;
  const brand = original[Symbol.for('ForgeTrust.AppSurface.Pwa.badging')];
  assert.equal(Object.isFrozen(original), true);
  assert.equal(Object.isFrozen(brand), true);
  assert.equal(brand.version, 1);
  assert.deepEqual(Object.keys(original), []);

  vm.runInContext(
    `installBadging(window, window.navigator, () => console.error("ASPWAJS002"));`,
    first.context);
  assert.equal(first.root.AppSurface.Pwa.badging, original);
  assert.deepEqual(first.errors, []);
});

test('forged duplicate brands are rejected without replacing the occupied API', () => {
  const brandKey = Symbol.for('ForgeTrust.AppSurface.Pwa.badging');
  const forged = {};
  Object.defineProperties(forged, {
    set: { value: async () => 'accepted' },
    clear: { value: async () => 'accepted' },
    [brandKey]: { value: Object.freeze({ version: 1, forged: true }) }
  });
  Object.freeze(forged);
  const result = install({ root: { AppSurface: { Pwa: { badging: forged } } } });

  assert.equal(result.root.AppSurface.Pwa.badging, forged);
  assert.deepEqual(result.errors, ['ASPWAJS002']);

  const extraApiProperty = {};
  Object.defineProperties(extraApiProperty, {
    set: { value: async () => 'accepted' },
    clear: { value: async () => 'accepted' },
    extra: { value: 'hidden' },
    [brandKey]: { value: Object.freeze({ version: 1 }) }
  });
  Object.freeze(extraApiProperty);
  const extraPropertyResult = install({ root: { AppSurface: { Pwa: { badging: extraApiProperty } } } });

  assert.equal(extraPropertyResult.root.AppSurface.Pwa.badging, extraApiProperty);
  assert.deepEqual(extraPropertyResult.errors, ['ASPWAJS002']);
});

test('adapter preserves registration helper in either load order', () => {
  const registrationFirst = install({ setup: 'window.AppSurface = { Pwa: { register: () => "kept" } };' });
  assert.equal(registrationFirst.root.AppSurface.Pwa.register(), 'kept');
  assert.equal(typeof registrationFirst.api.set, 'function');

  const badgingFirst = install();
  badgingFirst.context.document = {
    currentScript: { dataset: { appsurfacePwaWorker: '/service-worker.js', appsurfacePwaScope: '/' } }
  };
  badgingFirst.context.navigator.serviceWorker = { register: async () => ({}) };
  vm.runInContext(registrationSource, badgingFirst.context);
  assert.equal(typeof badgingFirst.root.AppSurface.Pwa.register, 'function');
  assert.equal(badgingFirst.root.AppSurface.Pwa.badging, badgingFirst.api);
});

test('conflicting, inherited, accessor, frozen, and hostile namespaces are contained', () => {
  const occupied = install({ root: { AppSurface: { Pwa: { badging: { occupied: true } } } } });
  assert.equal(occupied.root.AppSurface.Pwa.badging.occupied, true);
  assert.deepEqual(occupied.errors, ['ASPWAJS002']);

  const inheritedRoot = Object.create({ AppSurface: {} });
  assert.deepEqual(install({ root: inheritedRoot }).errors, ['ASPWAJS002']);

  const accessorRoot = {};
  Object.defineProperty(accessorRoot, 'AppSurface', { get() { throw new Error('secret accessor'); } });
  assert.deepEqual(install({ root: accessorRoot }).errors, ['ASPWAJS002']);

  const ignoredRootWrites = {};
  const lyingRoot = new Proxy(ignoredRootWrites, { defineProperty: () => true });
  assert.deepEqual(install({ root: lyingRoot }).errors, ['ASPWAJS002']);
  assert.equal(Object.hasOwn(ignoredRootWrites, 'AppSurface'), false);

  const ignoredPwaWrites = {};
  const lyingAppSurface = new Proxy(ignoredPwaWrites, { defineProperty: () => true });
  assert.deepEqual(install({ root: { AppSurface: lyingAppSurface } }).errors, ['ASPWAJS002']);
  assert.equal(Object.hasOwn(ignoredPwaWrites, 'Pwa'), false);

  const ignoredBadgingWrites = {};
  const lyingPwa = new Proxy(ignoredBadgingWrites, { defineProperty: () => true });
  assert.deepEqual(install({ root: { AppSurface: { Pwa: lyingPwa } } }).errors, ['ASPWAJS002']);
  assert.equal(Object.hasOwn(ignoredBadgingWrites, 'badging'), false);

  const frozen = install({ root: { AppSurface: { Pwa: Object.freeze({}) } } });
  assert.deepEqual(frozen.errors, ['ASPWAJS002']);

  const proxy = install({ root: new Proxy({}, { has() { throw new Error('secret proxy'); } }) });
  assert.deepEqual(proxy.errors, ['ASPWAJS002']);
});
