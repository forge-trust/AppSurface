import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const sectionCopyPath = new URL('../wwwroot/razorwire/section-copy.js', import.meta.url);

test('section copy generates plain buttons and copies encoded section urls', async () => {
  let copiedText = '';
  let feedbackTimer = null;
  const { context, document } = loadSectionCopyRuntime({
    clipboard: {
      writeText: async value => {
        copiedText = value;
      }
    },
    setTimeout: (callback, delay) => {
      if (delay === 2200) {
        feedbackTimer = callback;
        return 42;
      }

      return setTimeout(callback, delay);
    }
  });

  const heading = document.createElement('h2');
  heading.id = 'api key';
  heading.textContent = 'API Key';
  heading.setAttribute('data-rw-section-copy-target', 'true');
  document.body.appendChild(heading);

  context.window.RazorWire.sectionCopyManager.scan();

  const button = heading.nextElementSibling;
  assert.equal(button?.tagName, 'BUTTON');
  assert.equal(button.textContent, 'Copy link');
  assert.equal(button.getAttribute('data-rw-section-copy'), 'api key');
  assert.equal(button.getAttribute('data-rw-section-copy-inserted'), 'true');

  button.dispatchEvent(createEvent('click'));
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(copiedText, 'https://docs.example.test/guide?tab=api#api%20key');
  assert.equal(button.getAttribute('data-rw-section-copy-state'), 'copied');
  assert.match(button.getAttribute('data-rw-section-copy-message'), /Copied link to API Key\./);
  const status = document.querySelector('[data-rw-section-copy-status]');
  assert.match(status?.textContent, /Copied link to API Key\./);

  assert.equal(typeof feedbackTimer, 'function');
  feedbackTimer();

  assert.equal(button.getAttribute('data-rw-section-copy-state'), null);
  assert.equal(button.getAttribute('data-rw-section-copy-message'), null);
  assert.equal(status.textContent, '');
});

test('section copy shows an inline fallback when clipboard is unavailable', async () => {
  const { context, document } = loadSectionCopyRuntime();
  const target = document.createElement('h2');
  target.id = 'intro';
  target.textContent = 'Intro';
  const button = document.createElement('button');
  button.setAttribute('data-rw-section-copy', '#intro');
  button.setAttribute('data-rw-section-copy-title', 'Intro');
  document.body.append(target, button);

  context.window.RazorWire.sectionCopyManager.scan();
  button.dispatchEvent(createEvent('click'));
  await new Promise(resolve => setTimeout(resolve, 0));

  const fallback = document.querySelector('[data-rw-section-copy-fallback]');
  assert.equal(fallback?.getAttribute('role'), 'dialog');
  assert.equal(fallback?.getAttribute('aria-label'), 'Copy link to Intro');
  const input = fallback.querySelector('input');
  assert.equal(input.value, 'https://docs.example.test/guide?tab=api#intro');
  assert.equal(input.readOnly, true);
  assert.equal(input.getAttribute('aria-label'), 'Section link for Intro');
  assert.equal(button.getAttribute('data-rw-section-copy-state'), 'fallback');
  const status = document.querySelector('[data-rw-section-copy-status]');
  assert.match(status?.textContent, /Copy unavailable\. Select the link for Intro\./);

  fallback.dispatchEvent(createEvent('keydown', { key: 'Escape' }));

  assert.equal(document.querySelector('[data-rw-section-copy-fallback]'), null);
  assert.equal(button.getAttribute('data-rw-section-copy-state'), null);
  assert.equal(status.textContent, '');
  assert.equal(document.activeElement, button);
});

