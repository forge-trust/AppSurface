import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const behaviorKitPath = new URL('../wwwroot/razorwire/behavior-kit.js', import.meta.url);

test('behavior kit drains queued root and lifecycle registrations', () => {
  const { context, document } = loadRuntime({
    queued: [
      {
        kind: 'register',
        definition: {
          name: 'demo.widget',
          selector: '[data-widget]',
          connect(root, behavior) {
            root.setAttribute('data-connected', behavior.rootId);
            behavior.query('button').addEventListener('click', () => {
              root.setAttribute('data-clicked', 'true');
            }, { signal: behavior.signal });
          }
        }
      },
      {
        kind: 'registerLifecycle',
        definition: {
          name: 'demo.page',
          connect(lifecycle) {
            lifecycle.root.body.setAttribute('data-lifecycle', `${lifecycle.renderKind}:${lifecycle.url}`);
          }
        }
      }
    ]
  });
  const root = document.createElement('section');
  root.setAttribute('data-widget', 'true');
  const button = document.createElement('button');
  root.appendChild(button);
  document.body.appendChild(root);

  context.window.RazorWire.behaviors.scan();
  button.dispatchEvent(createEvent('click'));

  assert.match(root.getAttribute('data-connected'), /^rw-behavior-root-/);
  assert.equal(root.getAttribute('data-clicked'), 'true');
  assert.equal(document.body.getAttribute('data-lifecycle'), 'initial:https://example.test/settings');
});

test('root behavior registration is idempotent across repeated bundle evaluation', () => {
  const { context, document } = loadRuntime();
  const root = document.createElement('section');
  root.setAttribute('data-widget', 'true');
  document.body.appendChild(root);
  let connects = 0;

  context.window.RazorWire.behaviors.register({
    name: 'demo.widget',
    selector: '[data-widget]',
    connect() {
      connects += 1;
    }
  });
  context.window.RazorWire.behaviors.register({
    name: 'demo.widget',
    selector: '[data-widget]',
    connect() {
      connects += 1;
    }
  });
  context.window.RazorWire.behaviors.scan();

  assert.equal(connects, 1);
  assert.equal(context.window.RazorWire.behaviors.getDiagnostics().length, 0);
});

test('root scan includes the supplied root element and disconnects removed roots', () => {
  const { context, document } = loadRuntime();
  const root = document.createElement('section');
  root.setAttribute('data-widget', 'true');
  document.body.appendChild(root);
  let aborted = false;
  let cleaned = false;

  context.window.RazorWire.behaviors.register({
    name: 'demo.widget',
    selector: '[data-widget]',
    connect(_, behavior) {
      behavior.signal.addEventListener('abort', () => { aborted = true; });
      return () => { cleaned = true; };
    }
  });
  root.remove();
  context.window.RazorWire.behaviors.prune();

  assert.equal(aborted, true);
  assert.equal(cleaned, true);
});

test('lifecycle registrations run once per pass and can revisit the same URL', () => {
  const { context, document } = loadRuntime();
  const seen = [];

  context.window.RazorWire.behaviors.registerLifecycle({
    name: 'demo.page',
    connect(lifecycle) {
      seen.push(lifecycle.renderKind);
    }
  });
  context.window.RazorWire.behaviors.registerLifecycle({
    name: 'demo.page',
    connect(lifecycle) {
      seen.push(`duplicate:${lifecycle.renderKind}`);
    }
  });

  document.dispatchEvent(createEvent('turbo:load'));
  context.window.RazorWire.behaviors.registerLifecycle({
    name: 'demo.page',
    connect(lifecycle) {
      seen.push(`duplicate-after-load:${lifecycle.renderKind}`);
    }
  });
  document.dispatchEvent(createEvent('turbo:load'));

  assert.deepEqual(seen, ['initial', 'turbo:load', 'turbo:load']);
});

