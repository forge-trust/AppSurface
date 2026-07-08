/**
 * Browser global that exposes RazorWire runtime managers and runtime configuration for diagnostics and advanced integrations.
 * @public
 * @namespace RazorWire
 * @global
 * @name window.RazorWire
 */
window.RazorWire = window.RazorWire || {};

/**
 * Optional island module manifest that maps logical `data-rw-module` names to host-provided module specifiers before RazorWire imports them.
 * @public
 * @namespace RazorWire
 * @config window.RazorWireIslandModules
 * @type {Record<string, string>}
 * @source host page script or bundled app script
 * @property {string} moduleName - Logical island module name keyed by the value rendered in `data-rw-module`.
 */

/**
 * Runtime configuration merged from the `<rw:scripts />` script tag.
 * @public
 * @namespace RazorWire
 * @config window.RazorWire.config
 * @type {object}
 * @source <rw:scripts />
 * @property {boolean} developmentDiagnostics - Whether development diagnostics can be exposed.
 * @property {boolean} failureUxEnabled - Whether failed-form request markers, events, fallback rendering, and diagnostics are enabled.
 * @property {"auto"|"manual"|"off"} failureMode - Default failed-form behavior.
 * @property {string} defaultFailureMessage - Reader-facing fallback copy for unhandled form failures.
 */

/**
 * Behavior Kit manager for app-authored progressive enhancement on replaceable server-rendered DOM and logical browser visits.
 * @public
 * @namespace RazorWire
 * @config window.RazorWire.behaviors
 * @type {object}
 * @source <rw:scripts behavior-kit="true" />
 * @property {Function} register - Registers a root-scoped behavior definition with `name`, `selector`, and `connect(root, context)`.
 * @property {Function} registerLifecycle - Registers a page-lifecycle behavior definition with `name`, optional `events`, optional `frames`, and `connect(context)`.
 * @property {Function} scan - Re-scans the document or supplied root for registered behavior selectors.
 * @property {Function} prune - Disconnects controllers whose roots left the document or no longer match their selector.
 * @property {Function} getDiagnostics - Returns stable Behavior Kit diagnostics recorded since startup.
 * @property {Function} clearDiagnostics - Clears recorded Behavior Kit diagnostics.
 */

/**
 * Root-scoped Behavior Kit context passed to `window.RazorWire.behaviors.register(...).connect`.
 * @public
 * @namespace RazorWire
 * @typedef {Object} RazorWireBehaviorContext
 * @property {AbortSignal} signal - Signal aborted before cleanup when RazorWire disconnects the root.
 * @property {string} behaviorName - Registered behavior name.
 * @property {string} rootId - Stable runtime identity for the current behavior/root pair.
 * @property {Function} query - Scoped `querySelector` helper rooted at the connected element.
 * @property {Function} queryAll - Scoped `querySelectorAll` helper rooted at the connected element.
 * @property {Function} diagnostic - Records an app-owned behavior diagnostic with a message and fix.
 */

/**
 * Page-lifecycle Behavior Kit context passed to `window.RazorWire.behaviors.registerLifecycle(...).connect`.
 * @public
 * @namespace RazorWire
 * @typedef {Object} RazorWireLifecycleContext
 * @property {AbortSignal} signal - Signal aborted before cleanup when the lifecycle behavior reconnects on a later pass.
 * @property {string} behaviorName - Registered lifecycle behavior name.
 * @property {string} url - Current browser URL for the lifecycle pass.
 * @property {"initial"|"turbo:load"|"turbo:render"|"turbo:frame-load"} renderKind - Logical render lifecycle that triggered the pass.
 * @property {Document|Element} root - Document or frame element that owns the lifecycle pass.
 * @property {Function} diagnostic - Records an app-owned lifecycle diagnostic with a message and fix.
 */

/**
 * Stable Behavior Kit diagnostic codes.
 * @public
 * @namespace RazorWire
 * @typedef {"BehaviorSelectorInvalid"|"BehaviorRegistrationConflict"|"BehaviorConnectFailed"|"BehaviorCleanupFailed"|"BehaviorAbortUnsupported"|"BehaviorLifecycleEventInvalid"|"BehaviorKitNotLoaded"} RazorWireBehaviorDiagnosticCode
 */

