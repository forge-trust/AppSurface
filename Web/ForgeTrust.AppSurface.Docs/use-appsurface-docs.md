# Use AppSurface Docs in your repository

AppSurface Docs is the documentation surface for a repository that wants authored guidance, working examples, and source-derived API reference in one place. It is not a separate content management system. It reads the docs and code you already keep with the product, then serves them through a navigable ASP.NET Core experience.

AppSurface is the first proof site. The public docs you are reading are harvested from this repository, grouped with AppSurface Docs metadata, searched with the built-in search index, and published from the same package a consumer can install.

## When to use it

Use AppSurface Docs when your repository has more than API reference and less than a full documentation platform team.

Good fits:

- A .NET library or app with package READMEs, examples, and XML-doc-commented APIs.
- A product repo where install, upgrade, release, and troubleshooting docs need to stay close to code.
- An internal platform where engineers need a searchable source-of-truth site instead of scattered Markdown links.
- A docs site that should prove the package it describes by dogfooding its own renderer.

Poor fits:

- A marketing site where every section needs bespoke campaign design.
- A documentation source that does not live with code and does not benefit from generated API reference.
- A static artifact that must be edited by non-technical authors without touching Git.

## The consumer model

AppSurface Docs has three moving parts:

1. **A host** that runs `AppSurfaceDocsWebModule` or the standalone AppSurface Docs app.
2. **A source repository** that contains Markdown pages, package READMEs, examples, C# source, and annotated JavaScript browser contracts.
3. **Metadata** that tells AppSurface Docs how to group, feature, search, and explain those pages.

The result is a docs surface with:

- section-first navigation such as Start Here, Examples, Releases, Troubleshooting, and API Reference
- source-derived C# API pages
- annotation-first JavaScript public API pages for browser events, globals, attributes, config, module contracts, and CSS hooks
- a search index that includes titles, summaries, headings, aliases, keywords, and page types
- optional trust bars for release notes, policies, and provenance-heavy pages
- optional `Source of truth` links back to the exact files readers should inspect or edit

## Fastest path

For a dedicated docs host, reference `ForgeTrust.AppSurface.Docs` and run the module:

```csharp
await WebApp<AppSurfaceDocsWebModule>.RunAsync(args);
```

Point the host at the repository you want to harvest:

```json
{
  "AppSurfaceDocs": {
    "Mode": "Source",
    "Source": {
      "RepositoryRoot": "/path/to/repo"
    }
  }
}
```

If `AppSurfaceDocs:Source:RepositoryRoot` is omitted, AppSurface Docs falls back to repository discovery from the app content root. That is convenient for local dogfooding, but production hosts should make the repository root explicit so the docs source is not guessed from deployment layout.

## Run multiple independent Docs products

Use the named `AddAppSurfaceDocs` overload when one host owns separate Docs products with different source
boundaries, identities, route families, or authorization audiences. For example, a host can expose public product
documentation at `/docs` and contributor documentation at `/internal/docs`, with each product reading a different
configuration section:

```json
{
  "AppSurfaceDocs": {
    "Public": {
      "Source": {
        "RepositoryRoot": "/srv/public-product"
      },
      "Routing": {
        "RouteRootPath": "/docs"
      }
    },
    "Internal": {
      "Source": {
        "RepositoryRoot": "/srv/internal-product"
      },
      "Routing": {
        "RouteRootPath": "/internal/docs"
      }
    }
  }
}
```

Register each section during service configuration, retain both returned handles, map each handle once, and apply
host-owned authorization to the internal group. The public group below intentionally has no authorization convention,
so it remains public/default; `RequireAuthorization` is an ASP.NET Core host policy, not a Docs package policy:

```csharp
var publicDocs = builder.Services.AddAppSurfaceDocs(
    "public",
    builder.Configuration.GetSection("AppSurfaceDocs:Public"));
var internalDocs = builder.Services.AddAppSurfaceDocs(
    "internal",
    builder.Configuration.GetSection("AppSurfaceDocs:Internal"));

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.UseEndpoints(endpoints =>
{
    // One shared RazorWire transport serves both isolated Docs progress channels.
    endpoints.MapRazorWire();
    publicDocs.MapEndpoints(endpoints);
    internalDocs.MapEndpoints(endpoints)
        .RequireAuthorization("InternalDocs");
    endpoints.FinalizeAppSurfaceDocsInstances();
});
```

