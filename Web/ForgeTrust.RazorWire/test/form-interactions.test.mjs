import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const formInteractionsPath = new URL('../wwwroot/razorwire/form-interactions.js', import.meta.url);

test('form toggle hides targets, disables only runtime-owned controls, and restores them', () => {
  const { context, document } = loadRuntime();
  const form = document.createElement('form');
  const toggle = document.createElement('input');
  toggle.type = 'checkbox';
  toggle.checked = true;
  toggle.setAttribute('data-rw-form-toggle', 'no-action');
  toggle.setAttribute('data-rw-form-toggle-invert', 'true');
  const target = document.createElement('fieldset');
  target.id = 'draft-action';
  target.setAttribute('data-rw-form-toggle-target', 'no-action');
  target.setAttribute('data-rw-form-toggle-disable-when-hidden', 'true');
  const title = document.createElement('input');
  title.name = 'Actions[0].Title';
  const authoredDisabled = document.createElement('input');
  authoredDisabled.name = 'Actions[0].Locked';
  authoredDisabled.disabled = true;
  target.append(title, authoredDisabled);
  form.append(toggle, target);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();

  assert.equal(target.hidden, true);
  assert.equal(target.getAttribute('data-rw-form-toggle-target-state'), 'hidden');
  assert.equal(toggle.getAttribute('aria-controls'), 'draft-action');
  assert.equal(toggle.getAttribute('aria-expanded'), 'false');
  assert.equal(title.disabled, true);
  assert.equal(title.getAttribute('data-rw-disabled-by-form-toggle'), 'true');
  assert.equal(authoredDisabled.disabled, true);
  assert.equal(authoredDisabled.getAttribute('data-rw-disabled-by-form-toggle'), null);

  toggle.checked = false;
  toggle.dispatchEvent(createEvent('change'));

  assert.equal(target.hidden, false);
  assert.equal(toggle.getAttribute('aria-expanded'), 'true');
  assert.equal(title.disabled, false);
  assert.equal(title.getAttribute('data-rw-disabled-by-form-toggle'), null);
  assert.equal(authoredDisabled.disabled, true);
});

test('button form toggles sync on click without relying on change events', () => {
  const { context, document } = loadRuntime();
  const form = document.createElement('form');
  const toggle = document.createElement('button');
  toggle.value = 'pressed';
  toggle.setAttribute('data-rw-form-toggle', 'no-action');
  toggle.setAttribute('data-rw-form-toggle-invert', 'true');
  const target = document.createElement('fieldset');
  target.setAttribute('data-rw-form-toggle-target', 'no-action');
  target.setAttribute('data-rw-form-toggle-disable-when-hidden', 'true');
  const title = document.createElement('input');
  title.name = 'Actions[0].Title';
  target.appendChild(title);
  form.append(toggle, target);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();

  assert.equal(target.hidden, true);
  assert.equal(title.disabled, true);
  assert.equal(toggle.getAttribute('aria-expanded'), 'false');

  const event = createEvent('click');
  toggle.dispatchEvent(event);

  assert.equal(event.defaultPrevented, true);
  assert.equal(target.hidden, false);
  assert.equal(title.disabled, false);
  assert.equal(toggle.getAttribute('aria-expanded'), 'true');
});

test('collection add allocates sparse index markers and focuses the new editable field', () => {
  const { context, document } = loadRuntime();
  const { root, add, template } = buildCollection(document);
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  add.dispatchEvent(createEvent('click'));

  const rows = root.querySelectorAll('[data-rw-form-collection-row]');
  assert.equal(rows.length, 2);
  const added = rows[1];
  assert.equal(added.getAttribute('data-rw-form-index'), '1');
  assert.equal(added.querySelector('input[name="Actions.index"]').value, '1');
  assert.equal(added.querySelector('input[name="Actions[1].ClientIndex"]').value, '1');
  assert.equal(added.querySelector('input[name="Actions[1].Title"]').id, 'Actions_1__Title');
  assert.equal(added.querySelector('label').getAttribute('for'), 'Actions_1__Title');
  assert.equal(document.activeElement, added.querySelector('input[name="Actions[1].Title"]'));
  assert.match(root.querySelector('[data-rw-form-collection-status]').textContent, /Added action/);
  assert.equal(template.parentElement, root);
});

