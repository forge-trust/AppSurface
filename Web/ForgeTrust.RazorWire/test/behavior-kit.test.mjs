import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const behaviorKitPath = new URL('../wwwroot/razorwire/behavior-kit.js', import.meta.url);

test('behavior kit drains queued registrations and does not reconnect existing roots on repeated scans', () => {
  let connectCount = 0;
  let clickCount = 0;
  const root = null;
  const { context, document } = loadBehaviorKit({
    queuedDefinitions: [
      {
        name: 'demo.counter',
        selector: '[data-demo-counter]',
        connect(element, ctx) {
          connectCount += 1;
          element.addEventListener('click', () => {
            clickCount += 1;
          }, { signal: ctx.signal });
        }
      }
    ]
  });

  const counter = document.createElement('section');
  counter.setAttribute('data-demo-counter', 'true');
  document.body.appendChild(counter);

  context.window.RazorWire.behaviors.scan();
  context.window.RazorWire.behaviors.scan();
  counter.dispatchEvent(createEvent('click'));

  assert.equal(root, null);
  assert.equal(connectCount, 1);
  assert.equal(clickCount, 1);
  assert.equal(context.window.RazorWire.behaviors.__isRazorWireBehaviorManager, true);
});

test('behavior kit scan includes the supplied element root and prunes removed roots', () => {
  const connected = [];
  const cleaned = [];
  const { context, document } = loadBehaviorKit();
  const root = document.createElement('section');
  root.setAttribute('data-enhance-root', 'true');
  document.body.appendChild(root);

  context.window.RazorWire.behaviors.register({
    name: 'demo.root',
    selector: '[data-enhance-root]',
    connect(element) {
      connected.push(element);
      return () => cleaned.push(element);
    }
  });
  context.window.RazorWire.behaviors.clearDiagnostics();
  context.window.RazorWire.behaviors.scan(root);

  assert.deepEqual(connected, [root]);

  root.removeAttribute('data-enhance-root');
  context.window.RazorWire.behaviors.scan(document);

  assert.deepEqual(cleaned, [root]);
});

test('behavior kit rejects invalid selectors and conflicting duplicate registrations without blocking other behaviors', () => {
  const { context, document } = loadBehaviorKit();
  const root = document.createElement('div');
  root.setAttribute('data-good', 'true');
  document.body.appendChild(root);
  let connected = false;

  context.window.RazorWire.behaviors.register({
    selector: '[data-good]',
    connect() {
      throw new Error('invalid behavior must not connect');
    }
  });
  context.window.RazorWire.behaviors.register({
    name: 'demo.missing-selector',
    connect() {
      throw new Error('invalid behavior must not connect');
    }
  });
  context.window.RazorWire.behaviors.register({
    name: 'demo.bad-selector',
    selector: '[',
    connect() {
      throw new Error('must not connect');
    }
  });
  context.window.RazorWire.behaviors.register({
    name: 'demo.good',
    selector: '[data-good]',
    connect() {
      connected = true;
    }
  });
  context.window.RazorWire.behaviors.register({
    name: 'demo.good',
    selector: '[data-other]',
    connect() {
      throw new Error('conflicting behavior must not replace first definition');
    }
  });

  assert.equal(connected, true);
  const diagnostics = context.window.RazorWire.behaviors.getDiagnostics();
  assert.equal(diagnostics.some(diagnostic => diagnostic.code === 'BehaviorSelectorInvalid'), true);
  assert.equal(diagnostics.some(diagnostic => diagnostic.code === 'BehaviorRegistrationConflict'), true);
  const invalidRegistrations = diagnostics.filter(diagnostic => diagnostic.code === 'BehaviorRegistrationInvalid');
  assert.equal(invalidRegistrations.length, 2);
  assert.equal(invalidRegistrations.some(diagnostic => diagnostic.selector === '[data-good]'), true);
  assert.equal(invalidRegistrations.some(diagnostic => diagnostic.behaviorName === 'demo.missing-selector'), true);
});

test('behavior kit scopes turbo frame load scans to the event target frame', () => {
  const { context, document } = loadBehaviorKit();
  const connected = [];

  context.window.RazorWire.behaviors.register({
    name: 'demo.frame-scope',
    selector: '[data-frame-scope]',
    connect(element) {
      connected.push(element.id);
    }
  });

  const outside = document.createElement('section');
  outside.id = 'outside';
  outside.setAttribute('data-frame-scope', 'true');
  document.body.appendChild(outside);

  const frame = document.createElement('turbo-frame');
  document.body.appendChild(frame);
  const inside = document.createElement('section');
  inside.id = 'inside';
  inside.setAttribute('data-frame-scope', 'true');
  frame.appendChild(inside);

  document.dispatchEvent({ type: 'turbo:frame-load', target: frame });

  assert.deepEqual(connected, ['inside']);
});