`MapEndpoints` returns a deferred endpoint convention builder. The public mapping is the public/default audience;
the internal convention applies to every endpoint in that Docs product, including its operational routes. Register the
`InternalDocs` policy and the host's authentication middleware; AppSurface Docs does not create identities,
authentication handlers, or authorization policies. See the [ASP.NET Core authorization guidance](https://learn.microsoft.com/aspnet/core/security/authorization/introduction)
for policy design and the [AppSurface Docs operator-route guidance](./README.md#operator-diagnostics-routes) for the
package's own diagnostic exposure settings.

Named registration adds RazorWire services for the live harvest observatory, but does not map its shared HTTP
transport. Add `endpoints.MapRazorWire()` from `ForgeTrust.RazorWire` once as shown above. Stream authorization stays
host-owned; Docs additionally enforces the current product's diagnostics visibility/read policy and the complete
authorization requirements attached through its `MapEndpoints` handle. That includes default, role,
authentication-scheme, and multiple named `RequireAuthorization(...)` policies.

Call `FinalizeAppSurfaceDocsInstances` exactly once, after every handle has been mapped. Finalization validates all
named configuration, builds isolated runtimes, applies the deferred conventions, and publishes the route families.
Do not map a handle twice, map after finalization, finalize before every handle is mapped, or finalize twice. All
handles must be mapped on the same endpoint route builder. Host startup then runs Markdown-policy validation,
diagnostics exposure warnings, and harvest warmup/preflight for every finalized instance; an error identifies its
owning instance.

If host-owned views or extensions need the active named product, inject
`IAppSurfaceDocsRequestRuntimeAccessor` and call `GetRequiredRuntime()` during the request. Named endpoints carry
`AppSurfaceDocsEndpointMetadata` with the normalized product name, so the accessor selects the runtime from endpoint
metadata instead of guessing from a URL prefix. The returned `AppSurfaceDocsRuntime` provides the product `Name`,
immutable `Options`, and instance-aware `DocsUrlBuilder`; it is created during finalization and should not be
constructed or disposed by the host. See the [package reference for request-time runtime selection](./README.md#request-time-runtime-selection)
for the extension example and lifecycle details.

### Run the public/internal consumer proof

The repository includes a real Kestrel-hosted [public and internal ConsumerFixture walkthrough](../ForgeTrust.AppSurface.Docs.ConsumerFixture/MULTI_INSTANCE_WALKTHROUGH.md).
It compiles the same two registrations, one host-owned policy, two mappings, and one finalization call shown above.
Its integration test proves anonymous public reading, an internal challenge, authenticated contributor reading,
isolated search payloads, and distinct AppSurface/Graphite Docs themes in under five minutes. Treat the fixture header
authentication mechanism as test-only: production hosts must register their own scheme, policies, and middleware.

Named composition is strict and is not a second registration spelling for the legacy default surface:

- Use either named composition or the legacy parameterless `AddAppSurfaceDocs()`/`AppSurfaceDocsWebModule` path for a
  host, never both.
- Instance names are unique case-insensitively, 1–64 characters, and may contain only ASCII letters, digits,
  hyphens, and underscores. A host can declare at most eight named instances.
- Route families must be disjoint. Do not use the same route root or an ancestor/descendant pair; choose sibling
  roots such as `/docs` and `/internal/docs`.
- Every named product must explicitly set `Source:RepositoryRoot`; repository-root discovery is unavailable because it
  could merge product boundaries. Configured source roots must be different, and a configured branding asset request
  prefix must be disjoint from every other route family and configured branding prefix, including ancestor/descendant
  pairs.
- Each instance gets a complete snapshot from its own configuration section. Do not expect one instance's options,
  harvest cache, identity, search state, or version catalog to be shared with another.

Named instances host both live routes and configured [published version archives](./README.md#published-version-catalog).
Static export remains one-product-at-a-time: export each Docs product from a host configured for that product, then
point the named runtime at that product's catalog and trusted release root. A multi-instance host does not produce one
combined static tree.

### Five-minute protected Markdown download

Protected Markdown download is disabled by default. To enable the v1 browser attachment, the host must own and register a named ASP.NET Core reader policy; AppSurface Docs does not create identities, authentication handlers, or authorization policies.

1. Register a reader policy in the host and choose its name, for example `DocsMarkdownReader`:

   ```csharp
   services.AddAuthorization(options =>
   {
       options.AddPolicy("DocsMarkdownReader", policy =>
       {
           policy.RequireAuthenticatedUser();
           // Add host-specific reader requirements, such as roles or claims, here.
       });
   });
   ```

2. Put authentication before authorization in endpoint-aware middleware:

   ```csharp
   app.UseRouting();
   app.UseAuthentication();
   app.UseAuthorization();
   ```

3. Enable the feature and point it at that policy. The default snapshot limit is 8,388,608 bytes; the valid range is 1 through 33,554,432:

   ```json
   {
     "AppSurfaceDocs": {
       "MarkdownDownload": {
         "Enabled": true,
         "AuthorizationPolicy": "DocsMarkdownReader",
         "MaxSnapshotBytes": 8388608
       }
     }
   }
   ```

4. Add an exact inline opt-in to a Markdown page. A paired `.md.yml` or `.md.yaml` sidecar never grants download access:

   ```markdown
   ---
   download_markdown: true
   ---
   ```

5. Verify an authenticated browser `GET` and `HEAD` at `{DocsRootPath}/_markdown/{canonical path}`. `GET` returns a `text/markdown` attachment with `Cache-Control: private, no-store` and the exact original valid UTF-8 bytes, including any BOM, front matter, comments, line endings, and relative links. `HEAD` returns matching attachment metadata without a body. `{DocsRootPath}/_markdown` is package-reserved, so do not use it as a page or alias path.

Only the exact inline boolean `download_markdown: true` opts a page in. Quoted truthy strings, `True`, `1`, nested or duplicate keys, malformed front matter, aliases, generated pages, and archive paths do not. Disabled, unavailable, noncanonical, alias, generated, archive, missing-snapshot, and invalid-UTF-8-source requests return `404`; unsupported methods return `405` with `Allow: GET, HEAD`; anonymous and forbidden enabled requests retain the host's normal challenge/forbid behavior.

Source safety matters: this endpoint returns source bytes, not rendered or sanitized HTML. Keep secrets, private notes, credentials, generated build output, and other non-public material outside the configured [public source boundary](#define-the-public-source-boundary), and follow the [Markdown authoring guidance](#author-the-first-useful-page-set) when choosing inline front matter versus a sidecar. Browser attachment is v1 only; there is no API, batch, vendor, or automatic “second-brain” synchronization integration.

| Symptom | Check | Fix |
| --- | --- | --- |
| `404` for an intended page | The page has exact inline `download_markdown: true`, and the requested path is canonical. | Remove quotes and nesting, fix malformed front matter, or use the canonical Markdown route. |
| `404` after adding the flag to a sidecar | The flag is in `.md.yml` or `.md.yaml`. | Move the opt-in into the Markdown file's top-level inline front matter. |
| Policy startup failure | The named policy is missing or has no reader requirements. | Register a non-empty host-owned reader policy. |
| Unexpected `401`/`403` | Authentication or authorization middleware is missing or out of order. | Configure the host identity, then run authentication before authorization. |
| Download is unexpectedly refused | The aggregate opted-in source snapshot exceeds `MaxSnapshotBytes`, which must be between 1 and 33,554,432. | Reduce opted-in source bytes, raise the limit only for intentional published source, or keep the page out of the download surface. |

This is a compatibility-preserving opt-in. Existing hosts remain unchanged while `Enabled=false`; consumers that enable it must provide the named policy and accept that the endpoint exposes original source bytes. For the complete package contract, see the [protected Markdown download reference](./README.md#protected-markdown-download).

For production or preview hosts that expose diagnostics, configure a host-owned read policy before opting in:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Health": {
        "ExposeRoutes": "Always"
      }
    },
    "Diagnostics": {
      "ExposeRouteInspector": "Always",
      "OperatorReadPolicy": "DocsOperatorRead"
    }
  }
}
```

`Diagnostics:OperatorReadPolicy` protects `_harvest`, `_routes`, `_routes.json`, and the harvest progress stream; it also protects `_health` and `_health.json` unless the legacy `Harvest:Health:AuthorizationPolicy` is set for a split health-only audience. Register the named ASP.NET Core policy and call `UseAuthentication()` before `UseAuthorization()` in endpoint-aware middleware. Exposed non-development diagnostics without `OperatorReadPolicy` log a startup warning so proxy- or network-protected hosts can verify that boundary intentionally.

Add identity settings when the consuming repository should own the visible docs brand:

```json
{
  "AppSurfaceDocs": {
    "Identity": {
      "DisplayName": "Acme Platform Docs",
      "HomeHref": "/docs",
      "Wordmark": {
        "HighlightText": "Platform",
        "HighlightColor": "#38bdf8"
      },
      "Logo": {
        "Path": "/branding/docs-logo.png",
        "AltText": "Acme"
      },
      "Favicon": {
        "PngPath": "/branding/favicon.png",
        "IcoPath": "/branding/favicon.ico"
      },
      "BrandingAssets": {
        "DirectoryPath": "branding"
      }
    }
  }
}
```

Identity paths must be app-root paths such as `/branding/docs-logo.svg` or application-relative paths such as `~/branding/docs-logo.svg`. AppSurface Docs rejects remote URLs, relative paths, query strings, fragments, backslashes, and traversal segments during startup validation so the docs chrome cannot accidentally point at unsafe or environment-specific locations.
The configured `Identity:Logo:Path` is used by both the built-in docs chrome and the large root landing page mark, so custom-branded hosts should set it whenever that first-screen icon should differ from the packaged AppSurface Docs mark.

There are two branding asset use cases:

1. AppSurface Docs serves the files. Set `Identity:BrandingAssets:DirectoryPath` to a filesystem directory, usually `branding`, and point `Logo:Path` plus `Favicon:*Path` at browser URLs under `/branding`. With the default request prefix, `branding/docs-logo.png` is referenced as `/branding/docs-logo.png`.
2. The owning application already serves the files. Leave `Identity:BrandingAssets:DirectoryPath` blank and point `Logo:Path` plus `Favicon:*Path` at the host-owned browser URLs, such as `/assets/docs-logo.svg`.

`Identity:BrandingAssets:DirectoryPath` is a filesystem path, not a browser path. Relative values resolve against `AppSurfaceDocs:Source:RepositoryRoot` when that root is configured, then fall back to the host content root. `Logo:Path` and `Favicon:*Path` are browser URL paths, not filenames relative to `DirectoryPath`; AppSurface Docs does not join those values with the directory. Keep `DirectoryPath` pointed at a dedicated public branding directory; AppSurface Docs serves only `.avif`, `.gif`, `.ico`, `.jpg`, `.jpeg`, `.png`, and `.webp` files from that directory by default. Set `Identity:BrandingAssets:AllowSvgAssets=true` only for operator-owned and reviewed SVG branding files; standard SVG optimization does not make arbitrary SVG safe. Override `Identity:BrandingAssets:RequestPath` only when the default `/branding` URL prefix conflicts with an owning application route.

When `Identity:Favicon` is empty, the built-in layout links the packaged AppSurface Docs document-layers SVG mark.
Standalone AppSurface Docs hosts also serve that mark at `/favicon.ico` for the browser's conventional favicon probe.
When `Identity:Favicon:SvgPath` is configured in a standalone host, `/favicon.ico` redirects to that SVG path so the
conventional probe matches the rendered favicon metadata. Embedded hosts leave `/favicon.ico` to the owning application,
and any configured favicon path must be served by the host just like a logo asset.

Leave `Identity:Wordmark` unset for a plain-text docs title. The built-in sidebar and mobile header clip long display names with an ellipsis so they do not push the chrome outside its bounds. Configure `DisplayName`, `HighlightText`, and `HighlightColor` when the publishing repository wants a shorter product wordmark treatment. The highlight text must be a substring of `DisplayName`, and the color must be a CSS hex color so the docs layout cannot receive arbitrary style declarations from configuration.

Add theme settings when the consuming repository should make the built-in docs shell feel first-party without replacing views or overriding internal CSS selectors:

```json
{
  "AppSurfaceDocs": {
    "Theme": {
      "Preset": "GraphiteDark",
      "Colors": {
        "AccentColor": "#38bdf8",
        "AccentStrongColor": "#a5b4fc",
        "LinkColor": "#93c5fd",
        "VisitedLinkColor": "#c4b5fd"
      },
      "Layout": {
        "Density": "Compact",
        "Chrome": "Compact"
      }
    }
  }
}
```

The default theme is `AppSurfaceDark` with comfortable density and standard chrome. `GraphiteDark` is the second dark-family preset for lower-saturation surfaces; it stays a fixed Docs-local dark preset, not a shared pair. Blank color values use the selected preset default. Color overrides must be CSS hex colors and must meet startup contrast checks for their role; validation messages name the exact config key, bad value, required contrast ratio, tested preset background, and fix. The same keys work through environment variables, for example `AppSurfaceDocs__Theme__Preset=GraphiteDark` and `AppSurfaceDocs__Theme__Colors__AccentColor=#38bdf8`.

The supported Docs configuration contract is intentionally narrow. Use `Preset`, `Colors`, `Density`, and `Chrome` for package-owned docs chrome. Do not rely on `--docs-*` custom property names as a public API, do not use theme settings for arbitrary surface/text/syntax-token overrides, and do not expect view replacement, layout slots, or external theme packages in v1. Static exports and published release archives freeze the resolved Docs configuration into their exported HTML; changing host config later does not rewrite already-exported archives.

### Optional browser-local appearance choice

Docs can inherit a user-selected System/Light/Dark appearance without multiplying live or versioned pages. Keep the Docs `AppSurfaceDark` preset at its default and register the shared Graphite pair with the [Web browser-local preference adapter](../ForgeTrust.AppSurface.Web/README.md#browser-local-theme-preferences) before `AddAppSurfaceDocs()`:

```csharp
services.AddAppSurfaceTheming(options =>
{
    options.DefaultTheme = new AppSurfaceThemeId("graphite");
    options.DefaultMode = AppSurfaceThemeMode.System;
    options.Pairs.Add(AppSurfaceThemePair.Graphite());
});
services.AddAppSurfaceWebThemePreferences(options => options.StorageKey = "docs-theme");
services.AddAppSurfaceDocs();
```

The browser applies a valid saved Light/Dark value to the root before styles paint; otherwise System follows the operating system. The URL, canonical metadata, static tree, server cache behavior, and versioned Docs routes stay unchanged. The built-in layout supplies a hidden native `Appearance` radio group only when Docs uses the shared `AppSurfaceDark` bridge; the fixed `GraphiteDark` compatibility preset remains dark and deliberately omits it. The shared path preserves forced-colors and keyboard focus, while hosts own localized status feedback through the documented browser event. Storage denial leaves System usable or keeps the current page selection only for that visit. Raw protected Markdown download is deliberately unrelated: it stays a private, no-store `text/markdown` attachment and never receives a theme head, script, or HTML rewrite.

## Define the public source boundary

Before pointing AppSurface Docs at a large repository, decide which paths are meant to be public. The safest production shape is a small global include list, then optional Markdown, C#, and JavaScript refinements:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Paths": {
        "IncludeGlobs": [
          "README.md",
          "LICENSE",
          "docs/**/*.md",
          "src/**/*.cs",
          "src/**/*.js"
        ],
        "ExcludeGlobs": [
          "docs/drafts/**",
          "**/generated/**"
        ]
      },
      "Markdown": {
        "IncludeGlobs": [
          "README.md",
          "LICENSE",
          "docs/**/*.md"
        ],
        "MaxFileSizeBytes": 1048576,
        "MaxMetadataFileSizeBytes": 65536
      },
      "CSharp": {
        "IncludeGlobs": [
          "src/**/*.cs"
        ]
      },
      "JavaScript": {
        "IncludeGlobs": [
          "src/browser/**/*.js"
        ]
      }
    }
  }
}
```

Use repository-relative globs with `/` separators. AppSurface Docs rejects rooted paths, URI-shaped patterns, query strings, fragments, and `..` segments during startup validation. Empty includes mean the built-in harvester defaults are used; nonempty global includes become the outer boundary for Markdown, C#, and JavaScript.

Markdown resource limits are byte counts. `AppSurfaceDocs:Harvest:Markdown:MaxFileSizeBytes` defaults to `1048576` and skips oversized Markdown bodies before file read, inline-front-matter parsing, and Markdig parsing. `AppSurfaceDocs:Harvest:Markdown:MaxMetadataFileSizeBytes` defaults to `65536` and ignores oversized paired `.md.yml` or `.md.yaml` sidecars before YAML parsing while leaving the Markdown body eligible. These guards are not parser-complexity, AST-depth, timeout, or cancellation limits. Use `AppSurfaceDocs__Harvest__Markdown__MaxFileSizeBytes` and `AppSurfaceDocs__Harvest__Markdown__MaxMetadataFileSizeBytes` for environment-variable configuration.

The package also keeps protective defaults for build output, hidden directories, test projects, and C# source under `examples`. These defaults prevent common accidental publication without requiring every host to write the same excludes. If a default is too broad, use `DefaultExclusions:AllowGlobs` for narrow exceptions or `DefaultExclusions:DisabledGroups` when the entire group is intentionally public. Use the named group IDs, not numeric enum values; ordinals fail startup validation. Allows are group-aware, so a path inside `.github/bin` needs an allow for both `HiddenDirectories` and `BuildOutput` unless one group is disabled.

AppSurface Docs also honors repository-owned Git `.gitignore` files by default. That is meant to make older repositories safer to adopt: generated bundles, `bower_components/`, `dist/`, `build/`, and other ignored trees stay out of the docs harvest without every host writing duplicate AppSurface excludes. This is snapshot-scoped and reproducible; AppSurface reads `.gitignore` files under the configured source root, not `.git/info/exclude` or global developer ignore files.

The VCS-ignore contract is intentionally narrower than Git's full local environment. Repository `.gitignore` files are the source of truth, tracked files that match those rules are still excluded from docs, and matching is ordinal and case-sensitive so Linux, macOS, and Windows harvests agree. Configured AppSurface globs are separate from Git-ignore syntax and remain the package's normal repository-relative glob syntax.

Use `VcsIgnore:AllowGlobs` only for intentionally public docs under ignored paths:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Paths": {
        "VcsIgnore": {
          "AllowGlobs": [
            "docs/generated-public/**"
          ]
        }
      }
    }
  }
}
```

