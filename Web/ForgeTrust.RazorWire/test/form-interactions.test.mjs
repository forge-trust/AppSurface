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
  assert.equal(added.querySelector('[data-valmsg-for]').getAttribute('data-valmsg-replace'), 'true');
  assert.equal(document.activeElement, added.querySelector('input[name="Actions[1].Title"]'));
  assert.match(root.querySelector('[data-rw-form-collection-status]').textContent, /Added action/);
  assert.equal(template.parentElement, root);
});

test('collection add keeps new rows outside a wrapped command bar for empty and populated roots', () => {
  const { context, document } = loadRuntime();

  for (const indices of [[], ['0']]) {
    const collection = buildCollection(document);
    if (indices.length === 0) collection.row.remove();
    const commands = document.createElement('div');
    commands.setAttribute('class', 'commands');
    commands.appendChild(collection.add);
    collection.root.appendChild(commands);
    const form = document.createElement('form');
    form.appendChild(collection.root);
    document.body.appendChild(form);

    context.window.RazorWire.formInteractionsManager.scan();
    collection.add.dispatchEvent(createEvent('click'));
    collection.add.dispatchEvent(createEvent('click'));

    const rows = ownedRows(collection.root);
    const addedRows = rows.slice(indices.length);
    assert.equal(addedRows.length, 2);
    assert.deepEqual(addedRows.map(row => row.parentElement), [collection.root, collection.root]);
    assert.deepEqual(addedRows.map(row => row.getAttribute('data-rw-form-index')),
      indices.length === 0 ? ['0', '1'] : ['1', '2']);
    assert.deepEqual(commands.querySelectorAll('[data-rw-form-collection-row]'), []);
    assert.equal(collection.root.children.indexOf(addedRows[0]) < collection.root.children.indexOf(collection.template), true);
  }
});

test('empty collection add keeps rows outside a wrapper shared by its template and command', () => {
  const { context, document } = loadRuntime();
  const collection = buildCollection(document);
  collection.row.remove();
  const commands = document.createElement('div');
  const buttonWrapper = document.createElement('span');
  buttonWrapper.appendChild(collection.add);
  commands.append(collection.template, buttonWrapper);
  collection.root.appendChild(commands);
  const form = document.createElement('form');
  form.appendChild(collection.root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  collection.add.dispatchEvent(createEvent('click'));
  collection.add.dispatchEvent(createEvent('click'));

  const rows = ownedRows(collection.root);
  assert.equal(rows.length, 2);
  assert.equal(rows.every(row => row.parentElement === collection.root), true);
  assert.equal(commands.querySelectorAll('[data-rw-form-collection-row]').length, 0);
  assert.equal(collection.template.parentElement, commands);
});

test('adding beside a wrapped parent command preserves nested collection ownership', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document, {
    parentIndices: ['0'],
    childIndicesByParent: { 0: ['0'] },
    parentTemplateChildIndices: ['0']
  });
  const commands = document.createElement('div');
  commands.appendChild(fixture.add);
  fixture.root.appendChild(commands);
  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  fixture.add.dispatchEvent(createEvent('click'));
  const addedParent = ownedRows(fixture.root)[1];
  const childRoot = addedParent.querySelector('[data-rw-form-collection]');
  const childAdd = childRoot.querySelector('[data-rw-form-collection-add]');
  childAdd.dispatchEvent(createEvent('click'));

  assert.equal(addedParent.parentElement, fixture.root);
  assert.equal(addedParent.closest('[data-rw-form-collection]'), fixture.root);
  assert.equal(childRoot.parentElement, addedParent);
  assert.equal(ownedRows(childRoot).length, 2);
  assert.equal(ownedRows(fixture.root).length, 2);
  assert.equal(commands.querySelectorAll('[data-rw-form-collection-row]').length, 0);
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
  assert.equal(clone.querySelector('[data-valmsg-for]').getAttribute('data-valmsg-replace'), 'true');
  assert.equal(clone.querySelector('[data-valmsg-for]').textContent, '');
});

test('collection duplicate resets a root-selected delete field from a non-zero row', () => {
  const { context, document } = loadRuntime();
  const { root, duplicate } = buildCollection(document);
  root.setAttribute('data-rw-form-collection-delete-field', '[data-delete-control]');
  root.querySelector('input[name="Actions[0].Delete"]').setAttribute('data-delete-control', 'true');
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  duplicate.dispatchEvent(createEvent('click'));
  const secondRow = root.querySelectorAll('[data-rw-form-collection-row]')[1];
  const secondDeleteField = secondRow.querySelector('input[name="Actions[1].Delete"]');
  secondDeleteField.removeAttribute('value');
  secondRow.querySelector('[data-rw-form-collection-duplicate]').dispatchEvent(createEvent('click'));

  const cloneDeleteField = root.querySelector('input[name="Actions[2].Delete"]');
  assert.equal(cloneDeleteField.hasAttribute('data-rw-form-collection-delete-field'), false);
  assert.equal(cloneDeleteField.value, 'false');
  assert.equal(cloneDeleteField.disabled, false);
});

test('collection duplicate tolerates an invalid root delete-field selector', () => {
  const { context, document } = loadRuntime();
  const { root, duplicate } = buildCollection(document);
  root.setAttribute('data-rw-form-collection-delete-field', '[');
  const sourceRow = root.querySelector('[data-rw-form-collection-row]');
  const originalQuerySelector = sourceRow.querySelector.bind(sourceRow);
  sourceRow.querySelector = selector => {
    if (selector === '[') throw new Error('invalid selector');
    return originalQuerySelector(selector);
  };
  root.querySelector('input[name="Actions[0].Delete"]').value = 'true';
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  duplicate.dispatchEvent(createEvent('click'));

  const cloneDeleteField = root.querySelector('input[name="Actions[1].Delete"]');
  assert.equal(root.querySelectorAll('[data-rw-form-collection-row]').length, 2);
  assert.equal(cloneDeleteField.value, '');
});

test('collection duplicate keeps normal hidden-field clearing when the root selector has no match', () => {
  const { context, document } = loadRuntime();
  const { root, duplicate } = buildCollection(document);
  root.setAttribute('data-rw-form-collection-delete-field', '[data-missing-delete-field]');
  root.querySelector('input[name="Actions[0].Delete"]').value = 'true';
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  duplicate.dispatchEvent(createEvent('click'));

  const cloneDeleteField = root.querySelector('input[name="Actions[1].Delete"]');
  assert.equal(root.querySelectorAll('[data-rw-form-collection-row]').length, 2);
  assert.equal(cloneDeleteField.value, '');
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

test('[G05] legacy one-level duplicate copies controls before inserting a missing index marker', () => {
  const { context, document } = loadRuntime();
  const { root, duplicate } = buildCollection(document);
  const sourceRow = root.querySelector('[data-rw-form-collection-row]');
  sourceRow.querySelector('input[name="Actions.index"]').remove();
  sourceRow.querySelector('input[name="Actions[0].ClientIndex"]').value = '0';
  sourceRow.querySelector('input[name="Actions[0].Title"]').value = 'Call parent';
  const form = document.createElement('form');
  form.appendChild(root);
  document.body.appendChild(form);

  context.window.RazorWire.formInteractionsManager.scan();
  duplicate.dispatchEvent(createEvent('click'));

  const clone = root.querySelectorAll('[data-rw-form-collection-row]')[1];
  assert.equal(clone.querySelector('input[name="Actions.index"]').value, '1');
  assert.equal(clone.querySelector('input[name="Actions[1].ClientIndex"]').value, '1');
  assert.equal(clone.querySelector('input[name="Actions[1].Title"]').value, 'Call parent');
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

test('[G01] nested owners isolate child rows, templates, markers, delete controls, and status', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document);
  const actionRow = fixture.row;
  const tagPath = 'Actions[0].Tags';
  const tags = makeCollectionRoot(document, tagPath, '__tag_index__',
    (index, isTemplate) => makeSimpleNestedRow(document, tagPath, index, isTemplate), ['0'], {
      label: 'tag',
      deleteSelector: '[data-tag-delete]'
    });
  actionRow.appendChild(tags.root);
  tags.rows[0].querySelector('input[name="Actions[0].Tags[0].Id"]').setAttribute('data-tag-delete', 'true');

  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  context.window.RazorWire.formInteractionsManager.scan();

  const datesRoot = actionRow.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
  datesRoot.querySelector('[data-rw-form-collection-add]').dispatchEvent(createEvent('click'));

  assert.equal(ownedRows(fixture.root).length, 1);
  assert.equal(datesRoot.querySelectorAll('[data-rw-form-collection-row]').length, 2);
  assert.equal(tags.root.querySelectorAll('[data-rw-form-collection-row]').length, 1);
  assert.equal(fixture.status.textContent, '');
  assert.equal(datesRoot.querySelector('[data-rw-form-collection-status]').textContent, 'Added date.');
  assert.equal(tags.status.textContent, '');
  assert.equal(ownedRows(datesRoot)[1].querySelector('input[name="Actions[0].Dates.index"]').value, '1');
});

test('[G02] collection commands stay inside visible, enabled, live owners and forms', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document);
  const disabledAncestor = document.createElement('fieldset');
  disabledAncestor.disabled = true;
  disabledAncestor.appendChild(fixture.root);
  const form = document.createElement('form');
  form.appendChild(disabledAncestor);
  document.body.appendChild(form);
  context.window.RazorWire.formInteractionsManager.scan();

  const datesRoot = fixture.row.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
  const add = datesRoot.querySelector('[data-rw-form-collection-add]');
  const disabledClick = createEvent('click');
  add.dispatchEvent(disabledClick);
  assert.equal(disabledClick.defaultPrevented, false);
  assert.equal(datesRoot.querySelectorAll('[data-rw-form-collection-row]').length, 1);

  disabledAncestor.disabled = false;
  fixture.row.setAttribute('data-rw-form-collection-row-state', 'removed');
  const removedClick = createEvent('click');
  add.dispatchEvent(removedClick);
  assert.equal(removedClick.defaultPrevented, false);
  assert.equal(datesRoot.querySelectorAll('[data-rw-form-collection-row]').length, 1);
  fixture.row.removeAttribute('data-rw-form-collection-row-state');

  const outside = document.createElement('button');
  outside.setAttribute('data-rw-form-collection-add', 'true');
  document.body.appendChild(outside);
  const outsideClick = createEvent('click');
  outside.dispatchEvent(outsideClick);
  assert.equal(outsideClick.defaultPrevented, false);
  assert.equal(ownedRows(fixture.root).length, 1);
});