test('collection duplicate copies editable values, rewrites references, and clears identity hidden fields', () => {
  const { context, document } = loadRuntime();
  const { root, duplicate } = buildCollection(document);
  const sourceRow = root.querySelector('[data-rw-form-collection-row]');
  sourceRow.setAttribute('class', 'rounded-md border border-slate-200 p-4');
  sourceRow.setAttribute('data-consumer-state', 'step-0');
  const sourceTitle = root.querySelector('input[name="Actions[0].Title"]');
  sourceTitle.value = 'Call parent';
  root.querySelector('input[name="Actions[0].Id"]').value = 'persisted-1';
  root.querySelector('input[name="Actions[0].ClientIndex"]').value = '0';
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  duplicate.dispatchEvent(createEvent('click'));

  const rows = root.querySelectorAll('[data-rw-form-collection-row]');
  const clone = rows[1];
  assert.equal(clone.getAttribute('data-rw-form-index'), '1');
  assert.equal(clone.getAttribute('class'), 'rounded-md border border-slate-200 p-4');
  assert.equal(clone.getAttribute('data-consumer-state'), 'step-0');
  assert.equal(clone.querySelector('input[name="Actions.index"]').value, '1');
  assert.equal(clone.querySelector('input[name="Actions[1].ClientIndex"]').value, '1');
  assert.equal(clone.querySelector('input[name="Actions[1].Title"]').value, 'Call parent');
  assert.equal(clone.querySelector('input[name="Actions[1].Id"]').value, '');
  assert.equal(clone.querySelector('[data-valmsg-for]').getAttribute('data-valmsg-for'), 'Actions[1].Title');
  assert.equal(clone.querySelector('[data-valmsg-for]').textContent, '');
});

test('collection duplicate aborts when the source sparse index cannot be resolved', () => {
  const { context, document } = loadRuntime();
  const { root, duplicate } = buildCollection(document);
  const row = root.querySelector('[data-rw-form-collection-row]');
  row.removeAttribute('data-rw-form-index');
  row.querySelector('input[name="Actions.index"]').value = '';
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  context.window.RazorWire.formInteractionsManager.clearDiagnostics();
  duplicate.dispatchEvent(createEvent('click'));

  assert.equal(root.querySelectorAll('[data-rw-form-collection-row]').length, 1);
  assert.equal(
    context.window.RazorWire.formInteractionsManager
      .getDiagnostics()
      .some(diagnostic => /duplicate command has no source row index/.test(diagnostic.message)),
    true);
});

test('collection remove supports physical and mark-for-removal lanes', () => {
  const { context, document } = loadRuntime();
  const { root, remove } = buildCollection(document);
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  remove.dispatchEvent(createEvent('click'));
  assert.equal(root.querySelectorAll('[data-rw-form-collection-row]').length, 0);

  const second = buildCollection(document);
  second.root.setAttribute('data-rw-form-collection-remove-mode', 'mark');
  second.root.setAttribute('data-rw-form-toggle-target', 'draft-action');
  second.root.setAttribute('data-rw-form-toggle-disable-when-hidden', 'true');
  const toggle = document.createElement('input');
  toggle.type = 'checkbox';
  toggle.setAttribute('data-rw-form-toggle', 'draft-action');
  toggle.setAttribute('data-rw-form-toggle-invert', 'true');
  const deleteField = second.root.querySelector('input[name="Actions[0].Delete"]');
  deleteField.setAttribute('data-rw-form-collection-delete-field', 'true');
  const marker = second.root.querySelector('input[name="Actions.index"]');
  const id = second.root.querySelector('input[name="Actions[0].Id"]');
  id.setAttribute('data-rw-form-collection-preserve', 'true');
  const form2 = document.createElement('form');
  form2.append(toggle, second.root);
  document.body.appendChild(form2);
  context.window.RazorWire.formInteractionsManager.scan();
  second.remove.dispatchEvent(createEvent('click'));
  toggle.checked = true;
  toggle.dispatchEvent(createEvent('change'));

  const row = second.root.querySelector('[data-rw-form-collection-row]');
  assert.equal(second.root.hidden, true);
  assert.equal(row.hidden, true);
  assert.equal(row.getAttribute('data-rw-form-collection-row-state'), 'removed');
  assert.equal(deleteField.value, 'true');
  assert.equal(deleteField.disabled, false);
  assert.equal(marker.disabled, false);
  assert.equal(id.disabled, false);
  assert.equal(second.root.querySelector('input[name="Actions[0].Title"]').disabled, true);
});

