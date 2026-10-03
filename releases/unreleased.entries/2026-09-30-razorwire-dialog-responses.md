<!-- appsurface:unreleased-entry section="included" -->

### Explicit dialog responses for RazorWire

[RazorWire](../../Web/ForgeTrust.RazorWire/README.md#server-selected-dialog-responses) applications can return a status or save response in a supplied accessible modal shell, including an app-owned form. Choose `OpenDialog*` explicitly, overwrite a pending body with `ReplaceDialog*`, and use `CloseDialog()` to close after success. The response builder keeps one dialog slot and exposes `HasActiveDialog`; overwritten content is never rendered.

Start with the [dialog response guide](../../Web/ForgeTrust.RazorWire/Docs/dialog-responses.md) and the [MVC example](../../examples/razorwire-mvc/README.md#server-selected-dialogs). They cover validation focus, response ordering and stale-flow protection, full HTML fallbacks, and application concurrency responsibilities.