/**
 * Page navigation manager for same-page section links, active state, optional collapsible panels, and lifecycle-safe rebinding.
 * @public
 * @namespace RazorWire
 * @config window.RazorWire.pageNavigationManager
 * @type {object}
 * @source <rw:scripts /> with rendered `[data-rw-page-nav]` markup
 * @property {Function} scan - Re-scans the document for `[data-rw-page-nav]` roots after custom DOM updates.
 * @property {Function} prune - Removes controllers for disconnected roots.
 * @property {Function} refreshActiveFromHash - Recomputes active links from the current `window.location.hash`.
 * @property {Function} syncActiveLinkVisibility - Re-checks the current active link and reveals it inside the nearest visible vertical overflowing page-nav container without scrolling the document viewport.
 * @property {Function} getDiagnostics - Returns page navigation diagnostics recorded since startup.
 * @property {Function} clearDiagnostics - Clears recorded page navigation diagnostics.
 */

/**
 * Section copy manager for framework-neutral section permalink buttons, generated copy controls, clipboard fallback UI, and lifecycle-safe rebinding.
 * @public
 * @namespace RazorWire
 * @config window.RazorWire.sectionCopyManager
 * @type {object}
 * @source <rw:scripts /> with rendered `[data-rw-section-copy]` or `[data-rw-section-copy-target]` markup
 * @property {Function} scan - Re-scans the document for section-copy roots and markers after custom DOM updates.
 * @property {Function} prune - Removes controllers for disconnected roots.
 * @property {Function} getDiagnostics - Returns an array of stable section-copy diagnostic objects recorded since startup.
 * @property {string} getDiagnostics[].message - Required reader-facing problem statement for the invalid marker or runtime state.
 * @property {string} getDiagnostics[].impact - Required explanation of the behavior RazorWire skipped, changed, or could not guarantee.
 * @property {string} getDiagnostics[].fix - Required remediation guidance suitable for docs, tests, and development diagnostics.
 * @property {string} getDiagnostics[].docs - Required repository documentation path for the related troubleshooting guidance.
 * @property {Function} clearDiagnostics - Clears recorded section copy diagnostics.
 */

/**
 * A RazorWire-enhanced form started submitting through Turbo.
 * @public
 * @namespace RazorWire
 * @event razorwire:form:submit-start
 * @target form[data-rw-form="true"]
 * @firesWhen Turbo begins submitting a RazorWire-enhanced form and RazorWire marks it busy.
 * @property {HTMLFormElement} detail.form - Submitted form.
 * @property {HTMLElement|null} detail.submitter - Button or submit control that initiated the submission.
 * @bubbles true
 * @cancelable false
 */

/**
 * A RazorWire page navigation root changed its active link.
 * @public
 * @namespace RazorWire
 * @event razorwire:page-nav:active-change
 * @target [data-rw-page-nav]
 * @firesWhen RazorWire promotes a same-page link to the active section from hash, scroll, or click state.
 * @property {Element|null} detail.link - Active link element, or null when no target link is active.
 * @bubbles true
 * @cancelable false
 */

/**
 * Failure payload passed through event.detail for a failed RazorWire-enhanced form submission.
 * @public
 * @namespace RazorWire
 * @typedef {Object} FormFailureDetail
 * @property {HTMLFormElement} form - Submitted form.
 * @property {HTMLElement|null} submitter - Button or submit control that initiated the submission.
 * @property {number|null} statusCode - HTTP status code when available.
 * @property {boolean} handled - Whether the server response already handled the failure.
 * @property {"turbo-stream"|"html"|"json"|"unknown"|"network"} responseKind - Failure category.
 * @property {Element} target - Stream target or form that should own the failure UI.
 * @property {string} message - Reader-facing fallback message.
 * @property {Object|null} developmentDiagnostic - Development diagnostic payload when enabled.
 */