test('frame lifecycle runs only for lifecycle registrations with frames enabled', () => {
  const { context, document } = loadRuntime();
  const seen = [];
  const frame = document.createElement('turbo-frame');
  document.body.appendChild(frame);

  context.window.RazorWire.behaviors.registerLifecycle({
    name: 'demo.page',
    connect(lifecycle) {
      seen.push(`page:${lifecycle.renderKind}`);
    }
  });
  context.window.RazorWire.behaviors.registerLifecycle({
    name: 'demo.frame',
    frames: true,
    connect(lifecycle) {
      seen.push(`frame:${lifecycle.renderKind}:${lifecycle.root.tagName?.toLowerCase() ?? 'document'}`);
    }
  });
  frame.dispatchEvent(createEvent('turbo:frame-load', { target: frame }));

  assert.deepEqual(seen, ['page:initial', 'frame:initial:document', 'frame:turbo:frame-load:turbo-frame']);
});

test('cleanup failures are recorded without blocking disconnect or reconnect', () => {
  const { context, document } = loadRuntime();
  const root = document.createElement('section');
  root.setAttribute('data-widget', 'true');
  document.body.appendChild(root);
  const lifecyclePasses = [];
  let rootAborted = false;

  context.window.RazorWire.behaviors.register({
    name: 'demo.widget',
    selector: '[data-widget]',
    connect(_, behavior) {
      behavior.signal.addEventListener('abort', () => { rootAborted = true; });
      return () => { throw new Error('root cleanup failed'); };
    }
  });
  context.window.RazorWire.behaviors.registerLifecycle({
    name: 'demo.page',
    connect(lifecycle) {
      lifecyclePasses.push(lifecycle.renderKind);
      return () => { throw new Error('lifecycle cleanup failed'); };
    }
  });

  root.remove();
  context.window.RazorWire.behaviors.prune();
  document.dispatchEvent(createEvent('turbo:load'));

  const diagnostics = context.window.RazorWire.behaviors.getDiagnostics()
    .filter(diagnostic => diagnostic.code === 'BehaviorCleanupFailed');
  assert.equal(rootAborted, true);
  assert.deepEqual(lifecyclePasses, ['initial', 'turbo:load']);
  assert.equal(diagnostics.length, 2);
  assert.ok(diagnostics.some(diagnostic => diagnostic.message === 'Behavior "demo.widget" cleanup failed.'));
  assert.ok(diagnostics.some(diagnostic => diagnostic.message === 'Lifecycle behavior "demo.page" cleanup failed.'));
});

test('diagnostics cover conflicts, invalid selectors, failures, and missing abort support', () => {
  const { context, document } = loadRuntime();
  const root = document.createElement('section');
  root.setAttribute('data-widget', 'true');
  document.body.appendChild(root);
  let attempts = 0;

  context.window.RazorWire.behaviors.register({
    name: 'demo.widget',
    selector: '[data-widget]',
    connect() {
      attempts += 1;
      if (attempts === 1) throw new Error('first failure');
    }
  });
  context.window.RazorWire.behaviors.scan();
  context.window.RazorWire.behaviors.register({
    name: 'demo.widget',
    selector: '[data-other]',
    connect() {}
  });
  context.window.RazorWire.behaviors.register({
    name: 'bad.selector',
    selector: '[bad',
    connect() {}
  });
  context.window.RazorWire.behaviors.registerLifecycle({
    name: 'bad.lifecycle',
    events: ['made-up'],
    connect() {}
  });

  const codes = context.window.RazorWire.behaviors.getDiagnostics().map(diagnostic => diagnostic.code);
  assert.ok(codes.includes('BehaviorConnectFailed'));
  assert.ok(codes.includes('BehaviorRegistrationConflict'));
  assert.ok(codes.includes('BehaviorSelectorInvalid'));
  assert.ok(codes.includes('BehaviorLifecycleEventInvalid'));
  assert.equal(attempts, 2);

  const unsupported = loadRuntime({ abortController: undefined });
  const unsupportedRoot = unsupported.document.createElement('section');
  unsupportedRoot.setAttribute('data-widget', 'true');
  unsupported.document.body.appendChild(unsupportedRoot);
  unsupported.context.window.RazorWire.behaviors.register({
    name: 'demo.widget',
    selector: '[data-widget]',
    connect() {}
  });

  assert.equal(
    unsupported.context.window.RazorWire.behaviors.getDiagnostics().some(diagnostic => diagnostic.code === 'BehaviorAbortUnsupported'),
    true);
});

