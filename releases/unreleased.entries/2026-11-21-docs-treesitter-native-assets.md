<!-- appsurface:unreleased-entry section="migration-watch" -->

- The [AppSurface Docs NuGet package](../../Web/ForgeTrust.AppSurface.Docs/README.md#tree-sitter-native-assets-in-build-and-publish-output) now keeps only the SDK host's Tree-sitter natives in ordinary build and publish output when there is an exact eligible RID match. Use an explicit publish RID for a known destination or set `AppSurfaceDocsKeepAllTreeSitterRuntimes=true` for an artifact that needs all bundled platforms.