test('section copy clears previous feedback when another button copies', async () => {
  const copiedUrls = [];
  const feedbackTimers = new Map();
  const clearedTimers = [];
  let nextTimerId = 0;
  const { context, document } = loadSectionCopyRuntime({
    clipboard: {
      writeText: async value => {
        copiedUrls.push(value);
      }
    },
    setTimeout: (callback, delay) => {
      if (delay === 2200) {
        nextTimerId += 1;
        feedbackTimers.set(nextTimerId, callback);
        return nextTimerId;
      }

      return setTimeout(callback, delay);
    },
    clearTimeout: id => {
      clearedTimers.push(id);
    }
  });

  const firstTarget = document.createElement('h2');
  firstTarget.id = 'alpha';
  firstTarget.textContent = 'Alpha';
  const firstButton = document.createElement('button');
  firstButton.setAttribute('data-rw-section-copy', 'alpha');
  firstButton.setAttribute('data-rw-section-copy-title', 'Alpha');

  const secondTarget = document.createElement('h2');
  secondTarget.id = 'beta';
  secondTarget.textContent = 'Beta';
  const secondButton = document.createElement('button');
  secondButton.setAttribute('data-rw-section-copy', 'beta');
  secondButton.setAttribute('data-rw-section-copy-title', 'Beta');
  document.body.append(firstTarget, firstButton, secondTarget, secondButton);

  context.window.RazorWire.sectionCopyManager.scan();

  firstButton.dispatchEvent(createEvent('click'));
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(firstButton.getAttribute('data-rw-section-copy-state'), 'copied');

  secondButton.dispatchEvent(createEvent('click'));
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.deepEqual(copiedUrls, [
    'https://docs.example.test/guide?tab=api#alpha',
    'https://docs.example.test/guide?tab=api#beta'
  ]);
  assert.deepEqual(clearedTimers, [1]);
  assert.equal(firstButton.getAttribute('data-rw-section-copy-state'), null);
  assert.equal(firstButton.getAttribute('data-rw-section-copy-message'), null);
  assert.equal(secondButton.getAttribute('data-rw-section-copy-state'), 'copied');
  assert.match(secondButton.getAttribute('data-rw-section-copy-message'), /Copied link to Beta\./);
  const status = document.querySelector('[data-rw-section-copy-status]');
  assert.match(status?.textContent, /Copied link to Beta\./);

  feedbackTimers.get(1)();
  assert.equal(secondButton.getAttribute('data-rw-section-copy-state'), 'copied');
  assert.match(status?.textContent, /Copied link to Beta\./);

  feedbackTimers.get(2)();
  assert.equal(secondButton.getAttribute('data-rw-section-copy-state'), null);
  assert.equal(secondButton.getAttribute('data-rw-section-copy-message'), null);
  assert.equal(status.textContent, '');
});