test('diagnostics preserve root context for repeated failures', () => {
  const { context, document } = loadRuntime();
  for (let index = 0; index < 2; index += 1) {
    const root = document.createElement('section');
    root.setAttribute('data-widget', 'true');
    document.body.appendChild(root);
  }

  context.window.RazorWire.behaviors.register({
    name: 'demo.widget',
    selector: '[data-widget]',
    connect() {
      throw new Error('same failure');
    }
  });

  const diagnostics = context.window.RazorWire.behaviors.getDiagnostics()
    .filter(diagnostic => diagnostic.code === 'BehaviorConnectFailed');
  assert.equal(diagnostics.length, 2);
  assert.equal(new Set(diagnostics.map(diagnostic => diagnostic.rootId)).size, 2);
});

test('app-reported diagnostics use a distinct code for root and lifecycle contexts', () => {
  const { context, document } = loadRuntime();
  const root = document.createElement('section');
  root.setAttribute('data-widget', 'true');
  document.body.appendChild(root);

  context.window.RazorWire.behaviors.register({
    name: 'demo.widget',
    selector: '[data-widget]',
    connect(_, behavior) {
      behavior.diagnostic('Root warning', 'Fix the root behavior.');
    }
  });
  context.window.RazorWire.behaviors.registerLifecycle({
    name: 'demo.page',
    connect(lifecycle) {
      lifecycle.diagnostic('Lifecycle warning', 'Fix the lifecycle behavior.');
    }
  });

  const diagnostics = context.window.RazorWire.behaviors.getDiagnostics();
  assert.equal(diagnostics.filter(diagnostic => diagnostic.code === 'BehaviorDiagnostic').length, 2);
  assert.equal(diagnostics.some(diagnostic => diagnostic.code === 'BehaviorConnectFailed'), false);
  assert.ok(diagnostics.some(diagnostic => diagnostic.behaviorName === 'demo.widget' && diagnostic.rootId));
  assert.ok(diagnostics.some(diagnostic => diagnostic.behaviorName === 'demo.page'));
});

test('lifecycle conflicts are diagnosed and diagnostics can be cleared', () => {
  const { context } = loadRuntime();

  context.window.RazorWire.behaviors.registerLifecycle({
    name: 'demo.page',
    connect() {}
  });
  context.window.RazorWire.behaviors.registerLifecycle({
    name: 'demo.page',
    frames: true,
    connect() {}
  });

  const diagnostics = context.window.RazorWire.behaviors.getDiagnostics();
  assert.equal(diagnostics.length, 1);
  assert.equal(diagnostics[0].code, 'BehaviorRegistrationConflict');
  assert.match(diagnostics[0].message, /Lifecycle behavior "demo\.page"/);

  context.window.RazorWire.behaviors.clearDiagnostics();
  assert.equal(context.window.RazorWire.behaviors.getDiagnostics().length, 0);
});

function loadRuntime(options = {}) {
  const document = new FakeDocument();
  const abortController = Object.hasOwn(options, 'abortController') ? options.abortController : AbortController;
  const window = {
    RazorWireBehaviorKitInitialized: false,
    RazorWire: {
      config: { developmentDiagnostics: true },
      behaviors: {
        __razorWireBehaviorStub: true,
        __queue: options.queued ?? [],
        __diagnostics: []
      }
    },
    location: { href: 'https://example.test/settings', origin: 'https://example.test' },
    document,
    addEventListener() {},
    AbortController: abortController,
    console
  };
  const context = {
    window,
    document,
    Element: FakeElement,
    AbortController: abortController,
    console,
    __lifecycle: []
  };
  document.defaultView = window;
  vm.createContext(context);
  vm.runInContext(readFileSync(behaviorKitPath, 'utf8'), context);
  return { context, document };
}