/**
 * A RazorWire-enhanced form submission failed and custom UI may handle the failure.
 * @public
 * @namespace RazorWire
 * @event razorwire:form:failure
 * @target form[data-rw-form="true"]
 * @firesWhen Turbo reports a failed form submission or RazorWire catches a network failure.
 * @property {FormFailureDetail} detail - Failure payload.
 * @property {HTMLFormElement} detail.form - Submitted form.
 * @property {HTMLElement|null} detail.submitter - Button or submit control that initiated the submission.
 * @property {number|null} detail.statusCode - HTTP status code when available.
 * @property {boolean} detail.handled - Whether the server response already handled the failure.
 * @property {"turbo-stream"|"html"|"json"|"unknown"|"network"} detail.responseKind - Failure category.
 * @property {Element} detail.target - Stream target or form that should own the failure UI.
 * @property {string} detail.message - Reader-facing fallback message.
 * @property {Object|null} detail.developmentDiagnostic - Development diagnostic payload when enabled.
 * @bubbles true
 * @cancelable true
 */

/**
 * Development diagnostics are available for a failed RazorWire-enhanced form submission.
 * @public
 * @namespace RazorWire
 * @event razorwire:form:diagnostic
 * @target form[data-rw-form="true"]
 * @firesWhen development diagnostics are enabled for a failed RazorWire-enhanced form.
 * @property {HTMLFormElement} detail.form - Submitted form.
 * @property {number|null} detail.statusCode - HTTP status code when available.
 * @property {string} detail.title - Short diagnostic title.
 * @property {string} detail.detail - Diagnostic explanation.
 * @property {string} detail.docsHref - Documentation link target.
 * @property {string[]} detail.hints - Suggested fixes.
 * @bubbles true
 * @cancelable false
 */

/**
 * A RazorWire-enhanced form finished submitting.
 * @public
 * @namespace RazorWire
 * @event razorwire:form:submit-end
 * @target form[data-rw-form="true"]
 * @firesWhen Turbo finishes a RazorWire-enhanced form submission or RazorWire handles a fetch error.
 * @property {HTMLFormElement} detail.form - Submitted form.
 * @property {HTMLElement|null} detail.submitter - Button or submit control that initiated the submission.
 * @property {boolean} detail.success - Whether the submission succeeded.
 * @property {number|null} detail.statusCode - HTTP status code when available.
 * @property {boolean} detail.handled - Whether the server response already handled the result.
 * @bubbles true
 * @cancelable false
 */

/**
 * A RazorWire stream source reported a native EventSource error.
 * @public
 * @namespace RazorWire
 * @event razorwire:stream:error
 * @target rw-stream-source
 * @firesWhen The browser reports an EventSource error for a registered RazorWire stream source. Native EventSource does not expose HTTP status codes or response bodies to application JavaScript, so use server logs and the Network tab for exact rejection reasons.
 * @property {string|null} detail.channel - Client-derived channel token for the stream source.
 * @property {Element} detail.source - Stream source element that observed the error.
 * @property {"connecting"|"connected"|"disconnected"|string} detail.state - Last RazorWire stream state before the error callback.
 * @property {number} detail.readyState - Native EventSource readyState value.
 * @property {string} detail.src - Stream source URL.
 * @bubbles true
 * @cancelable false
 */

/**
 * Enables RazorWire form failure handling on a form.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-form
 * @target form
 * @type {"true"}
 */

/**
 * Marks a same-page navigation root that RazorWire should enhance.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-page-nav
 * @target nav, aside, div
 * @type {"true"}
 */

/**
 * Marks an optional root that scopes section-copy status, generated buttons, feedback timers, and cleanup.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-section-copy-root
 * @target section, article, main, div
 * @type {"true"}
 */

/**
 * Marks a button that copies a section permalink for the referenced target id.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-section-copy
 * @target button
 * @type {string}
 * @value target id with optional leading #
 */

/**
 * Provides reader-facing section title copy for `aria-label`, live status, and fallback dialog labels.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-section-copy-title
 * @target button[data-rw-section-copy], [data-rw-section-copy-target]
 * @type {string}
 */

/**
 * Marks an optional live status region for section-copy feedback.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-section-copy-status
 * @target span, div
 * @type {"true"}
 * @related aria-live
 */

/**
 * Marks a heading or section container that should receive a generated plain-text copy button.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-section-copy-target
 * @target h1, h2, h3, h4, h5, h6, header, section, div
 * @type {"true"}
 * @requires id
 */

/**
 * Marks an anchor as a RazorWire same-page navigation link.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-page-nav-link
 * @target a
 * @type {"true"}
 */

/**
 * Marks a button that toggles the optional page navigation panel.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-page-nav-toggle
 * @target button
 * @type {"true"}
 * @related aria-controls
 */

