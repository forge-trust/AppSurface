import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const runtimePath = new URL('../wwwroot/razorwire/razorwire.js', import.meta.url);

test('runtime merges config and exposes formFailureManager', () => {
  const { context, document } = loadRuntime();

  assert.equal(context.window.RazorWire.config.existing, true);
  assert.equal(context.window.RazorWire.config.developmentDiagnostics, true);
  assert.equal(context.window.RazorWire.config.failureUxEnabled, true);
  assert.equal(context.window.RazorWire.config.failureMode, 'auto');
  assert.ok(context.window.RazorWire.formFailureManager);
  assert.equal(document.head.querySelectorAll('#rw-form-failure-default-styles').length, 1);
});

test('before fetch marks RazorWire form requests with the durable header', () => {
  const { document } = loadRuntime();
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  document.body.appendChild(form);

  const event = {
    type: 'turbo:before-fetch-request',
    target: form,
    detail: { fetchOptions: { headers: {} } }
  };
  document.dispatchEvent(event);

  assert.equal(event.detail.fetchOptions.headers['X-RazorWire-Form'], 'true');
});

test('before fetch supports Headers-like request headers', () => {
  const { document } = loadRuntime();
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  document.body.appendChild(form);

  const headers = new FakeHeaders();
  document.dispatchEvent({
    type: 'turbo:before-fetch-request',
    target: form,
    detail: { fetchOptions: { headers } }
  });

  assert.equal(headers.get('X-RazorWire-Form'), 'true');
});

test('submit lifecycle disables only RazorWire-owned submitter state and restores it', () => {
  const { document } = loadRuntime();
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  const button = new FakeElement('button');
  form.appendChild(button);
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'turbo:submit-start',
    target: form,
    detail: { formSubmission: { submitter: button } }
  });

  assert.equal(form.getAttribute('data-rw-submitting'), 'true');
  assert.equal(form.getAttribute('aria-busy'), 'true');
  assert.equal(button.disabled, true);
  assert.equal(button.getAttribute('data-rw-submit-disabled-by-razorwire'), 'true');

  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: true,
      formSubmission: { submitter: button },
      fetchResponse: response(200, {})
    }
  });

  assert.equal(form.hasAttribute('data-rw-submitting'), false);
  assert.equal(form.hasAttribute('aria-busy'), false);
  assert.equal(button.disabled, false);
});

test('failed submit renders one scoped fallback block and injects styles once', () => {
  const { document } = loadRuntime();
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'turbo:submit-start',
    target: form,
    detail: { formSubmission: { submitter: null } }
  });
  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: false,
      formSubmission: { submitter: null },
      fetchResponse: response(500, { 'content-type': 'text/html' })
    }
  });
  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: false,
      formSubmission: { submitter: null },
      fetchResponse: response(500, { 'content-type': 'text/html' })
    }
  });

  assert.equal(form.querySelectorAll('[data-rw-form-error-generated="true"]').length, 1);
  assert.equal(document.head.querySelectorAll('#rw-form-failure-default-styles').length, 1);
  assert.equal(form.getAttribute('data-rw-submit-status'), 'failed');
  assert.equal(form.getAttribute('data-rw-last-status'), '500');
});

test('handled failures clear generated fallback instead of duplicating server UI', () => {
  const { document } = loadRuntime();
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  document.body.appendChild(form);

  assert.equal(windowHandled(response(422, { 'X-RazorWire-Form-Handled': 'true' })), true);
  assert.equal(windowHandled(response(422, { 'X-RazorWire-Form-Handled': '1' })), true);

  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: false,
      formSubmission: { submitter: null },
      fetchResponse: response(500, { 'content-type': 'text/html' })
    }
  });

  assert.equal(form.querySelectorAll('[data-rw-form-error-generated="true"]').length, 1);

  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: false,
      formSubmission: { submitter: null },
      fetchResponse: response(422, { 'X-RazorWire-Form-Handled': '1', 'content-type': 'text/vnd.turbo-stream.html' })
    }
  });

  assert.equal(form.querySelectorAll('[data-rw-form-error-generated="true"]').length, 0);
  assert.equal(form.getAttribute('data-rw-submit-status'), 'failed');
});

test('manual failures expose development diagnostic without rendering fallback', () => {
  const { document } = loadRuntime();
  const events = [];
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'manual');
  form.dispatchEvent = event => {
    events.push(event);
    return !event.defaultPrevented;
  };
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: false,
      formSubmission: { submitter: null },
      fetchResponse: response(500, { 'content-type': 'text/html' })
    }
  });

  const failure = events.find(event => event.type === 'razorwire:form:failure');
  assert.equal(failure.detail.developmentDiagnostic.title, 'RazorWire form submission failed');
  assert.equal(failure.detail.developmentDiagnostic.statusCode, 500);
  assert.equal(form.querySelectorAll('[data-rw-form-error-generated="true"]').length, 0);
});

