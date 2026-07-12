import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const asset = name => readFileSync(new URL(`../Assets/Pwa/${name}`, import.meta.url), 'utf8');
const sharedSource = asset('pwa-worker-shared.js');
const offlineSource = asset('pwa-worker-offline.js');
const pushSource = asset('pwa-worker-push.js');
const customHandlerSource = asset('pwa-worker-custom-handler.js');
const pathVectors = JSON.parse(asset('pwa-path-vectors.json'));

const defaultConfig = {
  offlineEnabled: false,
  cachePrefix: 'appsurface-pwa-scope-',
  cacheName: 'appsurface-pwa-scope-v1',
  legacyCacheNames: [],
  staticAssets: ['/tenant/app.css', '/tenant/offline'],
  offlineFallback: '/tenant/offline',
  pathBase: '/tenant',
  scope: '/tenant/'
};

function createWorker({ config = {}, sources = [sharedSource, pushSource], cacheKeys = [], importHandler = () => {} } = {}) {
  const listeners = new Map();
  const warnings = [];
  const deleted = [];
  const opened = [];
  const notifications = [];
  const context = {
    ArrayBuffer,
    TextDecoder,
    URL,
    Object,
    Promise,
    Error,
    console: { warn: value => warnings.push(value) },
    importScripts: path => importHandler(path, context),
    fetch: async request => ({ network: request.url ?? request }),
    caches: {
      keys: async () => [...cacheKeys],
      delete: async key => { deleted.push(key); return true; },
      open: async name => {
        const cache = { name, addAllCalls: [], async addAll(paths) { this.addAllCalls.push([...paths]); } };
        opened.push(cache);
        return cache;
      },
      match: async request => ({ cached: request.url ?? request })
    },
    self: {
      location: { origin: 'https://example.test' },
      addEventListener(type, listener) {
        const values = listeners.get(type) ?? [];
        values.push(listener);
        listeners.set(type, values);
      },
      skipWaiting: async () => {},
      clients: {
        claim: async () => {},
        matchAll: async () => [],
        openWindow: async url => ({ url })
      },
      registration: {
        async showNotification(title, options) { notifications.push({ title, options }); }
      }
    }
  };
  vm.createContext(context);
  const merged = { ...defaultConfig, ...config };
  vm.runInContext(`const APPSURFACE_PWA_CONFIG = ${JSON.stringify(merged)};\n${sources.join('\n')}`, context);

  async function emit(type, event = {}) {
    const promises = [];
    const fullEvent = { ...event, waitUntil(value) { promises.push(value); } };
    for (const listener of listeners.get(type) ?? []) listener(fullEvent);
    await Promise.all(promises);
    return { event: fullEvent, promises };
  }

  return { context, listeners, warnings, deleted, opened, notifications, emit };
}

function payloadData(value) {
  const bytes = typeof value === 'string' ? new TextEncoder().encode(value) : value;
  return { async arrayBuffer() { return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength); } };
}

test('shared lifecycle contains skipWaiting and claim failures and retires configured legacy caches', async () => {
  const worker = createWorker({
    config: { offlineEnabled: true, legacyCacheNames: ['appsurface-pwa-v1'] },
    cacheKeys: ['foreign', 'appsurface-pwa-v1', 'appsurface-pwa-scope-v0', 'appsurface-pwa-scope-v1']
  });
  worker.context.self.skipWaiting = async () => { throw new Error('secret skip value'); };
  worker.context.self.clients.claim = async () => { throw new Error('secret claim value'); };

  const install = await worker.emit('install');
  const activate = await worker.emit('activate');

  assert.equal(install.promises.length, 1);
  assert.equal(activate.promises.length, 1);
  assert.deepEqual(worker.deleted, ['appsurface-pwa-v1', 'appsurface-pwa-scope-v0']);
  assert.deepEqual(worker.warnings, ['ASPWAJS030', 'ASPWAJS030']);
  assert.equal(worker.warnings.join(' ').includes('secret'), false);
});

test('offline mode keeps its active cache, precaches assets, and installs fetch behavior', async () => {
  const worker = createWorker({
    config: { offlineEnabled: true },
    sources: [sharedSource, offlineSource],
    cacheKeys: ['appsurface-pwa-scope-v0', 'appsurface-pwa-scope-v1']
  });

  await worker.emit('install');
  await worker.emit('activate');
  assert.equal(worker.opened.length, 1);
  assert.equal(worker.opened[0].name, defaultConfig.cacheName);
  assert.deepEqual(worker.opened[0].addAllCalls[0], defaultConfig.staticAssets);
  assert.deepEqual(worker.deleted, ['appsurface-pwa-scope-v0']);
  assert.equal(worker.listeners.get('fetch').length, 1);

  let response;
  await worker.emit('fetch', {
    request: { method: 'GET', url: 'https://example.test/tenant/app.css?v=content-hash', mode: 'no-cors' },
    respondWith(value) { response = value; }
  });
  assert.deepEqual(await response, { cached: 'https://example.test/tenant/app.css?v=content-hash' });

  response = undefined;
  await worker.emit('fetch', {
    request: { method: 'POST', url: 'https://example.test/tenant/app.css', mode: 'no-cors' },
    respondWith(value) { response = value; }
  });
  assert.equal(response, undefined);
});