test('[G03] local placeholder defaults, custom tokens, ancestor distinction, and sibling reuse', () => {
  for (const blankMode of ['absent', 'blank']) {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document, { childIndexToken: '__index__' });
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    const datesRoot = fixture.row.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
    if (blankMode === 'absent') datesRoot.removeAttribute('data-rw-form-collection-index-token');
    else datesRoot.setAttribute('data-rw-form-collection-index-token', '   ');
    context.window.RazorWire.formInteractionsManager.scan();
    datesRoot.querySelector('[data-rw-form-collection-add]').dispatchEvent(createEvent('click'));
    assert.equal(ownedRows(datesRoot)[1].querySelector('input[name="Actions[0].Dates.index"]').value, '1');
  }

  {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document, { childIndexToken: '__date_index__' });
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    const datesRoot = fixture.row.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
    datesRoot.querySelector('[data-rw-form-collection-add]').dispatchEvent(createEvent('click'));
    assert.equal(datesRoot.querySelector('input[name="Actions[0].Dates[1].Kind"]').name, 'Actions[0].Dates[1].Kind');
    assert.equal(ownedRows(datesRoot)[1].querySelector('input[name="Actions[0].Dates.index"]').value, '1');
  }

  {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document, { childIndexToken: '__date_index__' });
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    const datesRoot = fixture.row.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
    datesRoot.setAttribute('data-rw-form-collection-index-token', 'date-index');
    context.window.RazorWire.formInteractionsManager.scan();
    context.window.RazorWire.formInteractionsManager.clearDiagnostics();
    datesRoot.querySelector('[data-rw-form-collection-add]').dispatchEvent(createEvent('click'));
    assert.equal(datesRoot.querySelectorAll('[data-rw-form-collection-row]').length, 1);
    assert.equal(context.window.RazorWire.formInteractionsManager.getDiagnostics()
      .some(diagnostic => /invalid template index token/.test(diagnostic.message)), true);
  }

  {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document, { parentIndices: [], childIndexToken: '__index__' });
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    fixture.add.dispatchEvent(createEvent('click'));
    assert.equal(ownedRows(fixture.root).length, 0);
    assert.equal(context.window.RazorWire.formInteractionsManager.getDiagnostics()
      .some(diagnostic => /ancestor and child template tokens must differ/.test(diagnostic.message)), true);
  }

  {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document, { parentIndices: [], childIndexToken: '__line_index__' });
    const templateAction = fixture.template.content.querySelector('[data-rw-form-collection-row]');
    const tagsPath = 'Actions[__index__].Tags';
    const tags = makeCollectionRoot(document, tagsPath, '__line_index__',
      (index, isTemplate) => makeSimpleNestedRow(document, tagsPath, index, isTemplate), []);
    templateAction.appendChild(tags.root);
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    fixture.add.dispatchEvent(createEvent('click'));

    const copiedAction = fixture.root.querySelector('[data-rw-form-collection-row]');
    const datesRoot = copiedAction.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
    const tagsRoot = copiedAction.querySelector('[data-rw-form-collection="Actions[0].Tags"]');
    assert.ok(datesRoot);
    assert.ok(tagsRoot);
    assert.equal(datesRoot.getAttribute('data-rw-form-collection-index-token'), '__line_index__');
    assert.equal(tagsRoot.getAttribute('data-rw-form-collection-index-token'), '__line_index__');
    assert.equal(datesRoot.querySelector('template').content.querySelector('input[name="Actions[0].Dates.index"]').value, '__line_index__');
    assert.equal(tagsRoot.querySelector('template').content.querySelector('input[name="Actions[0].Tags.index"]').value, '__line_index__');
  }
});

test('[G04] nested canonical markers reject missing, blank, disabled, multiple, duplicate, and mismatched keys', () => {
  const invalidators = [
    ['missing', row => row.querySelectorAll('input').find(input => input.name.endsWith('.index')).remove()],
    ['blank', row => { row.querySelectorAll('input').find(input => input.name.endsWith('.index')).value = '   '; }],
    ['disabled', row => { row.querySelectorAll('input').find(input => input.name.endsWith('.index')).disabled = true; }],
    ['multiple', (row, root, document) => {
      const marker = row.querySelectorAll('input').find(input => input.name.endsWith('.index'));
      row.appendChild(hidden(document, marker.name, marker.value));
    }],
    ['duplicate', (row, root, document) => {
      root.appendChild(makeDateRow(document, 'Actions[0].Dates', '0', false));
    }],
    ['mismatch', row => { row.setAttribute('data-rw-form-index', 'different'); }]
  ];

  for (const [name, invalidate] of invalidators) {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document);
    const datesRoot = fixture.row.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
    const dateRow = datesRoot.querySelector('[data-rw-form-collection-row]');
    invalidate(dateRow, datesRoot, document);
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    context.window.RazorWire.formInteractionsManager.clearDiagnostics();
    ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

    assert.equal(ownedRows(fixture.root).length, 1, name);
    assert.equal(context.window.RazorWire.formInteractionsManager.getDiagnostics()
      .some(diagnostic => diagnostic.message.includes('Actions[0].Dates')), true, name);
  }

  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document);
  const datesRoot = fixture.row.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
  const validMarker = datesRoot.querySelector('input[name="Actions[0].Dates.index"]');
  assert.equal(validMarker.type, 'hidden');
  assert.equal(validMarker.disabled, false);
  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  context.window.RazorWire.formInteractionsManager.scan();
  ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));
  const copy = ownedRows(fixture.root)[1];
  assert.equal(copy.querySelector('input[name="Actions[1].Dates.index"]').value, '1');
});

test('[G18] add and duplicate before-events expose their preparation stages and cancellation retains listener edits', () => {
  const { context, document } = loadRuntime();
  const fixture = buildCollection(document);
  const templateTitle = fixture.template.content.querySelector('input[name="Actions[__index__].Title"]');
  templateTitle.value = 'template draft';
  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  let afterAdd = 0;
  let beforeAddSnapshot;
  form.addEventListener('razorwire:form-collection:before-add', event => {
    const row = event.detail.row;
    beforeAddSnapshot = {
      metadata: row.getAttribute('data-rw-form-index'),
      markerName: row.querySelector('input[name="Actions.index"]').name,
      markerValue: row.querySelector('input[name="Actions.index"]').value,
      title: row.querySelector('input[name="Actions[1].Title"]').value,
      validationTarget: row.querySelector('[data-valmsg-for]').getAttribute('data-valmsg-for')
    };
  });
  form.addEventListener('razorwire:form-collection:add', () => afterAdd++);
  context.window.RazorWire.formInteractionsManager.scan();
  fixture.add.dispatchEvent(createEvent('click'));

  assert.deepEqual(beforeAddSnapshot, {
    metadata: null,
    markerName: 'Actions.index',
    markerValue: '__index__',
    title: 'template draft',
    validationTarget: 'Actions[1].Title'
  });
  const added = ownedRows(fixture.root)[1];
  assert.equal(added.getAttribute('data-rw-form-index'), '1');
  assert.equal(added.querySelector('input[name="Actions.index"]').value, '1');
  assert.equal(added.querySelector('input[name="Actions[1].Title"]').value, '');
  assert.equal(afterAdd, 1);

  const sourceTitle = fixture.row.querySelector('input[name="Actions[0].Title"]');
  sourceTitle.value = 'copied draft';
  let duplicateSnapshot;
  let afterDuplicate = 0;
  form.addEventListener('razorwire:form-collection:before-duplicate', event => {
    const row = event.detail.row;
    duplicateSnapshot = {
      metadata: row.getAttribute('data-rw-form-index'),
      marker: row.querySelector('input[name="Actions.index"]').value,
      title: row.querySelector('input[name="Actions[2].Title"]').value,
      identity: row.querySelector('input[name="Actions[2].Id"]').value,
      validationTarget: row.querySelector('[data-valmsg-for]').getAttribute('data-valmsg-for')
    };
    sourceTitle.value = 'listener edit retained';
    event.preventDefault();
  });
  form.addEventListener('razorwire:form-collection:duplicate', () => afterDuplicate++);
  fixture.row.querySelector('[data-rw-form-collection-duplicate]').dispatchEvent(createEvent('click'));

  assert.deepEqual(duplicateSnapshot, {
    metadata: '2', marker: '2', title: 'copied draft', identity: '',
    validationTarget: 'Actions[2].Title'
  });
  assert.equal(sourceTitle.value, 'listener edit retained');
  assert.equal(ownedRows(fixture.root).length, 2);
  assert.equal(afterDuplicate, 0);

  const liveRow = fixture.row;
  let beforeRemoveDetail;
  let afterRemove = 0;
  form.addEventListener('razorwire:form-collection:before-remove', event => {
    beforeRemoveDetail = { row: event.detail.row, index: event.detail.index };
    sourceTitle.value = 'remove listener edit';
    event.preventDefault();
  });
  form.addEventListener('razorwire:form-collection:remove', () => afterRemove++);
  liveRow.querySelector('[data-rw-form-collection-remove]').dispatchEvent(createEvent('click'));
  assert.equal(beforeRemoveDetail.row, liveRow);
  assert.equal(beforeRemoveDetail.index, '0');
  assert.equal(sourceTitle.value, 'remove listener edit');
  assert.equal(ownedRows(fixture.root).length, 2);
  assert.equal(afterRemove, 0);
});

test('[G19] before-listener structural interference aborts only RazorWire mutation and preserves listener work', () => {
  {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document);
    const form = document.createElement('form');
    const movedTo = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.append(form, movedTo);
    context.window.RazorWire.formInteractionsManager.scan();
    let afterDuplicate = 0;
    form.addEventListener('razorwire:form-collection:before-duplicate', event => {
      if (event.detail.root === fixture.root) movedTo.appendChild(fixture.root);
    });
    form.addEventListener('razorwire:form-collection:duplicate', () => afterDuplicate++);

    ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

    assert.equal(form.contains(fixture.root), false);
    assert.equal(movedTo.contains(fixture.root), true);
    assert.equal(ownedRows(fixture.root).length, 1);
    assert.equal(afterDuplicate, 0);
    assert.equal(context.window.RazorWire.formInteractionsManager.getDiagnostics()
      .some(diagnostic => /ownership changed/.test(diagnostic.message)), true);
  }

  {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document);
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    const sourceMarker = fixture.row.querySelector('input[name="Actions.index"]');
    let afterDuplicate = 0;
    form.addEventListener('razorwire:form-collection:before-duplicate', event => {
      if (event.detail.root === fixture.root) sourceMarker.value = 'listener-key';
    });
    form.addEventListener('razorwire:form-collection:duplicate', () => afterDuplicate++);

    ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

    assert.equal(sourceMarker.value, 'listener-key');
    assert.equal(ownedRows(fixture.root).length, 1);
    assert.equal(afterDuplicate, 0);
    assert.equal(context.window.RazorWire.formInteractionsManager.getDiagnostics()
      .some(diagnostic => /marker|ownership changed/.test(diagnostic.message)), true);
  }

  {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document);
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    let afterAdd = 0;
    form.addEventListener('razorwire:form-collection:before-add', event => {
      if (event.detail.root !== fixture.root) return;
      const listenerRow = makeActionRow(document, '1', false, { childIndices: [], childIndexToken: '__date_index__' });
      listenerRow.querySelector('input[name="Actions.index"]').value = '1';
      listenerRow.querySelector('input[name="Actions[1].Title"]').value = 'listener-created';
      fixture.root.appendChild(listenerRow);
    });
    form.addEventListener('razorwire:form-collection:add', () => afterAdd++);

    fixture.add.dispatchEvent(createEvent('click'));

    assert.deepEqual(ownedRows(fixture.root).map(row => row.querySelector('input[name="Actions.index"]').value), ['0', '1']);
    assert.equal(fixture.root.querySelector('input[name="Actions[1].Title"]').value, 'listener-created');
    assert.equal(afterAdd, 0);
    assert.equal(context.window.RazorWire.formInteractionsManager.getDiagnostics()
      .some(diagnostic => /index conflict|duplicate index/.test(diagnostic.message)), true);
  }

  {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document);
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    let afterParentAdd = 0;
    let afterChildAdd = 0;
    const existingDates = fixture.row.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
    form.addEventListener('razorwire:form-collection:before-add', event => {
      if (event.detail.root === fixture.root) existingDates.querySelector('[data-rw-form-collection-add]').dispatchEvent(createEvent('click'));
    });
    form.addEventListener('razorwire:form-collection:add', event => {
      if (event.detail.root === fixture.root) afterParentAdd++;
      else if (event.detail.root === existingDates) afterChildAdd++;
    });

    fixture.add.dispatchEvent(createEvent('click'));

    assert.equal(ownedRows(fixture.root).length, 2);
    assert.equal(ownedRows(existingDates).length, 2);
    assert.equal(afterParentAdd, 1);
    assert.equal(afterChildAdd, 1);
  }
});

