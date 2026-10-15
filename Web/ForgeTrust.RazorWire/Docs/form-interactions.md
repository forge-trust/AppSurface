# Form Interactions

Use RazorWire form interactions when a server-rendered form needs small local mechanics without page-specific JavaScript: reveal or disable conditional fields, add and duplicate model-bound rows at multiple collection levels, and remove rows while keeping ASP.NET Core model binding predictable. The package owns row mechanics; the application still owns its model, field markup, validation policy, persistence, and failed-post rendering keys.

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

For a one-level collection, use a hidden `.index` marker and an app-authored template with the default `__index__` placeholder. Existing markup continues to work without an index-token attribute:

```html
<div data-rw-form-collection="Actions" data-rw-form-collection-index-token="__index__" data-rw-form-collection-label="action">
  <fieldset data-rw-form-collection-row data-rw-form-index="0">
    <input type="hidden" name="Actions.index" value="0">
    <input type="hidden" name="Actions[0].Id" data-rw-form-collection-preserve>
    <input type="hidden" name="Actions[0].Delete" value="false" data-rw-form-collection-delete-field>
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

## Nested Collections and Index Tokens

[`FormCollectionTagHelper.IndexToken`](../TagHelpers/FormInteractionTagHelpers.cs) authors an unresolved template placeholder. It is not a submitted row key and is not a persisted `Id`. The helper attribute `rw-form-collection-index-token` renders as `data-rw-form-collection-index-token`. When omitted or whitespace-only, it emits `__index__`; nonblank values are trimmed and emitted for the runtime to validate. The runtime accepts the syntax `__[A-Za-z][A-Za-z0-9_]*__`. A nonblank value with invalid syntax is deliberately preserved in the HTML so the owner-scoped developer diagnostic can identify and block Add or cloning through that malformed template.

Unresolved ancestor collections need distinct placeholders so one operation cannot confuse the outer row token with a descendant's local template token. Sibling date collections may reuse `__date_index__` because each has a different owning action row. Concrete `.index` marker values are opaque, nonblank submitted keys: preserve their exact strings, including sparse, quoted, backslash, Unicode, delimiter-containing, or placeholder-like values, wherever the actual binder and consumer can represent them unambiguously. Do not parse a concrete key as placeholder syntax or derive it from the persisted `Id`.

This compact shape shows the two ownership levels. The [minimal DateOnly MVC quickstart](../../../examples/razorwire-mvc/NESTED-COLLECTIONS.md) is the canonical first-use flow at `/Reactivity/NestedCollections`; it includes the host command, both removal modes, sparse-key invalid redisplay, and ordinary no-JavaScript commands. Its [Razor view](../../../examples/razorwire-mvc/Views/Reactivity/NestedCollections.cshtml) is the runnable markup. Use the separate [version-bound V4 fixture](../../../examples/razorwire-mvc/NESTED-COLLECTIONS.md#faithful-v4-consumer-fixture) for consumer-policy and exact-package renderer evidence. A fixture result does not claim live Skoolit adoption.

```cshtml
<div rw-form-collection="Actions" rw-form-collection-index-token="__action_index__"
     rw-form-collection-label="action">
    <template rw-form-collection-template>
        <fieldset rw-form-collection-row>
            <input type="hidden" name="Actions.index" value="__action_index__" />
            <label for="Actions___action_index____Title">Action title</label>
            <input name="Actions[__action_index__].Title" id="Actions___action_index____Title" />

            <div rw-form-collection="Actions[__action_index__].Dates"
                 rw-form-collection-index-token="__date_index__" rw-form-collection-label="date">
                <template rw-form-collection-template>
                    <fieldset rw-form-collection-row>
                        <input type="hidden" name="Actions[__action_index__].Dates.index"
                               value="__date_index__" />
                        <label for="Actions___action_index___Dates___date_index____Kind">Date kind</label>
                        <input name="Actions[__action_index__].Dates[__date_index__].Kind"
                               id="Actions___action_index___Dates___date_index____Kind" />
                    </fieldset>
                </template>
                <button rw-form-collection-add>Add date</button>
                <span data-rw-form-collection-status role="status" aria-live="polite"></span>
            </div>
        </fieldset>
    </template>
    <button rw-form-collection-add>Add action</button>
    <span data-rw-form-collection-status role="status" aria-live="polite"></span>