test('offline fetches read only from the worker-owned cache', async () => {
  const worker = createWorker({ config: { offlineEnabled: true }, sources: [sharedSource, offlineSource] });
  const matchCalls = [];
  worker.context.caches.match = async (request, options) => {
    matchCalls.push({ request: request.url ?? request, options });
    return options?.cacheName === defaultConfig.cacheName ? { owned: true } : { foreign: true };
  };

  let response;
  await worker.emit('fetch', {
    request: { method: 'GET', url: 'https://example.test/tenant/app.css?v=content-hash', mode: 'no-cors' },
    respondWith(value) { response = value; }
  });
  assert.deepEqual(await response, { owned: true });

  worker.context.fetch = async () => { throw new Error('offline'); };
  await worker.emit('fetch', {
    request: { method: 'GET', url: 'https://example.test/tenant/page', mode: 'navigate' },
    respondWith(value) { response = value; }
  });
  assert.deepEqual(await response, { owned: true });
  assert.deepEqual(JSON.parse(JSON.stringify(matchCalls)), [
    {
      request: 'https://example.test/tenant/app.css?v=content-hash',
      options: { cacheName: defaultConfig.cacheName, ignoreSearch: true }
    },
    {
      request: defaultConfig.offlineFallback,
      options: { cacheName: defaultConfig.cacheName }
    }
  ]);
});

test('offline precache failures remain fatal while shared lifecycle failures stay contained', async () => {
  const offline = createWorker({ config: { offlineEnabled: true }, sources: [sharedSource, offlineSource] });
  offline.context.caches.open = async () => { throw new Error('precache failed'); };
  await assert.rejects(offline.emit('install'), /precache failed/u);
  assert.deepEqual(offline.warnings, []);

  const lifecycle = createWorker({ cacheKeys: ['appsurface-pwa-scope-v0'] });
  let claimCalled = false;
  lifecycle.context.caches.keys = async () => { throw new Error('retirement failed'); };
  lifecycle.context.self.clients.claim = async () => { claimCalled = true; };
  await lifecycle.emit('activate');
  assert.equal(claimCalled, true);
  assert.deepEqual(lifecycle.warnings, ['ASPWAJS030']);
});

test('push-only composition has no fetch listener, opens no cache, and retires its owned caches', async () => {
  const worker = createWorker({ cacheKeys: ['appsurface-pwa-v1', 'appsurface-pwa-scope-v1', 'foreign'] });
  await worker.emit('activate');
  assert.equal(worker.listeners.has('fetch'), false);
  assert.equal(worker.opened.length, 0);
  assert.deepEqual(worker.deleted, ['appsurface-pwa-scope-v1']);
});

test('custom handler import is executed once and failures preserve offline behavior', async () => {
  const imported = [];
  const successful = createWorker({
    config: { offlineEnabled: true, handlerScriptPath: '/tenant/workers/push.js' },
    sources: [sharedSource, offlineSource, customHandlerSource],
    importHandler: path => imported.push(path)
  });
  assert.deepEqual(imported, ['/tenant/workers/push.js']);
  assert.equal(successful.listeners.get('fetch').length, 1);

  const failed = createWorker({
    config: { offlineEnabled: true, handlerScriptPath: '/tenant/workers/private.js' },
    sources: [sharedSource, offlineSource, customHandlerSource],
    importHandler: () => { throw new Error('private import failure'); }
  });
  assert.deepEqual(failed.warnings, ['ASPWAJS030']);
  assert.equal(failed.warnings.join(' ').includes('private'), false);
  assert.equal(failed.listeners.get('fetch').length, 1);
  await failed.emit('install');
  assert.equal(failed.opened.length, 1);

  const partial = createWorker({
    config: { handlerScriptPath: '/tenant/workers/partial.js' },
    sources: [sharedSource, customHandlerSource],
    importHandler: (_path, workerContext) => {
      workerContext.self.addEventListener('push', () => {});
      throw new Error('failure after side effect');
    }
  });
  assert.equal(partial.listeners.get('push').length, 1);
  assert.deepEqual(partial.warnings, ['ASPWAJS030']);
});