Those allow globs use AppSurface glob syntax, not Git-ignore syntax. They restore only VCS-ignore exclusions; AppSurface default exclusions and configured `ExcludeGlobs` still win. If a host needs the pre-existing behavior, set `AppSurfaceDocs:Harvest:Paths:VcsIgnore:Enabled=false`.

If docs disappear after an upgrade, diagnose one repository-relative path first:

| Symptom | Check | Fix |
| --- | --- | --- |
| Generated or bundled docs vanished. | The path matches a repository `.gitignore` rule. | Add a narrow `VcsIgnore:AllowGlobs` entry for the public docs path. |
| A tracked file vanished even though Git still has it. | The tracked path also matches `.gitignore`. | Keep the ignore rule and add `VcsIgnore:AllowGlobs`, or move the public docs outside the ignored tree. |
| A restored path still does not harvest. | AppSurface default exclusions or configured `ExcludeGlobs` also match it. | Add the matching default-exclusion allow, disable the intended default group, or change the configured exclude. |
| A Markdown page is missing but harvest is still Healthy. | `_health.json` contains `appsurfacedocs.markdown.file_too_large` with the file path, actual bytes, and `MaxFileSizeBytes`. | Exclude generated or accidental docs with `Harvest:Markdown:ExcludeGlobs` or `Harvest:Paths:ExcludeGlobs`, or raise `MaxFileSizeBytes` only for intentional authored docs. |
| A page publishes but its sidecar metadata is missing. | `_health.json` contains `appsurfacedocs.markdown.metadata_file_too_large` for the `.md.yml` or `.md.yaml` sidecar. | Move long prose into Markdown, trim generated metadata, or raise `MaxMetadataFileSizeBytes` only for intentional authored metadata. |
| A configured JavaScript include reports `appsurfacedocs.javascript.reparse_point_skipped`. | The global or JavaScript include resolves to a symlink, junction, or other reparse point. | Replace the link with a real source file, include the real non-link source path, disable JavaScript harvesting, or use a custom harvester for that source. |
| A C# file reports `appsurfacedocs.csharp.file_too_large`. | A policy-approved `.cs` file exceeded `AppSurfaceDocs:Harvest:CSharp:MaxFileSizeBytes` before Roslyn parsing. | Exclude generated source with `AppSurfaceDocs:Harvest:CSharp:ExcludeGlobs`, or raise the C# byte limit only for authored API source that should publish. |
| The host needs time to migrate. | The repository relied on pre-existing AppSurface behavior. | Temporarily set `AppSurfaceDocs:Harvest:Paths:VcsIgnore:Enabled=false` while moving public docs or adding allow globs. |

