import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const runtimePath = new URL('../wwwroot/razorwire/razorwire.js', import.meta.url);

test('generic 400 diagnostics do not point developers at antiforgery fixes', () => {
  // Regression: ISSUE-001 - bare 400 fallback showed antiforgery-specific hints.
  // Found by /qa on 2026-05-03.
  // Report: .gstack/qa-reports/qa-report-127-0-0-1-2026-05-03.md
  const { window } = loadRuntime();

  const diagnostic = window.RazorWire.formFailureManager.diagnosticForFailure({
    form: new FakeElement('form'),
    statusCode: 400,
    responseKind: 'unknown'
  });
  const hints = Array.from(diagnostic.hints);

  assert.deepEqual(hints, [
    'Check the response status and whether the server set X-RazorWire-Form-Handled for custom UI.',
    'Check server logs or the response body for the Bad Request reason.',
    'For expected validation failures, return a handled stream with FormError or FormValidationErrors instead of a bare 400.'
  ]);
  assert.equal(hints.some(hint => hint.includes('__RequestVerificationToken')), false);
  assert.equal(hints.some(hint => hint.includes('AntiForgeryToken')), false);
});

function loadRuntime() {
  const document = new FakeDocument();
  const window = {
    RazorWireInitialized: false,
    RazorWire: {},
    location: { origin: 'https://example.test' },
    addEventListener: () => {}
  };
  const context = {
    console: { log: () => {} },
    document,
    window,
    Element: FakeElement,
    HTMLFormElement: FakeElement,
    CustomEvent: class {
      constructor(name, options = {}) {
        this.type = name;
        this.detail = options.detail;
        this.cancelable = options.cancelable === true;
        this.defaultPrevented = false;
      }

      preventDefault() {
        if (this.cancelable) this.defaultPrevented = true;
      }
    },
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

  return { window };
}

class FakeDocument {
  constructor() {
    this.readyState = 'complete';
    this.body = new FakeElement('body');
    this.head = new FakeElement('head');
    this.documentElement = new FakeElement('html');
    this.currentScript = new FakeElement('script');
    this.currentScript.dataset = {
      rwDevelopmentDiagnostics: 'true',
      rwFormFailureMode: 'auto',
      rwDefaultFailureMessage: 'We could not submit this form. Check your input and try again.'
    };
  }

  addEventListener() {}

  querySelector(selector) {
    if (selector.includes('/razorwire/razorwire.js')) return this.currentScript;

    return null;
  }

  querySelectorAll() {
    return [];
  }

  getElementById() {
    return null;
  }

  createElement(tagName) {
    return new FakeElement(tagName);
  }
}

class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.attributes = new Map();
    this.children = [];
    this.dataset = {};
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.get(name) ?? null;
  }

  removeAttribute(name) {
    this.attributes.delete(name);
  }

  appendChild(child) {
    this.children.push(child);

    return child;
  }

  querySelector() {
    return null;
  }

  querySelectorAll() {
    return [];
  }
}
