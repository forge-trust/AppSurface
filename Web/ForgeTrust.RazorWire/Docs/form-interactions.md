# Form Interactions

Use RazorWire form interactions when a server-rendered form needs small local mechanics without page-specific JavaScript: reveal or disable conditional fields, add one-dimensional model-bound rows, duplicate draft rows, and remove rows while keeping ASP.NET Core model binding predictable.

The no-JavaScript fallback is ordinary form markup. RazorWire owns behavior, state attributes, sparse index allocation, and accessibility status hooks. Your app owns field markup, labels, row layout, validation spans, command text, CSS, persistence, and server validation.

## Form Interactions in 3 Minutes

Plain `<rw:scripts/>` is enough. It lazy-loads `form-interactions.js` when the page contains `data-rw-form-toggle` or `data-rw-form-collection`. Use `<rw:scripts form-interactions="true" />` only when you intentionally want eager loading.

```html
<form method="post">
  <label>
    <input type="checkbox"
           name="ExpectedNoAction"
           data-rw-form-toggle="draft-action"
           data-rw-form-toggle-invert="true">
    No action expected
  </label>

  <fieldset id="draft-action"
            data-rw-form-toggle-target="draft-action"
            data-rw-form-toggle-disable-when-hidden="true">
    <legend>Draft action</legend>
    <input name="Actions.index" type="hidden" value="0">
    <input name="Actions[0].Title" id="Actions_0__Title">
    <label for="Actions_0__Title">Title</label>
  </fieldset>
</form>
```

For collections, use a hidden `.index` marker and an app-authored template with the `__index__` token:

```html
<div data-rw-form-collection="Actions" data-rw-form-collection-label="action">
  <fieldset data-rw-form-collection-row data-rw-form-index="0">
    <input type="hidden" name="Actions.index" value="0">
    <input type="hidden" name="Actions[0].Id" data-rw-form-collection-preserve>
    <input type="hidden" name="Actions[0].Delete" data-rw-form-collection-delete-field>
    <label for="Actions_0__Title">Title</label>
    <input name="Actions[0].Title" id="Actions_0__Title">
    <button type="button" data-rw-form-collection-duplicate>Duplicate</button>
    <button type="button" data-rw-form-collection-remove>Remove</button>
    <button type="button" data-rw-form-collection-remove="mark">Delete saved row</button>
  </fieldset>

  <template data-rw-form-collection-template>
    <fieldset data-rw-form-collection-row>
      <input type="hidden" name="Actions.index" value="__index__">
      <label for="Actions___index____Title">Title</label>
      <input name="Actions[__index__].Title" id="Actions___index____Title">
    </fieldset>
  </template>

  <button type="button" data-rw-form-collection-add>Add action</button>
  <span data-rw-form-collection-status aria-live="polite"></span>
</div>
```

## Razor TagHelpers

The TagHelpers render the same public `data-rw-*` contract as raw HTML:

```cshtml
<input type="checkbox" name="ExpectedNoAction" rw-form-toggle="draft-action" rw-form-toggle-invert="true" />
<fieldset rw-form-toggle-target="draft-action" rw-form-toggle-disable-when-hidden="true">...</fieldset>

<div rw-form-collection="Actions" rw-form-collection-label="action">
    <fieldset rw-form-collection-row rw-form-index="0">...</fieldset>
    <template rw-form-collection-template>...</template>
    <button rw-form-collection-add>Add action</button>
</div>
```

Command TagHelpers set `type="button"` when the app did not already specify a type. They never generate hidden `.index`, id, delete, or validation fields; those fields are part of the app-owned model contract.

## Attribute Reference

| Attribute | Target | Behavior |
| --- | --- | --- |
| `data-rw-form-toggle="name"` | form control | Reveals matching targets when active. |
| `data-rw-form-toggle-invert="true"` | toggle | Inverts checkbox/radio/value state. |
| `data-rw-form-toggle-target="name"` | fieldset, section, div | Target shown or hidden by matching toggles in the same form. |
| `data-rw-form-toggle-disable-when-hidden="true"` | target | Disables descendant controls while hidden so stale values do not submit. |
| `data-rw-form-collection="Actions"` | collection root | Marks one-dimensional collection. |
| `data-rw-form-collection-label="action"` | collection root | Reader-facing copy for generated status messages. |
| `data-rw-form-collection-row` | row | Marks one app-authored row. |
| `data-rw-form-index="0"` | row | Optional current row index. |
| `data-rw-form-collection-template` | template | App-authored row template containing `__index__`. |
| `data-rw-form-collection-add` | button | Adds a cleared row from the template. |
| `data-rw-form-collection-duplicate` | button | Duplicates the nearest row with copied editable values. |
| `data-rw-form-collection-remove` | button | Physically removes the nearest row by default; use value `mark` for mark-for-removal. |
| `data-rw-form-collection-delete-field` | hidden input | App-owned delete field set in mark-for-removal mode. |
| `data-rw-form-collection-preserve` | input | Keeps a field enabled in mark-for-removal mode. |
| `data-rw-form-collection-copyable` | hidden input | Allows duplicate to copy an otherwise identity-like hidden value. |