/**
 * Marks the optional panel that should close after successful same-page navigation.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-page-nav-panel
 * @target div, nav, ul
 * @type {"true"}
 */

/**
 * Stable selector for page navigation roots that have been enhanced by RazorWire.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-page-nav-enhanced="true"]
 * @hookKind data-attribute
 * @target [data-rw-page-nav]
 * @stability stable
 */

/**
 * Stable selector for the active page navigation link.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-page-nav-active="true"]
 * @hookKind data-attribute
 * @target a[data-rw-page-nav-link]
 * @stability stable
 */

/**
 * Logical CSS inset contract for active page navigation reveal inside overflowing vertical nav surfaces.
 * @public
 * @namespace RazorWire
 * @cssHook scroll-padding-block
 * @hookKind css-property
 * @target visible vertical scrollable ancestor between `a[data-rw-page-nav-link]` and `[data-rw-page-nav]`
 * @stability stable
 */

/**
 * Start-side CSS inset contract for active page navigation reveal inside overflowing vertical nav surfaces.
 * @public
 * @namespace RazorWire
 * @cssHook scroll-padding-top
 * @hookKind css-property
 * @target visible vertical scrollable ancestor between `a[data-rw-page-nav-link]` and `[data-rw-page-nav]`
 * @stability stable
 */

/**
 * End-side CSS inset contract for active page navigation reveal inside overflowing vertical nav surfaces.
 * @public
 * @namespace RazorWire
 * @cssHook scroll-padding-bottom
 * @hookKind css-property
 * @target visible vertical scrollable ancestor between `a[data-rw-page-nav-link]` and `[data-rw-page-nav]`
 * @stability stable
 */

/**
 * Stable selector for page navigation panels that RazorWire closed.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-page-nav-panel-state="closed"]
 * @hookKind data-attribute
 * @target [data-rw-page-nav-panel]
 * @stability stable
 */

/**
 * Stable selector for roots enhanced by the section-copy runtime.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-section-copy-enhanced="true"]
 * @hookKind data-attribute
 * @target [data-rw-section-copy-root], body
 * @stability stable
 */

/**
 * Stable selector for generated section-copy buttons.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-section-copy-inserted="true"]
 * @hookKind data-attribute
 * @target button[data-rw-section-copy]
 * @stability stable
 */

/**
 * Stable selector for transient copy feedback state.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-section-copy-state="copied|fallback"]
 * @hookKind data-attribute
 * @target button[data-rw-section-copy]
 * @stability stable
 */

/**
 * Stable selector for generated section-copy feedback text attached to a button.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-section-copy-message]
 * @hookKind data-attribute
 * @target button[data-rw-section-copy]
 * @stability stable
 */

/**
 * Stable selector for runtime-generated section-copy clipboard fallback UI.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-section-copy-fallback="true"]
 * @hookKind data-attribute
 * @target generated fallback dialog
 * @stability stable
 */

/**
 * Stable selector for runtime-generated section-copy status regions.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-section-copy-status-generated="true"]
 * @hookKind data-attribute
 * @target generated status region
 * @stability stable
 */

/**
 * Selects how RazorWire renders unhandled form failures.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-form-failure
 * @target form[data-rw-form="true"]
 * @type {"auto"|"manual"|"off"}
 * @default auto
 */

/**
 * Stable selector for generated form failure UI.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-form-error-generated="true"]
 * @hookKind data-attribute
 * @target generated form failure UI
 * @stability stable
 */

/**
 * Controls generated form failure text color.
 * @public
 * @namespace RazorWire
 * @cssCustomProperty --rw-form-error-text
 * @target [data-rw-form-error-generated="true"]
 * @syntax <color>
 * @default #3f3f46
 */

/**
 * Island modules may export mount to hydrate a server-rendered root.
 * @public
 * @namespace RazorWire
 * @moduleContract mount
 * @target module referenced by data-rw-module
 * @signature mount(root, props)
 * @param {HTMLElement} root - Island root element.
 * @param {Record<string, unknown>} props - Parsed island props.
 */

/**
 * Names the browser module that should hydrate an island root.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-module
 * @target [data-rw-module]
 * @type {string}
 * @description Logical module name, import-map specifier, or safe module URL. Hosts may define `window.RazorWireIslandModules` to map logical names to host-provided module specifiers before RazorWire calls dynamic import. Inline `data:` module content and protocol-relative URLs are rejected.
 */

