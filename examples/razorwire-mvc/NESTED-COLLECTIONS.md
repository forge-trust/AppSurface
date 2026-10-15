# Nested Collections Quickstart

Run the existing MVC host to try RazorWire's nested collection commands with a small `DateOnly` model. Each action owns its own dates; hidden `.index` markers retain sparse binding keys through an invalid POST and correction. The [Form Interactions guide](../../Web/ForgeTrust.RazorWire/Docs/form-interactions.md#nested-collections-and-index-tokens) is the canonical API and migration reference, including template placeholders, copy/removal rules, events, and diagnostics.

## Run the DateOnly sample

From the repository root, with the .NET 10 SDK installed, run the host command documented in the [MVC example README](README.md#start-here-return-razor-fragments):

```bash
dotnet run --project examples/razorwire-mvc/RazorWireWebExample.csproj
```

Use the origin printed by the host and open either route:

- `/Reactivity/NestedCollections?mode=physical`
- `/Reactivity/NestedCollections?mode=mark`

The existing [host project](RazorWireWebExample.csproj) references the repository's RazorWire project. This command exercises local source and its generated browser asset; it does not establish consumption of a packed NuGet version. First restore needs the host's configured package feeds and network access. An existing application uses the [RazorWire package setup](../../Web/ForgeTrust.RazorWire/README.md#60-second-quickstart) and its matching browser assets, with the ordinary `<rw:scripts />` detector; see the [runtime contract pipeline](../../Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md) for package/asset alignment.

The [view](Views/Reactivity/NestedCollections.cshtml) and its two partials author the complete form rather than asking RazorWire to generate fields:

- [_NestedAction.cshtml](Views/Reactivity/_NestedAction.cshtml) renders `Actions.index`, action identity/concurrency/delete fields, an action title, and the owned `Actions[key].Dates` root. The parent template uses the default `__index__`.
- [_NestedDate.cshtml](Views/Reactivity/_NestedDate.cshtml) renders `Actions[key].Dates.index`, date identity/concurrency/delete fields, `Kind`, and a text `Date` field bound to nullable `DateOnly`. Its template uses the distinct `__date_index__` placeholder.
- Each root has its own Add command and raw `data-rw-form-collection-status` region. Duplicate and Remove commands live in their row. The raw remove buttons inherit the root's selected mode through `data-rw-form-collection-remove="true"`.

The [model](Models/NestedCollectionsSampleModel.cs) and [controller](Controllers/NestedCollectionsController.cs) own validation and rendering keys. The [sample policy script](https://github.com/forge-trust/AppSurface/blob/main/examples/razorwire-mvc/wwwroot/js/nested-collections-policy.js) initializes fields from after-event `detail.row` and cancels child Add at eight live dates. **That eight-date limit is illustrative policy for this DateOnly sample only.** RazorWire imposes no date limit, and the [V4 fixture](#faithful-v4-consumer-fixture) does not inherit this rule.

## Try the complete round trip

Start with a fresh GET for each removal mode. These are expected results from the current sample and the [browser test source](../../Web/ForgeTrust.RazorWire.IntegrationTests/RazorWireNestedCollectionsPlaywrightTests.cs), not a claim that the commands were executed during this documentation edit.

| Step | Action | Expected result |
| --- | --- | --- |
| 1 | Open the sample. | Parent keys are `4,9`. Each parent owns date keys `0,7`; the repeated child keys are independent. Titles are `Action 4` and `Action 9`, kinds are `Scheduled`, and dates are `2026-10-14`. |
| 2 | In action `4`, optionally change its title to `Edited source action`, date `7`'s kind to `Changed source kind`, and its date to `2026-10-20`. Then use **Remove date** on date `0`. | Action `9` still has `0,7`. Physical mode leaves action `4` with marker `7`; mark mode retains markers `0,7`, hides date `0`, and submits its `Delete=true` and preserved `Id`. |
| 3 | Use **Duplicate action** on action `4`. | The new parent key is `10`, inserted after `4`; posted parent order is `4,10,9`. It contains one live date with fresh key `8`, copied from source date `7`. The removed date is omitted in both modes. Edited live values copy; copied `Id`, `RowVersion`, and `ConcurrencyToken` values clear recursively. Source keys and persisted identities remain unchanged. |
| 4 | Set the copied date's **Date (yyyy-MM-dd)** field to `not-a-date`, then use **Save actions**. | The POST returns `422`. The field named `Actions[10].Dates[8].Date` still shows `not-a-date`, with its validation message on the same binding path. Parent and child marker order remains unchanged. |
| 5 | Correct that field to `2026-11-30` and use **Save actions** again. | The POST returns `200`, and the result reads `Saved 3 action row(s).` Parent order remains `4,10,9`; the copy's date key remains `8`. This is validation acceptance in a sample with no durable store. |

The key payload after removal and duplication is:

| Field | Physical removal | Mark removal |
| --- | --- | --- |
| `Actions.index` in posted order | `4`, `10`, `9` | `4`, `10`, `9` |
| `Actions[4].Dates.index` | `7` | `0`, `7` |
| `Actions[9].Dates.index` | `0`, `7` | `0`, `7` |
| `Actions[10].Dates.index` | `8` | `8` |
| `Actions[4].Dates[0].Delete` | Absent | `true` |
| `Actions[4].Dates[0].Id` | Absent | `date-4-0` |
| `Actions[4].Dates[0].Kind` and `.Date` | Absent | Absent while disabled |
| `Actions[10].Id`, `.RowVersion`, and `Actions[10].Dates[8].Id`, `.ConcurrencyToken` | Empty | Empty |

After duplication, focus goes to `Actions[10].Title` and the parent status says `Duplicated action.` A child operation announces in its own dates status. To check a future template, use **Add date** within action `10` after the round trip; its local template must use the copied parent prefix while action `9` remains untouched. Reload before repeating the exact numeric sequence above, because later commands reserve additional keys.

## Keep posted keys and ModelState together

The [controller's posted-marker adapter](Controllers/NestedCollectionsController.cs) pairs `Request.Form["Actions.index"]` with bound actions in marker order. For each parent it pairs `Request.Form[$"Actions[{key}].Dates.index"]` with that parent's dates, then sets their rendering `ClientIndex` values from those canonical keys. It rejects blank/duplicate keys, marker counts that disagree with bound rows, or markers with no bound row fields with `400`; it does not replace them with list ordinals. `ClientIndex` is a rendering carrier, separate from persisted `Id`.

For the journey above, the binder produces actions in `4,10,9` order even though the bound list positions are `0,1,2`. In physical mode the child key arrays are `[7]`, `[8]`, and `[0,7]` in that parent order. In mark mode the original action `4` also contains the removed child `0`; the controller skips its consumer validation because `Delete` is true. The sample retains any already reported binding error even on a deleted row; applications own their policy for validating tombstones.

On invalid input, the controller keeps ModelState. The partial uses `Html.TextBox` and `Html.ValidationMessage` with the exact name `Actions[10].Dates[8].Date`. ModelState's attempted value therefore wins over the bound null `DateOnly`, and its error remains attached to the same field. Keep this adapter when adopting nested rows; clearing ModelState, rendering `Actions[1].Dates[0]`, or rebuilding keys from `Enumerable.Range` would disconnect the submitted value from its error. See the [binding rules](../../Web/ForgeTrust.RazorWire/Docs/form-interactions.md#aspnet-model-binding-rules) for the consumer boundary.

## Use ordinary commands without JavaScript

Disable JavaScript before loading the page, then open a fresh sample route. **Save actions** is an ordinary antiforgery-protected POST; this sample's form uses `data-turbo="false"` so enhanced structural commands also submit through that same full-page path.

The `<noscript>` buttons are application commands implemented by the controller:

- **Add date on server** posts `addDateFor=4` or `addDateFor=9`. The controller adds a blank date to that parent with a new opaque GUID key and retains its `0,7` markers. Fill the new `Kind` and `Date`, then Save.
- **Add action on server** posts `addAction=true`. The controller adds a blank action with a new opaque GUID key. Fill its title, use its **Add date on server** command if needed, then Save.

These submit commands have `formnovalidate` to let the server redisplay the draft while editing. Existing values and ModelState stay with their keys. A fresh unchanged form accepts two actions and shows `Saved 2 action row(s).`; adding a date does not change the number of action rows. Client Add/Duplicate/Remove buttons do not provide server commands on their own. Applications supply the ordinary-submit commands their workflow needs; the V4 fixture below includes its source-shaped server commands.

## Faithful V4 consumer fixture

The same host exposes `/Reactivity/SkoolitV4Collections?mode=physical` and `/Reactivity/SkoolitV4Collections?mode=mark`. Its [controller](Controllers/SkoolitV4CollectionController.cs) and [model](Models/SkoolitV4CollectionModels.cs) adapt the Skoolit V4 editor pinned to commit `a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e`. This is a version-bound fixture with no data store; it does not claim live Skoolit adoption or a measured first-use time.

The fixture keeps the source's temporal shape: `Kind` is `eventStart`, `dueBy`, or `overdueSince`; `Shape` is `Discrete` or `Range`; fields are `StartMonth`, `StartDay`, `StartYear`, `EndMonth`, `EndDay`, `EndYear`, `StartTime`, `EndTime`, and `TimeZone`. Missing source years remain blank rather than being inferred. Discrete end date-part controls are hidden and disabled; `EndTime` remains available. Temporal edits announce unsaved changes. Parent Add/Duplicate policy initialization uses the existing after-event's `detail.row` subtree and checks `detail.root`, without synthetic child events or duplicate structural JavaScript.

Its rendering adaptation pairs posted parent/child markers with bound rows in binder order and restores their `ClientIndex` rendering carriers from those canonical markers. Blank or duplicate marker keys, marker counts that disagree with bound rows, or markers with no bound row fields return `400`. Invalid input returns `422` with the exact sparse field names, ModelState attempted values, and errors retained. Its ordinary `draftIntent` submit commands support `add-action`, `add-date|actionPosition`, `duplicate-action|actionPosition`, `remove-action|actionPosition`, and `remove-date|actionPosition|datePosition`; positions identify application commands, while collection field names retain their posted keys.

The repo-local host command above remains source validation. To establish the required packed-consumer proof, consume the exact produced RazorWire package and record its package ID/version/hash, the served `form-interactions.js` hash, the pinned source revision, both removal-mode payloads and bound row order, copied identities, invalid field names/errors/attempted values, correction, and the ordinary no-JavaScript commands. Follow the [migration and package transition guidance](../../Web/ForgeTrust.RazorWire/Docs/form-interactions.md#migrating-an-existing-one-level-collection) before replacing a consumer's prior local mechanics. The local package and fixture results below are version-bound observations; published package selection, live Skoolit adoption, and human first-use timing remain unverified.

Use the repository's [scripts/verify-nested-collection-consumer.sh harness](https://github.com/forge-trust/AppSurface/blob/main/scripts/verify-nested-collection-consumer.sh) from the repository root:

```bash
scripts/verify-nested-collection-consumer.sh prepare
scripts/verify-nested-collection-consumer.sh start
# Exercise the physical and mark routes at the printed host origin, then:
scripts/verify-nested-collection-consumer.sh stop
```

The harness needs the local build tools plus an authenticated `gh` CLI with access to `forge-trust/skoolit` and network access for source retrieval and package restore. `prepare` automatically fetches the V4 view and page script from the pinned Skoolit commit above and verifies their Git blob identities. It packs the coordinated local packages with the normal generated-asset freshness guard, copies the MVC host, replaces its RazorWire source reference with the exact local `PackageReference`, restores and builds it, and records package, asset, and pinned-source hashes. `start` launches that isolated host on loopback and verifies that its served `form-interactions.js` matches the packed asset. `stop` stops only the recorded host after checking the process identity. Start and stop default to the latest prepared run; pass the printed run ID to select a specific run. Receipts and logs live under `.gstack/issue-868/packproof/<run-id>/`. Preparation and readiness establish package/asset identity; the sparse remove/duplicate/invalid/corrected round trip and ordinary no-JavaScript commands still need to be exercised against that host and recorded separately.

If a local command aborts, inspect the [owner-scoped diagnostics](../../Web/ForgeTrust.RazorWire/Docs/form-interactions.md#diagnostics) and repair the named token, marker, template, ownership, or ID conflict. If the sample form is not enhanced, confirm the loaded browser asset matches the current package/source and that `<rw:scripts />` is present in the host layout.

## Reproduce the first-use trial

Use an existing .NET 10 MVC/RazorWire host with working package feeds. Start the human clock on first arrival at the [Form Interactions guide](../../Web/ForgeTrust.RazorWire/Docs/form-interactions.md). Read the nested contract, update the package and matching assets, adapt the linked model/controller/view and templates, build and run, then end the clock when an independent child command and copied parent rows are confirmed through ordinary POST. Record the starting package/asset versions, feed, prerequisite state, reading/adaptation actions, start/end timestamps and resulting submitted names. The planning target is 2–5 minutes. Human first-use elapsed time remains unmeasured; cold SDK installation and feed setup have a separate clock.

Automated verification is a separate observation. On 2026-10-14 an initial nested browser suite ran 32 cases in 9 seconds after build, with no failures or skips. It covered source-backed DateOnly and scale fixtures, plus all three V4 journeys against the isolated `ForgeTrust.RazorWire` `0.1.0-issue868` package (a local proof version, not a published dependency recommendation). The source, embedded and served form runtime SHA-256 was `cfd710b20eaf5cc3b89ad37c6aefde6730b76de24ecafcc06b9f1ee76dbc4ce0`. This receipt applies only to that runtime hash; subsequent runtime changes require a fresh package and consumer run. This runner duration excludes reading, adaptation, restore and build and is not a human first-use result. Reproduce the exact-package trial with the harness above and select its printed origin:

A refreshed run on 2026-10-14 passed all 33 nested, scale, and V4 browser cases with no failures or skips in 17.367 seconds after build. Its three V4 journeys consumed a freshly packed `0.1.0-issue868` package; the source, embedded, and served runtime SHA-256 matched `4bb4f6c78df46e0dfda5c45d4bdc59fdbdf1a62734e5ff0bebd021e5b4ee6f87`. The remaining cases exercised the source-backed DateOnly and scale fixtures, including third-level collections and 10/100-parent forms. These observations remain local package and automated-run evidence; human first-use timing and live Skoolit adoption remain unverified.

```bash
RW_NESTED_COLLECTIONS_PROOF_URL=http://127.0.0.1:PORT \
  dotnet test Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj \
  --filter FullyQualifiedName~RazorWireSkoolitV4CollectionsPlaywrightTests
```

The [V4 browser tests](../../Web/ForgeTrust.RazorWire.IntegrationTests/RazorWireSkoolitV4CollectionsPlaywrightTests.cs) verify physical and mark removal, sparse remove/duplicate/add payloads, attempted Discrete end parts on a `422`, unchanged error paths/keys, correction to `200`, cleared copied identities and ordinary no-JavaScript commands. The fixture remains a version-bound consumer proof with no data store.
