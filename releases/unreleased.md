# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.4`. It stays provisional until the next tag is cut.

## What is taking shape

- LocalSecrets Linux `secret-tool` resolution now uses trusted system candidates or an explicit absolute override instead
  of executing the first `secret-tool` discovered on `PATH`.
- `ForgeTrust.AppSurface.Web` now rejects the literal CORS origin wildcard `*` outside Development when AppSurface owns
  the CORS policy, so permissive production APIs must either name explicit browser origins or register host-owned
  ASP.NET Core CORS.

## Included in the next coordinated version

### Release and docs surface

- Stable package release automation now has a protected NuGet publish gate for annotated `vX.Y.Z` tags, including stable-only package-version validation, trusted-publishing environment checks, fresh smoke-install proof, publish-ledger evidence, and `./eng/release publish --base-ref` support so GitHub Release creation can be tied to the intended source branch after NuGet proof is complete.
- Stable package release publishing now gates on verified AppSurface Docs archive evidence. Release authors pass the
  staged docs `versions.json` and trusted archive root into `appsurface-release check` or `publish`; the tool verifies the
  selected public catalog entry, exact tree path, pinned release-manifest digest, route manifest safety, and every
  serveable file before stable GitHub Release publishing can continue.
- `ForgeTrust.AppSurface.Config.LocalSecrets` hardens the explicit file fallback path. Unix fallback directories are created with `0700` mode bits when missing, existing loose parent directories fail closed instead of being modified in place, and JSON files are written or repaired with `0600` mode bits during `set`, `delete`, and `doctor`; reads reject symbolic-link paths and non-canonical mode bits before returning a secret value. `appsurface secrets doctor --store-file` now treats `ready`, `repaired`, and `degraded` posture diagnostics as doctor-style success while keeping `unsupported` path shapes terminal. This is Unix mode-bit hardening, not Windows ACL hardening or a universal POSIX ACL proof; OS-backed LocalSecrets stores remain the recommended local-development path.
- RazorWire hybrid islands now reject inline `data:` module specifiers from both `client-module`/`data-rw-module` and `window.RazorWireIslandModules`, and also reject protocol-relative `//...` module URLs. Move any prototype inline module such as `data:text/javascript,...` into a served module like `/js/my-island.js` that exports `mount(root, props)`.
- RazorWire export now owns HTTP redirect handling for artifact-producing fetches, including crawled routes and conventional `404.html` staging. Same-origin redirects remain supported, while redirects outside the configured export origin and base path fail with `RWEXPORT008` before response content is read or written; routes that intentionally point to a different host or app path should be modeled as external references instead of exporter-managed artifacts.
- AppSurface CLI export and docs export now configure the shared RazorWire `ExportEngine` HTTP client with automatic redirects disabled, so `RWEXPORT008` redirect-boundary checks run before artifact response bodies are read or written.
- RazorWire export now guards generated artifact materialization and AppSurface Docs release archive traversal with `RWEXPORT009`. HTML, CSS, binary assets, `404.html`, docs partials, redirect alias HTML, `_redirects`, frozen route manifests, and release manifests are validated before parent creation, final writes, archive enumeration, metadata reads, or hashing, so symlinks, junctions, reparse points, and lexical output-root escapes are rejected without following them.
- AppSurface LocalSecrets now hardens Linux Secret Service command selection. Linux uses `/usr/bin/secret-tool`, then
  `/bin/secret-tool`, or an explicit trusted absolute path through `AppSurfaceLocalSecretsOptions.LinuxSecretToolPath`
  and `appsurface secrets --secret-tool-path`. PATH matches are reported only as ignored diagnostic context, invalid
  overrides fail before command launch, and `--secret-tool-path` cannot be combined with `--store-file`.
- AppSurface Web CORS startup validation now fails closed before policy registration when non-development
  `CorsOptions.AllowedOrigins` includes the exact literal `*`, while preserving Development all-origin convenience and
  wildcard subdomain origins such as `https://*.example.com`.

## Migration watch

- Production AppSurface-managed CORS no longer accepts `AllowedOrigins = ["*"]`. Replace the literal wildcard with
  explicit origins such as `["https://app.example.com"]`; keep local permissive behavior behind
  `EnableAllOriginsInDevelopment`; use wildcard subdomains such as `["https://*.example.com"]` only when matching
  subdomains; and register/apply host-owned ASP.NET Core CORS when an API is intentionally public to every browser
  origin.
- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
- RazorWire no longer treats `data:text/javascript,...` values in `window.RazorWireIslandModules` as importable modules. Use a relative, root-relative, same-origin, explicit HTTPS, or bare import-map module specifier instead.