## State Contract

| State | Attributes and submission | Focus and announcement |
| --- | --- | --- |
| Enhanced | Form receives `data-rw-form-interactions-enhanced="true"`. | No focus movement. |
| Toggle hidden | Target is hidden; descendant controls disabled only if the target opts in. | Toggle keeps focus; `aria-expanded` mirrors state when applicable. |
| Toggle shown | RazorWire only re-enables controls it disabled itself. | Toggle keeps focus. |
| Add | New row receives a sparse `.index` marker and rewritten names/ids/labels. | First enabled user-editable control in the new row receives focus. |
| Duplicate | Editable values copy; validation state and identity/concurrency hidden values clear unless copyable. A marked delete field resets to its authored `value` attribute, or `false` when no value is authored, so boolean model binding receives an inactive value instead of an empty string. | First enabled user-editable control in the clone receives focus. |
| Physical remove | Row and its `.index` marker leave the submitted payload. | Focus moves to the next row command, previous row command, then add command. |
| Mark remove | Row is hidden; `.index`, preserved fields, and delete field submit; normal editable fields are disabled. | Focus moves as with physical remove. |
| Canceled before-event | No mutation occurs. | App-owned event handler owns any user feedback. |

## Events

Before events are cancelable and fire before DOM mutation. After events are not cancelable.

- `razorwire:form-toggle:before-change`
- `razorwire:form-toggle:change`
- `razorwire:form-collection:before-add`
- `razorwire:form-collection:add`
- `razorwire:form-collection:before-duplicate`
- `razorwire:form-collection:duplicate`
- `razorwire:form-collection:before-remove`
- `razorwire:form-collection:remove`

Event detail includes the owning `form`, relevant `root`, command `control`, affected `target` or `row`, sparse `index`, optional `previousIndex`, collection `action`, and `removeMode`.

## ASP.NET Model-Binding Rules

- The hidden `Actions.index` marker is the binding contract. RazorWire never renumbers existing rows.
- Indices may be sparse. Server validation should re-render the posted indices, not `Enumerable.Range(0, Count)`.
- Disabled controls do not post. Hidden controls still post unless disabled.
- Checkbox hidden fallbacks must stay in the same row as the checkbox they support.
- Physical remove only removes fields from the submitted payload. It does not mean "delete an existing database row" unless the server infers that separately.
- Mark-for-removal mode is the explicit persisted-delete lane: keep app-owned ids and delete fields enabled, and let the server decide what deletion means.

## Diagnostics

RazorWire exposes `window.RazorWire.formInteractionsManager.scan()`, `prune()`, `getDiagnostics()`, and `clearDiagnostics()`. Call `scan()` after app-owned DOM updates add form-interaction markers, and call `prune()` after removing forms when you need to release disconnected controllers before the next automatic lifecycle scan. Diagnostics use `message`, `impact`, `fix`, and `docs`.

Diagnostics report missing or cross-form toggle targets, missing or malformed collection templates, missing, invalid, duplicate, or mismatched `.index` markers, nested collections, non-button commands, missing mark-remove delete fields, and file inputs that cannot be cloned by browser APIs.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| Toggle does nothing | Missing matching target in the same form. | Match `data-rw-form-toggle` and `data-rw-form-toggle-target` values. |
| Hidden fields still submit | Target is hidden but not configured to disable controls. | Add `data-rw-form-toggle-disable-when-hidden="true"`. |
| Added row does not bind | Template lacks `.index` marker or `__index__`. | Include `name="Actions.index" value="__index__"` and use `__index__` in row names. |
| Duplicated identity values persist | Hidden identity field is marked copyable or not recognized. | Remove `data-rw-form-collection-copyable` or clear identity fields in a cancelable before-event handler. |
| Saved row is not deleted | Physical remove was used for a persisted row. | Use mark-for-removal with an app-owned delete field. |
| Diagnostic says nested collection | A collection root appears inside another collection root. | Keep RazorWire collections one-dimensional. |