Use the harvest health page and JSON endpoint to inspect VCS-ignore counts, Markdown resource warnings, and sample paths when a source-backed snapshot looks unexpectedly small. A `Healthy` snapshot can still contain warning diagnostics; check `diagnostics` when release workflows require warning-free docs.

For CI, prefer a diagnostic-code check over a new strict runtime option. Fetch `{DocsRootPath}/_health.json` from the docs host and fail the job when `diagnostics[].code` includes `appsurfacedocs.csharp.file_too_large` for source that should be public. Leave generated files out of the harvest boundary instead of raising the limit globally.

The C# parser-input default is `1048576` bytes. JavaScript remains unchanged at `262144` bytes and still reports `appsurfacedocs.javascript.file_too_large` through the existing JavaScript strict-health rules.

## Understand first harvest behavior

AppSurface Docs starts the first source-backed harvest during application startup by default. If the docs cache is still warming when a user opens `/docs`, the request waits for `AppSurfaceDocs:Harvest:InitialRequestWaitBudgetMilliseconds` and then shows a live RazorWire harvest observatory. The default wait budget is `350` milliseconds.

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "StartupMode": "Background",
      "InitialRequestWaitBudgetMilliseconds": 350,
      "TestingPreHarvestDelayMilliseconds": 0,
      "TestingDelayPerHarvesterMilliseconds": 0,
      "TestingDelayPerDocumentMilliseconds": 0
    }
  }
}
```

Use `StartupMode=Background` for normal hosts, `Blocking` for hosts that must finish docs warmup before accepting traffic, and `Disabled` only when you intentionally want the old first-request lazy harvest. Strict startup failure still comes from `Harvest:FailOnFailure=true`; when strict mode is enabled, startup waits for harvest health and fails only when every active harvester fails.

For manual UI testing, set the `Testing*Delay*Milliseconds` knobs to positive values. `TestingPreHarvestDelayMilliseconds` pauses after the run is published but before any harvester starts, `TestingDelayPerHarvesterMilliseconds` pauses each harvester after it reports `Running`, and `TestingDelayPerDocumentMilliseconds` paces each **real positive parser-output report** once for every document in that report. It does not delay zero-output source inspection. For example, `TestingPreHarvestDelayMilliseconds=1000` and `TestingDelayPerDocumentMilliseconds=150` make the live observatory easy to inspect locally. Keep every knob at `0` for production traffic.

### Read the live harvest observatory

The observatory is package-owned current-state telemetry, not a file activity log. Built-in Markdown, C#, and enabled JavaScript harvesters expose a closed `phase` value (`Waiting`, `Discovering`, `Parsing`, `Finalizing`, or `Terminal`), a non-negative `sourceUnitsProcessed` count, and their current `docCount`. A source unit is a policy-accepted parser input that has begun inspection; it can legitimately yield no document. Terminal health retains the source-unit evidence while replacing status and final document count with the authoritative health result.

`builtInDocumentsPerSecond` is an additive nullable JSON field and the UI labels it `Built-in docs/s`. `null` renders as `Measuring…` until the reporter has a valid 250 ms rolling observation window; numeric `0` is a valid observed result for zero-output parsing. The rate intentionally includes only package-owned parser reports, so custom `IDocHarvester` implementations retain status-only observability and do not need an API upgrade. Snapshot consumers should tolerate this nullable field and the additive phase/count fields without changing existing behavior.

Every ordinary source/output update is coalesced to one full snapshot at most every 250 ms, with bounded replay; phase and terminal state are published promptly. This keeps large repositories and late subscribers on the latest redacted state. The payload never includes a source path, file name, source content, raw exception, stack trace, percentage, or source total. For a local smoke check, set the test-only delays, open `/_harvest`, verify phase/source movement, see `Measuring…` become a rate or `0`, then confirm the terminal row and no-JavaScript continuation before resetting the delays to `0`.

When the first harvest completes, active JavaScript users receive a live-only RazorWire visit command after the retained completion state is published. Late subscribers replay only safe progress state and use the normal continuation link. The live observatory uses the same redacted diagnostics as harvest health; do not put secrets, absolute repository paths, or raw exception messages into diagnostic fields that can reach client-visible UI.

## Author the first useful page set

Start with pages that answer adoption questions before you tune visuals:

- `README.md` for the repository-level entry point.
- `packages/README.md` or package-level READMEs for install choices.
- `examples/.../README.md` for exportable proof paths.
- `releases/README.md`, `CHANGELOG.md`, or upgrade-policy pages when release risk matters.
- Troubleshooting pages for the failure modes your users actually hit.
- `NAMESPACE.md` files beside package/project files when generated API reference needs human orientation above the symbol list. Docs-owned namespace README files such as `docs/ForgeTrust.RazorWire/README.md` are still supported for portable folder-index layouts, but `NAMESPACE.md` is the AppSurface house style.

Use sidecar metadata for portability-sensitive files such as README pages:

```yaml
# README.md.yml
title: My Product
summary: Start here when you need to choose the right package and prove the first workflow.
featured_page_groups:
  - intent: adopt
    label: Adopt the docs package
    summary: The shortest path from repository Markdown to a usable docs site.
    pages:
      - question: How do I host these docs?
        path: docs/hosting.md
        supporting_copy: Start with the host shape, then add metadata once the page renders.
