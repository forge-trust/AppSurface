# RazorDocs JavaScript Parser Decision

Issue: https://github.com/forge-trust/AppSurface/issues/299

## Decision

Use `Acornima` as the first parser dependency for the future RazorDocs JavaScript public API harvester.

The spike keeps the dependency in `ForgeTrust.AppSurface.Docs.Tests` only. The product harvester should add the package to `ForgeTrust.AppSurface.Docs` in the later implementation PR after the public JavaScript harvest options and diagnostics are built.

## Why Acornima

- NuGet-contained parser. It does not require Node, npm, or a runtime package manager.
- `ParserOptions.OnComment` exposes skipped comments, including block comment ranges.
- AST nodes expose zero-based `Start`, `End`, `Range`, and line/column `Location`.
- Parser errors are catchable through `ParseErrorException` with line and zero-based column data.
- The package targets `net8.0`, `netstandard2.0`, `netstandard2.1`, and `net462`, so it fits the package and CLI distribution story.
- The package is BSD-3-Clause licensed, which is acceptable for AppSurface package and CLI distribution with normal third-party notice handling.
- The installed `1.6.1` package is 632,439 bytes as a `.nupkg`, and the `net8.0` assembly is 363,520 bytes.

## Candidate Result

| Question | Result |
| --- | --- |
| Parser package | `Acornima` `1.6.1` |
| Supported ECMAScript version | `ParserOptions.EcmaVersion` supports through `ES17` / `ES2026`, with `Latest` as the default. |
| Block comment spans | Available through `ParserOptions.OnComment`; `Comment.Range` gives zero-based start/end and `Comment.ContentRange` gives the comment body. |
| AST node spans | Available on every `Node` through `Start`, `End`, `Range`, and `Location`. |
| Source line/column behavior | Lines are one-based. Columns are zero-based. This matches parser error columns and node/comment locations. |
| Parser failure behavior | Malformed JavaScript throws a catchable `ParseErrorException` subclass. The probe catches `ParseErrorException` for `malformed.js`, with line and column populated. |
| Package size | `.nupkg`: 632,439 bytes. `lib/net8.0/Acornima.dll`: 363,520 bytes. |
| License compatibility | BSD-3-Clause. Acceptable for AppSurface package and CLI distribution. |
| Maintenance status | Current NuGet package, current README, and current ECMAScript coverage claim. This is healthier than depending on an abandoned grammar fork. |
| Comment attachment algorithm | Attach a block comment only when it immediately precedes a supported declaration with whitespace only between `comment.End` and `node.Start`. Standalone `@event` and `@typedef` doclets are collected from unattached block comments. |

## Rejected Alternatives

- `Esprima` remains viable but supports ECMAScript 2022 on the current NuGet line, while Acornima reports current ECMAScript 2026 Test262 coverage and the same .NET-contained parser model.
- `Jint` is an execution engine, not a parser-only dependency. It is too much runtime surface for a docs harvester that only needs syntax and comments.
- `NUglify` is optimized for minification scenarios, not source-provenance-heavy public API documentation.
- `ANTLR` is a parser-generator strategy, not a ready JavaScript parser decision. It would make RazorDocs own generated parser code, grammar updates, hidden-channel comment handling, and AST shaping before the first JavaScript harvester proves user value.
- `Tree-sitter` is the more interesting future multi-language parser substrate because it is built around source-spanned concrete syntax trees and broad grammar coverage. It should be evaluated when RazorDocs starts a second or third code-language harvester, but the native/runtime packaging story needs a separate AppSurface CLI distribution review.
- A regex-only harvester is rejected. It would fail the source-span and failure-isolation requirements and would make public API docs depend on source formatting luck.

## Multi-Language Parser Strategy

Do not make the JavaScript v1 harvester pay for a cross-language parser abstraction yet. The right near-term boundary is a small internal RazorDocs parser result model that can be backed by Acornima for JavaScript now and by another parser later.

Revisit ANTLR, Tree-sitter, and language-specific parsers after RazorDocs has at least two code-language harvesters with concrete needs. Otherwise the abstraction is theory, and theory has a way of becoming a 400-line options object.

## License Compliance Case

Current spike use:

- Acornima is referenced only by `ForgeTrust.AppSurface.Docs.Tests`, and that project has `IsPackable=false`.
- The spike does not add Acornima to `ForgeTrust.AppSurface.Docs`, `ForgeTrust.AppSurface.Cli`, or any other shipped package.
- Because the current use is test-only, Acornima is not redistributed in an AppSurface package or CLI artifact by this spike.

Future product use:

- If the JavaScript harvester adds Acornima to a shipped package, package, tool, or self-contained CLI artifact, that artifact must carry Acornima's BSD-3-Clause copyright notice, license conditions, and disclaimer in third-party notice materials.
- No endorsement: release and marketing copy must not imply Acornima, Adam Simon, or Acornima contributors endorse AppSurface or RazorDocs without specific written permission.
- The product implementation PR should add or update the repository's third-party notice flow before moving Acornima from the test project into product code.

## Parser Probe

Runnable command:

```bash
dotnet test Web/ForgeTrust.AppSurface.Docs.Tests/ForgeTrust.AppSurface.Docs.Tests.csproj --filter JavaScriptParserDecisionTests
```

The probe verifies:

- block comment spans
- AST node spans
- stable one-based line and zero-based column data
- deterministic leading block comment attachment
- standalone public `@event` doclets
- standalone public `@typedef` doclets
- `window.RazorWire = { ... }` assignment detection
- catchable malformed-file parse failures
- lightweight parse-cost measurements for real and synthetic JavaScript inputs

## Parse-Cost Snapshot

Captured locally with:

```bash
dotnet test Web/ForgeTrust.AppSurface.Docs.Tests/ForgeTrust.AppSurface.Docs.Tests.csproj --filter JavaScriptParserDecisionTests --logger "console;verbosity=detailed"
```

| Fixture/file | Size | Parse time | Notes |
| --- | ---: | ---: | --- |
| `Web/ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.js` | 41,380 bytes | 13.434 ms | real RazorWire dogfood file; parsed successfully with 4,380 AST nodes and 2 block comments |
| `Web/ForgeTrust.AppSurface.Docs/wwwroot/docs/search-client.js` | 68,314 bytes | 4.560 ms | larger RazorDocs browser asset; parsed successfully with 9,460 AST nodes |
| `Web/ForgeTrust.AppSurface.Docs/wwwroot/docs/minisearch.min.js` | 944 bytes | skipped | should stay excluded by `*.min.js` by default |
| `malformed.js` | 107 bytes | 0.450 ms | failure path; catchable parse exception |
| synthetic large public-doclet fixture | 117,965 bytes | 5.563 ms | 750 public function doclets; parsed successfully with 4,503 AST nodes and 750 block comments |

## Known Unsupported Syntax For V1 Harvesting

The parser can accept more JavaScript than RazorDocs v1 should harvest. The first harvester should still treat these as unsupported public shapes until later issues add explicit product behavior:

- TypeScript syntax
- JSX unless `Acornima.Extras` is deliberately added
- JavaScript classes and class members
- default export inference
- CommonJS `module.exports` inference
- automatic event inference from `dispatchEvent(new CustomEvent(...))`
- cross-doclet typedef enrichment

## First Max-File-Size Recommendation

The first default should skip files larger than 256 KiB and continue excluding `*.min.js` by default.

That threshold covers the current real dogfood files measured by this spike, leaves headroom above `search-client.js`, and avoids accidentally parsing large bundles before the harvester has production diagnostics and configuration.