</div>
```

When RazorWire adds an action, it resolves only `__action_index__` in that action's row and nested collection prefix. The child collection keeps `__date_index__` unresolved, ready for its own Add command. Adding a date then resolves only that child placeholder. A seeded live date in an action template uses its own concrete marker value, such as `0`, while the child template keeps its unresolved local token.

Each live row's enabled hidden `.index` input is the canonical binding key. Optional `rw-form-index` metadata must agree with that marker when both are present. Existing one-level rows retain the metadata-only compatibility path when the hidden marker is absent; nested operations require their owned enabled hidden marker. Duplicate sibling markers or marker/metadata disagreement abort the owning operation with a diagnostic. Existing live keys are never renumbered.

A template must bind its owned row through names such as `Actions[__action_index__].Title`. Nested templates also require the owning enabled hidden `.index` marker whose value is that local placeholder. Legacy top-level templates may omit the marker when they contain an owned field with the correct prefixed binding name; Add creates the marker during final preparation. Any authored prototype marker must still be unique, enabled, hidden, and match the local placeholder. The runtime validates actual owned binding fields and markers; a token in a comment, display text, unrelated attribute, or another owner's field does not make the template valid. A malformed or disabled prototype marker aborts the command before insertion.

Allocation reserves owned live and marked rows, plus source and pending destination keys during recursive duplication; inert prototypes do not reserve live keys. Whole decimal keys use a fresh safe integer increment when possible. Mixed opaque keys, numeric-prefix strings, or unsafe increments use an opaque generated key with collision checking. Leading zeros in existing keys remain unchanged. These are allocation choices, not a grammar imposed on submitted keys.

### Nested event and identity behavior

Before-add receives the attribute-rebased detached row at the established preparation stage, before editable values clear and before final row metadata and marker assignment. Before-duplicate receives its fully prepared detached clone. Before-remove receives the current live row. These existing events are cancelable: cancellation prevents RazorWire-owned mutation, focus movement, and success announcement, while listener-authored edits remain. After-events run only after the mutation succeeds. A child command's `detail.root` identifies its child collection and events bubble normally; a parent add or duplicate emits one parent event, not synthetic child commands.

Use the parent after-event's `detail.row` subtree and `detail.root` to apply consumer-owned descendant initialization or policy. A V4-style date rule, maximum, validation, or server command remains application-owned. Do not add a parallel child event or keep duplicate client-side add/duplicate/remove mechanics after adopting the package.

Duplicate copies live editable values recursively, clears validation decoration, clears file selections and recognized hidden identity/concurrency fields (`.Id`, `.RowVersion`, `.ConcurrencyStamp`, `.ConcurrencyToken`, `.ETag`), and preserves explicitly copyable hidden values. Recognized hidden identities in copied inert templates clear too. The identity classification is intentionally closed; clear a custom hidden identity in the cancelable before-duplicate listener if the consumer owns that field. An explicitly marked or owner-selected hidden delete input resets to its authored inactive value or `false` before the copyable exception is considered; other hidden `.Delete`/`.IsDeleted` values follow the existing identity-clearing rule. A marked descendant row is a tombstone and is omitted from a duplicated parent. Source row keys and IDs stay unchanged, and every copied live row gets a fresh key in its own destination collection.

Physical removal removes the row subtree and all its submitted markers. Mark removal retains the parent and descendant markers, the selected row's delete field, and explicitly preserved fields; it hides the subtree and disables ordinary editable fields. It does not infer a cascade or activate every child delete flag, and independently marked descendants keep their own submitted deletion state. The server decides how a removed parent is validated and persisted. After add or duplicate, focus goes to the first eligible enabled editable control in the new subtree; when none exists, it goes to the first eligible collection command there (including an empty nested Add), then remains on the triggering command. Hidden, inert, disabled, readonly, or CSS-hidden candidates are excluded. Removal uses the next row command, previous row command, then the owning collection's Add command. Successful operations announce once through the owning collection status region.

A removed ancestor suppresses descendant structural commands and ordinary editable controls. A later toggle refresh cannot re-enable those controls beneath the removed ancestor; canonical markers, preserved fields, and independently marked descendants' deletion controls retain their submission roles.

RazorWire maps DOM identity independently from model-binding names. It refreshes cloned live IDs, including IDs without binder tokens, and maps references within the corresponding clone/template fragment. The supported ID-reference attributes are `label[for]`, `list`, and `aria-errormessage` (one ID each), plus `output[for]`, `aria-controls`, `aria-describedby`, `aria-labelledby`, and `aria-owns` (whitespace-separated ID lists). Each ID-list item is mapped independently in order; unmapped external references remain intact. `data-valmsg-for` is a model-binding path and follows prefix rebasing, not DOM-ID mapping. Other reference attributes remain application-owned. IDs must remain unique across the whole document, including other forms; ambiguous duplicate IDs inside one source reference scope diagnose and abort rather than guessing.

### Internal ownership and clone invariants

The [authored runtime helpers](https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.RazorWire/assets/src/form-interactions.ts) discover owners from the current DOM for each command. Their transient owner descriptors and detached clone plan are internal implementation records, with no public constructor or persistent collection tree. A row, template, command, marker, delete field, or status is selected only within its nearest collection and row ownership boundary. The clone plan pairs source and cloned elements before inserting markers or pruning cloned tombstones, so copying control state cannot shift onto another row's fields. Binding rebasing uses the longest matching full owned prefix at a property/index boundary; it never interprets concrete key characters as a regular expression or replaces an equal child key incidentally.

Each inert template `DocumentFragment` has its own ID-reference scope. Lookup starts in that fragment's map, then walks its containing fragment/clone scopes for ancestor references; it never borrows a sibling template's map. Local placeholders remain symbolic until their owner is instantiated. Live IDs are reserved against the whole document and the pending clone, and checked again when a prototype becomes live. These mapping and source/clone-pair invariants are documented beside the runtime helpers and exercised through the [existing runtime tests](https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.RazorWire/test/form-interactions.test.mjs).

The Add plan retains its selected template, local token, and source row's fragment lineage. Source and detached-clone snapshots retain each binding element's identity, paths, direct parent node, and canonical marker type, value, and enabled state. After a before-event, replacing or moving those sources invalidates the plan even when their names and row keys are unchanged. Detached descendant templates are traversed again to verify hidden marker type, enabled state, and local-token agreement, including third-level prototypes. Listener edits remain intact when validation aborts.

Preparation preserves the [existing before-event stages](#events): attribute-rebased detached Add before clearing values and assigning its final marker, fully prepared detached Duplicate, and live Remove. Cancellation leaves listener edits intact and prevents runtime insertion/removal, focus, and success status changes. Before-event listeners may edit ordinary values, classes, and consumer attributes. During before-add and before-duplicate, listeners cannot add, remove, or move planned binding elements in the detached clone, or change its binding names, collection prefixes, validation paths, local placeholders, or row/collection owners. After those listeners return, the runtime revalidates the clone's structural bindings, live owner/source relationships, canonical markers, planned token reservations, and document ID collisions before insertion; Add also validates its final prepared row. After before-remove, the runtime rechecks the live row/root relationship, collection prefix, command eligibility, and canonical markers before removal. An invalidated operation aborts with a diagnostic, without rollback, automatic retry, or an after-event. After successful insertion/removal, the existing after-event is the consumer policy hook; use the [manager scan workflow](#diagnostics) after app-owned structural updates.

### Migrating an existing one-level collection

1. Update to the package and its matching published browser assets together. Keep the ordinary `<rw:scripts />` loading path; it detects collection markup. The [runtime contract pipeline](runtime-contract-pipeline.md) explains asset/package alignment.
2. Add the nested collection root inside its owning row, with a full binding prefix such as `Actions[__action_index__].Dates` and a distinct local token such as `__date_index__`. Add its row marker, enabled hidden `.index` marker, app-authored template, Add/duplicate/remove commands, and status region.
3. Keep the consumer's validation, date limits, delete policy, server commands, and renderer that pairs posted marker keys with bound rows. On an invalid POST, render names, error references, and attempted values from the exact posted keys in marker order; a normal list object's current ordinal is not a replacement key.
4. Use the [MVC nested-collections quickstart](../../../examples/razorwire-mvc/NESTED-COLLECTIONS.md) for the minimal DateOnly first-use path and ordinary no-JavaScript submission. Separately run the [exact-package V4 fixture proof](../../../examples/razorwire-mvc/NESTED-COLLECTIONS.md#faithful-v4-consumer-fixture): record the pinned consumer source revision, produced package ID/version/hash, and consumed `form-interactions.js` asset hash; submit sparse nested keys, verify bound order and ModelState paths, render the invalid response without clearing ModelState or re-keying, then correct and resubmit.
5. Remove the consumer's duplicate structural JavaScript only after the packed proof passes. Keep the prior consumer path available until that point; a package-version edit alone is not adoption proof. If rollback is needed, restore authored source, generated assets, docs/examples, and any changed consumer files together and verify package/asset alignment. A fixture result does not claim live Skoolit adoption.

For the first-use path in an existing supported MVC/RazorWire host, start at this guide, update the package, adapt the linked DateOnly sample's model/view, build and run it, then confirm an independent child command and copied rows through POST. The planning target is 2–5 minutes; no human first-use measurement is claimed. Record automated execution time separately. A cold SDK/feed setup has separate prerequisites and is not part of that target; invalid redisplay and resubmit remain required proof in the separate V4 fixture.

The [pinned V4 faithful fixture](../../../examples/razorwire-mvc/NESTED-COLLECTIONS.md#faithful-v4-consumer-fixture) retains the consumer's temporal field shape: `Kind` is `eventStart`, `dueBy`, or `overdueSince`; `Shape` is `Discrete` or `Range`; date parts are `StartMonth`, `StartDay`, `StartYear`, `EndMonth`, `EndDay`, and `EndYear`, with `StartTime`, `EndTime`, and `TimeZone`. The optional source year remains optional and is never inferred. For `Discrete`, end date-part controls are hidden and disabled; `EndTime` remains available. Temporal edits announce that the draft is unsaved. After a parent action is added or duplicated, consumer policy initializes descendants from the parent's after-event `detail.row` and scopes it with `detail.root`; it needs no synthetic child command/event. The DateOnly quickstart's illustrative eight-date limit belongs only to that sample and must not be carried into the V4 fixture. Record the actual package identity, posted marker payload/order, bound rows, ModelState paths, rendered names/errors/attempted values, invalid response, and successful resubmit from an executed fixture before reporting those observations.

## Razor TagHelpers

The TagHelpers render the same public `data-rw-*` contract as raw HTML. The [C# definitions and XML reference](../TagHelpers/FormInteractionTagHelpers.cs) document each helper's properties and normalization; the [authored public JavaScript contract manifest](https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.RazorWire/assets/contracts/razorwire-public-contracts.js) documents the browser attributes, manager, and event details harvested by the [runtime contract pipeline](runtime-contract-pipeline.md). The [internal helper semantics](#internal-ownership-and-clone-invariants) describe ownership and preparation without introducing another consumer API.

```cshtml
<input type="checkbox" name="ExpectedNoAction" rw-form-toggle="draft-action" rw-form-toggle-invert="true" />
<fieldset rw-form-toggle-target="draft-action" rw-form-toggle-disable-when-hidden="true">...</fieldset>