test('mark-for-removal handles selector fields and invalid selectors safely', () => {
  const { context, document } = loadRuntime();
  const { root, remove } = buildCollection(document);
  root.setAttribute('data-rw-form-collection-remove-mode', 'mark');
  root.setAttribute('data-rw-form-collection-delete-field', 'input[name="Actions[0].Delete"]');
  const deleteField = root.querySelector('input[name="Actions[0].Delete"]');
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  remove.dispatchEvent(createEvent('click'));

  assert.equal(deleteField.getAttribute('data-rw-form-collection-delete-field'), 'true');
  assert.equal(deleteField.disabled, false);

  const second = buildCollection(document);
  second.root.setAttribute('data-rw-form-collection-remove-mode', 'mark');
  second.root.setAttribute('data-rw-form-collection-delete-field', '[');
  const form2 = document.createElement('form');
  form2.appendChild(second.root);
  document.body.appendChild(form2);

  context.window.RazorWire.formInteractionsManager.scan();
  context.window.RazorWire.formInteractionsManager.clearDiagnostics();
  const originalQuerySelector = second.row.querySelector.bind(second.row);
  second.row.querySelector = selector => {
    if (selector === '[') throw new Error('invalid selector');
    return originalQuerySelector(selector);
  };
  second.remove.dispatchEvent(createEvent('click'));

  assert.equal(second.row.hidden, false);
  assert.equal(
    context.window.RazorWire.formInteractionsManager
      .getDiagnostics()
      .some(diagnostic => /invalid delete-field selector/.test(diagnostic.message)),
    true);

  const third = buildCollection(document);
  third.root.setAttribute('data-rw-form-collection-remove-mode', 'mark');
  third.root.setAttribute('data-rw-form-collection-delete-field', '[data-delete-wrapper]');
  const wrapper = document.createElement('span');
  wrapper.setAttribute('data-delete-wrapper', 'true');
  third.row.appendChild(wrapper);
  const form3 = document.createElement('form');
  form3.appendChild(third.root);
  document.body.appendChild(form3);

  context.window.RazorWire.formInteractionsManager.scan();
  context.window.RazorWire.formInteractionsManager.clearDiagnostics();
  third.remove.dispatchEvent(createEvent('click'));

  assert.equal(third.row.hidden, false);
  assert.equal(
    context.window.RazorWire.formInteractionsManager
      .getDiagnostics()
      .some(diagnostic => /mark-remove has no app-owned delete field/.test(diagnostic.message)),
    true);
});

test('collection remove focus skips hidden marked rows and disabled commands', () => {
  const { context, document } = loadRuntime();
  const { root, remove, add } = buildCollection(document);
  root.setAttribute('data-rw-form-collection-remove-mode', 'mark');
  const deleteField = root.querySelector('input[name="Actions[0].Delete"]');
  deleteField.setAttribute('data-rw-form-collection-delete-field', 'true');
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  remove.dispatchEvent(createEvent('click'));
  root.setAttribute('data-rw-form-collection-remove-mode', 'physical');
  add.dispatchEvent(createEvent('click'));

  const addedRemove = root.querySelector('input[name="Actions[1].Title"]')
    .closest('[data-rw-form-collection-row]')
    .querySelector('[data-rw-form-collection-remove]');
  addedRemove.dispatchEvent(createEvent('click'));

  assert.equal(document.activeElement, add);
});

test('non-button collection commands are diagnosed without preventing fallback clicks', () => {
  const { context, document } = loadRuntime();
  const { root } = buildCollection(document);
  const link = document.createElement('a');
  link.setAttribute('href', '#fallback');
  link.setAttribute('data-rw-form-collection-add', 'true');
  root.appendChild(link);
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  context.window.RazorWire.formInteractionsManager.clearDiagnostics();
  const event = createEvent('click');
  link.dispatchEvent(event);

  assert.equal(event.defaultPrevented, false);
  assert.equal(root.querySelectorAll('[data-rw-form-collection-row]').length, 1);
  assert.equal(
    context.window.RazorWire.formInteractionsManager
      .getDiagnostics()
      .some(diagnostic => /a uses a collection command attribute but is not a button/.test(diagnostic.message)),
    true);
});

test('cancelable collection before events prevent mutation', () => {
  const { context, document } = loadRuntime();
  const { root, add } = buildCollection(document);
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);
  form.addEventListener('razorwire:form-collection:before-add', event => event.preventDefault());

  context.window.RazorWire.formInteractionsManager.scan();
  add.dispatchEvent(createEvent('click'));

  assert.equal(root.querySelectorAll('[data-rw-form-collection-row]').length, 1);
});