```

Use inline front matter for ordinary authored pages when GitHub rendering is not the primary surface:

```yaml
---
title: Troubleshoot search indexing
summary: Fix missing or stale search results in an AppSurface Docs host.
page_type: troubleshooting
nav_group: Troubleshooting
aliases:
  - search index
  - missing results
---
```

Use `aliases` for search and discovery text. Use `redirect_aliases` only when an old browser URL route should redirect to the page's canonical route:

```yaml
canonical_slug: troubleshooting/search
redirect_aliases:
  - old/search-help
  - old/search-help.md.html
```

`redirect_aliases` values are docs-root-relative routes, not Netlify `_redirects` syntax. Leave out query strings, fragments, host names, splats, placeholders, and status codes. Static export uses HTML alias files by default for generic hosts; use `appsurface docs export --mode cdn --redirects netlify` when publishing to Netlify-compatible CDN hosts. For Netlify export, avoid defining two aliases that differ only by percent encoding unless they point to the same canonical page.

### Fix a poorly ranked page

When a page exists but ranks below a less useful result, start with the authored content and metadata before changing search code:

1. Open `{DocsRootPath}/search?q=your%20query` and note the current top five results. `{DocsRootPath}` defaults to `/docs` when the host has not customized the docs root.
2. Inspect `{DocsRootPath}/search-index.json` for the intended page. Check `title`, `summary`, `headings`, `aliases`, `keywords`, `pageType`, `navGroup`, `audience`, `sourcePath`, and namespace `entryPoints`.
3. Choose the smallest truthful fix:
   - edit `title` or `summary` when the page itself does not describe the reader's intent clearly
   - add `aliases` for page-specific terms readers already use
   - add `keywords` for compact search terms that belong to that page but would read awkwardly in prose
   - set `page_type` and `nav_group` so task pages, API pages, troubleshooting pages, and internal pages are classified honestly
   - add namespace `entry_points` when a generated API namespace needs human entry terms such as registration methods or options types
4. Refresh the source-backed harvest or restart the local host, then verify `{DocsRootPath}/search?q=...` and `{DocsRootPath}/search-index.json` again.
5. Promote important or repeated queries into the search relevance fixture suite so the fix survives future ranking changes.

Do not use metadata as a bag of unrelated synonyms. Page-specific aliases and keywords belong on the page or sidecar that owns them. Cross-page language bridges belong in reviewed relevance fixtures or a deliberately shared synonym layer. Use `aliases` for search terms; use `redirect_aliases` only for browser URLs that should redirect.

For namespace API pages, keep the intro as normal Markdown and put the namespace target plus entry-point metadata in the sidecar:

```yaml
# Web/ForgeTrust.RazorWire/NAMESPACE.md.yml
namespace: ForgeTrust.RazorWire
title: ForgeTrust.RazorWire
summary: Start here for registration, endpoint mapping, and options.
entry_points:
  - label: AddRazorWire(...)
    summary: Register RazorWire services and package-owned options.
    target: ForgeTrust-RazorWire-RazorWireServiceCollectionExtensions-AddRazorWire-method-group
  - label: RazorWireOptions
    summary: Configure stream paths, caching policy names, and form behavior.
    target: ForgeTrust-RazorWire-RazorWireOptions