test('default push adapter accepts the minimal and full v1 contracts', async () => {
  const worker = createWorker();
  await worker.emit('push', { data: payloadData(JSON.stringify({ version: 1, title: 'Ready' })) });
  await worker.emit('push', { data: payloadData(JSON.stringify({
    version: 1,
    title: 'Ready',
    body: 'Review it',
    iconPath: '/icons/app.png',
    badgePath: '/icons/badge.png',
    tag: 'ready',
    destinationPath: '/review?id=1'
  })) });

  assert.equal(worker.notifications.length, 2);
  assert.deepEqual(JSON.parse(JSON.stringify(worker.notifications[0])), { title: 'Ready', options: {} });
  assert.deepEqual(JSON.parse(JSON.stringify(worker.notifications[1])), {
    title: 'Ready',
    options: {
      body: 'Review it',
      icon: 'https://example.test/tenant/icons/app.png',
      badge: 'https://example.test/tenant/icons/badge.png',
      tag: 'ready',
      data: { appSurfaceDestination: 'https://example.test/tenant/review?id=1' }
    }
  });
  assert.deepEqual(worker.warnings, []);
});

test('default push adapter follows browser raw-prefix scope semantics', async () => {
  const worker = createWorker({ config: { scope: '/tenant/app' } });
  await worker.emit('push', {
    data: payloadData(JSON.stringify({ version: 1, title: 'Ready', destinationPath: '/application' }))
  });

  assert.equal(worker.notifications.length, 1);
  assert.equal(
    worker.notifications[0].options.data.appSurfaceDestination,
    'https://example.test/tenant/application');
});

test('default push adapter follows the shared path conformance vectors', async () => {
  for (const vector of pathVectors.assetPaths) {
    const worker = createWorker();
    await worker.emit('push', { data: payloadData(JSON.stringify({ version: 1, title: 'x', iconPath: vector.value })) });
    assert.equal(worker.notifications.length === 1, vector.valid, `asset path: ${vector.value}`);
  }

  for (const vector of pathVectors.destinationPaths) {
    const worker = createWorker();
    await worker.emit('push', { data: payloadData(JSON.stringify({ version: 1, title: 'x', destinationPath: vector.value })) });
    assert.equal(worker.notifications.length === 1, vector.valid, `destination path: ${vector.value}`);
  }
});

test('default push adapter rejects malformed schema, paths, encoding, and unsafe optional values with content-free diagnostics', async () => {
  const invalid = [
    null,
    [],
    { version: 1 },
    { version: 2, title: 'x' },
    { version: 1, title: '' },
    { version: 1, title: 'x'.repeat(257) },
    { version: 1, title: 'private-title', body: null },
    { version: 1, title: 'x', body: 'x'.repeat(2049) },
    { version: 1, title: 'x', tag: '' },
    { version: 1, title: 'x', tag: 'x'.repeat(129) },
    { version: 1, title: 'x', unknown: 'secret' },
    { version: 1, title: 'x', iconPath: '//evil.test/a' },
    { version: 1, title: 'x', iconPath: '/a?secret=1' },
    { version: 1, title: 'x', badgePath: '/a%2f..%2fb' },
    { version: 1, title: 'x', destinationPath: '/%2e%2e/outside' },
    { version: 1, title: 'x', destinationPath: '/a#fragment' },
    { version: 1, title: 'x', destinationPath: '/a?one?two' },
    { version: 1, title: 'x', destinationPath: '/a?bad=%zz' }
  ];
  const worker = createWorker();
  for (const value of invalid) {
    await worker.emit('push', { data: payloadData(JSON.stringify(value)) });
  }
  await worker.emit('push', { data: payloadData(new Uint8Array([0xc3, 0x28])) });
  await worker.emit('push', { data: null });

  assert.equal(worker.notifications.length, 0);
  assert.equal(worker.warnings.length, invalid.length + 2);
  assert.equal(worker.warnings.every(value => value === 'ASPWAJS010'), true);
  assert.equal(worker.warnings.join(' ').includes('private-title'), false);
  assert.equal(worker.warnings.join(' ').includes('secret'), false);
});

function payloadAtByteLength(target) {
  const payload = {
    version: 1,
    title: 't'.repeat(256),
    body: 'b'.repeat(2048),
    iconPath: `/${'i'.repeat(1023)}`,
    badgePath: '/'
  };
  const initial = JSON.stringify(payload);
  const needed = target - new TextEncoder().encode(initial).byteLength;
  assert.ok(needed >= 0 && needed <= 1023);
  payload.badgePath += 'd'.repeat(needed);
  assert.equal(new TextEncoder().encode(JSON.stringify(payload)).byteLength, target);
  return JSON.stringify(payload);
}