test('section copy records diagnostics for malformed controls', () => {
  const { context, document } = loadSectionCopyRuntime();
  const link = document.createElement('a');
  link.setAttribute('data-rw-section-copy', '#missing');
  document.body.appendChild(link);

  context.window.RazorWire.sectionCopyManager.scan();

  const diagnostics = context.window.RazorWire.sectionCopyManager.getDiagnostics();
  assert.equal(diagnostics.length, 1);
  assert.match(diagnostics[0].message, /is not a button/);
  assert.match(diagnostics[0].docs, /section-copy\.md#troubleshooting/);
});

test('section copy retires the ambient controller when explicit roots own all markers', async () => {
  let copyCount = 0;
  const { context, document } = loadSectionCopyRuntime({
    clipboard: {
      writeText: async () => {
        copyCount += 1;
      }
    }
  });
  const target = document.createElement('h2');
  target.id = 'intro';
  target.textContent = 'Intro';
  const button = document.createElement('button');
  button.setAttribute('data-rw-section-copy', 'intro');
  document.body.append(target, button);

  const manager = context.window.RazorWire.sectionCopyManager;
  manager.scan();
  assert.equal(manager.controllers.has(document.body), true);

  const root = document.createElement('section');
  root.setAttribute('data-rw-section-copy-root', 'true');
  root.append(target, button);
  document.body.appendChild(root);
  manager.scan();

  assert.equal(manager.controllers.has(document.body), false);
  assert.equal(manager.controllers.has(root), true);

  button.dispatchEvent(createEvent('click'));
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(copyCount, 1);
});

test('section copy keeps ambient and explicit root ownership separate', async () => {
  const copiedUrls = [];
  const { context, document } = loadSectionCopyRuntime({
    clipboard: {
      writeText: async value => {
        copiedUrls.push(value);
      }
    }
  });

  const explicitRoot = document.createElement('section');
  explicitRoot.setAttribute('data-rw-section-copy-root', 'true');
  const explicitTarget = document.createElement('h2');
  explicitTarget.id = 'owned';
  explicitTarget.textContent = 'Owned';
  const explicitButton = document.createElement('button');
  explicitButton.setAttribute('data-rw-section-copy', 'owned');
  explicitRoot.append(explicitTarget, explicitButton);

  const ambientTarget = document.createElement('h2');
  ambientTarget.id = 'ambient';
  ambientTarget.textContent = 'Ambient';
  const ambientButton = document.createElement('button');
  ambientButton.setAttribute('data-rw-section-copy', 'ambient');
  document.body.append(explicitRoot, ambientTarget, ambientButton);

  const manager = context.window.RazorWire.sectionCopyManager;
  manager.scan();

  assert.equal(manager.controllers.has(document.body), true);
  assert.equal(manager.controllers.has(explicitRoot), true);

  explicitButton.dispatchEvent(createEvent('click'));
  ambientButton.dispatchEvent(createEvent('click'));
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.deepEqual(copiedUrls, [
    'https://docs.example.test/guide?tab=api#owned',
    'https://docs.example.test/guide?tab=api#ambient'
  ]);
});

test('section copy binds markers on the root element itself', async () => {
  let copiedText = '';
  const { context, document } = loadSectionCopyRuntime({
    clipboard: {
      writeText: async value => {
        copiedText = value;
      }
    }
  });
  const rootTarget = document.createElement('section');
  rootTarget.id = 'overview';
  rootTarget.textContent = 'Overview';
  rootTarget.setAttribute('data-rw-section-copy-root', 'true');
  rootTarget.setAttribute('data-rw-section-copy-target', 'true');
  document.body.appendChild(rootTarget);

  context.window.RazorWire.sectionCopyManager.scan();

  const button = rootTarget.querySelector('button[data-rw-section-copy]');
  assert.equal(button?.getAttribute('data-rw-section-copy'), 'overview');

  button.dispatchEvent(createEvent('click'));
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(copiedText, 'https://docs.example.test/guide?tab=api#overview');
});

test('section copy does not double-bind markers inside nested explicit roots', async () => {
  let copyCount = 0;
  const { context, document } = loadSectionCopyRuntime({
    clipboard: {
      writeText: async () => {
        copyCount += 1;
      }
    }
  });
  const outerRoot = document.createElement('section');
  outerRoot.setAttribute('data-rw-section-copy-root', 'true');
  const innerRoot = document.createElement('section');
  innerRoot.setAttribute('data-rw-section-copy-root', 'true');
  const target = document.createElement('h2');
  target.id = 'nested';
  target.textContent = 'Nested';
  const button = document.createElement('button');
  button.setAttribute('data-rw-section-copy', 'nested');
  innerRoot.append(target, button);
  outerRoot.appendChild(innerRoot);
  document.body.appendChild(outerRoot);

  const manager = context.window.RazorWire.sectionCopyManager;
  manager.scan();

  assert.equal(manager.controllers.has(outerRoot), true);
  assert.equal(manager.controllers.has(innerRoot), true);

  button.dispatchEvent(createEvent('click'));
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(copyCount, 1);
});

test('section copy unregisters connected roots that lose ownership', async () => {
  let copyCount = 0;
  const { context, document } = loadSectionCopyRuntime({
    clipboard: {
      writeText: async () => {
        copyCount += 1;
      }
    }
  });
  const root = document.createElement('section');
  root.setAttribute('data-rw-section-copy-root', 'true');
  const target = document.createElement('h2');
  target.id = 'moved';
  target.textContent = 'Moved';
  const button = document.createElement('button');
  button.setAttribute('data-rw-section-copy', 'moved');
  root.append(target, button);
  document.body.appendChild(root);

  const manager = context.window.RazorWire.sectionCopyManager;
  manager.scan();
  assert.equal(manager.controllers.has(root), true);

  root.removeAttribute('data-rw-section-copy-root');
  manager.scan();

  assert.equal(manager.controllers.has(root), false);
  assert.equal(manager.controllers.has(document.body), true);

  button.dispatchEvent(createEvent('click'));
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(copyCount, 1);
});

function loadSectionCopyRuntime(options = {}) {
  const document = new FakeDocument();
  const window = {
    RazorWire: { config: { developmentDiagnostics: true } },
    location: { href: 'https://docs.example.test/guide?tab=api' },
    setTimeout: options.setTimeout ?? ((callback, delay) => setTimeout(callback, delay)),
    clearTimeout: options.clearTimeout ?? (id => clearTimeout(id)),
    addEventListener() {}
  };
  const context = {
    window,
    document,
    Element: FakeElement,
    HTMLElement: FakeElement,
    Node: FakeElement,
    navigator: options.clipboard ? { clipboard: options.clipboard } : {},
    console,
    URL,
    setTimeout: window.setTimeout,
    clearTimeout: window.clearTimeout
  };
  window.document = document;
  document.defaultView = window;

  vm.createContext(context);
  vm.runInContext(readFileSync(sectionCopyPath, 'utf8'), context);
  return { context, document };
}

function createEvent(type, extras = {}) {
  return {
    type,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
    ...extras
  };
}

class FakeDocument {
  constructor() {
    this.readyState = 'complete';
    this.documentElement = new FakeElement('html', this);
    this.body = new FakeElement('body', this);
    this.documentElement.appendChild(this.body);
    this.listeners = new Map();
    this.activeElement = null;
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

  getElementById(id) {
    return this.querySelectorAll('[id]').find(element => element.id === id) ?? null;
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  removeEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    this.listeners.set(type, listeners.filter(candidate => candidate !== listener));
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
    this.textContent = '';
    this.isConnected = true;
  }

  get id() {
    return this.getAttribute('id') ?? '';
  }

  set id(value) {
    this.setAttribute('id', value);
  }

  get nextElementSibling() {
    if (!this.parentElement) return null;
    const siblings = this.parentElement.children;
    const index = siblings.indexOf(this);
    return index >= 0 ? siblings[index + 1] ?? null : null;
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
    for (const node of nodes) {
      this.appendChild(node);
    }
  }

  appendChild(node) {
    if (node.parentElement) {
      node.parentElement.children = node.parentElement.children.filter(child => child !== node);
    }
    node.parentElement = this;
    node.ownerDocument = this.ownerDocument;
    node.isConnected = true;
    this.children.push(node);
    return node;
  }

  insertAdjacentElement(position, element) {
    if (position !== 'afterend' || !this.parentElement) {
      return this.appendChild(element);
    }

    const siblings = this.parentElement.children;
    const index = siblings.indexOf(this);
    element.parentElement = this.parentElement;
    element.ownerDocument = this.ownerDocument;
    siblings.splice(index + 1, 0, element);
    return element;
  }

  remove() {
    if (this.parentElement) {
      this.parentElement.children = this.parentElement.children.filter(child => child !== this);
    }
    this.parentElement = null;
    this.isConnected = false;
  }

  contains(node) {
    if (node === this) return true;
    return this.children.some(child => child.contains(node));
  }

  closest(selector) {
    let current = this;
    while (current) {
      if (current.matches(selector)) return current;
      current = current.parentElement;
    }

    return null;
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] ?? null;
  }

  querySelectorAll(selector) {
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
    const tagAttribute = selector.match(/^([a-z0-9-]+)?\[([^=\]]+)(?:=['"]?([^'"\]]+)['"]?)?\]$/i);
    if (tagAttribute) {
      const [, tagName, attribute, value] = tagAttribute;
      if (tagName && this.tagName !== tagName.toUpperCase()) return false;
      if (!this.hasAttribute(attribute)) return false;
      return value === undefined || this.getAttribute(attribute) === value;
    }

    return this.tagName === selector.toUpperCase();
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
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

  focus() {
    this.ownerDocument.activeElement = this;
  }

  select() {
    this.selectionStart = 0;
    this.selectionEnd = this.value?.length ?? 0;
  }
}