test('[G06] opaque concrete parent and child keys survive duplication and child allocation', () => {
  const opaqueKeys = [
    '__index__',
    '__date_index__',
    'quoted"\\key',
    'closing]delimiter',
    'snow-雪-☃'
  ];

  for (const key of opaqueKeys) {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document);
    const oldPrefix = 'Actions[0]';
    const newPrefix = `Actions[${key}]`;
    const rebaseFixture = element => {
      for (const attribute of ['name', 'data-valmsg-for', 'data-rw-form-collection']) {
        const value = element.getAttribute(attribute);
        if (value === oldPrefix || value?.startsWith(`${oldPrefix}.`) || value?.startsWith(`${oldPrefix}[`)) {
          element.setAttribute(attribute, newPrefix + value.slice(oldPrefix.length));
        }
      }
      if (element.localName === 'template') {
        for (const child of element.content.children) rebaseFixture(child);
      }
      for (const child of element.children) rebaseFixture(child);
    };
    rebaseFixture(fixture.row);

    const findInput = (root, name) => Array.from(root.querySelectorAll('input')).find(element => element.name === name);
    const parentMarker = findInput(fixture.row, 'Actions.index');
    parentMarker.value = key;
    fixture.row.setAttribute('data-rw-form-index', key);
    const sourceDates = Array.from(fixture.row.querySelectorAll('[data-rw-form-collection]'))
      .find(root => root.getAttribute('data-rw-form-collection') === `${newPrefix}.Dates`);
    const sourceDate = ownedRows(sourceDates)[0];
    const childMarker = findInput(sourceDate, `${newPrefix}.Dates.index`);
    childMarker.value = key;
    sourceDate.setAttribute('data-rw-form-index', key);
    rebaseOwnedPath(sourceDate, `${newPrefix}.Dates[0]`, `${newPrefix}.Dates[${key}]`);
    const sourceKind = Array.from(sourceDate.querySelectorAll('input')).find(element => element.name.endsWith('.Kind'));
    sourceKind.value = `submitted:${key}`;

    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

    const copy = ownedRows(fixture.root).find(row => row !== fixture.row);
    const copiedParentKey = findInput(copy, 'Actions.index').value;
    assert.ok(copiedParentKey);
    assert.notEqual(copiedParentKey, key);
    assert.equal(parentMarker.value, key);
    const copiedDates = Array.from(copy.querySelectorAll('[data-rw-form-collection]'))
      .find(root => root.getAttribute('data-rw-form-collection') === `Actions[${copiedParentKey}].Dates`);
    const copiedDate = ownedRows(copiedDates)[0];
    const copiedChildMarker = findInput(copiedDate, `Actions[${copiedParentKey}].Dates.index`);
    const copiedChildKey = copiedChildMarker.value;
    assert.ok(copiedChildKey);
    assert.notEqual(copiedChildKey, key);
    assert.equal(childMarker.value, key);
    assert.equal(copiedDate.getAttribute('data-rw-form-index'), copiedChildKey);
    const copiedKind = Array.from(copiedDate.querySelectorAll('input')).find(element => element.name.endsWith('.Kind'));
    assert.equal(copiedKind.name, `Actions[${copiedParentKey}].Dates[${copiedChildKey}].Kind`);
    assert.equal(copiedKind.value, `submitted:${key}`);

    const copiedDateTemplate = ownedTemplate(copiedDates);
    const prototypeKind = Array.from(copiedDateTemplate.content.querySelectorAll('input'))
      .find(element => element.name.endsWith('.Kind'));
    const prototypeMarker = Array.from(copiedDateTemplate.content.querySelectorAll('input'))
      .find(element => element.name.endsWith('.Dates.index'));
    assert.equal(prototypeKind.name, `Actions[${copiedParentKey}].Dates[__date_index__].Kind`);
    assert.equal(prototypeMarker.value, '__date_index__');

    copiedDates.querySelector('[data-rw-form-collection-add]').dispatchEvent(createEvent('click'));
    const copiedDatesAfterAdd = ownedRows(copiedDates);
    const childKeysAfterAdd = copiedDatesAfterAdd.map(row => findInput(row, `Actions[${copiedParentKey}].Dates.index`).value);
    assert.equal(childKeysAfterAdd.length, 2);
    assert.equal(new Set(childKeysAfterAdd).size, 2);
    const addedDate = copiedDatesAfterAdd.find(row => row !== copiedDate);
    const addedKey = findInput(addedDate, `Actions[${copiedParentKey}].Dates.index`).value;
    assert.notEqual(addedKey, '__date_index__');
    assert.equal(Array.from(addedDate.querySelectorAll('input')).find(element => element.name.endsWith('.Kind')).name,
      `Actions[${copiedParentKey}].Dates[${addedKey}].Kind`);
    assert.equal(prototypeKind.name, `Actions[${copiedParentKey}].Dates[__date_index__].Kind`);
    assert.equal(findInput(sourceDate, `${newPrefix}.Dates.index`).value, key);
  }
});

test('[G07] allocator avoids case-only collisions without rewriting opaque keys or reserving other owners', () => {
  for (const { key, marked } of [
    { key: 'RW01', marked: false },
    { key: 'RW01', marked: true },
    { key: '_0_1', marked: false },
    { key: '_0_1', marked: true }
  ]) {
    const { context, document } = loadRuntime();
    context.Date = { now: () => 0 };
    const collection = makeCollectionRoot(document, 'Actions', '__index__',
      (index, isTemplate) => makeSimpleNestedRow(document, 'Actions', index, isTemplate), [key]);
    if (marked) collection.rows[0].setAttribute('data-rw-form-collection-row-state', 'removed');
    const form = document.createElement('form');
    form.appendChild(collection.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    collection.add.dispatchEvent(createEvent('click'));

    const keys = ownedRows(collection.root).map(row => findInput(row, 'Actions.index').value);
    assert.equal(keys.length, 2);
    assert.equal(keys[0], key);
    assert.equal(new Set(keys.map(key => key.toUpperCase())).size, 2);
    assert.equal(keys[1], keys[1].toUpperCase());
    assert.equal(keys[1], keys[1].toLowerCase());
    assert.equal(findInput(collection.rows[0], `Actions[${key}].Text`).name, `Actions[${key}].Text`);
  }

  const { context, document } = loadRuntime();
  context.Date = { now: () => 0 };
  const collection = makeCollectionRoot(document, 'Actions', '__index__',
    (index, isTemplate) => makeSimpleNestedRow(document, 'Actions', index, isTemplate), ['opaque']);
  const sibling = makeCollectionRoot(document, 'OtherActions', '__index__',
    (index, isTemplate) => makeSimpleNestedRow(document, 'OtherActions', index, isTemplate), ['_0_1']);
  const form = document.createElement('form');
  form.append(collection.root, sibling.root);
  document.body.appendChild(form);
  context.window.RazorWire.formInteractionsManager.scan();
  collection.add.dispatchEvent(createEvent('click'));

  assert.deepEqual(ownedRows(collection.root).map(row => findInput(row, 'Actions.index').value), ['opaque', '_0_1']);
  assert.equal(findInput(sibling.rows[0], 'OtherActions.index').value, '_0_1');
});

test('[G07/G12] reentrant commands abort when a listener reserves the generated key with different casing', () => {
  for (const action of ['add', 'duplicate']) {
    const { context, document } = loadRuntime();
    context.Date = { now: () => 0 };
    const collection = makeCollectionRoot(document, 'Actions', '__index__',
      (index, isTemplate) => makeSimpleNestedRow(document, 'Actions', index, isTemplate), ['opaque']);
    const form = document.createElement('form');
    form.appendChild(collection.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    let listenerRow;
    form.addEventListener(`razorwire:form-collection:before-${action}`, event => {
      listenerRow = makeSimpleNestedRow(document, 'Actions', event.detail.index.toUpperCase(), false);
      collection.root.appendChild(listenerRow);
    });
    const command = action === 'add' ? collection.add : ownedCommand(collection.rows[0], 'duplicate');
    command.dispatchEvent(createEvent('click'));

    const rows = ownedRows(collection.root);
    assert.equal(rows.length, 2);
    assert.equal(rows[0], collection.rows[0]);
    assert.equal(rows[1], listenerRow);
    assert.equal(findInput(collection.rows[0], 'Actions.index').value, 'opaque');
    assert.ok(context.window.RazorWire.formInteractionsManager.getDiagnostics()
      .some(diagnostic => /index conflict/.test(diagnostic.message)));
  }
});

test('[G07] allocator handles numeric boundaries and reserves nested descendant keys', () => {
  const cases = [
    { label: 'empty', indices: [], expected: '0' },
    { label: 'full decimal', indices: ['0', '7'], expected: '8' },
    { label: 'leading zeros', indices: ['00', '07'], expected: '8' },
    { label: 'mixed opaque', indices: ['0', 'opaque'], fallback: true },
    { label: 'numeric prefix', indices: ['1x'], fallback: true },
    { label: 'maximum safe integer', indices: [String(Number.MAX_SAFE_INTEGER)], fallback: true },
    { label: 'unsafe input integer', indices: ['9007199254740992'], fallback: true },
    { label: 'decimal overflow', indices: ['9'.repeat(400)], fallback: true },
    { label: 'tombstone reservation', indices: ['0', '7'], tombstone: '7', expected: '8' }
  ];

  for (const allocation of cases) {
    const { context, document } = loadRuntime();
    const collection = makeCollectionRoot(document, 'Actions', '__index__',
      (index, isTemplate) => makeSimpleNestedRow(document, 'Actions', index, isTemplate), allocation.indices);
    if (allocation.tombstone) {
      collection.rows.find(row => row.getAttribute('data-rw-form-index') === allocation.tombstone)
        .setAttribute('data-rw-form-collection-row-state', 'removed');
    }
    const form = document.createElement('form');
    form.appendChild(collection.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    collection.add.dispatchEvent(createEvent('click'));

    const keys = ownedRows(collection.root).map(row => row.querySelectorAll('input').find(input => input.name === 'Actions.index').value);
    const allocated = keys.at(-1);
    assert.ok(allocated, allocation.label);
    assert.equal(new Set(keys).size, keys.length, allocation.label);
    assert.equal(allocation.indices.includes(allocated), false, allocation.label);
    if (allocation.expected) assert.equal(allocated, allocation.expected, allocation.label);
    if (allocation.fallback) assert.equal(/^\d+$/.test(allocated), false, allocation.label);
  }

  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document);
  // Seed two descendant keys in the existing owner; parent duplication reserves both before assigning copies.
  const dates = Array.from(fixture.row.querySelectorAll('[data-rw-form-collection]'))
    .find(root => root.getAttribute('data-rw-form-collection') === 'Actions[0].Dates');
  dates.appendChild(makeDateRow(document, 'Actions[0].Dates', '7', false));
  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  context.window.RazorWire.formInteractionsManager.scan();
  ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));
  const copy = ownedRows(fixture.root).find(row => row !== fixture.row);
  const copiedDates = Array.from(copy.querySelectorAll('[data-rw-form-collection]'))
    .find(root => root.getAttribute('data-rw-form-collection') === `Actions[${copy.getAttribute('data-rw-form-index')}].Dates`);
  const copiedKeys = ownedRows(copiedDates).map(row => row.querySelectorAll('input')
    .find(input => input.name.endsWith('.Dates.index')).value);
  assert.deepEqual(copiedKeys, ['8', '9']);
  assert.deepEqual(ownedRows(dates).map(row => row.querySelectorAll('input')
    .find(input => input.name.endsWith('.Dates.index')).value), ['0', '7']);
});

