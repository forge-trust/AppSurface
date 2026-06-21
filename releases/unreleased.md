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

- RazorWire hybrid islands now reject inline `data:` module specifiers from both `client-module`/`data-rw-module` and `window.RazorWireIslandModules`, and also reject protocol-relative `//...` module URLs. Move any prototype inline module such as `data:text/javascript,...` into a served module like `/js/my-island.js` that exports `mount(root, props)`.
- RazorWire export now owns HTTP redirect handling for artifact-producing fetches, including crawled routes and conventional `404.html` staging. Same-origin redirects remain supported, while redirects outside the configured export origin and base path fail with `RWEXPORT008` before response content is read or written; routes that intentionally point to a different host or app path should be modeled as external references instead of exporter-managed artifacts.
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
