# Use RazorDocs in your repository

RazorDocs is the documentation surface for a repository that wants authored guidance, working examples, and source-derived API reference in one place. It is not a separate content management system. It reads the docs and code you already keep with the product, then serves them through a navigable ASP.NET Core experience.

AppSurface is the first proof site. The public docs you are reading are harvested from this repository, grouped with RazorDocs metadata, searched with the built-in search index, and published from the same package a consumer can install.

## When to use it

Use RazorDocs when your repository has more than API reference and less than a full documentation platform team.

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

RazorDocs has three moving parts:

1. **A host** that runs `RazorDocsWebModule` or the standalone RazorDocs app.
2. **A source repository** that contains Markdown pages, package READMEs, examples, and C# source.
3. **Metadata** that tells RazorDocs how to group, feature, search, and explain those pages.

The result is a docs surface with:

- section-first navigation such as Start Here, Examples, Releases, Troubleshooting, and API Reference
- source-derived C# API pages
- a search index that includes titles, summaries, headings, aliases, keywords, and page types
- optional trust bars for release notes, policies, and provenance-heavy pages
- optional `Source of truth` links back to the exact files readers should inspect or edit

## Fastest path

For a dedicated docs host, reference `ForgeTrust.AppSurface.Docs` and run the module:

```csharp
await WebApp<RazorDocsWebModule>.RunAsync(args);
```

Point the host at the repository you want to harvest:

```json
{
  "RazorDocs": {
    "Mode": "Source",
    "Source": {
      "RepositoryRoot": "/path/to/repo"
    }
  }
}
```

If `RazorDocs:Source:RepositoryRoot` is omitted, RazorDocs falls back to repository discovery from the app content root. That is convenient for local dogfooding, but production hosts should make the repository root explicit so the docs source is not guessed from deployment layout.

## Author the first useful page set

Start with pages that answer adoption questions before you tune visuals:

- `README.md` for the repository-level entry point.
- `packages/README.md` or package-level READMEs for install choices.
- `examples/.../README.md` for exportable proof paths.
- `releases/README.md`, `CHANGELOG.md`, or upgrade-policy pages when release risk matters.
- Troubleshooting pages for the failure modes your users actually hit.

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
summary: Fix missing or stale search results in a RazorDocs host.
page_type: troubleshooting
nav_group: Troubleshooting
aliases:
  - search index
  - missing results
---
```

## Curate the landing page

RazorDocs does not require a bespoke homepage template for each repo. The root landing can be curated from metadata:

- Put `featured_page_groups` in `README.md.yml`.
- Group destinations by reader intent, not by folder structure.
- Link to real harvested pages by source path.
- Keep each row focused on the question the reader has in their head.

The important part is that curation stays authored content. If the product story changes, edit Markdown or sidecar YAML. Do not fork `DocsController` to hardcode a new marketing panel.

## Add reference and proof over time

Once the first pages render, improve the docs in layers:

1. Add XML docs to public C# APIs so generated reference pages are useful.
2. Add `summary`, `page_type`, `nav_group`, `aliases`, and `keywords` metadata to high-traffic pages.
3. Add troubleshooting pages for the first support questions people ask.
4. Add release notes and trust metadata when adoption depends on upgrade confidence.
5. Add versioned published trees only after the live source-backed docs are useful.

That order matters. A beautiful archive of weak docs is still weak docs.

## Adoption checklist

- Pick a host: embedded AppSurface web module or standalone RazorDocs app.
- Configure `RazorDocs:Source:RepositoryRoot` for the repository to harvest.
- Keep `RazorDocs:Mode` set to `Source` unless a later bundle-hosting slice changes that contract.
- Add sidecar metadata for repository and package README files.
- Feature the first consumer paths through `featured_page_groups`.
- Verify `/docs`, `/docs/search`, and `/docs/search-index.json`.
- Run the standalone host or export pipeline in CI before publishing a public docs surface.

## Where to go next

- [RazorDocs package reference](./README.md)
- [Standalone RazorDocs host](../ForgeTrust.AppSurface.Docs.Standalone/README.md)
- [Package chooser](../../packages/README.md)
- [Release hub](../../releases/README.md)
