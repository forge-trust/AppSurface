/**
 * Browser global that exposes RazorWire runtime managers and runtime configuration for diagnostics and advanced integrations.
 * @public
 * @namespace RazorWire
 * @global
 * @name window.RazorWire
 */
window.RazorWire = window.RazorWire || {};

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
 * A RazorWire-enhanced form started submitting through Turbo.
 * @public
 * @namespace RazorWire
 * @event razorwire:form:submit-start
 * @target form[data-rw-form="true"]
 * @firesWhen Turbo begins submitting a RazorWire-enhanced form and RazorWire marks it busy.
 * @detail none
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
 * @property {Element} detail.target - Stream target or form that should own the failure UI.
 * @property {string} detail.message - Reader-facing fallback message.
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
 * @property {number} detail.statusCode - HTTP status code when available.
 * @property {string} detail.title - Short diagnostic title.
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
 * @property {boolean} detail.success - Whether the submission succeeded.
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