/**
 * Form interactions manager for conditional form targets and one-dimensional model-bound collection mechanics.
 * @public
 * @namespace RazorWire
 * @config window.RazorWire.formInteractionsManager
 * @type {object}
 * @source <rw:scripts /> with rendered `[data-rw-form-toggle]` or `[data-rw-form-collection]` markup
 * @property {Function} scan - Re-scans the document for forms containing form-interaction markers after custom DOM updates.
 * @property {Function} prune - Removes controllers for disconnected forms.
 * @property {Function} getDiagnostics - Returns stable form-interaction diagnostic objects recorded since startup.
 * @property {string} getDiagnostics[].message - Required problem statement for invalid form-interaction markup.
 * @property {string} getDiagnostics[].impact - Required explanation of skipped behavior or submit-semantics risk.
 * @property {string} getDiagnostics[].fix - Required remediation guidance.
 * @property {string} getDiagnostics[].docs - Required repository documentation path for troubleshooting.
 * @property {Function} clearDiagnostics - Clears recorded diagnostics.
 */

/**
 * A conditional form toggle is about to reveal or hide its targets.
 * @public
 * @namespace RazorWire
 * @event razorwire:form-toggle:before-change
 * @target [data-rw-form-toggle]
 * @firesWhen A form toggle value changes and RazorWire has resolved the same-form targets that may be shown or hidden.
 * @property {HTMLFormElement} detail.form - Owning form.
 * @property {HTMLElement} detail.control - Toggle control.
 * @property {HTMLElement} detail.target - First matched target.
 * @property {boolean} detail.visible - Whether targets will be shown.
 * @bubbles true
 * @cancelable true
 */

/**
 * A conditional form toggle finished revealing or hiding its targets.
 * @public
 * @namespace RazorWire
 * @event razorwire:form-toggle:change
 * @target [data-rw-form-toggle]
 * @firesWhen RazorWire finishes applying visibility, state hooks, and optional disabled state for a conditional form target.
 * @property {HTMLFormElement} detail.form - Owning form.
 * @property {HTMLElement} detail.control - Toggle control.
 * @property {HTMLElement} detail.target - First matched target.
 * @property {boolean} detail.visible - Whether targets are shown.
 * @bubbles true
 * @cancelable false
 */

/**
 * A model-bound collection command is about to add a row.
 * @public
 * @namespace RazorWire
 * @event razorwire:form-collection:before-add
 * @target [data-rw-form-collection-add]
 * @firesWhen An add command has cloned the app-authored template and allocated a sparse collection index, before insertion.
 * @property {HTMLFormElement} detail.form - Owning form.
 * @property {HTMLElement} detail.root - Collection root.
 * @property {HTMLElement} detail.control - Command button.
 * @property {HTMLElement} detail.row - Row that will be inserted.
 * @property {string} detail.index - New sparse model-binding index.
 * @property {"add"} detail.action - Collection action.
 * @bubbles true
 * @cancelable true
 */

/**
 * A model-bound collection command added a row.
 * @public
 * @namespace RazorWire
 * @event razorwire:form-collection:add
 * @target [data-rw-form-collection-add]
 * @firesWhen RazorWire inserts a new collection row and enables its hidden `.index` marker.
 * @property {HTMLFormElement} detail.form - Owning form.
 * @property {HTMLElement} detail.root - Collection root.
 * @property {HTMLElement} detail.control - Command button.
 * @property {HTMLElement} detail.row - Inserted row.
 * @property {string} detail.index - New sparse model-binding index.
 * @property {"add"} detail.action - Collection action.
 * @bubbles true
 * @cancelable false
 */

/**
 * A model-bound collection command is about to duplicate a row.
 * @public
 * @namespace RazorWire
 * @event razorwire:form-collection:before-duplicate
 * @target [data-rw-form-collection-duplicate]
 * @firesWhen A duplicate command has cloned the source row, rewritten index tokens, and prepared copyable user values before insertion.
 * @property {HTMLFormElement} detail.form - Owning form.
 * @property {HTMLElement} detail.root - Collection root.
 * @property {HTMLElement} detail.control - Command button.
 * @property {HTMLElement} detail.row - Cloned row that will be inserted.
 * @property {string|null} detail.previousIndex - Source row index.
 * @property {string} detail.index - New sparse model-binding index.
 * @property {"duplicate"} detail.action - Collection action.
 * @bubbles true
 * @cancelable true
 */