test('network failures dispatch submit end lifecycle event', () => {
  const { document } = loadRuntime();
  const events = [];
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  form.dispatchEvent = event => {
    events.push(event);
    return !event.defaultPrevented;
  };
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'turbo:fetch-request-error',
    target: form,
    detail: {}
  });

  const submitEnd = events.find(event => event.type === 'razorwire:form:submit-end');
  assert.equal(submitEnd.detail.success, false);
  assert.equal(submitEnd.detail.statusCode, null);
  assert.equal(submitEnd.detail.handled, false);
});

test('global disabled failure UX ignores stale form-level auto markup', () => {
  const { document } = loadRuntime({ formFailureEnabled: 'false', failureMode: 'off' });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  const button = new FakeElement('button');
  form.appendChild(button);
  document.body.appendChild(form);

  const beforeFetch = {
    type: 'turbo:before-fetch-request',
    target: form,
    detail: { fetchOptions: { headers: {} } }
  };
  document.dispatchEvent(beforeFetch);
  document.dispatchEvent({
    type: 'turbo:submit-start',
    target: form,
    detail: { formSubmission: { submitter: button } }
  });

  assert.equal(beforeFetch.detail.fetchOptions.headers['X-RazorWire-Form'], undefined);
  assert.equal(form.hasAttribute('data-rw-submitting'), false);
  assert.equal(button.disabled, false);
  assert.equal(document.head.querySelectorAll('#rw-form-failure-default-styles').length, 0);
});

test('legacy runtime mode off also disables stale form-level auto markup', () => {
  const { context } = loadRuntime({ omitFormFailureEnabled: true, failureMode: 'off' });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');

  assert.equal(context.window.RazorWire.config.failureUxEnabled, false);
  assert.equal(context.window.RazorWire.formFailureManager.isRazorWireForm(form), false);
});

function loadRuntime(runtimeOptions = {}) {
  const document = new FakeDocument(runtimeOptions);
  const window = {
    RazorWireInitialized: false,
    RazorWire: { config: { existing: true } },
    location: { origin: 'https://example.test' },
    addEventListener: () => {}
  };
  const context = {
    console: { log: () => {} },
    document,
    window,
    Element: FakeElement,
    HTMLFormElement: FakeForm,
    CustomEvent: FakeCustomEvent,
    MutationObserver: class {
      observe() {}
      disconnect() {}
    },
    Turbo: {
      connectStreamSource: () => {},
      disconnectStreamSource: () => {}
    },
    setTimeout,
    clearTimeout,
    setInterval: () => 1,
    clearInterval: () => {},
    Date,
    Intl,
    URL
  };
  context.globalThis = context;
  vm.createContext(context);
  vm.runInContext(readFileSync(runtimePath, 'utf8'), context);

  return { context, document, window };
}

function response(status, headers) {
  const normalized = new Map(Object.entries(headers).map(([key, value]) => [key.toLowerCase(), value]));
  return {
    response: {
      status,
      headers: {
        get: name => normalized.get(String(name).toLowerCase()) || null
      }
    }
  };
}

function windowHandled(fetchResponse) {
  const { context } = loadRuntime();

  return context.window.RazorWire.formFailureManager.isHandled(fetchResponse);
}

class FakeCustomEvent {
  constructor(type, options = {}) {
    this.type = type;
    this.bubbles = options.bubbles || false;
    this.cancelable = options.cancelable || false;
    this.detail = options.detail;
    this.defaultPrevented = false;
  }

  preventDefault() {
    if (this.cancelable) this.defaultPrevented = true;
  }
}

class FakeHeaders {
  constructor() {
    this.values = new Map();
  }

  set(name, value) {
    this.values.set(String(name).toLowerCase(), String(value));
  }

  get(name) {
    return this.values.get(String(name).toLowerCase()) || null;
  }
}

class FakeElement {
  constructor(tagName = 'div') {
    this.tagName = tagName.toUpperCase();
    this.attributes = new Map();
    this.children = [];
    this.parentElement = null;
    this.textContent = '';
    this.disabled = false;
    this.id = '';
    this.dataset = {};
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
    if (name === 'id') this.id = String(value);
    if (name.startsWith('data-')) {
      this.dataset[toDatasetName(name)] = String(value);
    }
  }

  getAttribute(name) {
    return this.attributes.get(name) ?? null;
  }

  hasAttribute(name) {
    return this.attributes.has(name);
  }

  removeAttribute(name) {
    this.attributes.delete(name);
  }

  appendChild(child) {
    child.parentElement = this;
    this.children.push(child);
    return child;
  }

  append(...children) {
    children.forEach(child => this.appendChild(child));
  }

  prepend(child) {
    child.parentElement = this;
    this.children.unshift(child);
    return child;
  }

  remove() {
    if (!this.parentElement) return;
    this.parentElement.children = this.parentElement.children.filter(child => child !== this);
    this.parentElement = null;
  }

  focus() {}

  scrollIntoView() {}

  dispatchEvent(event) {
    event.target = event.target || this;
    return !event.defaultPrevented;
  }