test('behavior kit scans once for each Turbo lifecycle update', () => {
  const { context, document } = loadBehaviorKit();
  const connected = [];

  context.window.RazorWire.behaviors.register({
    name: 'demo.turbo-events',
    selector: '[data-turbo-event-root]',
    connect(element) {
      connected.push(element.id);
    }
  });

  const renderRoot = document.createElement('section');
  renderRoot.id = 'render';
  renderRoot.setAttribute('data-turbo-event-root', 'true');
  document.body.appendChild(renderRoot);
  document.dispatchEvent({ type: 'turbo:render' });
  document.dispatchEvent({ type: 'turbo:render' });

  const loadRoot = document.createElement('section');
  loadRoot.id = 'load';
  loadRoot.setAttribute('data-turbo-event-root', 'true');
  document.body.appendChild(loadRoot);
  document.dispatchEvent({ type: 'turbo:load' });
  document.dispatchEvent({ type: 'turbo:load' });

  const frame = document.createElement('turbo-frame');
  document.body.appendChild(frame);
  const frameRoot = document.createElement('section');
  frameRoot.id = 'frame';
  frameRoot.setAttribute('data-turbo-event-root', 'true');
  frame.appendChild(frameRoot);
  document.dispatchEvent({ type: 'turbo:frame-load', target: frame });
  document.dispatchEvent({ type: 'turbo:frame-load', target: frame });

  assert.deepEqual(connected, ['render', 'load', 'frame']);
});

test('behavior kit second evaluation keeps existing manager and scans for new roots once', () => {
  const { context, document } = loadBehaviorKit();
  const connected = [];

  context.window.RazorWire.behaviors.register({
    name: 'demo.second-load',
    selector: '[data-second-load]',
    connect(element) {
      connected.push(element.id);
    }
  });

  const first = document.createElement('section');
  first.id = 'first';
  first.setAttribute('data-second-load', 'true');
  document.body.appendChild(first);
  context.window.RazorWire.behaviors.scan();

  const second = document.createElement('section');
  second.id = 'second';
  second.setAttribute('data-second-load', 'true');
  document.body.appendChild(second);
  vm.runInContext(readFileSync(behaviorKitPath, 'utf8'), context);

  assert.deepEqual(connected, ['first', 'second']);
  assert.equal(context.window.RazorWire.behaviors.getDiagnostics().length, 0);
});

test('behavior kit aborts partial connect failures and permits retry after the callback is fixed', () => {
  const { context, document } = loadBehaviorKit();
  const root = document.createElement('button');
  root.setAttribute('data-retry', 'true');
  document.body.appendChild(root);
  let clickCount = 0;
  let shouldThrow = true;

  context.window.RazorWire.behaviors.register({
    name: 'demo.retry',
    selector: '[data-retry]',
    connect(element, ctx) {
      element.addEventListener('click', () => {
        clickCount += 1;
      }, { signal: ctx.signal });
      if (shouldThrow) {
        shouldThrow = false;
        throw new Error('first connect fails');
      }
    }
  });

  root.dispatchEvent(createEvent('click'));
  assert.equal(clickCount, 0);
  assert.equal(
    context.window.RazorWire.behaviors.getDiagnostics()
      .some(diagnostic => diagnostic.code === 'BehaviorConnectFailed'),
    true);

  context.window.RazorWire.behaviors.scan(root);
  root.dispatchEvent(createEvent('click'));

  assert.equal(clickCount, 1);
});

test('behavior kit records cleanup failures after aborting the behavior signal', () => {
  const { context, document } = loadBehaviorKit();
  const root = document.createElement('button');
  root.setAttribute('data-cleanup', 'true');
  document.body.appendChild(root);
  let clickCount = 0;

  context.window.RazorWire.behaviors.register({
    name: 'demo.cleanup',
    selector: '[data-cleanup]',
    connect(element, ctx) {
      element.addEventListener('click', () => {
        clickCount += 1;
      }, { signal: ctx.signal });
      return () => {
        throw new Error('cleanup failed');
      };
    }
  });

  root.dispatchEvent(createEvent('click'));
  root.remove();
  context.window.RazorWire.behaviors.prune();
  root.dispatchEvent(createEvent('click'));

  assert.equal(clickCount, 1);
  assert.equal(
    context.window.RazorWire.behaviors.getDiagnostics()
      .some(diagnostic => diagnostic.code === 'BehaviorCleanupFailed'),
    true);
});