// Value: protects=owned row carriers and control pairing after omitting a tombstone;
// fails_when=surviving cloned controls or ClientIndex/RowIndex use a different row's key;
// why_new=the existing duplicate cases did not place a tombstone before two live carrier rows;
// seam=none; no_claim=real DOM and MVC binding, which have separate browser proofs.
test('[G12] row carriers stay paired with live controls when an earlier tombstone is omitted', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document, {
    childIndicesByParent: { 0: ['0', '7', '9'] }
  });
  const findInput = (row, name) => Array.from(row.querySelectorAll('input'))
    .find(input => input.name === name);
  const findCollection = (row, name) => Array.from(row.querySelectorAll('[data-rw-form-collection]'))
    .find(root => root.getAttribute('data-rw-form-collection') === name);
  const sourceDatesName = 'Actions[0].Dates';
  const sourceDates = findCollection(fixture.row, sourceDatesName);
  sourceDates.setAttribute('data-rw-form-collection-remove-mode', 'mark');

  findInput(fixture.row, 'Actions[0].ClientIndex').value = '0';
  findInput(fixture.row, 'Actions[0].RowIndex').value = '0';
  const sourceRows = ownedRows(sourceDates);
  for (const row of sourceRows) {
    const key = row.getAttribute('data-rw-form-index');
    const prefix = sourceDatesName + '[' + key + ']';
    findInput(row, sourceDatesName + '.index').value = key;
    findInput(row, prefix + '.ClientIndex').value = key;
    findInput(row, prefix + '.RowIndex').value = key;
    findInput(row, prefix + '.Kind').value = 'source-' + key;
  }
  const sourceSnapshots = sourceRows.map(row => {
    const key = row.getAttribute('data-rw-form-index');
    const prefix = sourceDatesName + '[' + key + ']';
    return {
      marker: findInput(row, sourceDatesName + '.index').value,
      clientIndex: findInput(row, prefix + '.ClientIndex').value,
      rowIndex: findInput(row, prefix + '.RowIndex').value,
      kind: findInput(row, prefix + '.Kind').value
    };
  });

  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  context.window.RazorWire.formInteractionsManager.scan();

  const tombstone = sourceRows[0];
  ownedCommand(tombstone, 'remove').dispatchEvent(createEvent('click'));
  assert.equal(tombstone.getAttribute('data-rw-form-collection-row-state'), 'removed');
  assert.equal(findInput(tombstone, sourceDatesName + '[0].Delete').value, 'true');
  ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

  const copy = ownedRows(fixture.root).find(row => row !== fixture.row);
  const copiedParentKey = copy.getAttribute('data-rw-form-index');
  const copiedParentPrefix = 'Actions[' + copiedParentKey + ']';
  assert.equal(findInput(copy, copiedParentPrefix + '.ClientIndex').value, copiedParentKey);
  assert.equal(findInput(copy, copiedParentPrefix + '.RowIndex').value, copiedParentKey);
  assert.equal(findInput(fixture.row, 'Actions[0].ClientIndex').value, '0');
  assert.equal(findInput(fixture.row, 'Actions[0].RowIndex').value, '0');

  const copiedDatesName = copiedParentPrefix + '.Dates';
  const copiedDates = findCollection(copy, copiedDatesName);
  const copiedRows = ownedRows(copiedDates);
  assert.equal(copiedRows.length, 2);
  const copiedSnapshots = copiedRows.map(row => {
    const key = findInput(row, copiedDatesName + '.index').value;
    const prefix = copiedDatesName + '[' + key + ']';
    return {
      marker: key,
      clientIndex: findInput(row, prefix + '.ClientIndex').value,
      rowIndex: findInput(row, prefix + '.RowIndex').value,
      kind: findInput(row, prefix + '.Kind').value
    };
  });

  assert.deepEqual(copiedSnapshots.map(snapshot => snapshot.kind), ['source-7', 'source-9']);
  assert.equal(copiedSnapshots.some(snapshot => snapshot.kind === 'source-0'), false);
  assert.equal(new Set(copiedSnapshots.map(snapshot => snapshot.marker)).size, 2);
  for (const snapshot of copiedSnapshots) {
    assert.equal(['0', '7', '9'].includes(snapshot.marker), false);
    assert.equal(snapshot.clientIndex, snapshot.marker);
    assert.equal(snapshot.rowIndex, snapshot.marker);
  }
  assert.deepEqual(sourceRows.map(row => {
    const key = row.getAttribute('data-rw-form-index');
    const prefix = sourceDatesName + '[' + key + ']';
    return {
      marker: findInput(row, sourceDatesName + '.index').value,
      clientIndex: findInput(row, prefix + '.ClientIndex').value,
      rowIndex: findInput(row, prefix + '.RowIndex').value,
      kind: findInput(row, prefix + '.Kind').value
    };
  }), sourceSnapshots);
});

test('[G14] recursive duplicate applies control, identity, delete, file, and copyable policies', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document, {
    includeGrandchildren: true,
    grandchildIndicesByDate: { 0: ['0'] }
  });
  const findInput = (root, name) => Array.from(root.querySelectorAll('input')).find(element => element.name === name);
  const dateRoot = Array.from(fixture.row.querySelectorAll('[data-rw-form-collection]'))
    .find(root => root.getAttribute('data-rw-form-collection') === 'Actions[0].Dates');
  const date = ownedRows(dateRoot)[0];
  const reminderRoot = Array.from(date.querySelectorAll('[data-rw-form-collection]'))
    .find(root => root.getAttribute('data-rw-form-collection') === 'Actions[0].Dates[0].Reminders');
  const reminder = ownedRows(reminderRoot)[0];

  findInput(fixture.row, 'Actions[0].Id').value = 'source-action-id';
  findInput(fixture.row, 'Actions[0].ConcurrencyStamp').value = 'source-action-stamp';
  findInput(date, 'Actions[0].Dates[0].Id').value = 'source-date-id';
  findInput(date, 'Actions[0].Dates[0].ConcurrencyToken').value = 'source-date-token';
  findInput(reminder, 'Actions[0].Dates[0].Reminders[0].Id').value = 'source-reminder-id';
  const copyable = hidden(document, 'Actions[0].Related.Id', 'copyable-domain-id');
  copyable.setAttribute('data-rw-form-collection-copyable', 'true');
  fixture.row.appendChild(copyable);
  const customIdentity = hidden(document, 'Actions[0].LegacyRecordKey', 'consumer-owned-key');
  fixture.row.appendChild(customIdentity);

  const title = findInput(fixture.row, 'Actions[0].Title');
  title.value = 'preserved read-only value';
  title.readOnly = true;
  const select = document.createElement('select');
  select.setAttribute('name', 'Actions[0].Dates[0].Schedule');
  select.value = 'late';
  select.selectedIndex = 2;
  date.appendChild(select);
  const checkbox = input(document, 'Actions[0].Dates[0].Enabled', 'checkbox');
  checkbox.checked = true;
  date.appendChild(checkbox);
  const file = input(document, 'Actions[0].Dates[0].Attachment', 'file');
  file.value = 'C:\\fakepath\\evidence.pdf';
  date.appendChild(file);
  const actionDelete = findInput(fixture.row, 'Actions[0].IsDeleted');
  actionDelete.value = 'true';
  const dateDelete = findInput(date, 'Actions[0].Dates[0].Delete');
  dateDelete.value = 'true';
  const dateTemplate = ownedTemplate(dateRoot);
  const prototypeIdentity = Array.from(dateTemplate.content.querySelectorAll('input'))
    .find(element => element.name.endsWith('.ConcurrencyToken'));
  prototypeIdentity.value = 'stale-prototype-token';

  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  form.addEventListener('razorwire:form-collection:before-duplicate', event => {
    const cloneCustomIdentity = Array.from(event.detail.row.querySelectorAll('input'))
      .find(element => element.name.endsWith('.LegacyRecordKey'));
    assert.equal(cloneCustomIdentity.value, 'consumer-owned-key');
    cloneCustomIdentity.value = '';
  });
  context.window.RazorWire.formInteractionsManager.scan();
  ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

  const clone = ownedRows(fixture.root).find(row => row !== fixture.row);
  const cloneDates = Array.from(clone.querySelectorAll('[data-rw-form-collection]'))
    .find(root => root.getAttribute('data-rw-form-collection').endsWith('.Dates'));
  const cloneDate = ownedRows(cloneDates)[0];
  const cloneReminders = Array.from(cloneDate.querySelectorAll('[data-rw-form-collection]'))
    .find(root => root.getAttribute('data-rw-form-collection').endsWith('.Reminders'));
  const cloneReminder = ownedRows(cloneReminders)[0];
  const clonePrefix = clone.getAttribute('data-rw-form-index');
  const cloneDateKey = cloneDate.getAttribute('data-rw-form-index');
  const cloneReminderKey = cloneReminder.getAttribute('data-rw-form-index');

  assert.equal(findInput(fixture.row, 'Actions[0].Id').value, 'source-action-id');
  assert.equal(findInput(fixture.row, 'Actions[0].ConcurrencyStamp').value, 'source-action-stamp');
  assert.equal(findInput(clone, `Actions[${clonePrefix}].Id`).value, '');
  assert.equal(findInput(clone, `Actions[${clonePrefix}].ConcurrencyStamp`).value, '');
  assert.equal(findInput(cloneDate, `Actions[${clonePrefix}].Dates[${cloneDateKey}].Id`).value, '');
  assert.equal(findInput(cloneDate, `Actions[${clonePrefix}].Dates[${cloneDateKey}].ConcurrencyToken`).value, '');
  assert.equal(findInput(cloneReminder, `Actions[${clonePrefix}].Dates[${cloneDateKey}].Reminders[${cloneReminderKey}].Id`).value, '');
  assert.equal(findInput(clone, `Actions[${clonePrefix}].Related.Id`).value, 'copyable-domain-id');
  assert.equal(findInput(clone, `Actions[${clonePrefix}].LegacyRecordKey`).value, '');
  assert.equal(findInput(clone, `Actions[${clonePrefix}].IsDeleted`).value, 'false');
  assert.equal(findInput(cloneDate, `Actions[${clonePrefix}].Dates[${cloneDateKey}].Delete`).value, 'false');
  assert.equal(findInput(clone, `Actions[${clonePrefix}].Title`).value, 'preserved read-only value');
  assert.equal(findInput(clone, `Actions[${clonePrefix}].Title`).readOnly, true);
  const cloneSelect = Array.from(cloneDate.querySelectorAll('select')).find(element => element.name.endsWith('.Schedule'));
  assert.equal(cloneSelect.value, 'late');
  assert.equal(cloneSelect.selectedIndex, 2);
  assert.equal(findInput(cloneDate, `Actions[${clonePrefix}].Dates[${cloneDateKey}].Enabled`).checked, true);
  assert.equal(findInput(cloneDate, `Actions[${clonePrefix}].Dates[${cloneDateKey}].Attachment`).value, '');
  assert.equal(file.value, 'C:\\fakepath\\evidence.pdf');
  const clonePrototypeIdentity = Array.from(ownedTemplate(cloneDates).content.querySelectorAll('input'))
    .find(element => element.name.endsWith('.ConcurrencyToken'));
  assert.equal(clonePrototypeIdentity.value, '');
});

