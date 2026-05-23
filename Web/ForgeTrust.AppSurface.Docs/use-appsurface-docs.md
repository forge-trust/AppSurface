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
2. **A source repository** that contains Markdown pages, package READMEs, examples, and C# source.
3. **Metadata** that tells AppSurface Docs how to group, feature, search, and explain those pages.

The result is a docs surface with:

- section-first navigation such as Start Here, Examples, Releases, Troubleshooting, and API Reference
- source-derived C# API pages
- opt-in source-derived JavaScript public API pages for browser events and globals
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
        "Path": "/branding/docs-logo.svg",
        "AltText": "Acme"
      },
      "Favicon": {
        "SvgPath": "/branding/favicon.svg",
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

1. AppSurface Docs serves the files. Set `Identity:BrandingAssets:DirectoryPath` to a filesystem directory, usually `branding`, and point `Logo:Path` plus `Favicon:*Path` at browser URLs under `/branding`. With the default request prefix, `branding/docs-logo.svg` is referenced as `/branding/docs-logo.svg`.
2. The owning application already serves the files. Leave `Identity:BrandingAssets:DirectoryPath` blank and point `Logo:Path` plus `Favicon:*Path` at the host-owned browser URLs, such as `/assets/docs-logo.svg`.

`Identity:BrandingAssets:DirectoryPath` is a filesystem path, not a browser path. Relative values resolve against `AppSurfaceDocs:Source:RepositoryRoot` when that root is configured, then fall back to the host content root. `Logo:Path` and `Favicon:*Path` are browser URL paths, not filenames relative to `DirectoryPath`; AppSurface Docs does not join those values with the directory. Keep `DirectoryPath` pointed at a dedicated public branding directory; AppSurface Docs serves only `.avif`, `.gif`, `.ico`, `.jpg`, `.jpeg`, `.png`, `.svg`, and `.webp` files from that directory. Override `Identity:BrandingAssets:RequestPath` only when the default `/branding` URL prefix conflicts with an owning application route.

When `Identity:Favicon` is empty, the built-in layout links the packaged AppSurface Docs document-layers SVG mark.
Standalone AppSurface Docs hosts also serve that mark at `/favicon.ico` for the browser's conventional favicon probe.
When `Identity:Favicon:SvgPath` is configured in a standalone host, `/favicon.ico` redirects to that SVG path so the
conventional probe matches the rendered favicon metadata. Embedded hosts leave `/favicon.ico` to the owning application,
and any configured favicon path must be served by the host just like a logo asset.

Leave `Identity:Wordmark` unset for a plain-text docs title. The built-in sidebar and mobile header clip long display names with an ellipsis so they do not push the chrome outside its bounds. Configure `DisplayName`, `HighlightText`, and `HighlightColor` when the publishing repository wants a shorter product wordmark treatment. The highlight text must be a substring of `DisplayName`, and the color must be a CSS hex color so the docs layout cannot receive arbitrary style declarations from configuration.

## Define the public source boundary

Before pointing AppSurface Docs at a large repository, decide which paths are meant to be public. The safest production shape is a small global include list, then optional Markdown and C# refinements:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Paths": {
        "IncludeGlobs": [
          "README.md",
          "LICENSE",
          "docs/**/*.md",
          "src/**/*.cs"
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
        ]
      },
      "CSharp": {
        "IncludeGlobs": [
          "src/**/*.cs"
        ]
      }
    }
  }
}
```

Use repository-relative globs with `/` separators. AppSurface Docs rejects rooted paths, URI-shaped patterns, query strings, fragments, and `..` segments during startup validation. Empty includes mean the built-in harvester defaults are used; nonempty global includes become the outer boundary for both Markdown and C#.

The package also keeps protective defaults for build output, hidden directories, test projects, and C# source under `examples`. These defaults prevent common accidental publication without requiring every host to write the same excludes. If a default is too broad, use `DefaultExclusions:AllowGlobs` for narrow exceptions or `DefaultExclusions:DisabledGroups` when the entire group is intentionally public. Use the named group IDs, not numeric enum values; ordinals fail startup validation. Allows are group-aware, so a path inside `.github/bin` needs an allow for both `HiddenDirectories` and `BuildOutput` unless one group is disabled.

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
2. Add narrow JavaScript harvesting for intentional browser contracts such as public custom events or globals.
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
- Keep `AppSurfaceDocs:Mode` set to `Source` unless a later bundle-hosting slice changes that contract.
- If browser runtime contracts matter, enable `AppSurfaceDocs:Harvest:JavaScript` with one or more narrow `IncludeGlobs` entries and explicit `@public` doclets.
- Add sidecar metadata for repository and package README files.
- Feature the first consumer paths through `featured_page_groups`.
- Configure `AppSurfaceDocs:Localization` and `translation_key` metadata before adding translated files at scale.
- Verify `/docs`, `/docs/search`, and `/docs/search-index.json`. The search page is server-rendered and should still expose starter query URLs plus browse links before the client index loads; a blocked or missing index must degrade to those links, not to a blank page.
- For custom docs roots, path bases, or static exports, inspect the generated `search.html` and confirm its search index URL plus fallback anchors point at the mounted root.
- For package maintainers changing built-in Docs browser assets, run `pnpm --dir Web run assets:build` and `pnpm --dir Web run assets:verify` before building or exporting docs. AppSurface Docs embeds `wwwroot/docs/search-client.js` and `wwwroot/docs/minisearch.min.js`, so stale generated assets can otherwise ship inside the package assembly.
- Run the standalone host or export pipeline in CI before publishing a public docs surface.

## Search fallback checks

Treat the search page as useful server-rendered content, not as a blank client-only workspace. The first response for `/docs/search` should include starter query links and browse links for high-value routes such as Start Here, Examples, Packages, Troubleshooting, and API Reference. The browser script can replace the loading skeleton with live results after `search-index.json` loads, but a crawler, no-JS reader, or temporarily blocked index request must still have those links.

When validating a host or release artifact:

- Open `/docs/search` before the client search index settles. Confirm starter searches point at `/docs/search?q=...` and browse cards point at real docs routes.
- Block or rename `/docs/search-index.json`. The page should show a specific search-index failure message and retry button while keeping the starter and browse links visible.
- Confirm the failure panel does not contain replacement navigation links. The durable fallback links live in the server-rendered browse section so they stay available before and after client initialization.
- For custom docs roots or path bases, confirm the search config, starter query URLs, and browse fallback anchors all include the mounted root.
- For static exports or published release trees, inspect `search.html` and confirm `search-index.json`, starter query URLs, and fallback anchors are rewritten to the exact release root, including any path base.

## Where to go next

- [AppSurface Docs package reference](./README.md)
- [Standalone AppSurface Docs host](../ForgeTrust.AppSurface.Docs.Standalone/README.md)
- [Package chooser](../../packages/README.md)
- [Release hub](../../releases/README.md)