/**
 * A model-bound collection command duplicated a row.
 * @public
 * @namespace RazorWire
 * @event razorwire:form-collection:duplicate
 * @target [data-rw-form-collection-duplicate]
 * @firesWhen RazorWire inserts a duplicated collection row with a new sparse model-binding index.
 * @property {HTMLFormElement} detail.form - Owning form.
 * @property {HTMLElement} detail.root - Collection root.
 * @property {HTMLElement} detail.control - Command button.
 * @property {HTMLElement} detail.row - Inserted clone.
 * @property {string|null} detail.previousIndex - Source row index.
 * @property {string} detail.index - New sparse model-binding index.
 * @property {"duplicate"} detail.action - Collection action.
 * @bubbles true
 * @cancelable false
 */

/**
 * A model-bound collection command is about to remove or mark a row.
 * @public
 * @namespace RazorWire
 * @event razorwire:form-collection:before-remove
 * @target [data-rw-form-collection-remove]
 * @firesWhen A remove command resolves its physical-remove or mark-remove mode, before RazorWire mutates the row.
 * @property {HTMLFormElement} detail.form - Owning form.
 * @property {HTMLElement} detail.root - Collection root.
 * @property {HTMLElement} detail.control - Command button.
 * @property {HTMLElement} detail.row - Row that will be removed or marked.
 * @property {"physical-remove"|"mark-remove"} detail.action - Remove action.
 * @property {"physical"|"mark"} detail.removeMode - Remove mode.
 * @property {string} detail.index - Existing model-binding index.
 * @bubbles true
 * @cancelable true
 */

/**
 * A model-bound collection command removed or marked a row.
 * @public
 * @namespace RazorWire
 * @event razorwire:form-collection:remove
 * @target [data-rw-form-collection-remove]
 * @firesWhen RazorWire finishes physically removing a row or marking it for app-owned deletion.
 * @property {HTMLFormElement} detail.form - Owning form.
 * @property {HTMLElement} detail.root - Collection root.
 * @property {HTMLElement} detail.control - Command button.
 * @property {HTMLElement} detail.row - Row that was removed or marked.
 * @property {"physical-remove"|"mark-remove"} detail.action - Remove action.
 * @property {"physical"|"mark"} detail.removeMode - Remove mode.
 * @property {string} detail.index - Existing model-binding index.
 * @bubbles true
 * @cancelable false
 */

/**
 * Marks a control that reveals or hides conditional form targets inside the same form.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-form-toggle
 * @target input, select, textarea, button
 * @type {string}
 */

/**
 * Marks an app-authored target revealed or hidden by a matching form toggle.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-form-toggle-target
 * @target fieldset, section, div
 * @type {string}
 */

/**
 * Marks a one-dimensional ASP.NET Core model-bound collection root.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-form-collection
 * @target div, section, fieldset
 * @type {string}
 */

/**
 * Marks an app-authored collection row.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-form-collection-row
 * @target fieldset, div, tr
 * @type {"true"}
 */

/**
 * Marks the app-authored row template that contains the `__index__` token.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-form-collection-template
 * @target template
 * @type {"true"}
 */

/**
 * Marks a button that adds a row from the collection template.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-form-collection-add
 * @target button
 * @type {"true"}
 */

/**
 * Marks a button that duplicates the nearest collection row.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-form-collection-duplicate
 * @target button
 * @type {"true"}
 */

/**
 * Marks a button that physically removes or marks the nearest collection row.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-form-collection-remove
 * @target button
 * @type {"physical"|"mark"|"true"}
 */

/**
 * Stable selector for forms enhanced by the form-interactions runtime.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-form-interactions-enhanced="true"]
 * @hookKind data-attribute
 * @target form
 * @stability stable
 */

/**
 * Selects when an island module hydrates.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-strategy
 * @target [data-rw-module]
 * @type {"load"|"idle"|"visible"|"only"}
 * @default load
 */

/**
 * JSON props passed to an island module's mount function.
 * @public
 * @namespace RazorWire
 * @attribute data-rw-props
 * @target [data-rw-module]
 * @type {string}
 */