test('default push adapter enforces the 3993-byte UTF-8 document boundary', async () => {
  const worker = createWorker();
  await worker.emit('push', { data: payloadData(payloadAtByteLength(3992)) });
  await worker.emit('push', { data: payloadData(payloadAtByteLength(3993)) });
  await worker.emit('push', { data: payloadData(payloadAtByteLength(3994)) });
  assert.equal(worker.notifications.length, 2);
  assert.deepEqual(worker.warnings, ['ASPWAJS010']);
});

test('notification display failure is contained without exception details', async () => {
  const worker = createWorker();
  worker.context.self.registration.showNotification = async () => { throw new Error('payload secret'); };
  await worker.emit('push', { data: payloadData(JSON.stringify({ version: 1, title: 'private-title' })) });
  assert.deepEqual(worker.warnings, ['ASPWAJS011']);
});

function notificationClick(destination) {
  let closed = false;
  return {
    notification: {
      data: { appSurfaceDestination: destination },
      close() { closed = true; }
    },
    wasClosed: () => closed
  };
}

test('notification click opens the exact destination when a client differs by fragment', async () => {
  const worker = createWorker();
  const calls = [];
  worker.context.self.clients.matchAll = async options => {
    calls.push(options);
    return [
      { url: 'https://example.test/tenant/other', focus: async () => ({}) },
      { url: 'https://example.test/tenant/review?id=1#old', focus: async () => { calls.push('focus'); return {}; } }
    ];
  };
  worker.context.self.clients.openWindow = async url => { calls.push(['open', url]); };
  const click = notificationClick('https://example.test/tenant/review?id=1');
  await worker.emit('notificationclick', click);
  assert.equal(click.wasClosed(), true);
  assert.deepEqual(JSON.parse(JSON.stringify(calls)), [
    { type: 'window', includeUncontrolled: true },
    ['open', 'https://example.test/tenant/review?id=1']
  ]);
});

test('notification click opens when no exact match or focus fails, and does so only once', async () => {
  const worker = createWorker();
  const opened = [];
  worker.context.self.clients.matchAll = async () => [{
    url: 'https://example.test/tenant/review',
    focus: async () => { throw new Error('focus secret'); }
  }];
  worker.context.self.clients.openWindow = async url => { opened.push(url); return {}; };
  await worker.emit('notificationclick', notificationClick('https://example.test/tenant/review'));
  assert.deepEqual(opened, ['https://example.test/tenant/review']);

  worker.context.self.clients.matchAll = async () => [];
  await worker.emit('notificationclick', notificationClick('https://example.test/tenant/next'));
  assert.deepEqual(opened, ['https://example.test/tenant/review', 'https://example.test/tenant/next']);
});

test('notification click canonicalizes recursively encoded safe paths before exact matching', async () => {
  const worker = createWorker();
  let focused = false;
  worker.context.self.clients.matchAll = async () => [{
    url: 'https://example.test/tenant/start/notes',
    focus: async () => { focused = true; return {}; }
  }];

  await worker.emit(
    'notificationclick',
    notificationClick('https://example.test/tenant/start%252fnotes'));

  assert.equal(focused, true);
  assert.deepEqual(worker.warnings, []);
});

test('notification click treats a null open result as a contained client failure', async () => {
  const worker = createWorker();
  worker.context.self.clients.matchAll = async () => [];
  worker.context.self.clients.openWindow = async () => null;
  await worker.emit('notificationclick', notificationClick('https://example.test/tenant/review'));
  assert.deepEqual(worker.warnings, ['ASPWAJS021']);
});

test('notification click independently rejects forged destinations and contains client failures', async () => {
  const worker = createWorker();
  const unsafe = [
    undefined,
    'not a url',
    'https://evil.test/tenant/review',
    'https://example.test/outside',
    'https://example.test/tenant/a%252f..%252f..%252foutside',
    'https://example.test/tenant/%zz',
    'https://example.test/tenant/review#fragment',
    'https://user@example.test/tenant/review'
  ];
  for (const destination of unsafe) {
    await worker.emit('notificationclick', notificationClick(destination));
  }
  worker.context.self.clients.matchAll = async () => { throw new Error('client secret'); };
  await worker.emit('notificationclick', notificationClick('https://example.test/tenant/review'));
  assert.deepEqual(worker.warnings, [...unsafe.map(() => 'ASPWAJS020'), 'ASPWAJS021']);
  assert.equal(worker.warnings.join(' ').includes('client secret'), false);
});

test('notification click contains throwing notification data accessors', async () => {
  const worker = createWorker();
  const notification = {
    close() {},
    get data() { throw new Error('notification secret'); }
  };
  await worker.emit('notificationclick', { notification });
  assert.deepEqual(worker.warnings, ['ASPWAJS020']);
});
