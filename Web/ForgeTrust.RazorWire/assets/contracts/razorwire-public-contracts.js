/**
 * Browser global that exposes RazorWire runtime managers and runtime configuration for diagnostics and advanced integrations.
 * @public
 * @namespace RazorWire
 * @global
 * @name window.RazorWire
 */
window.RazorWire = window.RazorWire || {};

/**
 * Optional island module manifest that maps logical `data-rw-module` names to approved module URLs before RazorWire imports them.
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
 * Page navigation manager for same-page section links, active state, optional collapsible panels, and lifecycle-safe rebinding.
 * @public
 * @namespace RazorWire
 * @config window.RazorWire.pageNavigationManager
 * @type {object}
 * @source <rw:scripts /> with rendered `[data-rw-page-nav]` markup
 * @property {Function} scan - Re-scans the document for `[data-rw-page-nav]` roots after custom DOM updates.
 * @property {Function} prune - Removes controllers for disconnected roots.
 * @property {Function} refreshActiveFromHash - Recomputes active links from the current `window.location.hash`.
 * @property {Function} getDiagnostics - Returns page navigation diagnostics recorded since startup.
 * @property {Function} clearDiagnostics - Clears recorded page navigation diagnostics.
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
 * A RazorWire-enhanced form submission failed and custom UI may handle the failure.
 * @public
 * @namespace RazorWire
 * @event razorwire:form:failure
 * @target form[data-rw-form="true"]
 * @firesWhen Turbo reports a failed form submission or RazorWire catches a network failure.
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
 * Stable selector for page navigation panels that RazorWire closed.
 * @public
 * @namespace RazorWire
 * @cssHook [data-rw-page-nav-panel-state="closed"]
 * @hookKind data-attribute
 * @target [data-rw-page-nav-panel]
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
 * @description Logical module name, import-map specifier, or safe module URL. Hosts may define `window.RazorWireIslandModules` to map logical names to pre-approved module URLs before RazorWire calls dynamic import.
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
