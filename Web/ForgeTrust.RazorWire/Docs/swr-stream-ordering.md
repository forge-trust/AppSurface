# SWR frames and live stream ordering

An [`rw:island` with `swr`](../README.md#rwisland) can refresh its HTML when the page loads or its stream reconnects. A refresh can overlap a live update: its server snapshot may already be in flight when a Turbo Stream updates the island. Applying that older snapshot afterward would erase the newer browser state.

The [bundled Turbo runtime](runtime-contract-pipeline.md#public-contract) now preserves stream changes on the affected SWR frame. Each frame GET captures a local mutation epoch and request order. A stream action that targets that frame or one of its descendants invalidates earlier requests when the action actually executes. RazorWire discards an invalidated snapshot before it can replace the DOM. A later request starts with the new epoch and can render normally. An older response also cannot overwrite a newer committed response.

This is browser rendering order, not server data authority, authorization, or a server revision protocol. A backend that independently serves an outdated snapshot still needs its own version and consistency contract.

## Behavior and scope

- The guard applies to `turbo-frame` elements with `data-rw-swr` and `src`, including the markup emitted by `rw:island swr`. Ordinary frames keep their existing behavior.
- Turbo's declared `target` and `targets` determine which SWR frames a stream action affects. This includes enclosing frames and descendant SWR frames retained by a morph of their ancestor. Unrelated frames are not invalidated. Custom actions that mutate other DOM outside their declared targets own that additional coordination.
- A canceled request, a detached frame, a snapshot invalidated by a stream action, or a response older than the last committed response does not replace the live contents.
- Asynchronous stream renderers remain pending until their returned promise settles. An overlapping snapshot cannot commit through that pending write. A custom frame renderer must retain Turbo's synchronous DOM-commit contract; Turbo does not await an asynchronous frame `renderElement` implementation.
- Discarding a snapshot does not move permanent descendants, scroll, change focus, or execute scripts already in the retained contents. Await the frame's `loaded` promise when observing completion; completion and Turbo lifecycle events do not prove that an invalidated snapshot replaced the contents.
- The guard adds no retry loop, request header, endpoint, or public configuration option. The next ordinary revalidation or explicit frame `reload()` captures current state. A live update that invalidates initial loading preserves the authored/live contents rather than installing the older snapshot; hosts still own coherent initial data and stream targets in their placeholders.

## Internal execution contract

The authored `swr-render-guard.ts` adapter uses the pinned Turbo 8.0.23 frame-controller contract. It binds each `requestSucceededWithResponse` or `requestFailedWithResponse` callback to that request's exact `fetchOptions` and raw `Response`. A per-frame `loadResponse` chain retains the association through body parsing and queued rendering, including untracked loads. A rejected load releases its context and does not poison later loads.

After `allowsImmediateRender` has selected the renderer and run host event listeners, the adapter guards the renderer's synchronous permanent-element preservation/DOM-commit boundary. This closes the repaint gap after `turbo:before-frame-render` as well as the body-reading gap after response arrival. The copied Turbo asset is unchanged; there is no global `fetch` replacement or response-HTML marker. Hosts using the [host-managed Turbo mode](../README.md#choose-who-supplies-turbo) own compatibility with any runtime that differs from the pinned frame-controller methods.

The existing dialog stream queue calls the adapter around the final selected stream renderer. Targets are resolved at execution, not at message arrival, so an earlier queued action cannot accidentally bind a later action to stale DOM. Targetless stream actions retain their selected renderer without consulting Turbo’s target accessor; ordinary actions keep Turbo’s own validation. Weak maps hold frame/request/response state; detached elements and completed request objects are not retained by a global request registry. Host code that pauses a frame-render event must still complete its normal resume/cancellation lifecycle.

## Verification

The [runtime contract pipeline](runtime-contract-pipeline.md#maintainer-quickstart) runs deterministic Node lifecycle tests and verifies generated asset provenance and the existing payload limits. Browser regression cases hold an older response with one or eight concurrent registration writers, both before response delivery and after the frame-render event. They first observe every new user through the live stream, then release the stale snapshot and verify those users remain. A subsequent authoritative refresh must replace a local sentinel, proving that rejection does not leave a sticky dirty flag.

The original same-session registration/antiforgery browser test retains its inputs, assertions, and timeouts. Increasing a timeout, adding a retry, skipping the test, or updating the baseline does not substitute for correct stream/frame ordering.