test('[G15] marked nested payload remains submitted while parent duplication omits tombstones', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document, {
    childIndicesByParent: { 0: ['0', '7'] },
    includeGrandchildren: true,
    grandchildIndicesByDate: { 0: ['0', '7'] }
  });
  const findInput = (root, name) => Array.from(root.querySelectorAll('input')).find(element => element.name === name);
  const findCollection = (root, name) => Array.from(root.querySelectorAll('[data-rw-form-collection]'))
    .find(element => element.getAttribute('data-rw-form-collection') === name);
  const dates = findCollection(fixture.row, 'Actions[0].Dates');
  dates.setAttribute('data-rw-form-collection-remove-mode', 'mark');
  const reminderRoot = findCollection(ownedRows(dates)[0], 'Actions[0].Dates[0].Reminders');
  reminderRoot.setAttribute('data-rw-form-collection-remove-mode', 'mark');
  const addReminderDeleteField = root => {
    const prefix = root.getAttribute('data-rw-form-collection');
    for (const row of ownedRows(root)) {
      const key = row.getAttribute('data-rw-form-index');
      const deletion = hidden(document, `${prefix}[${key}].Delete`, 'false');
      deletion.setAttribute('data-reminder-delete', 'true');
      deletion.setAttribute('value', 'false');
      row.appendChild(deletion);
    }
  };
  addReminderDeleteField(reminderRoot);

  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  context.window.RazorWire.formInteractionsManager.scan();
  const markedDate = ownedRows(dates).find(row => row.getAttribute('data-rw-form-index') === '7');
  ownedCommand(markedDate, 'remove').dispatchEvent(createEvent('click'));
  const markedReminder = ownedRows(reminderRoot).find(row => row.getAttribute('data-rw-form-index') === '0');
  ownedCommand(markedReminder, 'remove').dispatchEvent(createEvent('click'));

  const sourceDateMarker = findInput(markedDate, 'Actions[0].Dates.index');
  const sourceDateDelete = findInput(markedDate, 'Actions[0].Dates[7].Delete');
  const sourceReminderMarker = findInput(markedReminder, 'Actions[0].Dates[0].Reminders.index');
  const sourceReminderDelete = findInput(markedReminder, 'Actions[0].Dates[0].Reminders[0].Delete');
  assert.equal(sourceDateMarker.value, '7');
  assert.equal(sourceDateDelete.value, 'true');
  assert.equal(sourceDateMarker.disabled, false);
  assert.equal(sourceDateDelete.disabled, false);
  assert.equal(sourceReminderMarker.value, '0');
  assert.equal(sourceReminderDelete.value, 'true');
  assert.equal(sourceReminderMarker.disabled, false);
  assert.equal(sourceReminderDelete.disabled, false);

  ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));
  const copy = ownedRows(fixture.root).find(row => row !== fixture.row);
  const copiedDates = findCollection(copy, `Actions[${copy.getAttribute('data-rw-form-index')}].Dates`);
  assert.deepEqual(ownedRows(copiedDates).map(row => row.getAttribute('data-rw-form-index')), ['8']);
  const copiedDate = ownedRows(copiedDates)[0];
  const copiedReminderRoot = findCollection(copiedDate,
    `Actions[${copy.getAttribute('data-rw-form-index')}].Dates[8].Reminders`);
  assert.deepEqual(ownedRows(copiedReminderRoot).map(row => row.getAttribute('data-rw-form-index')), ['8']);
  assert.equal(ownedRows(dates).length, 2);
  assert.equal(ownedRows(reminderRoot).length, 2);
});

test('[G16/G17] clone IDs reserve document-wide and rewrite every supported ID reference', () => {
  {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document);
    const sourceTitle = Array.from(fixture.row.querySelectorAll('input')).find(element => element.name === 'Actions[0].Title');
    const sourceTitleId = sourceTitle.id;
    const secondForm = document.createElement('form');
    const collision = document.createElement('span');
    collision.id = 'Actions_1__Title';
    secondForm.appendChild(collision);
    const outsideReference = document.createElement('span');
    outsideReference.id = 'external-status';
    const labelElement = label(document, sourceTitleId);
    fixture.row.appendChild(labelElement);
    const list = document.createElement('datalist');
    list.id = 'schedule-options';
    fixture.row.appendChild(list);
    const error = document.createElement('span');
    error.id = 'schedule-error';
    fixture.row.appendChild(error);
    const controls = document.createElement('div');
    controls.id = 'schedule-controls';
    fixture.row.appendChild(controls);
    const description = document.createElement('span');
    description.id = 'schedule-description';
    fixture.row.appendChild(description);
    const heading = document.createElement('span');
    heading.id = 'schedule-heading';
    fixture.row.appendChild(heading);
    const owner = document.createElement('div');
    owner.id = 'schedule-owner';
    fixture.row.appendChild(owner);
    const listInput = input(document, 'Actions[0].ScheduledAt');
    listInput.setAttribute('list', 'schedule-options');
    listInput.setAttribute('aria-errormessage', 'schedule-error');
    fixture.row.appendChild(listInput);
    const output = document.createElement('output');
    output.setAttribute('for', `${sourceTitleId} external-status`);
    fixture.row.appendChild(output);
    const references = document.createElement('div');
    references.setAttribute('aria-controls', `${sourceTitleId} external-status`);
    references.setAttribute('aria-describedby', `${description.id} external-status`);
    references.setAttribute('aria-labelledby', `${heading.id} external-status`);
    references.setAttribute('aria-owns', `${owner.id} external-status`);
    fixture.row.appendChild(references);
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    form.appendChild(outsideReference);
    document.body.append(form, secondForm);
    context.window.RazorWire.formInteractionsManager.scan();
    ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

    const clone = ownedRows(fixture.root).find(row => row !== fixture.row);
    const cloneTitle = Array.from(clone.querySelectorAll('input')).find(element => element.name === 'Actions[1].Title');
    assert.notEqual(cloneTitle.id, sourceTitleId);
    assert.notEqual(cloneTitle.id, collision.id);
    assert.equal(sourceTitle.id, sourceTitleId);
    const cloneLabel = clone.querySelector('label');
    assert.equal(cloneLabel.getAttribute('for'), cloneTitle.id);
    const cloneList = Array.from(clone.querySelectorAll('[id]')).find(element => element.id.startsWith('schedule-options'));
    const cloneError = Array.from(clone.querySelectorAll('[id]')).find(element => element.id.startsWith('schedule-error'));
    const cloneDescription = Array.from(clone.querySelectorAll('[id]')).find(element => element.id.startsWith('schedule-description'));
    const cloneHeading = Array.from(clone.querySelectorAll('[id]')).find(element => element.id.startsWith('schedule-heading'));
    const cloneOwner = Array.from(clone.querySelectorAll('[id]')).find(element => element.id.startsWith('schedule-owner'));
    const cloneListInput = Array.from(clone.querySelectorAll('input')).find(element => element.name === 'Actions[1].ScheduledAt');
    assert.equal(cloneListInput.getAttribute('list'), cloneList.id);
    assert.equal(cloneListInput.getAttribute('aria-errormessage'), cloneError.id);
    const cloneOutput = clone.querySelector('output');
    assert.deepEqual(cloneOutput.getAttribute('for').split(' '), [cloneTitle.id, 'external-status']);
    assert.deepEqual(referencesIn(clone, 'aria-controls'), [cloneTitle.id, 'external-status']);
    assert.deepEqual(referencesIn(clone, 'aria-describedby'), [cloneDescription.id, 'external-status']);
    assert.deepEqual(referencesIn(clone, 'aria-labelledby'), [cloneHeading.id, 'external-status']);
    assert.deepEqual(referencesIn(clone, 'aria-owns'), [cloneOwner.id, 'external-status']);
    const documentIds = Array.from(document.querySelectorAll('[id]')).map(element => element.id).filter(Boolean);
    assert.equal(new Set(documentIds).size, documentIds.length);
    assert.equal(collision.id, 'Actions_1__Title');
  }

  {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document);
    const first = document.createElement('span');
    const second = document.createElement('span');
    first.id = 'ambiguous-private-source-id';
    second.id = 'ambiguous-private-source-id';
    fixture.row.append(first, second);
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    context.window.RazorWire.formInteractionsManager.clearDiagnostics();
    ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

    assert.equal(ownedRows(fixture.root).length, 1);
    assert.equal(fixture.row.querySelectorAll('[id]').filter(element => element.id === 'ambiguous-private-source-id').length, 2);
    const diagnostic = context.window.RazorWire.formInteractionsManager.getDiagnostics()
      .find(item => item.message.includes('duplicate source IDs'));
    assert.ok(diagnostic);
    assert.equal(diagnostic.message.includes('ambiguous-private-source-id'), false);
    assert.equal(diagnostic.message.includes('Actions[0]'), false);
    assert.equal(diagnostic.impact, 'Aborted; values retained.');
  }
});