  closest(selector) {
    let current = this;
    while (current) {
      if (matches(current, selector)) return current;
      current = current.parentElement;
    }

    return null;
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] || null;
  }

  querySelectorAll(selector) {
    const matchesFound = [];
    walk(this, element => {
      if (element !== this && matches(element, selector)) matchesFound.push(element);
    });
    return matchesFound;
  }

  set innerHTML(value) {
    this._innerHTML = value;
    this.children = [];
    if (value.includes('data-rw-form-error-title')) {
      const title = new FakeElement('strong');
      title.setAttribute('data-rw-form-error-title', 'true');
      this.appendChild(title);
    }
    if (value.includes('data-rw-form-error-message')) {
      const message = new FakeElement('p');
      message.setAttribute('data-rw-form-error-message', 'true');
      this.appendChild(message);
    }
    if (value.includes('data-rw-form-error-diagnostic')) {
      const diagnostic = new FakeElement('div');
      diagnostic.setAttribute('data-rw-form-error-diagnostic', 'true');
      const detail = new FakeElement('p');
      detail.setAttribute('data-rw-form-error-detail', 'true');
      const hints = new FakeElement('ul');
      hints.setAttribute('data-rw-form-error-hints', 'true');
      diagnostic.appendChild(detail);
      diagnostic.appendChild(hints);
      this.appendChild(diagnostic);
    }
  }
}

class FakeForm extends FakeElement {
  constructor() {
    super('form');
  }
}

class FakeDocument {
  constructor(runtimeOptions = {}) {
    this.readyState = 'complete';
    this.listeners = new Map();
    this.head = new FakeElement('head');
    this.body = new FakeElement('body');
    this.activeElement = null;
    this.currentScript = new FakeElement('script');
    this.currentScript.setAttribute('src', '/_content/ForgeTrust.RazorWire/razorwire/razorwire.js');
    this.currentScript.setAttribute('data-rw-development-diagnostics', runtimeOptions.developmentDiagnostics ?? 'true');
    if (!runtimeOptions.omitFormFailureEnabled) {
      this.currentScript.setAttribute('data-rw-form-failure-enabled', runtimeOptions.formFailureEnabled ?? 'true');
    }
    this.currentScript.setAttribute('data-rw-form-failure-mode', runtimeOptions.failureMode ?? 'auto');
    this.currentScript.setAttribute('data-rw-default-failure-message', runtimeOptions.defaultFailureMessage ?? 'Default failure');
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
    return !event.defaultPrevented;
  }

  createElement(tagName) {
    return new FakeElement(tagName);
  }

  getElementById(id) {
    let found = null;
    [this.head, this.body].forEach(root => walk(root, element => {
      if (!found && element.id === id) found = element;
    }));
    return found;
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] || null;
  }

  querySelectorAll(selector) {
    const results = [];
    [this.currentScript, this.head, this.body].forEach(root => {
      if (matches(root, selector)) results.push(root);
      walk(root, element => {
        if (element !== root && matches(element, selector)) results.push(element);
      });
    });
    return results;
  }
}

function walk(root, callback) {
  for (const child of root.children) {
    callback(child);
    walk(child, callback);
  }
}

function matches(element, selector) {
  if (selector === 'rw-stream-source') return element.tagName === 'RW-STREAM-SOURCE';
  if (selector === 'time[data-rw-time]') return element.tagName === 'TIME' && element.hasAttribute('data-rw-time');
  if (selector === 'time[data-rw-time][data-rw-time-display="relative"]') {
    return element.tagName === 'TIME'
      && element.hasAttribute('data-rw-time')
      && element.getAttribute('data-rw-time-display') === 'relative';
  }
  if (selector === 'form[data-rw-form="true"]') {
    return element.tagName === 'FORM' && element.getAttribute('data-rw-form') === 'true';
  }
  if (selector === '[data-rw-form-errors]') return element.hasAttribute('data-rw-form-errors');
  if (selector === '[data-rw-form-error-generated="true"]') {
    return element.getAttribute('data-rw-form-error-generated') === 'true';
  }
  if (selector === '[data-rw-form-error-title="true"]') return element.getAttribute('data-rw-form-error-title') === 'true';
  if (selector === '[data-rw-form-error-message="true"]') return element.getAttribute('data-rw-form-error-message') === 'true';
  if (selector === '[data-rw-form-error-detail="true"]') return element.getAttribute('data-rw-form-error-detail') === 'true';
  if (selector === '[data-rw-form-error-hints="true"]') return element.getAttribute('data-rw-form-error-hints') === 'true';
  if (selector === '#rw-form-failure-default-styles') return element.id === 'rw-form-failure-default-styles';
  if (selector.startsWith('script[src*=')) return element.tagName === 'SCRIPT' && element.getAttribute('src')?.includes('/razorwire/razorwire.js');
  if (selector.startsWith('[data-rw-form-error-generated="true"][data-rw-form-error-owner="')) {
    const owner = selector.match(/data-rw-form-error-owner="([^"]+)"/)?.[1];
    return element.getAttribute('data-rw-form-error-generated') === 'true'
      && element.getAttribute('data-rw-form-error-owner') === owner;
  }
  return false;
}

function toDatasetName(name) {
  return name
    .slice(5)
    .replace(/-([a-z])/g, (_, char) => char.toUpperCase());
}