```

The namespace intro is consumed into `Namespaces/{Dotted.Namespace}` and removed as a standalone page. Copied source-shaped intro URLs such as `Web/ForgeTrust.RazorWire/NAMESPACE.md` redirect to the namespace page after a successful merge. Entry-point `target` values are generated anchors on that namespace page; stale targets render as unlinked rows and produce harvest-health warnings instead of breaking the docs site. If `NAMESPACE.md` cannot resolve a generated namespace target, AppSurface Docs hides the standalone source and reports a harvest-health warning so the author can add `namespace: ...` or rename the file to an ordinary guide.

For troubleshooting pages that repeat the same H3 headings under each issue, AppSurface Docs automatically keeps the `On this page` outline focused on the H2 issue headings while leaving the H3 headings and hash targets in the rendered page body. Override that only when the repeated H3 entries are genuinely useful as reader waypoints:

```yaml
---
page_type: troubleshooting
outline:
  repeated_heading_policy: include
---
```

Use `outline.max_heading_level: 2` when a page should always expose an H2-only outline, or `outline.max_heading_level: 3` when it should always expose H2-H3 entries. `max_heading_level` wins when both outline fields are present.

## Curate the landing page

AppSurface Docs does not require a bespoke homepage template for each repo. The root landing can be curated from metadata:

- Put `featured_page_groups` in `README.md.yml`.
- Group destinations by reader intent, not by folder structure.
- Link to real harvested pages by source path.
- Keep each row focused on the question the reader has in their head.

The important part is that curation stays authored content. If the product story changes, edit Markdown or sidecar YAML. Do not fork `DocsController` to hardcode a new marketing panel.

## Add reference and proof over time

Once the first pages render, improve the docs in layers:

1. Add XML docs to public C# APIs so generated reference pages are useful.
2. Add explicit `@public` JavaScript doclets for intentional browser contracts such as custom events, data attributes, runtime config, island module contracts, and CSS hooks.
3. Add `summary`, `page_type`, `nav_group`, `aliases`, and `keywords` metadata to high-traffic pages.
4. Add troubleshooting pages for the first support questions people ask.
5. Add release notes and trust metadata when adoption depends on upgrade confidence.
6. Add localization metadata when users need more than one language.
7. Add versioned published trees only after the live source-backed docs are useful.

That order matters. A beautiful archive of weak docs is still weak docs.

## Prepare for multiple languages

Localization is optional and disabled by default. Turn it on when the docs system needs to know which files are translations of the same page, even before you expose a language switcher or localized routes.

```json
{
  "AppSurfaceDocs": {
    "Localization": {
      "Enabled": true,
      "DefaultLocale": "en",
      "Locales": [
        { "Code": "en", "Label": "English", "Lang": "en-US", "RoutePrefix": "en" },
        { "Code": "fr", "Label": "Français", "Lang": "fr-FR", "RoutePrefix": "fr" }
      ]
    }
  }
}
```

For colocated translations, files like `README.md` and `README.fr.md` are a good default. AppSurface Docs infers the `fr` locale from the configured suffix and groups the files under one translation key. When translated paths differ, author an explicit key:

```yaml
---
title: Démarrer
locale: fr
translation_key: guides/getting-started
---
```

Use locale folders only with explicit `translation_key` metadata. A file such as `fr/guides/demarrer.md` will not be treated as French just because it lives under `fr/`; that keeps ordinary folders from becoming language roots by accident.

Phase 1 builds the locale graph, validates configuration, and reports diagnostics. Visible `/fr/...` routes, fallback pages, language switchers, localized SEO tags, and locale-filtered search are follow-up surfaces.

## Adoption checklist

- Pick a host: embedded AppSurface web module or standalone AppSurface Docs app.
- Configure `AppSurfaceDocs:Source:RepositoryRoot` for the repository to harvest.
- Configure `AppSurfaceDocs:Harvest:Paths` so only intentional public source paths are eligible.
- Exclude generated C# before raising `AppSurfaceDocs:Harvest:CSharp:MaxFileSizeBytes`; oversized C# reports `appsurfacedocs.csharp.file_too_large` in harvest health.
- Keep `AppSurfaceDocs:Mode` set to `Source` unless a later bundle-hosting slice changes that contract.
- If browser runtime contracts matter, add explicit `@public` JavaScript doclets. JavaScript harvesting is enabled by default; use `AppSurfaceDocs:Harvest:JavaScript:Enabled=false` only to opt out, and use `IncludeGlobs` only to narrow scanning.
- Add sidecar metadata for repository and package README files.
- Feature the first consumer paths through `featured_page_groups`.
- Configure `AppSurfaceDocs:Localization` and `translation_key` metadata before adding translated files at scale.
- Verify `/docs`, `/docs/search`, and `/docs/search-index.json`. The search page is server-rendered and should still expose starter query URLs plus browse links before the client index loads; a blocked or missing index must degrade to those links, not to a blank page.
- If you need search-quality analytics, configure `AppSurfaceDocs:Metrics` explicitly. Static exports should set `Metrics:BrowserCollector:EndpointUrl` to a reviewed HTTPS collector. Hosted docs can enable `Metrics:HostedCollection` and leave the endpoint blank so the layout uses `{DocsRootPath}/_metrics/collect`.
- Keep `Metrics:HostedReview:Exposure=DevelopmentOnly` unless a trusted operator surface protects `{DocsRootPath}/_search-quality`; the hosted review is bounded, process-local diagnostics rather than durable analytics.
- For custom docs roots, path bases, or static exports, inspect the generated `search.html` and confirm its search index URL plus fallback anchors point at the mounted root.
- For published release trees, inspect `search-index.json` before publishing. Stored `documents[].path` values should stay canonical and deployment-independent, such as `/docs/guide.html`; do not include request path bases, custom route roots, origins, executable schemes, traversal, or docs operational routes. AppSurface Docs rewrites valid canonical paths to the mounted root while serving the archive.
- For static exports with redirect aliases, use the default HTML strategy for GitHub Pages and generic static hosts, or `--mode cdn --redirects netlify` for Netlify-compatible providers. Do not hand-author `_redirects` in the export output.
- For generated export output, use a regular build directory with no symlinks, junctions, reparse points, or lexical escapes in artifact paths. RazorWire reports `RWEXPORT009` before creating parents, opening generated files, enumerating release archive entries, reading lengths, or hashing when HTML, CSS, binary assets, `404.html`, docs partials, redirect aliases, `_redirects`, `.appsurface-docs-route-manifest.json`, or `.appsurface-docs-release-manifest.json` would cross the physical output boundary. This is not a bypassable deploy option; hostile concurrent mutation after validation is outside the export contract, so run export in an operator-owned directory that other processes do not modify.
- For published version catalogs, keep the version catalog file beside the `releases/` layout when possible and leave `AppSurfaceDocs:Versioning:TrustedReleaseRootPath` unset. If your release store lives elsewhere, configure `TrustedReleaseRootPath` once and keep every catalog `exactTreePath` relative to that directory.
- For published release stores with unusually large generated pages, check exported `.html` files and root `search-index.json` before upgrading. The default `AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes` is 4 MiB and applies only to rewritten published-tree HTML/search-index artifacts, not images, CSS, JavaScript, fonts, or source harvesting.
- When migrating older catalogs, replace absolute `exactTreePath` values such as `/srv/appsurface-docs/releases/1.2.3` with `TrustedReleaseRootPath=/srv/appsurface-docs/releases` and `exactTreePath=1.2.3`.
- Treat the trusted release root as an operator-owned immutable static export store. Symlinks, junctions, reparse points, hidden path segments, rooted catalog paths, and `../` escapes are unavailable and are never mounted.
- For package maintainers changing built-in Docs browser assets, run `pnpm --dir Web run assets:build` and `pnpm --dir Web run assets:verify` before building or exporting docs. AppSurface Docs embeds `wwwroot/docs/search-client.js` and `wwwroot/docs/minisearch.min.js`, so stale generated assets can otherwise ship inside the package assembly.
- Run the standalone host or export pipeline in CI before publishing a public docs surface.

### Search relevance refresh paths

| Surface | What changes relevance | Refresh and verify |
| --- | --- | --- |
| Live source-backed docs | Markdown body, sidecar/front matter metadata, generated API metadata, or the package search runtime | Refresh/restart harvest, then inspect `{DocsRootPath}/search`, `{DocsRootPath}/search?q=...`, and `{DocsRootPath}/search-index.json`. |
| Package consumers | Updates to the bundled ranking runtime in `search-client.js` or MiniSearch assets | Consume a package containing rebuilt assets; verify the package's docs surface and asset version query strings. |
| Static exports | Exported HTML, `search.html`, `search-index.json`, and bundled search assets | Regenerate the export, then inspect `search.html`, root `search-index.json`, and representative query URLs before publishing. |
| Exact version archives | The archive's frozen `search-client.js` and root `search-index.json` | Existing exact archives keep their own search behavior; new relevance behavior appears only in newly exported or intentionally republished trees. |

## Search fallback checks

Treat the search page as useful server-rendered content, not as a blank client-only workspace. The first response for `/docs/search` should include starter query links and browse links for high-value routes such as Start Here, Examples, Packages, Troubleshooting, and API Reference. The browser script can replace the loading skeleton with live results after `search-index.json` loads, but a crawler, no-JS reader, or temporarily blocked index request must still have those links.

When validating a host or release artifact:

- Open `/docs/search` before the client search index settles. Confirm starter searches point at `/docs/search?q=...` and browse cards point at real docs routes.
- Block or rename `/docs/search-index.json`. The page should show a specific search-index failure message and retry button while keeping the starter and browse links visible.
- Confirm the failure panel does not contain replacement navigation links. The durable fallback links live in the server-rendered browse section so they stay available before and after client initialization.
- For custom docs roots or path bases, confirm the search config, starter query URLs, and browse fallback anchors all include the mounted root.
- For static exports or published release trees, inspect `search.html` and confirm `search-index.json`, starter query URLs, and fallback anchors are rewritten to the exact release root, including any path base. Keep the stored `search-index.json` document paths canonical (`/docs/...`) so the same archive can be safely mounted under a custom root or virtual directory later.

## Search quality metrics checks

Metrics are opt-in and docs-specific. Disabled hosts should render no collector endpoint, no feedback controls, and no
hosted metrics routes. Enabled static exports should send only safe AppSurface Docs product events to the configured
HTTPS endpoint; the browser collector omits credentials and drops transport failures. Enabled hosted docs should accept
POST JSON at `{DocsRootPath}/_metrics/collect`, validate through `AppSurfaceProductEventRegistry`, and show recent
aggregate buckets at `{DocsRootPath}/_search-quality` when review exposure allows it.

Do not use metrics configuration to pass API keys, query strings, or custom headers. Put authentication, retention, and
CORS policy in the host-owned collector or analytics service, not in the AppSurface Docs static export.

## Where to go next

- [AppSurface Docs package reference](./README.md)
- [Standalone AppSurface Docs host](../ForgeTrust.AppSurface.Docs.Standalone/README.md)
- [Package chooser](../../packages/README.md)
- [Release hub](../../releases/README.md)