test('[G20] nested template token text cannot replace an owned marker or correctly prefixed field', () => {
  {
    const { context, document } = loadRuntime();
    const fixture = buildCollection(document);
    fixture.template.content.querySelector('input[name="Actions.index"]').remove();
    // The root-level legacy fallback is allowed when the template has a real owned field.
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    fixture.add.dispatchEvent(createEvent('click'));
    const added = ownedRows(fixture.root)[1];
    assert.equal(added.querySelector('input[name="Actions.index"]').value, '1');
    assert.equal(added.querySelector('input[name="Actions[1].Title"]').name, 'Actions[1].Title');
  }

  for (const malformed of ['missing-marker', 'wrong-prefix']) {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document);
    const dates = fixture.row.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
    const template = ownedTemplate(dates);
    const prototype = template.content.querySelector('[data-rw-form-collection-row]');
    if (malformed === 'missing-marker') {
      prototype.querySelector('input[name="Actions[0].Dates.index"]').remove();
      template.innerHTML = '<!-- __date_index__ -->';
    } else {
      for (const control of prototype.querySelectorAll('input')) {
        if (control.name.startsWith('Actions[0].Dates[__date_index__]')) control.name = control.name.replace('Actions[0].Dates[__date_index__]', 'Elsewhere[__date_index__]');
      }
      template.innerHTML = 'text containing __date_index__';
    }
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    context.window.RazorWire.formInteractionsManager.scan();
    context.window.RazorWire.formInteractionsManager.clearDiagnostics();
    dates.querySelector('[data-rw-form-collection-add]').dispatchEvent(createEvent('click'));

    assert.equal(ownedRows(dates).length, 1, malformed);
    const diagnostic = context.window.RazorWire.formInteractionsManager.getDiagnostics()
      .find(item => /template binding:.*missing/.test(item.message) && item.message.includes('Actions[0].Dates'));
    assert.ok(diagnostic, malformed);
    assert.equal(diagnostic.impact, 'Aborted; values retained.', malformed);
    assert.equal(diagnostic.docs, 'Web/ForgeTrust.RazorWire/Docs/form-interactions.md#troubleshooting', malformed);
  }
});

test('[G21] duplicate aborts when before-duplicate changes a planned field binding name', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document);
  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  let detachedClone;
  let afterDuplicate = 0;
  form.addEventListener('razorwire:form-collection:before-duplicate', event => {
    detachedClone = event.detail.row;
    const title = Array.from(detachedClone.querySelectorAll('input')).find(input => input.name.endsWith('.Title'));
    title.name = 'Elsewhere[1].Title';
  });
  form.addEventListener('razorwire:form-collection:duplicate', () => afterDuplicate++);
  context.window.RazorWire.formInteractionsManager.scan();
  ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

  assert.equal(ownedRows(fixture.root).length, 1);
  assert.equal(detachedClone.querySelector('input[name="Elsewhere[1].Title"]').name, 'Elsewhere[1].Title');
  assert.equal(afterDuplicate, 0);
  assert.ok(context.window.RazorWire.formInteractionsManager.getDiagnostics()
    .some(item => /ownership changed|binding paths changed|markers changed/.test(item.message)
      && item.impact === 'Aborted; values retained.'));
});

test('[G21] duplicate aborts when before-duplicate changes an owned collection root', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document);
  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  let detachedClone;
  let afterDuplicate = 0;
  form.addEventListener('razorwire:form-collection:before-duplicate', event => {
    detachedClone = event.detail.row;
    detachedClone.querySelector('[data-rw-form-collection="Actions[1].Dates"]')
      .setAttribute('data-rw-form-collection', 'Elsewhere[1].Dates');
  });
  form.addEventListener('razorwire:form-collection:duplicate', () => afterDuplicate++);
  context.window.RazorWire.formInteractionsManager.scan();
  ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

  assert.equal(ownedRows(fixture.root).length, 1);
  assert.ok(detachedClone.querySelector('[data-rw-form-collection="Elsewhere[1].Dates"]'));
  assert.equal(afterDuplicate, 0);
  assert.ok(context.window.RazorWire.formInteractionsManager.getDiagnostics()
    .some(item => /ownership changed|binding paths changed|markers changed/.test(item.message)
      && item.impact === 'Aborted; values retained.'));
});

test('[G21] add and duplicate abort when a listener corrupts an inert descendant marker', () => {
  // Value: protects=usable descendant templates after the cancelable preparation event;
  // fails_when=a malformed prototype marker is inserted or listener edits are rolled back;
  // why_new=existing interference tests covered binding names and live markers only;
  // seam=none; no_claim=browser focus or MVC binding, which have separate integration proofs.
  for (const action of ['add', 'duplicate']) {
    for (const depth of [2, 3]) {
      for (const surface of ['clone', 'source']) {
        for (const mutation of ['value', 'disabled', 'type']) {
          const { context, document } = loadRuntime();
          const fixture = buildNestedFixture(document, { includeGrandchildren: true });
          const form = document.createElement('form');
          form.appendChild(fixture.root);
          document.body.appendChild(form);
          let detachedRow;
          let changedMarker;
          let after = 0;
          form.addEventListener(`razorwire:form-collection:before-${action}`, event => {
            detachedRow = event.detail.row;
            const inspectedRow = surface === 'clone' ? detachedRow : action === 'add' ? fixture.template.content.firstElementChild : fixture.row;
            const dates = inspectedRow.querySelector('[data-rw-form-collection]');
            let prototype = ownedTemplate(dates).content;
            if (depth === 3) prototype = ownedTemplate(prototype.querySelector('[data-rw-form-collection]')).content;
            changedMarker = prototype.querySelectorAll('input').find(input => input.name.endsWith('.index'));
            assert.ok(changedMarker);
            if (mutation === 'value') changedMarker.value = 'listener-changed-marker';
            else if (mutation === 'disabled') changedMarker.disabled = true;
            else changedMarker.type = 'text';
          });
          form.addEventListener(`razorwire:form-collection:${action}`, () => after++);
          context.window.RazorWire.formInteractionsManager.scan();
          context.window.RazorWire.formInteractionsManager.clearDiagnostics();
          const sourceSnapshot = () => fixture.row.querySelectorAll('input').map(input => ({ name: input.name, value: input.value, type: input.type, disabled: input.disabled }));
          const sourceBefore = sourceSnapshot();
          const command = action === 'add' ? fixture.add : ownedCommand(fixture.row, 'duplicate');
          command.focus();
          command.dispatchEvent(createEvent('click'));

          const scenario = `${action}/${surface}/${depth}/${mutation}`;
          assert.equal(ownedRows(fixture.root).length, 1, scenario);
          assert.equal(detachedRow.isConnected, false, scenario);
          assert.deepEqual(sourceSnapshot(), sourceBefore, scenario);
          assert.equal(after, 0, scenario);
          assert.equal(fixture.status.textContent, '', scenario);
          assert.equal(document.activeElement, command, scenario);
          if (mutation === 'value') assert.equal(changedMarker.value, 'listener-changed-marker', scenario);
          else if (mutation === 'disabled') assert.equal(changedMarker.disabled, true, scenario);
          else assert.equal(changedMarker.type, 'text', scenario);
          assert.ok(context.window.RazorWire.formInteractionsManager.getDiagnostics()
            .some(diagnostic => /template binding:.*missing/.test(diagnostic.message)
              && diagnostic.impact === 'Aborted; values retained.'
              && diagnostic.fix === 'Match template token and marker.'
              && diagnostic.docs === 'Web/ForgeTrust.RazorWire/Docs/form-interactions.md#troubleshooting'), scenario);
        }
      }
    }
  }
});

test('[G21] add aborts when a listener replaces its template or source lineage', () => {
  // Value: protects=the current authored Add source; fails_when=an invalidated template inserts stale markup;
  // why_new=earlier ownership tests changed live roots and detached bindings only; seam=none.
  for (const mutation of ['remove-template', 'replace-template', 'replace-source']) {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document);
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    const source = fixture.template.content.firstElementChild;
    let replacement;
    let detachedRow;
    let after = 0;
    form.addEventListener('razorwire:form-collection:before-add', event => {
      detachedRow = event.detail.row;
      if (mutation === 'replace-source') {
        replacement = source.cloneNode(true);
        source.remove();
        fixture.template.content.appendChild(replacement);
      } else {
        fixture.template.remove();
        if (mutation === 'replace-template') {
          replacement = fixture.template.cloneNode(true);
          fixture.root.appendChild(replacement);
        }
      }
    });
    form.addEventListener('razorwire:form-collection:add', () => after++);
    context.window.RazorWire.formInteractionsManager.scan();
    context.window.RazorWire.formInteractionsManager.clearDiagnostics();
    fixture.add.focus();
    fixture.add.dispatchEvent(createEvent('click'));
    assert.equal(ownedRows(fixture.root).length, 1, mutation);
    assert.equal(detachedRow.isConnected, false, mutation);
    assert.equal(after, 0, mutation);
    assert.equal(fixture.status.textContent, '', mutation);
    assert.equal(document.activeElement, fixture.add, mutation);
    if (mutation === 'replace-source') {
      assert.equal(source.parentNode, null);
      assert.equal(fixture.template.content.firstElementChild, replacement);
    } else {
      assert.equal(fixture.template.isConnected, false);
      assert.equal(ownedTemplate(fixture.root), replacement);
    }
    assert.ok(context.window.RazorWire.formInteractionsManager.getDiagnostics().some(diagnostic =>
      diagnostic.message === 'Collection "Actions" ownership changed.'
      && diagnostic.impact === 'Aborted; values retained.' && diagnostic.fix === 'Keep owners stable.'
      && diagnostic.docs === 'Web/ForgeTrust.RazorWire/Docs/form-interactions.md#troubleshooting'), mutation);
  }
});

test('[G21] duplicate aborts when a listener moves a descendant owner or its direct parent', () => {
  // Value: protects=live descendant ownership at insertion; fails_when=containment alone accepts a moved owner;
  // why_new=prior checks retained names and keys without checking row/parent lineage; seam=none.
  for (const mutation of ['row-owner', 'direct-parent']) {
    const { context, document } = loadRuntime();
    const fixture = buildNestedFixture(document, {
      childIndicesByParent: { 0: ['0', '7'] }, includeGrandchildren: true,
      grandchildIndicesByDate: { 0: ['0'] }
    });
    const form = document.createElement('form');
    form.appendChild(fixture.root);
    document.body.appendChild(form);
    const dates = fixture.row.querySelector('[data-rw-form-collection="Actions[0].Dates"]');
    const dateRows = ownedRows(dates);
    const reminder = dateRows[0].querySelector('[data-rw-form-collection]');
    const title = fixture.row.querySelector('input[name="Actions[0].Title"]');
    title.value = 'Original action';
    let newParent;
    let detachedRow;
    let after = 0;
    form.addEventListener('razorwire:form-collection:before-duplicate', event => {
      detachedRow = event.detail.row;
      if (mutation === 'row-owner') newParent = dateRows[1];
      else {
        newParent = document.createElement('div');
        dateRows[0].appendChild(newParent);
      }
      newParent.appendChild(reminder);
    });
    form.addEventListener('razorwire:form-collection:duplicate', () => after++);
    context.window.RazorWire.formInteractionsManager.scan();
    context.window.RazorWire.formInteractionsManager.clearDiagnostics();
    const command = ownedCommand(fixture.row, 'duplicate');
    command.focus();
    command.dispatchEvent(createEvent('click'));
    assert.equal(ownedRows(fixture.root).length, 1, mutation);
    assert.equal(detachedRow.isConnected, false, mutation);
    assert.equal(reminder.parentElement, newParent, mutation);
    assert.equal(reminder.getAttribute('data-rw-form-collection'), 'Actions[0].Dates[0].Reminders');
    assert.equal(reminder.closest('[data-rw-form-collection-row]'), mutation === 'row-owner' ? dateRows[1] : dateRows[0]);
    assert.equal(title.value, 'Original action');
    assert.equal(after, 0, mutation);
    assert.equal(fixture.status.textContent, '', mutation);
    assert.equal(document.activeElement, command, mutation);
    assert.ok(context.window.RazorWire.formInteractionsManager.getDiagnostics().some(diagnostic =>
      diagnostic.message === 'Collection "Actions" ownership changed.'
      && diagnostic.impact === 'Aborted; values retained.' && diagnostic.fix === 'Keep owners stable.'
      && diagnostic.docs === 'Web/ForgeTrust.RazorWire/Docs/form-interactions.md#troubleshooting'), mutation);
  }
});

