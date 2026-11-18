<!-- appsurface:unreleased-entry section="included" -->
### Offline release artifact binding

- Add [internal release and promotion verification](../../tools/ForgeTrust.AppSurface.ReleaseContracts/README.md#offline-release-artifact-binding) for a pinned release manifest, a complete observed artifact inventory, and separately identified promotion receipts.
- Replay an actual PackageIndex producer manifest offline. The original package inventory verifies, and substituting one archive under the same ID, version, and filename fails against the original pin.
