<!-- appsurface:unreleased-entry section="included" -->

### Stable Docs sanitizer package proof

- [`ForgeTrust.AppSurface.Docs`](../../Web/ForgeTrust.AppSurface.Docs/README.md#dependency-security-boundary) now ships the reviewed stable `AngleSharp`, `AngleSharp.Css`, and `HtmlSanitizer` dependency graph. Package validation rejects missing, duplicate, ranged, or prerelease declarations and independently restores the packed Docs artifact in a locked consumer, so publish evidence verifies the package a downstream application actually resolves.
