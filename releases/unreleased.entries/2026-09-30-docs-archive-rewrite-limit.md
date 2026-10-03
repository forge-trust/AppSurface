<!-- appsurface:unreleased-entry section="included" -->
### Larger published Docs archives

- The [AppSurface Docs archive verifier](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-docs-verify-archive) now accepts `--max-rewritten-file-size-bytes` so operators can check an exact tree against the same explicit [rewrite limit](../../Web/ForgeTrust.AppSurface.Docs/README.md#published-tree-rewrite-limit) configured on a mounting host. The default remains 4 MiB, and the supported ceiling remains 32 MiB. Hosts mounting the `0.2.0-preview.11` docs archive need a 16 MiB limit for its 8.7 MB search index; the release publication workflow uses that limit while preserving the immutable tagged archive.