<div rw-form-collection="Actions" rw-form-collection-label="action" rw-form-collection-index-token="__index__">
    <fieldset rw-form-collection-row rw-form-index="0">...</fieldset>
    <template rw-form-collection-template>...</template>
    <button rw-form-collection-add>Add action</button>
</div>
```

Command TagHelpers set `type="button"` when the app did not already specify a type. They never generate hidden `.index`, id, delete, or validation fields; those fields are part of the app-owned model contract.

`FormCollectionTagHelper` exposes `Name`, `Label`, `RemoveMode`, and optional `IndexToken`. `Name` is the full collection binding prefix; `Label` changes announcement text only. `RemoveMode` supplies the collection default (`physical`, or `mark`/`mark-remove`). `FormCollectionRowTagHelper.Index` supplies optional current-key metadata. Add, Duplicate, and template helpers mark app-authored elements. `FormCollectionRemoveTagHelper.Mode` sets a command override; bare `rw-form-collection-remove` normalizes to `physical`. To inherit the owning root's removal mode, author a raw button with `type="button" data-rw-form-collection-remove="true"`, as the quickstart does.

Author the status region as `<span data-rw-form-collection-status role="status" aria-live="polite"></span>`. There is no status TagHelper; `rw-form-collection-status` does not emit the browser contract. Keep a status inside its owning collection. If absent, RazorWire creates that owner's fallback status after a successful command.

## Attribute Reference

| Attribute | Target | Behavior |
| --- | --- | --- |
| `data-rw-form-toggle="name"` | form control | Reveals matching targets when active. |
| `data-rw-form-toggle-invert="true"` | toggle | Inverts checkbox/radio/value state. |
| `data-rw-form-toggle-target="name"` | fieldset, section, div | Target shown or hidden by matching toggles in the same form. |
| `data-rw-form-toggle-disable-when-hidden="true"` | target | Disables descendant controls while hidden so stale values do not submit. |
| `data-rw-form-collection="Actions"` | collection root | Marks a collection root and scopes nested row commands to its owning row. |
| `data-rw-form-collection-label="action"` | collection root | Reader-facing copy for generated status messages. |
| `data-rw-form-collection-index-token="__index__"` | collection root | Unresolved placeholder used by this root's row template; defaults to `__index__`, and nested ancestors use distinct placeholders. |
| `data-rw-form-collection-remove-mode="mark"` | collection root or remove button | Default removal policy on a root, or explicit override on a command. `mark`/`mark-remove` mark for removal; other values use physical removal. |
| `data-rw-form-collection-row` | row | Marks one app-authored row. |
| `data-rw-form-index="0"` | row | Optional current row index. |
| `data-rw-form-collection-template` | template | App-authored row template containing the owning collection's configured index token. |
| `data-rw-form-collection-add` | button | Adds a cleared row from the template. |
| `data-rw-form-collection-duplicate` | button | Duplicates the nearest row with copied editable values. |
| `data-rw-form-collection-remove` | button | Removes the nearest owned row. Blank or `true` uses the root's default; `mark`/`mark-remove` or `physical` overrides it. The default without a root policy is physical. |
| `data-rw-form-collection-status` | element inside collection root | Owner-local success announcement region. Author raw `data-rw-*` markup; no status TagHelper exists. |
| `data-rw-form-collection-delete-field` | collection root or hidden input | On the root, an index-agnostic CSS selector such as `[data-delete-control]` resolves an app-owned hidden input relative to each row; do not embed one row's sparse index in the selector. On that hidden input, the attribute marks the field directly and needs no root selector. A visible or non-input target is invalid. Mark-for-removal activates the field, while duplicate restores its authored inactive value or `false`. |
| `data-rw-form-collection-preserve` | input | Keeps a field enabled in mark-for-removal mode. |
| `data-rw-form-collection-copyable` | hidden input | Allows duplicate to copy an otherwise identity-like hidden value. |

## State Contract

| State | Attributes and submission | Focus and announcement |
| --- | --- | --- |
| Enhanced | Form receives `data-rw-form-interactions-enhanced="true"`. | No focus movement. |
| Toggle hidden | Target is hidden; descendant controls disabled only if the target opts in. | Toggle keeps focus; `aria-expanded` mirrors state when applicable. |
| Toggle shown | RazorWire only re-enables controls it disabled itself. | Toggle keeps focus. |
| Add | New row receives a sparse `.index` marker and rewritten owned names/IDs/references. Only this collection's placeholder resolves; descendant local placeholders remain available. | First eligible editable control in the new subtree receives focus, then its first eligible collection command if no editable control exists. |
| Duplicate | Live editable values copy recursively; recognized identity/concurrency hidden values and validation state clear unless copyable. A marked delete field resets to its authored `value` attribute, or `false` when no value is authored. Descendant tombstones are omitted. | Focus follows the add fallback within the clone. |
| Physical remove | Row and its `.index` marker leave the submitted payload. | Focus moves to the next row command, previous row command, then add command. |
| Mark remove | Row is hidden; `.index`, preserved fields, and delete field submit; normal editable fields are disabled. | Focus moves as with physical remove. |
| Canceled or invalid operation | No RazorWire-owned structural mutation, focus movement, or success announcement occurs; listener-authored changes remain. | Cancellation feedback is application-owned; structural failures are developer diagnostics. |

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

Event detail includes the owning `form`, relevant `root`, command `control`, affected `target` or `row`, sparse `index`, optional `previousIndex`, collection `action`, and `removeMode`. Nested child events identify the child root in `detail.root` and bubble normally. The before-event stages differ intentionally: add exposes the attribute-rebased, not-yet-cleared detached row; duplicate exposes the fully prepared detached clone; remove exposes the live source row. After Add/Duplicate listeners return, RazorWire rechecks clone structural bindings, ownership, markers, reserved tokens, and ID collisions before insertion. Remove rechecks live ownership, collection prefix, command eligibility, and markers before removal.

Physical removal detaches the command together with its row. Its `razorwire:form-collection:remove` after-event therefore dispatches on the connected owning root so form/document listeners can observe it, retaining the original `detail.control`, detached `detail.row`, and other detail fields. Mark removal leaves the command connected and dispatches there. Read ownership from `detail.root` and the command from `detail.control`; the event target is not always the command.

## ASP.NET Model-Binding Rules

- The enabled hidden `Actions.index` marker is the binding contract at each level. RazorWire never renumbers existing rows. Keep the placeholder (`__action_index__`) distinct from the concrete marker value and from an action's persisted `Id`.
- Indices may be sparse. Server validation should re-render the posted indices, not `Enumerable.Range(0, Count)`.
- Bind posted parent and child marker keys alongside their rows in posted marker order. On failed validation, use those exact keys for field names and `data-valmsg-for`, and retain ModelState attempted values and errors. Do not clear ModelState or silently replace sparse keys with list ordinals.
- Disabled controls do not post. Hidden controls still post unless disabled.
- Checkbox hidden fallbacks must stay in the same row as the checkbox they support.
- Physical remove only removes fields from the submitted payload. It does not mean "delete an existing database row" unless the server infers that separately.
- Mark-for-removal mode is the explicit persisted-delete lane: keep app-owned ids and delete fields enabled, and let the server decide what deletion means.

## Diagnostics

RazorWire exposes `window.RazorWire.formInteractionsManager.scan()`, `prune()`, `getDiagnostics()`, and `clearDiagnostics()`. Call `scan()` after app-owned DOM updates add form-interaction markers, and call `prune()` after removing forms when you need to release disconnected controllers before the next automatic lifecycle scan. Diagnostics use `message`, `impact`, `fix`, and `docs`; `docs` points to this guide's [Troubleshooting](#troubleshooting) section. When `window.RazorWire.config.developmentDiagnostics` is `true`, diagnostics are also written to the development console. They describe markup/structure and do not include ordinary submitted field values.

Collection diagnostics identify the owning collection, the structural cause and impact, a concrete repair, and this guide's canonical troubleshooting destination. They cover missing or malformed owned template bindings, invalid index-token syntax, missing/invalid/duplicate/mismatched owned markers, late token or ID collisions, changed binding paths or ownership after a before-event, non-button commands, missing or non-hidden mark-remove delete fields, ambiguous source IDs, and file inputs that cannot be cloned by browser APIs. A failed or canceled command keeps its triggering focus and does not announce success. Policy cancellation messages belong in the consumer's before-event listener.

For example, an invalid child template token produces the following structural diagnostic. Replace that owner's token with a valid placeholder such as `__date_index__` and make its template names/marker use the same token, then retry the command:

```json
{
  "message": "Collection \"Actions[4].Dates\" invalid template index token.",
  "impact": "Aborted; values retained.",
  "fix": "Use __name__ or __index__.",
  "docs": "Web/ForgeTrust.RazorWire/Docs/form-interactions.md#troubleshooting"
}
```

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| Toggle does nothing | Missing matching target in the same form. | Match `data-rw-form-toggle` and `data-rw-form-toggle-target` values. |
| Hidden fields still submit | Target is hidden but not configured to disable controls. | Add `data-rw-form-toggle-disable-when-hidden="true"`. |
| `Collection "" blank prefix.` diagnostic | The collection root has no nonblank binding prefix. | Set `data-rw-form-collection` to the full model collection prefix, such as `Actions` or `Actions[4].Dates`, and match its field and marker names. |
| `is missing a template.` diagnostic | The collection has no template within its nearest collection ownership boundary. | Add an owned `template[data-rw-form-collection-template]` with correctly prefixed fields and a matching local token/marker. A child or sibling owner's template cannot supply it. See [Nested Collections and Index Tokens](#nested-collections-and-index-tokens). |
| Added nested row does not bind | Its template marker or field names use the ancestor token in place of the child token, or the enabled owned `.index` marker is missing. | Give unresolved ancestors distinct tokens, use the child's token in its own marker/name, and retain the exact posted keys during invalid redisplay. See [Nested Collections and Index Tokens](#nested-collections-and-index-tokens). |
| Collection command aborts with a configuration diagnostic | The named owner has invalid token syntax, marker disagreement/duplication, changed ownership, a token/ID collision, or an ambiguous ID reference scope. | Read the diagnostic's collection, cause, impact, and fix; repair that owner's markup, then invoke the command again. Do not re-key submitted rows to hide the structural conflict. |
| Duplicated identity values persist | Hidden identity field is marked copyable or not recognized. | Remove `data-rw-form-collection-copyable` or clear identity fields in a cancelable before-event handler. |
| Saved row is not deleted | Physical remove was used for a persisted row. | Use mark-for-removal with an app-owned delete field. |
| Child command changes the wrong row | Child root, row, or status is being selected outside its nearest owning row. | Keep each child root and its commands inside the owner row; use the child event's `detail.root` when applying consumer policy. |