test('behavior kit leaves roots unconnected when AbortController support is missing', () => {
  const { context, document } = loadBehaviorKit({ abortSupported: false });
  const root = document.createElement('div');
  root.setAttribute('data-abort-required', 'true');
  document.body.appendChild(root);
  let connected = false;

  context.window.RazorWire.behaviors.register({
    name: 'demo.abort-required',
    selector: '[data-abort-required]',
    connect() {
      connected = true;
    }
  });

  assert.equal(connected, false);
  assert.equal(
    context.window.RazorWire.behaviors.getDiagnostics()
      .some(diagnostic => diagnostic.code === 'BehaviorAbortUnsupported'),
    true);
});

function loadBehaviorKit(options = {}) {
  const document = new FakeDocument();
  const queuedDefinitions = options.queuedDefinitions ?? [];
  const window = {
    RazorWireBehaviorKitInitialized: false,
    RazorWire: {
      config: { developmentDiagnostics: true },
      behaviors: {
        __queuedDefinitions: queuedDefinitions,
        register(definition) {
          queuedDefinitions.push(definition);
        },
        scan() {},
        prune() {},
        getDiagnostics() {
          return [];
        },
        clearDiagnostics() {}
      }
    },
    addEventListener() {}
  };
  const context = {
    window,
    document,
    Element: FakeElement,
    console
  };

  if (options.abortSupported !== false) {
    context.AbortController = AbortController;
    context.AbortSignal = AbortSignal;
  }

  window.document = document;
  document.defaultView = window;

  vm.createContext(context);
  vm.runInContext(readFileSync(behaviorKitPath, 'utf8'), context);
  return { context, document };
}

function createEvent(type) {
  return {
    type,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    }
  };
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
    return this.querySelectorAll(selector)[0] ?? null;
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
    for (const listener of this.listeners.get(event.type) ?? []) {
      listener(event);
    }
  }
}

class FakeElement {
  constructor(tagName, ownerDocument) {
    this.tagName = tagName.toUpperCase();
    this.localName = tagName.toLowerCase();
    this.ownerDocument = ownerDocument;
    this.attributes = new Map();
    this.children = [];
    this.parentElement = null;
    this.listeners = new Map();
    this.isConnected = true;
    this.textContent = '';
  }

  get id() {
    return this.getAttribute('id') ?? '';
  }

  set id(value) {
    this.setAttribute('id', value);
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.has(name) ? this.attributes.get(name) : null;
  }

  removeAttribute(name) {
    this.attributes.delete(name);
  }

  hasAttribute(name) {
    return this.attributes.has(name);
  }

  append(...nodes) {
    for (const node of nodes) this.appendChild(node);
  }

  appendChild(node) {
    if (node.parentElement) {
      node.parentElement.children = node.parentElement.children.filter(child => child !== node);
    }

    node.parentElement = this;
    node.ownerDocument = this.ownerDocument;
    node.setConnected(this.isConnected);
    this.children.push(node);
    return node;
  }

  remove() {
    if (this.parentElement) {
      this.parentElement.children = this.parentElement.children.filter(child => child !== this);
    }

    this.parentElement = null;
    this.setConnected(false);
  }

  setConnected(isConnected) {
    this.isConnected = isConnected;
    for (const child of this.children) child.setConnected(isConnected);
  }

  contains(node) {
    if (node === this) return true;
    return this.children.some(child => child.contains(node));
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] ?? null;
  }

  querySelectorAll(selector) {
    if (selector === '[') throw new Error('invalid selector');
    const selectors = selector.split(',').map(part => part.trim()).filter(Boolean);
    const matches = [];
    const visit = element => {
      for (const child of element.children) {
        if (selectors.some(candidate => child.matches(candidate))) {
          matches.push(child);
        }
        visit(child);
      }
    };
    visit(this);
    return matches;
  }

  matches(selector) {
    if (selector === '[') throw new Error('invalid selector');
    const tagAttribute = selector.match(/^([a-z0-9-]+)?\[([^=\]]+)(?:=(?:"([^"]*)"|'([^']*)'|([^\]]+)))?\]$/i);
    if (tagAttribute) {
      const [, tagName, attribute, doubleQuoted, singleQuoted, unquoted] = tagAttribute;
      const value = doubleQuoted ?? singleQuoted ?? unquoted;
      if (tagName && this.tagName !== tagName.toUpperCase()) return false;
      if (!this.hasAttribute(attribute)) return false;
      return value === undefined || this.getAttribute(attribute) === value;
    }

    return this.tagName === selector.toUpperCase();
  }

  addEventListener(type, listener, options = {}) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);

    if (options.signal) {
      options.signal.addEventListener('abort', () => {
        this.removeEventListener(type, listener);
      }, { once: true });
    }
  }

  removeEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    this.listeners.set(type, listeners.filter(candidate => candidate !== listener));
  }

  dispatchEvent(event) {
    event.target ??= this;
    for (const listener of this.listeners.get(event.type) ?? []) {
      listener(event);
    }
  }
}