test('[G22] physical remove event reaches listeners through its connected collection owner', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document);
  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  let afterRemove;
  form.addEventListener('razorwire:form-collection:remove', event => { afterRemove = event; });
  context.window.RazorWire.formInteractionsManager.scan();
  ownedCommand(fixture.row, 'remove').dispatchEvent(createEvent('click'));

  assert.ok(afterRemove);
  assert.equal(afterRemove.target, fixture.root);
  assert.equal(afterRemove.detail.root, fixture.root);
  assert.equal(afterRemove.detail.row, fixture.row);
  assert.equal(fixture.row.isConnected, false);
  assert.equal(fixture.root.isConnected, true);
});

test('[G23] mark removal requires its selected delete field to be a hidden input', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document);
  fixture.root.setAttribute('data-rw-form-collection-remove-mode', 'mark');
  const title = fixture.row.querySelector('input[name="Actions[0].Title"]');
  const deleteField = fixture.row.querySelector('input[name="Actions[0].IsDeleted"]');
  deleteField.removeAttribute('data-action-delete');
  title.setAttribute('data-action-delete', 'true');
  title.value = 'keep this value';
  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  let afterRemove = 0;
  form.addEventListener('razorwire:form-collection:remove', () => afterRemove++);
  context.window.RazorWire.formInteractionsManager.scan();
  ownedCommand(fixture.row, 'remove').dispatchEvent(createEvent('click'));

  assert.equal(fixture.row.hidden, false);
  assert.equal(fixture.row.getAttribute('data-rw-form-collection-row-state'), null);
  assert.equal(title.value, 'keep this value');
  assert.equal(title.disabled, false);
  assert.equal(deleteField.value, 'false');
  assert.equal(afterRemove, 0);
  assert.ok(context.window.RazorWire.formInteractionsManager.getDiagnostics()
    .some(item => /no app-owned delete field/.test(item.message) && item.impact === 'Aborted; values retained.'));
});

test('[G08/G13] parent Add preserves seeded child keys and clears textarea, radio and select values', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document, { parentTemplateChildIndices: ['seeded'] });
  const prototype = fixture.template.content.querySelector('[data-rw-form-collection-row]');
  const textarea = document.createElement('textarea');
  textarea.setAttribute('name', 'Actions[__index__].Notes');
  textarea.value = 'prototype notes';
  const radio = input(document, 'Actions[__index__].Selected', 'radio');
  radio.checked = true;
  const select = document.createElement('select');
  select.setAttribute('name', 'Actions[__index__].Choice');
  select.selectedIndex = 2;
  select.value = 'prototype choice';
  prototype.append(textarea, radio, select);
  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  context.window.RazorWire.formInteractionsManager.scan();
  fixture.add.dispatchEvent(createEvent('click'));

  const added = ownedRows(fixture.root)[1];
  const dates = findCollection(added, 'Actions[1].Dates');
  assert.deepEqual(ownedRows(dates).map(row => row.getAttribute('data-rw-form-index')), ['seeded']);
  assert.equal(findInput(ownedRows(dates)[0], 'Actions[1].Dates.index').value, 'seeded');
  assert.equal(added.querySelector('textarea').value, '');
  assert.equal(findInput(added, 'Actions[1].Selected').checked, false);
  assert.equal(added.querySelector('select').selectedIndex, -1);
  assert.ok(ownedTemplate(dates).content.querySelector('input[name="Actions[1].Dates[__date_index__].Kind"]'));
});

test('[G13] parent Duplicate copies edited textarea and radio state without changing its source', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document);
  const textarea = document.createElement('textarea');
  textarea.setAttribute('name', 'Actions[0].Notes');
  textarea.value = 'live notes';
  const radio = input(document, 'Actions[0].Selected', 'radio');
  radio.checked = true;
  fixture.row.append(textarea, radio);
  const form = document.createElement('form');
  form.appendChild(fixture.root);
  document.body.appendChild(form);
  context.window.RazorWire.formInteractionsManager.scan();
  ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));

  const copied = ownedRows(fixture.root)[1];
  assert.equal(copied.querySelector('textarea').name, 'Actions[1].Notes');
  assert.equal(copied.querySelector('textarea').value, 'live notes');
  assert.equal(findInput(copied, 'Actions[1].Selected').checked, true);
  assert.equal(textarea.value, 'live notes');
  assert.equal(radio.checked, true);
});

test('[G22] toggle refresh never restores editable descendants beneath a marked parent', () => {
  const { context, document } = loadRuntime();
  const fixture = buildNestedFixture(document);
  fixture.root.setAttribute('data-rw-form-collection-remove-mode', 'mark');
  const dates = findCollection(fixture.row, 'Actions[0].Dates');
  const date = ownedRows(dates)[0];
  const editable = findInput(date, 'Actions[0].Dates[0].Kind');
  const target = document.createElement('fieldset');
  target.setAttribute('data-rw-form-toggle-target', 'date-details');
  target.setAttribute('data-rw-form-toggle-disable-when-hidden', 'true');
  target.appendChild(editable);
  date.appendChild(target);
  const toggle = document.createElement('input');
  toggle.type = 'checkbox';
  toggle.checked = true;
  toggle.setAttribute('data-rw-form-toggle', 'date-details');
  toggle.setAttribute('data-rw-form-toggle-invert', 'true');
  const form = document.createElement('form');
  form.append(toggle, fixture.root);
  document.body.appendChild(form);
  const manager = context.window.RazorWire.formInteractionsManager;
  manager.scan();
  assert.equal(editable.getAttribute('data-rw-disabled-by-form-toggle'), 'true');

  ownedCommand(fixture.row, 'remove').dispatchEvent(createEvent('click'));
  toggle.checked = false;
  toggle.dispatchEvent(createEvent('change'));
  manager.scan();

  assert.equal(fixture.row.hidden, true);
  assert.equal(editable.disabled, true);
  assert.equal(findInput(date, 'Actions[0].Dates.index').disabled, false);
  assert.equal(findInput(fixture.row, 'Actions[0].IsDeleted').value, 'true');
  const count = ownedRows(dates).length;
  dates.querySelector('[data-rw-form-collection-add]').dispatchEvent(createEvent('click'));
  assert.equal(ownedRows(dates).length, count);
});

test('[G24] late clone failures identify owner, cause, impact, fix and docs without editable values', () => {
  for (const cause of ['ownership changed', 'markers changed', 'index conflict', 'ID conflict']) {
    for (const developmentDiagnostics of [false, true]) {
      const { context, document } = loadRuntime({ developmentDiagnostics });
      const warnings = [];
      context.console = { warn: message => warnings.push(message) };
      const fixture = buildNestedFixture(document);
      const form = document.createElement('form');
      const otherForm = document.createElement('form');
      form.appendChild(fixture.root);
      document.body.append(form, otherForm);
      const manager = context.window.RazorWire.formInteractionsManager;
      manager.scan();
      manager.clearDiagnostics();
      warnings.length = 0;
      const title = findInput(fixture.row, 'Actions[0].Title');
      title.value = 'private editable fixture value';
      const initialStatus = fixture.status.textContent;
      let after = 0;
      form.addEventListener('razorwire:form-collection:duplicate', () => after++);
      form.addEventListener('razorwire:form-collection:before-duplicate', event => {
        title.value = 'listener retains private fixture value';
        if (cause === 'ownership changed') otherForm.appendChild(fixture.root);
        if (cause === 'markers changed') findInput(event.detail.row, 'Actions.index').value = 'changed-by-listener';
        if (cause === 'index conflict') fixture.root.appendChild(makeActionRow(document, '1'));
        if (cause === 'ID conflict') {
          const collision = document.createElement('span');
          collision.id = findInput(event.detail.row, 'Actions[1].Title').id;
          otherForm.appendChild(collision);
        }
      });
      ownedCommand(fixture.row, 'duplicate').dispatchEvent(createEvent('click'));
      const diagnostic = manager.getDiagnostics().find(item => item.message.includes(cause));
      assert.ok(diagnostic, cause);
      assert.match(diagnostic.message, /Collection "Actions"/);
      assert.equal(diagnostic.impact, 'Aborted; values retained.');
      assert.ok(diagnostic.fix.length > 0);
      assert.equal(diagnostic.docs, 'Web/ForgeTrust.RazorWire/Docs/form-interactions.md#troubleshooting');
      assert.equal(JSON.stringify(diagnostic).includes('private fixture value'), false);
      assert.equal(after, 0);
      assert.equal(fixture.status.textContent, initialStatus);
      assert.equal(title.value, 'listener retains private fixture value');
      assert.equal(warnings.length, developmentDiagnostics ? 1 : 0);
    }
  }
});

function findInput(root, name) {
  return Array.from(root.querySelectorAll('input')).find(element => element.name === name);
}

function findCollection(root, name) {
  return Array.from(root.querySelectorAll('[data-rw-form-collection]'))
    .find(element => element.getAttribute('data-rw-form-collection') === name);
}

function referencesIn(root, attribute) {
  const element = Array.from(root.querySelectorAll('*')).find(candidate => candidate.hasAttribute(attribute));
  return element.getAttribute(attribute).split(/\s+/);
}

function ownedTemplate(root) {
  return Array.from(root.querySelectorAll('template'))
    .find(template => template.closest('[data-rw-form-collection]') === root);
}

function rebaseOwnedPath(root, oldPrefix, newPrefix) {
  const visit = element => {
    for (const attribute of ['name', 'data-valmsg-for', 'data-rw-form-collection']) {
      const value = element.getAttribute(attribute);
      if (value === oldPrefix || value?.startsWith(`${oldPrefix}.`) || value?.startsWith(`${oldPrefix}[`)) {
        element.setAttribute(attribute, newPrefix + value.slice(oldPrefix.length));
      }
    }
    if (element.localName === 'template') {
      for (const child of element.content.children) visit(child);
    }
    for (const child of element.children) visit(child);
  };
  visit(root);
}

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

function buildNestedFixture(document, {
  parentIndices = ['0'],
  childIndicesByParent = { 0: ['0'] },
  parentTemplateChildIndices = [],
  childIndexToken = '__date_index__',
  includeGrandchildren = false,
  grandchildIndicesByDate = {}
} = {}) {
  const makeAction = (index, isTemplate) => makeActionRow(document, index, isTemplate, {
    childIndices: isTemplate ? parentTemplateChildIndices : childIndicesByParent[index] ?? [],
    childIndexToken,
    includeGrandchildren,
    grandchildIndicesByDate
  });
  const collection = makeCollectionRoot(document, 'Actions', '__index__', makeAction, parentIndices, {
    label: 'action',
    deleteSelector: '[data-action-delete]'
  });
  return { ...collection, row: collection.rows[0] ?? null };
}

