# RazorWire Runtime Contract Pipeline

RazorWire browser assets are authored in TypeScript and delivered through the same package paths that hosts already use. Application code does not need to move to new script URLs, globals, custom events, DOM hooks, CSS hooks, or island strategy names.

## Maintainer Quickstart

Install the shared web workspace once from the repository root:

```bash
pnpm --dir Web install --frozen-lockfile
```

For a no-change freshness check, run:

```bash
pnpm --dir Web run assets:razorwire:typecheck
pnpm --dir Web run assets:razorwire:test
pnpm --dir Web run assets:razorwire:verify
```

For an edit-build-verify loop, update files under `Web/ForgeTrust.RazorWire/assets/src`, then run:

```bash
pnpm --dir Web run assets:razorwire:build
pnpm --dir Web run assets:razorwire:test
pnpm --dir Web run assets:razorwire:verify
```

The root `assets:typecheck`, `assets:test`, `assets:build`, and `assets:verify` commands run both Docs and RazorWire asset pipelines.

## Source And Output Ownership

- `assets/src/razorwire.ts` is the authored source for the core runtime exposed at `/_content/ForgeTrust.RazorWire/razorwire/razorwire.js`.
- `assets/src/razorwire.islands.ts` is the authored source for the island loader exposed at `/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js`.
- `wwwroot/razorwire/razorwire.js` and `wwwroot/razorwire/razorwire.islands.js` are generated, minified, committed package outputs. Do not edit them by hand.
- `assets/contracts/razorwire-public-contracts.js` is a docs-only manifest for AppSurface Docs JavaScript API harvesting. It documents browser contracts without forcing the harvester to parse generated runtime bundles.
- `wwwroot/razorwire/exampleJsInterop.js` remains hand-authored, demo-only JavaScript. It is not part of the generated runtime pipeline.

Generated outputs stay committed because Razor Class Library static web assets, embedded fallback resources, command-line hosts, and existing consumers depend on those physical paths.

## Public Contract

The TypeScript migration preserves the public browser surface:

- Script paths: `/_content/ForgeTrust.RazorWire/razorwire/razorwire.js` and `/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js`.
- Tag helper output: `<rw:scripts />`, Turbo attributes, optional Turbo CDN URL, SRI, and `crossorigin`.
- Global state: `window.RazorWire`, `window.RazorWire.config`, `connectionManager`, `localTimeFormatter`, and `formFailureManager`.
- Form events: `razorwire:form:submit-start`, `razorwire:form:failure`, `razorwire:form:diagnostic`, and `razorwire:form:submit-end`.
- DOM hooks: `data-rw-*` form and island attributes, including generated failure UI markers.
- CSS hooks: RazorWire form failure variables and generated form error attributes.
- Island strategies: `load`, `idle`, `visible`, and `only`. Use `only` for client-only islands; `immediate` is intentionally not a public strategy name.

No consumer migration is required for the TypeScript asset pipeline. Hosts should keep using `<rw:scripts />` unless they already have a deliberate custom script loading path.

## Diagnostics

Asset diagnostics are one-line, grep-friendly messages with this shape:

```text
CODE Summary. Problem: what failed. Cause: likely reason. Fix: concrete repair. Docs: Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md.
```

Current diagnostics:

- `RWASSET001`: Node.js could not start the asset verifier.
- `RWASSET002`: a generated asset exceeded its raw or gzip budget.
- `RWASSET003`: generated package outputs changed when rebuilt from `assets/src`.
- `RWPACK001`: `dotnet pack` refused to embed stale or unverifiable generated runtime assets.

## Pack Guard

`ForgeTrust.RazorWire.csproj` runs `pnpm --dir Web run assets:razorwire:verify` before `Pack`. This is intentionally pack-only: normal builds and tests do not require Node.js just to compile C#.

If a release is blocked by a confirmed infrastructure failure, use the emergency bypass:

```bash
dotnet pack Web/ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj /p:VerifyRazorWireGeneratedAssetsBeforePack=false
```

Treat that property as an incident escape hatch. Re-run the asset verifier and remove the bypass before publishing whenever possible.

## Pitfalls

- Do not commit TypeScript source changes without regenerated `wwwroot/razorwire/*.js` outputs.
- Do not change public strategy taxonomy from `only` to `immediate`; existing docs and generated UI use `only`.
- Do not enable source maps by default. Package consumers should receive compact runtime assets without source-map sidecars.
- Do not move AppSurface Docs harvesting back to minified runtime outputs. Update `assets/contracts/razorwire-public-contracts.js` when the public JavaScript contract changes.
- Do not treat `exampleJsInterop.js` as a runtime source file. It exists for demos and examples only.
