import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const sectionCopyPath = new URL('../wwwroot/razorwire/section-copy.js', import.meta.url);

test('section copy generates plain buttons and copies encoded section urls', async () => {
  let copiedText = '';
  const { context, document } = loadSectionCopyRuntime({
    clipboard: {
      writeText: async value => {
        copiedText = value;
      }
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
  assert.equal(button.getAttribute('data-rw-section-copy-state'), 'fallback');

  fallback.dispatchEvent(createEvent('keydown', { key: 'Escape' }));

  assert.equal(document.querySelector('[data-rw-section-copy-fallback]'), null);
  assert.equal(button.getAttribute('data-rw-section-copy-state'), null);
  assert.equal(document.activeElement, button);
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

function loadSectionCopyRuntime(options = {}) {
  const document = new FakeDocument();
  const window = {
    RazorWire: { config: { developmentDiagnostics: true } },
    location: { href: 'https://docs.example.test/guide?tab=api' },
    setTimeout: (callback, delay) => setTimeout(callback, delay),
    clearTimeout: id => clearTimeout(id),
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
    setTimeout,
    clearTimeout
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