function makeCollectionRoot(document, name, indexToken, makeRow, rowIndices, { label: collectionLabel = name, deleteSelector } = {}) {
  const root = document.createElement('div');
  root.setAttribute('data-rw-form-collection', name);
  root.setAttribute('data-rw-form-collection-label', collectionLabel);
  if (indexToken !== null) root.setAttribute('data-rw-form-collection-index-token', indexToken);
  if (deleteSelector) root.setAttribute('data-rw-form-collection-delete-field', deleteSelector);

  const rows = rowIndices.map(index => makeRow(index, false));
  root.append(...rows);

  const template = document.createElement('template');
  template.setAttribute('data-rw-form-collection-template', 'true');
  template.innerHTML = `${name}[${indexToken}].Value`;
  template.content.appendChild(makeRow(indexToken, true));
  const add = document.createElement('button');
  add.setAttribute('data-rw-form-collection-add', 'true');
  const status = document.createElement('span');
  status.setAttribute('data-rw-form-collection-status', 'true');
  status.setAttribute('aria-live', 'polite');
  root.append(template, add, status);
  return { root, rows, template, add, status };
}

function makeActionRow(document, index, isTemplate, {
  childIndices = [],
  childIndexToken = '__date_index__',
  includeGrandchildren = false,
  grandchildIndicesByDate = {}
} = {}) {
  const row = makeCollectionRow(document, 'Actions', index, isTemplate);
  row.append(
    hidden(document, `Actions[${index}].Id`, 'action-id'),
    hidden(document, `Actions[${index}].ConcurrencyStamp`, 'action-stamp'),
    hidden(document, `Actions[${index}].ClientIndex`, index),
    hidden(document, `Actions[${index}].RowIndex`, index),
    input(document, `Actions[${index}].Title`, 'text', `Actions_${index}__Title`),
    hidden(document, `Actions[${index}].IsDeleted`, 'false'),
  );
  row.querySelector(`input[name="Actions[${index}].IsDeleted"]`).setAttribute('data-action-delete', 'true');

  const childPath = `Actions[${index}].Dates`;
  const dates = makeCollectionRoot(document, childPath, childIndexToken,
    (childIndex, childIsTemplate) => makeDateRow(document, childPath, childIndex, childIsTemplate, {
      includeGrandchildren,
      grandchildIndices: grandchildIndicesByDate[childIndex] ?? []
    }), childIndices, { label: 'date', deleteSelector: '[data-date-delete]' });
  row.appendChild(dates.root);

  const duplicate = document.createElement('button');
  duplicate.setAttribute('data-rw-form-collection-duplicate', 'true');
  const remove = document.createElement('button');
  remove.setAttribute('data-rw-form-collection-remove', 'true');
  row.append(duplicate, remove);
  return row;
}

function makeDateRow(document, collection, index, isTemplate, { includeGrandchildren = false, grandchildIndices = [] } = {}) {
  const row = makeCollectionRow(document, collection, index, isTemplate);
  row.append(
    hidden(document, `${collection}[${index}].Id`, 'date-id'),
    hidden(document, `${collection}[${index}].ConcurrencyToken`, 'date-token'),
    hidden(document, `${collection}[${index}].ClientIndex`, index),
    hidden(document, `${collection}[${index}].RowIndex`, index),
    input(document, `${collection}[${index}].Kind`, 'text', `${collection.replaceAll(/[^A-Za-z0-9]/g, '_')}_${index}__Kind`),
    hidden(document, `${collection}[${index}].Delete`, 'false'),
  );
  row.querySelector(`input[name="${collection}[${index}].Delete"]`).setAttribute('data-date-delete', 'true');

  if (includeGrandchildren) {
    const grandchildPath = `${collection}[${index}].Reminders`;
    const reminders = makeCollectionRoot(document, grandchildPath, '__reminder_index__',
      (grandchildIndex, grandchildIsTemplate) => makeSimpleNestedRow(document, grandchildPath, grandchildIndex, grandchildIsTemplate),
      grandchildIndices, { label: 'reminder', deleteSelector: '[data-reminder-delete]' });
    row.appendChild(reminders.root);
  }

  const duplicate = document.createElement('button');
  duplicate.setAttribute('data-rw-form-collection-duplicate', 'true');
  const remove = document.createElement('button');
  remove.setAttribute('data-rw-form-collection-remove', 'true');
  row.append(duplicate, remove);
  return row;
}

function makeSimpleNestedRow(document, collection, index, isTemplate) {
  const row = makeCollectionRow(document, collection, index, isTemplate);
  row.append(
    hidden(document, `${collection}[${index}].Id`, 'reminder-id'),
    input(document, `${collection}[${index}].Text`, 'text')
  );
  const duplicate = document.createElement('button');
  duplicate.setAttribute('data-rw-form-collection-duplicate', 'true');
  const remove = document.createElement('button');
  remove.setAttribute('data-rw-form-collection-remove', 'true');
  row.append(duplicate, remove);
  return row;
}

function makeCollectionRow(document, collection, index, isTemplate) {
  const row = document.createElement('fieldset');
  row.setAttribute('data-rw-form-collection-row', 'true');
  if (!isTemplate) row.setAttribute('data-rw-form-index', index);
  row.appendChild(hidden(document, `${collection}.index`, index));
  return row;
}

function ownedRows(root) {
  return Array.from(root.querySelectorAll('[data-rw-form-collection-row]'))
    .filter(row => row.closest('[data-rw-form-collection]') === root);
}

function ownedCommand(row, action) {
  return Array.from(row.querySelectorAll(`[data-rw-form-collection-${action}]`))
    .find(command => command.closest('[data-rw-form-collection-row]') === row);
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

function loadRuntime({ developmentDiagnostics = true } = {}) {
  const document = new FakeDocument();
  const window = {
    RazorWire: { config: { developmentDiagnostics } },
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
    HTMLInputElement: FakeInputElement,
    HTMLSelectElement: FakeSelectElement,
    HTMLTextAreaElement: FakeTextAreaElement,
    HTMLButtonElement: FakeButtonElement,
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
    this.documentElement.isConnected = true;
    this.body = new FakeElement('body', this);
    this.documentElement.appendChild(this.body);
    this.activeElement = null;
    this.listeners = new Map();
  }

  createElement(tagName) {
    switch (tagName.toLowerCase()) {
      case 'template': return new FakeTemplateElement(this);
      case 'input': return new FakeInputElement(this);
      case 'select': return new FakeSelectElement(this);
      case 'textarea': return new FakeTextAreaElement(this);
      case 'button': return new FakeButtonElement(this);
      default: return new FakeElement(tagName, this);
    }
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
    this.parentNode = null;
    this.isConnected = false;
  }

  get parentElement() {
    return null;
  }

  get firstElementChild() {
    return this.children[0] ?? null;
  }

  appendChild(node) {
    if (node.parentNode?.children) {
      node.parentNode.children = node.parentNode.children.filter(child => child !== node);
    }
    node.parentNode = this;
    node.ownerDocument = this.ownerDocument;
    setConnected(node, false);
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
    this.parentNode = null;
    this.listeners = new Map();
    this.textContent = '';
    this.isConnected = false;
    this.hidden = false;
    this.disabled = false;
    this.value = '';
    this.checked = false;
    this.type = tagName.toLowerCase() === 'button' ? 'submit' : 'text';
    this.name = '';
    this.selectedIndex = 0;
    this.readOnly = false;
  }

  get parentElement() {
    return this.parentNode instanceof FakeElement ? this.parentNode : null;
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
    if (name === 'disabled') this.disabled = true;
    if (name === 'readonly') this.readOnly = true;
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
    if (name === 'disabled') this.disabled = false;
    if (name === 'readonly') this.readOnly = false;
  }

  hasAttribute(name) {
    return this._attributes.has(name) || (name === 'id' && Boolean(this.idValue));
  }

  append(...nodes) {
    for (const node of nodes) this.appendChild(node);
  }

  appendChild(node) {
    if (node instanceof FakeFragment) {
      for (const child of [...node.children]) this.appendChild(child);
      return node;
    }
    if (node.parentNode?.children) {
      node.parentNode.children = node.parentNode.children.filter(child => child !== node);
    }
    node.parentNode = this;
    node.ownerDocument = this.ownerDocument;
    setConnected(node, this.isConnected);
    this.children.push(node);
    return node;
  }

  insertAdjacentElement(position, element) {
    if (!this.parentElement || (position !== 'afterend' && position !== 'beforebegin' && position !== 'afterbegin')) {
      return this.appendChild(element);
    }

    if (position === 'afterbegin') {
      if (element.parentNode?.children) element.parentNode.children = element.parentNode.children.filter(child => child !== element);
      element.parentNode = this;
      element.ownerDocument = this.ownerDocument;
      setConnected(element, this.isConnected);
      this.children.unshift(element);
      return element;
    }

    const siblings = this.parentNode.children;
    const index = siblings.indexOf(this);
    if (element.parentNode?.children) element.parentNode.children = element.parentNode.children.filter(child => child !== element);
    element.parentNode = this.parentNode;
    element.ownerDocument = this.ownerDocument;
    setConnected(element, this.parentNode.isConnected);
    siblings.splice(position === 'beforebegin' ? index : index + 1, 0, element);
    return element;
  }

  remove() {
    if (this.parentNode?.children) {
      this.parentNode.children = this.parentNode.children.filter(child => child !== this);
    }
    this.parentNode = null;
    setConnected(this, false);
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
    const clone = this.ownerDocument.createElement(this.localName);
    for (const { name, value } of this.attributes) clone.setAttribute(name, value);
    clone.textContent = this.textContent;
    clone.hidden = this.hidden;
    clone.disabled = this.disabled;
    clone.value = this.value;
    clone.checked = this.checked;
    clone.type = this.type;
    clone.name = this.name;
    clone.selectedIndex = this.selectedIndex;
    clone.readOnly = this.readOnly;
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

class FakeInputElement extends FakeElement {
  constructor(ownerDocument) { super('input', ownerDocument); }
}

class FakeSelectElement extends FakeElement {
  constructor(ownerDocument) { super('select', ownerDocument); }
}

class FakeTextAreaElement extends FakeElement {
  constructor(ownerDocument) { super('textarea', ownerDocument); }
}

class FakeButtonElement extends FakeElement {
  constructor(ownerDocument) { super('button', ownerDocument); }
}

function setConnected(element, connected) {
  element.isConnected = connected;
  for (const child of element.children ?? []) setConnected(child, connected);
}

function queryAll(root, selector) {
  validateFakeSelector(selector);
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

function validateFakeSelector(selector) {
  let brackets = 0;
  let quote = null;
  for (const character of selector) {
    if (quote) {
      if (character === quote) quote = null;
      continue;
    }
    if (character === '"' || character === "'") quote = character;
    else if (character === '[') brackets++;
    else if (character === ']' && --brackets < 0) throw new Error('invalid selector');
  }
  if (quote || brackets !== 0) throw new Error('invalid selector');
}
