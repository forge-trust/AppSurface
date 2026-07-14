import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const exampleDirectory = path.resolve(testDirectory, '..');
const scriptPath = path.join(exampleDirectory, 'wwwroot', 'js', 'pwa-registration-proof.js');
const viewPath = path.join(exampleDirectory, 'Views', 'Home', 'Index.cshtml');
const script = await readFile(scriptPath, 'utf8');

const expectedWorkerUrl = 'https://app.example.test/service-worker.js';
const expectedScopeUrl = 'https://app.example.test/';

class FakeElement {
  constructor({ disabled = false, state, textContent = '' } = {}) {
    this.className = '';
    this.dataset = state ? { state } : {};
    this.disabled = disabled;
    this.listeners = new Map();
    this.textContent = textContent;
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  async dispatch(type) {
    const listeners = this.listeners.get(type) ?? [];
    await Promise.all(listeners.map(listener => listener({ type, target: this })));
  }
}

class FakeWorker {
  constructor(state = 'installing', scriptURL = expectedWorkerUrl) {
    this.state = state;
    this.scriptURL = scriptURL;
    this.listeners = new Map();
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  transitionTo(state) {
    this.state = state;
    for (const listener of this.listeners.get('statechange') ?? []) {
      listener({ type: 'statechange', target: this });
    }
  }
}

const settle = async () => {
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
};

function createHarness(options = {}) {
  const status = new FakeElement({
    state: 'initializing',
    textContent: 'Checking browser support and existing registrations…'
  });
  const button = new FakeElement({ disabled: true, textContent: 'Register service worker' });
  const workerCapability = new FakeElement({ textContent: 'Not proven' });
  workerCapability.className = 'pill pending';
  const documentListeners = new Map();
  let permissionCalls = 0;
  let subscriptionCalls = 0;
  let registrationCalls = 0;

  const document = {
    readyState: options.readyState ?? 'complete',
    getElementById(id) {
      return {
        'registration-status': status,
        'register-worker': button,
        'worker-capability': workerCapability
      }[id] ?? null;
    },
    querySelector(selector) {
      if (options.metadata === false) {
        return null;
      }
      if (selector === 'meta[name="appsurface:pwa-service-worker"]') {
        return { content: options.workerPath ?? '/service-worker.js' };
      }
      if (selector === 'meta[name="appsurface:pwa-service-worker-scope"]') {
        return { content: options.scopePath ?? '/' };
      }
      return null;
    },
    addEventListener(type, listener) {
      const listeners = documentListeners.get(type) ?? [];
      listeners.push(listener);
      documentListeners.set(type, listeners);
    },
    async dispatch(type) {
      await Promise.all((documentListeners.get(type) ?? []).map(listener => listener({ type })));
    }
  };

  const serviceWorker = options.serviceWorker === false
    ? undefined
    : {
        async getRegistration() {
          if (options.getRegistrationError) {
            throw new Error('inspection failed');
          }
          const registration = options.existingRegistration ?? null;
          if (registration && registration.scope === undefined) registration.scope = expectedScopeUrl;
          return registration;
        }
      };
  const navigator = serviceWorker === undefined ? {} : { serviceWorker };

  const register = options.register ?? (async () => {
    registrationCalls += 1;
    return { active: new FakeWorker('activated'), scope: expectedScopeUrl };
  });
  const window = {
    location: { href: 'https://app.example.test/proof' }
  };
  if (options.helperAccessError) {
    Object.defineProperty(window, 'AppSurface', {
      get() { throw new Error('namespace unavailable'); }
    });
  } else if (options.helper !== false) {
    window.AppSurface = {
      Pwa: {
        register: async () => {
          if (options.register) {
            registrationCalls += 1;
          }
          const registration = await register();
          if (registration && registration.scope === undefined) registration.scope = expectedScopeUrl;
          return registration;
        }
      }
    };
  }

  const Notification = {
    requestPermission() {
      permissionCalls += 1;
    }
  };
  function PushManager() {}
  PushManager.prototype.subscribe = function subscribe() {
    subscriptionCalls += 1;
  };

  vm.runInNewContext(script, {
    document,
    navigator,
    Notification,
    PushManager,
    URL,
    window
  }, { filename: scriptPath });

  return {
    button,
    document,
    status,
    window,
    workerCapability,
    get permissionCalls() { return permissionCalls; },
    get registrationCalls() { return registrationCalls; },
    get subscriptionCalls() { return subscriptionCalls; }
  };
}

test('view loads the exact proof script as a versioned external asset', async () => {
  const view = await readFile(viewPath, 'utf8');
  assert.match(view, /<script src="~\/js\/pwa-registration-proof\.js" asp-append-version="true" defer><\/script>/u);
  assert.doesNotMatch(view, /const setState|navigator\.serviceWorker\.getRegistration/u);
  assert.match(view, /PwaOptions\.Enabled/u);
  assert.match(view, /PwaOptions\.Offline\.Enabled/u);
  assert.match(view, /PwaOptions\.Push\.Enabled/u);
});

test('initializing remains visible until DOM readiness, then transitions to ready', async () => {
  const harness = createHarness({ readyState: 'loading' });

  assert.equal(harness.status.dataset.state, 'initializing');
  assert.equal(harness.button.disabled, true);

  await harness.document.dispatch('DOMContentLoaded');
  await settle();

  assert.equal(harness.status.dataset.state, 'ready');
  assert.equal(harness.button.disabled, false);
});

test('unsupported and helper-conflict states remain truthful and disabled', async t => {
  await t.test('unsupported browser', async () => {
    const harness = createHarness({ serviceWorker: false });
    await settle();
    assert.equal(harness.status.dataset.state, 'unsupported');
    assert.equal(harness.button.disabled, true);
  });

  await t.test('helper namespace conflict', async () => {
    const harness = createHarness({ helper: false });
    await settle();
    assert.equal(harness.status.dataset.state, 'helper-conflict');
    assert.equal(harness.button.disabled, true);
  });

  await t.test('throwing helper namespace', async () => {
    const harness = createHarness({ helperAccessError: true });
    await settle();
    assert.equal(harness.status.dataset.state, 'helper-conflict');
    assert.equal(harness.button.disabled, true);
  });
});

test('an exact active worker is already-registered while another worker is not', async t => {
  await t.test('exact script match', async () => {
    const harness = createHarness({
      existingRegistration: { active: new FakeWorker('activated', expectedWorkerUrl) }
    });
    await settle();
    assert.equal(harness.status.dataset.state, 'already-registered');
    assert.equal(harness.workerCapability.textContent, 'Active');
    assert.equal(harness.button.disabled, true);
  });

  await t.test('different script at a matching scope', async () => {
    const harness = createHarness({
      existingRegistration: { active: new FakeWorker('activated', 'https://app.example.test/other-worker.js') }
    });
    await settle();
    assert.equal(harness.status.dataset.state, 'ready');
    assert.equal(harness.workerCapability.textContent, 'Not proven');
    assert.equal(harness.button.disabled, false);
  });

  await t.test('same script at a different scope is not already registered', async () => {
    const harness = createHarness({
      existingRegistration: {
        active: new FakeWorker('activated', expectedWorkerUrl),
        scope: 'https://app.example.test/old/'
      }
    });
    await settle();
    assert.equal(harness.status.dataset.state, 'ready');
    assert.equal(harness.workerCapability.textContent, 'Not proven');
    assert.equal(harness.button.disabled, false);
  });

  await t.test('invalid worker metadata cannot match an existing registration', async () => {
    const harness = createHarness({
      workerPath: 'http://[',
      existingRegistration: { active: new FakeWorker('activated', expectedWorkerUrl) }
    });
    await settle();
    assert.equal(harness.status.dataset.state, 'ready');
    assert.equal(harness.workerCapability.textContent, 'Not proven');
  });

  await t.test('old active worker does not overclaim a pending AppSurface update', async () => {
    const pending = new FakeWorker('installing', expectedWorkerUrl);
    const harness = createHarness({
      existingRegistration: {
        active: new FakeWorker('activated', 'https://app.example.test/old-worker.js'),
        installing: pending
      }
    });
    await settle();
    assert.equal(harness.status.dataset.state, 'activation-pending');
    assert.equal(harness.workerCapability.textContent, 'Not proven');

    pending.transitionTo('activated');
    assert.equal(harness.status.dataset.state, 'active');
    assert.equal(harness.workerCapability.textContent, 'Active');
  });
});

test('registering disables duplicate clicks and resolves directly to active', async () => {
  let resolveRegistration;
  const registrationPromise = new Promise(resolve => {
    resolveRegistration = resolve;
  });
  const harness = createHarness({ register: () => registrationPromise });
  await settle();

  const firstClick = harness.button.dispatch('click');
  await settle();
  assert.equal(harness.status.dataset.state, 'registering');
  assert.equal(harness.button.disabled, true);

  await harness.button.dispatch('click');
  assert.equal(harness.registrationCalls, 1);

  resolveRegistration({ active: new FakeWorker('activated') });
  await firstClick;
  assert.equal(harness.status.dataset.state, 'active');
  assert.equal(harness.workerCapability.textContent, 'Active');
});

test('a pending worker becomes active through its statechange event', async () => {
  const worker = new FakeWorker('installing');
  const harness = createHarness({ register: async () => ({ active: null, installing: worker }) });
  await settle();

  await harness.button.dispatch('click');
  assert.equal(harness.status.dataset.state, 'activation-pending');
  assert.equal(harness.button.disabled, true);

  worker.transitionTo('activated');
  assert.equal(harness.status.dataset.state, 'active');
  assert.equal(harness.workerCapability.textContent, 'Active');
});

test('registration response does not treat a different active worker as AppSurface active', async () => {
  const pending = new FakeWorker('installing', expectedWorkerUrl);
  const harness = createHarness({
    register: async () => ({
      active: new FakeWorker('activated', 'https://app.example.test/old-worker.js'),
      installing: pending
    })
  });
  await settle();

  await harness.button.dispatch('click');
  assert.equal(harness.status.dataset.state, 'activation-pending');
  assert.equal(harness.workerCapability.textContent, 'Not proven');

  pending.transitionTo('activated');
  assert.equal(harness.status.dataset.state, 'active');
  assert.equal(harness.workerCapability.textContent, 'Active');
});

test('registration response with an unexpected scope fails truthfully', async () => {
  const harness = createHarness({
    register: async () => ({
      active: new FakeWorker('activated', expectedWorkerUrl),
      scope: 'https://app.example.test/old/'
    })
  });
  await settle();

  await harness.button.dispatch('click');
  assert.equal(harness.status.dataset.state, 'failed');
  assert.match(harness.status.textContent, /unexpected script or scope/u);
  assert.equal(harness.workerCapability.textContent, 'Not proven');
});

test('an existing pending worker is watched and can become failed', async () => {
  const worker = new FakeWorker('installed');
  const harness = createHarness({ existingRegistration: { active: null, waiting: worker } });
  await settle();

  assert.equal(harness.status.dataset.state, 'activation-pending');
  assert.equal(harness.button.disabled, true);

  worker.transitionTo('redundant');
  assert.equal(harness.status.dataset.state, 'failed');
  assert.equal(harness.button.textContent, 'Registration failed');
  assert.equal(harness.workerCapability.textContent, 'Not proven');
  assert.equal(harness.workerCapability.className, 'pill pending');
});

test('a redundant installing worker does not hide a viable waiting worker', async () => {
  const installing = new FakeWorker('installing');
  const waiting = new FakeWorker('installed');
  const harness = createHarness({
    existingRegistration: {
      active: new FakeWorker('activated', 'https://app.example.test/old-worker.js'),
      installing,
      waiting
    }
  });
  await settle();

  installing.transitionTo('redundant');
  assert.equal(harness.status.dataset.state, 'activation-pending');
  assert.equal(harness.workerCapability.textContent, 'Not proven');

  waiting.transitionTo('activated');
  assert.equal(harness.status.dataset.state, 'active');
  assert.equal(harness.workerCapability.textContent, 'Active');
});

test('registration handles support disappearing and delayed activation without a worker candidate', async t => {
  await t.test('helper returns unsupported', async () => {
    const harness = createHarness({ register: async () => null });
    await settle();
    await harness.button.dispatch('click');
    assert.equal(harness.status.dataset.state, 'unsupported');
    assert.equal(harness.button.disabled, true);
  });

  await t.test('registration has no active, installing, or waiting worker yet', async () => {
    const harness = createHarness({ register: async () => ({ active: null }) });
    await settle();
    await harness.button.dispatch('click');
    assert.equal(harness.status.dataset.state, 'activation-pending');
    assert.match(harness.status.textContent, /no active worker is visible yet/u);
  });

  await t.test('helper disappears after initialization', async () => {
    const harness = createHarness();
    await settle();
    harness.window.AppSurface.Pwa.register = undefined;
    await harness.button.dispatch('click');
    assert.equal(harness.status.dataset.state, 'helper-conflict');
  });
});

test('inspection and registration failures produce the failed state', async t => {
  await t.test('inspection failure', async () => {
    const harness = createHarness({ getRegistrationError: true });
    await settle();
    assert.equal(harness.status.dataset.state, 'failed');
    assert.equal(harness.button.textContent, 'Inspection failed');
  });

  await t.test('registration failure', async () => {
    const harness = createHarness({ register: async () => { throw new Error('registration failed'); } });
    await settle();
    await harness.button.dispatch('click');
    assert.equal(harness.status.dataset.state, 'failed');
    assert.equal(harness.button.textContent, 'Registration failed');
  });
});

test('proof never requests permission or creates a subscription', async () => {
  const harness = createHarness();
  await settle();
  await harness.button.dispatch('click');

  assert.equal(harness.permissionCalls, 0);
  assert.equal(harness.subscriptionCalls, 0);
  assert.equal(harness.registrationCalls, 1);
});
