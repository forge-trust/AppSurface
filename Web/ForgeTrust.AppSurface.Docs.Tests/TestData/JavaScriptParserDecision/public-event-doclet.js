/**
 * Raised when a RazorWire form response returns validation diagnostics.
 * @public
 * @event razorwire:form:failure
 * @target form
 * @firesWhen the server returns validation errors for a submitted form
 * @property {string} detail.message - Human-readable summary for custom UI.
 * @example
 * form.addEventListener("razorwire:form:failure", (event) => {
 *   showError(event.detail.message);
 * });
 */