function createEvent(type, extras = {}) {
  return new FakeEvent(type, { bubbles: true, cancelable: true, ...extras });
}

class FakeEvent {
  constructor(type, options = {}) {
    this.type = type;
    this.bubbles = options.bubbles ?? false;
    this.cancelable = options.cancelable ?? false;
    this.defaultPrevented = false;
    this.target = options.target ?? null;
  }

  preventDefault() {
    if (this.cancelable) this.defaultPrevented = true;
  }
}

class FakeDocument {
  constructor() {
    this.readyState = 'complete';
    this.documentElement = new FakeElement('html', this);
    this.body = new FakeElement('body', this);
    this.documentElement.appendChild(this.body);
    this.listeners = new Map();
  }

  createElement(tagName) {
    return new FakeElement(tagName, this);
  }

  querySelector(selector) {
    return this.documentElement.querySelector(selector);
  }

  querySelectorAll(selector) {
    return this.documentElement.querySelectorAll(selector);
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  dispatchEvent(event) {
    event.target ??= this;
    for (const listener of this.listeners.get(event.type) ?? []) {
      listener(event);
    }
    return !event.defaultPrevented;
  }
}

class FakeElement {
  constructor(tagName, ownerDocument) {
    this.tagName = tagName.toUpperCase();
    this.ownerDocument = ownerDocument;
    this.parentElement = null;
    this.children = [];
    this.attributes = new Map();
    this.listeners = new Map();
  }

  get isConnected() {
    let current = this;
    while (current) {
      if (current === this.ownerDocument.documentElement) return true;
      current = current.parentElement;
    }
    return false;
  }

  appendChild(child) {
    child.parentElement = this;
    this.children.push(child);
    return child;
  }

  append(...children) {
    for (const child of children) this.appendChild(child);
  }

  remove() {
    if (!this.parentElement) return;
    this.parentElement.children = this.parentElement.children.filter(child => child !== this);
    this.parentElement = null;
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.has(name) ? this.attributes.get(name) : null;
  }

  hasAttribute(name) {
    return this.attributes.has(name);
  }

  matches(selector) {
    if (selector.includes('[bad')) throw new Error('Invalid selector');
    if (/^\[[^\]=]+(?:="[^"]*")?\]$/.test(selector)) {
      const [, name, rawValue] = selector.match(/^\[([^=\]]+)(?:="([^"]*)")?\]$/);
      if (!this.hasAttribute(name)) return false;
      return rawValue === undefined || this.getAttribute(name) === rawValue;
    }
    if (selector.startsWith('#')) return this.getAttribute('id') === selector.slice(1);
    return this.tagName.toLowerCase() === selector.toLowerCase();
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] ?? null;
  }

  querySelectorAll(selector) {
    const matches = [];
    for (const child of this.children) {
      if (child.matches(selector)) matches.push(child);
      matches.push(...child.querySelectorAll(selector));
    }
    return matches;
  }

  closest(selector) {
    let current = this;
    while (current) {
      if (current.matches(selector)) return current;
      current = current.parentElement;
    }
    return null;
  }

  addEventListener(type, listener, options = {}) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
    options.signal?.addEventListener('abort', () => {
      this.listeners.set(type, (this.listeners.get(type) ?? []).filter(candidate => candidate !== listener));
    }, { once: true });
  }

  removeEventListener(type, listener) {
    this.listeners.set(type, (this.listeners.get(type) ?? []).filter(candidate => candidate !== listener));
  }

  dispatchEvent(event) {
    event.target ??= this;
    for (const listener of this.listeners.get(event.type) ?? []) {
      listener(event);
    }
    if (event.bubbles && this.parentElement) this.parentElement.dispatchEvent(event);
    if (event.bubbles && !this.parentElement) this.ownerDocument.dispatchEvent(event);
    return !event.defaultPrevented;
  }
}