test('manager records diagnostics for malformed collection markup', () => {
  const { context, document } = loadRuntime();
  const form = document.createElement('form');
  const root = document.createElement('div');
  root.setAttribute('data-rw-form-collection', 'Actions');
  const add = document.createElement('button');
  add.setAttribute('data-rw-form-collection-add', 'true');
  root.appendChild(add);
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();

  const diagnostics = context.window.RazorWire.formInteractionsManager.getDiagnostics();
  assert.equal(diagnostics.some(diagnostic => /missing a template/.test(diagnostic.message)), true);
  assert.equal(diagnostics.every(diagnostic => /form-interactions\.md#troubleshooting/.test(diagnostic.docs)), true);
});

test('manager diagnoses cross-form targets and invalid collection indices', () => {
  const { context, document } = loadRuntime();
  const firstForm = document.createElement('form');
  const toggle = document.createElement('input');
  toggle.setAttribute('data-rw-form-toggle', 'other-form');
  firstForm.appendChild(toggle);
  document.body.appendChild(firstForm);

  const secondForm = document.createElement('form');
  const target = document.createElement('fieldset');
  target.setAttribute('data-rw-form-toggle-target', 'other-form');
  secondForm.appendChild(target);
  document.body.appendChild(secondForm);

  const collectionForm = document.createElement('form');
  const root = document.createElement('div');
  root.setAttribute('data-rw-form-collection', 'Actions');
  const template = document.createElement('template');
  template.setAttribute('data-rw-form-collection-template', 'true');
  template.innerHTML = '__index__';
  root.appendChild(template);

  const missingMarker = document.createElement('fieldset');
  missingMarker.setAttribute('data-rw-form-collection-row', 'true');
  const duplicateA = rowWithIndex(document, '0');
  const duplicateB = rowWithIndex(document, '0');
  const mismatched = rowWithIndex(document, '7');
  mismatched.setAttribute('data-rw-form-index', '8');
  const blank = rowWithIndex(document, '');
  const fileRow = rowWithIndex(document, '9');
  fileRow.appendChild(input(document, 'Actions[9].Attachment', 'file'));
  root.append(missingMarker, duplicateA, duplicateB, mismatched, blank, fileRow);
  collectionForm.appendChild(root);
  document.body.appendChild(collectionForm);

  context.window.RazorWire.formInteractionsManager.scan();

  const messages = context.window.RazorWire.formInteractionsManager
    .getDiagnostics()
    .map(diagnostic => diagnostic.message);
  assert.equal(messages.some(message => /outside its form/.test(message)), true);
  assert.equal(messages.some(message => /row is missing Actions\.index/.test(message)), true);
  assert.equal(messages.some(message => /duplicate index "0"/.test(message)), true);
  assert.equal(messages.some(message => /row index "8" does not match Actions\.index "7"/.test(message)), true);
  assert.equal(messages.some(message => /invalid Actions\.index marker/.test(message)), true);
  assert.equal(messages.some(message => /file input that duplicate cannot clone/.test(message)), true);
});

function buildCollection(document) {
  const root = document.createElement('div');
  root.setAttribute('data-rw-form-collection', 'Actions');
  root.setAttribute('data-rw-form-collection-label', 'action');

  const row = document.createElement('fieldset');
  row.setAttribute('data-rw-form-collection-row', 'true');
  row.setAttribute('data-rw-form-index', '0');
  row.append(
    hidden(document, 'Actions.index', '0'),
    input(document, 'Actions[0].Id', 'hidden'),
    input(document, 'Actions[0].Delete', 'hidden'),
    hidden(document, 'Actions[0].ClientIndex', '0'),
    label(document, 'Actions_0__Title'),
    input(document, 'Actions[0].Title', 'text', 'Actions_0__Title'),
    validation(document, 'Actions[0].Title'),
  );
  const duplicate = document.createElement('button');
  duplicate.setAttribute('data-rw-form-collection-duplicate', 'true');
  const remove = document.createElement('button');
  remove.setAttribute('data-rw-form-collection-remove', 'true');
  row.append(duplicate, remove);

  const template = document.createElement('template');
  template.setAttribute('data-rw-form-collection-template', 'true');
  template.innerHTML = '__index__';
  const templateRow = document.createElement('fieldset');
  templateRow.setAttribute('data-rw-form-collection-row', 'true');
  templateRow.append(
    hidden(document, 'Actions.index', '__index__'),
    input(document, 'Actions[__index__].Id', 'hidden'),
    input(document, 'Actions[__index__].Delete', 'hidden'),
    hidden(document, 'Actions[__index__].ClientIndex', '__index__'),
    label(document, 'Actions___index____Title'),
    input(document, 'Actions[__index__].Title', 'text', 'Actions___index____Title'),
    validation(document, 'Actions[__index__].Title'),
  );
  const templateDuplicate = document.createElement('button');
  templateDuplicate.setAttribute('data-rw-form-collection-duplicate', 'true');
  const templateRemove = document.createElement('button');
  templateRemove.setAttribute('data-rw-form-collection-remove', 'true');
  templateRow.append(templateDuplicate, templateRemove);
  template.content.appendChild(templateRow);

  const add = document.createElement('button');
  add.setAttribute('data-rw-form-collection-add', 'true');
  const status = document.createElement('span');
  status.setAttribute('data-rw-form-collection-status', 'true');
  status.setAttribute('aria-live', 'polite');
  root.append(row, template, add, status);
  return { root, row, template, add, duplicate, remove };
}

function input(document, name, type = 'text', id = '') {
  const element = document.createElement('input');
  element.setAttribute('name', name);
  element.setAttribute('type', type);
  if (id) element.id = id;
  return element;
}

function hidden(document, name, value) {
  const element = input(document, name, 'hidden');
  element.value = value;
  return element;
}

function rowWithIndex(document, value) {
  const row = document.createElement('fieldset');
  row.setAttribute('data-rw-form-collection-row', 'true');
  row.appendChild(hidden(document, 'Actions.index', value));
  return row;
}

function label(document, target) {
  const element = document.createElement('label');
  element.setAttribute('for', target);
  return element;
}

function validation(document, target) {
  const element = document.createElement('span');
  element.setAttribute('data-valmsg-for', target);
  element.setAttribute('data-valmsg-replace', 'true');
  element.setAttribute('aria-invalid', 'true');
  element.textContent = 'Old error';
  return element;
}

function loadRuntime() {
  const document = new FakeDocument();
  const window = {
    RazorWire: { config: { developmentDiagnostics: true } },
    document,
    CustomEvent: FakeCustomEvent,
    addEventListener() {},
    setTimeout,
    clearTimeout
  };
  const context = {
    window,
    document,
    Element: FakeElement,
    HTMLElement: FakeElement,
    HTMLFormElement: FakeElement,
    HTMLInputElement: FakeElement,
    HTMLSelectElement: FakeElement,
    HTMLTextAreaElement: FakeElement,
    HTMLButtonElement: FakeElement,
    DocumentFragment: FakeFragment,
    CustomEvent: FakeCustomEvent,
    console,
    Date,
    setTimeout,
    clearTimeout
  };
  document.defaultView = window;
  vm.createContext(context);
  vm.runInContext(readFileSync(formInteractionsPath, 'utf8'), context);
  return { context, document };
}

function createEvent(type, extras = {}) {
  return new FakeCustomEvent(type, { bubbles: true, cancelable: true, ...extras });
}

class FakeCustomEvent {
  constructor(type, options = {}) {
    this.type = type;
    this.bubbles = options.bubbles ?? false;
    this.cancelable = options.cancelable ?? false;
    this.detail = options.detail;
    this.defaultPrevented = false;
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
    this.activeElement = null;
    this.listeners = new Map();
  }

  createElement(tagName) {
    return tagName.toLowerCase() === 'template'
      ? new FakeTemplateElement(this)
      : new FakeElement(tagName, this);
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
}

class FakeFragment {
  constructor(ownerDocument) {
    this.ownerDocument = ownerDocument;
    this.children = [];
    this.parentElement = null;
    this.isConnected = true;
  }

  get firstElementChild() {
    return this.children[0] ?? null;
  }

  appendChild(node) {
    node.parentElement = this;
    node.ownerDocument = this.ownerDocument;
    this.children.push(node);
    return node;
  }

  append(...nodes) {
    for (const node of nodes) this.appendChild(node);
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] ?? null;
  }

  querySelectorAll(selector) {
    return queryAll(this, selector);
  }

  cloneNode(deep) {
    const fragment = new FakeFragment(this.ownerDocument);
    if (deep) {
      for (const child of this.children) fragment.appendChild(child.cloneNode(true));
    }
    return fragment;
  }
}

class FakeElement {
  constructor(tagName, ownerDocument) {
    this.tagName = tagName.toUpperCase();
    this.localName = tagName.toLowerCase();
    this.ownerDocument = ownerDocument;
    this._attributes = new Map();
    this.children = [];
    this.parentElement = null;
    this.listeners = new Map();
    this.textContent = '';
    this.isConnected = true;
    this.hidden = false;
    this.disabled = false;
    this.value = '';
    this.checked = false;
    this.type = tagName.toLowerCase() === 'button' ? 'submit' : 'text';
    this.name = '';
    this.selectedIndex = 0;
  }

  get attributes() {
    return Array.from(this._attributes.entries()).map(([name, value]) => ({ name, value }));
  }

  get id() {
    return this.getAttribute('id') ?? '';
  }

  set id(value) {
    this.setAttribute('id', value);
  }

  setAttribute(name, value) {
    this._attributes.set(name, String(value));
    if (name === 'id') this.idValue = String(value);
    if (name === 'name') this.name = String(value);
    if (name === 'type') this.type = String(value);
  }

  getAttribute(name) {
    if (name === 'id' && this.idValue) return this.idValue;
    if (name === 'name' && this.name) return this.name;
    if (name === 'type' && this.type) return this.type;
    return this._attributes.has(name) ? this._attributes.get(name) : null;
  }

  removeAttribute(name) {
    this._attributes.delete(name);
    if (name === 'id') this.idValue = '';
  }

  hasAttribute(name) {
    return this._attributes.has(name) || (name === 'id' && Boolean(this.idValue));
  }

  append(...nodes) {
    for (const node of nodes) this.appendChild(node);
  }

  appendChild(node) {
    if (node.parentElement?.children) {
      node.parentElement.children = node.parentElement.children.filter(child => child !== node);
    }
    node.parentElement = this;
    node.ownerDocument = this.ownerDocument;
    node.isConnected = true;
    this.children.push(node);
    return node;
  }

  insertAdjacentElement(position, element) {
    if (!this.parentElement || (position !== 'afterend' && position !== 'beforebegin' && position !== 'afterbegin')) {
      return this.appendChild(element);
    }

    if (position === 'afterbegin') {
      element.parentElement = this;
      this.children.unshift(element);
      return element;
    }

    const siblings = this.parentElement.children;
    const index = siblings.indexOf(this);
    element.parentElement = this.parentElement;
    element.ownerDocument = this.ownerDocument;
    siblings.splice(position === 'beforebegin' ? index : index + 1, 0, element);
    return element;
  }

  remove() {
    if (this.parentElement?.children) {
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
      if (current.matches?.(selector)) return current;
      current = current.parentElement;
    }
    return null;
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] ?? null;
  }

  querySelectorAll(selector) {
    return queryAll(this, selector);
  }

  matches(selector) {
    if (selector === '*') return true;
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
    event.currentTarget = this;
    for (const listener of this.listeners.get(event.type) ?? []) listener(event);
    if (event.bubbles && this.parentElement) this.parentElement.dispatchEvent(event);
    return !event.defaultPrevented;
  }

  focus() {
    this.ownerDocument.activeElement = this;
  }

  cloneNode(deep) {
    const clone = this instanceof FakeTemplateElement
      ? new FakeTemplateElement(this.ownerDocument)
      : new FakeElement(this.localName, this.ownerDocument);
    for (const { name, value } of this.attributes) clone.setAttribute(name, value);
    clone.textContent = this.textContent;
    clone.hidden = this.hidden;
    clone.disabled = this.disabled;
    clone.value = this.value;
    clone.checked = this.checked;
    clone.type = this.type;
    clone.name = this.name;
    clone.selectedIndex = this.selectedIndex;
    if (deep) {
      for (const child of this.children) clone.appendChild(child.cloneNode(true));
      if (this instanceof FakeTemplateElement) {
        clone.innerHTML = this.innerHTML;
        clone.content = this.content.cloneNode(true);
      }
    }
    return clone;
  }
}

class FakeTemplateElement extends FakeElement {
  constructor(ownerDocument) {
    super('template', ownerDocument);
    this.content = new FakeFragment(ownerDocument);
    this.innerHTML = '';
  }
}

function queryAll(root, selector) {
  const selectors = selector.split(',').map(part => part.trim()).filter(Boolean);
  const matches = [];
  const visit = element => {
    for (const child of element.children ?? []) {
      if (selectors.some(candidate => child.matches?.(candidate))) matches.push(child);
      visit(child);
    }
  };
  visit(root);
  return matches;
}
