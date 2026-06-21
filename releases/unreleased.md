# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.4`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- RazorWire hybrid islands now reject inline `data:` module specifiers from both `client-module`/`data-rw-module` and `window.RazorWireIslandModules`, and also reject protocol-relative `//...` module URLs. Move any prototype inline module such as `data:text/javascript,...` into a served module like `/js/my-island.js` that exports `mount(root, props)`.
- RazorWire export now owns HTTP redirect handling for artifact-producing fetches, including crawled routes and conventional `404.html` staging. Same-origin redirects remain supported, while redirects outside the configured export origin and base path fail with `RWEXPORT008` before response content is read or written; routes that intentionally point to a different host or app path should be modeled as external references instead of exporter-managed artifacts.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
- RazorWire no longer treats `data:text/javascript,...` values in `window.RazorWireIslandModules` as importable modules. Use a relative, root-relative, same-origin, explicit HTTPS, or bare import-map module specifier instead.
