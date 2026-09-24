# Design: Python Docstring Harvesting Spike

Related issue: [#771](https://github.com/forge-trust/AppSurface/issues/771)

## Decision

AppSurface Docs now includes a bounded Python documentation vertical slice backed by
[TreeSitter.DotNet 1.3.0](https://www.nuget.org/packages/TreeSitter.DotNet/1.3.0). The goal is to prove that the
package can present source-linked documentation for a Python sidecar inside a broader .NET codebase; it is not a
claim of complete Python documentation support or of improved AI answers.

The earlier 5 MiB compressed-package preference was reversed. The exact artifact is 53,401,399 bytes (50.93 MiB
compressed; 617,084,456 bytes uncompressed) because it bundles native Tree-sitter libraries and grammars for more than
Python. That is a material distribution cost, but no longer a rejection criterion: its multi-language capability is
part of the product case. The source-controlled [candidate record](https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs.Tests/TestData/PythonParserDecision/README.md)
keeps the exact SHA-256, license/provenance declaration, and advertised RID inventory.

The package contains Windows (`x86`, `x64`, `arm64`), Linux (`x86`, `x64`, `arm`, `arm64`), and macOS (`x64`, `arm64`)
native assets. The spike has executed the real grammar preflight and fixture suite on macOS ARM64. That validates the
current host only; a RID-specific package-consumer matrix is follow-up evidence, not a release-blocking requirement for
this proof. Per-RID distribution bundles would be preferable in a future packaging refinement, but are not required
for this decision.

## Problem and boundary

A .NET application can own Python scripts, embedded workflows, or sidecar modules without having a clean boundary for
making those sources discoverable in its documentation. The immediate proof is a single documentation surface with:

- C# API documentation from the existing Roslyn harvester;
- static Python module, class, function, async-function, and method docstrings;
- source anchors, navigation, and generated-API search metadata for both languages; and
- an explicit C#-to-Python ownership link for readers recovering from either side.

The harvester never imports a Python module, starts a Python interpreter, evaluates a Python expression, discovers a
Python environment, or interprets a docstring dialect. It reads policy-approved source text and parses it with the
Tree-sitter Python grammar. Native parser initialization is isolated behind a nonfatal preflight; an unavailable native
asset produces `appsurfacedocs.python.parser_unavailable` rather than an unhandled load error.

## Implemented scope

### Opt-in harvesting

`AppSurfaceDocsHarvestOptions.Python` exposes `Enabled`, `IncludeGlobs`, `ExcludeGlobs`, standard default-exclusion
controls, `StrictHealth`, and `MaxFileSizeBytes` (262,144 bytes by default). Python remains intentionally
boundary-first: when enabled with no usable explicit `IncludeGlobs`, it reads no `.py` files and emits the actionable,
nonfatal `appsurfacedocs.python.missing_include` diagnostic. This avoids silently imposing a repository-wide Python
visibility policy on a mixed codebase.

Approved inputs share the existing traversal, VCS-ignore, reparse-point, cancellation, and parser-input-budget
protections. Oversized input is skipped before parsing with `appsurfacedocs.python.file_too_large`.

### Public API selection and docstrings

For this proof, a module must declare exactly one unannotated top-level literal `__all__` list or tuple of unprefixed
string literals. Only named module-level classes and functions in that boundary publish. Documented methods beneath an
exported class publish as children; unexported top-level helpers do not. Missing, dynamic, malformed, or repeated
boundaries emit a structured diagnostic and no module page. Unsupported exports and missing declarations are likewise
reported without making the harvest fatal.

Supported docstrings are unprefixed, non-concatenated single- or triple-quoted literals. Raw, bytes, formatted, and
other prefixed literals are deliberately outside the spike. The harvester preserves the literal body without Python
escape evaluation, expands tabs at width eight, removes common indentation from subsequent nonblank lines, and trims
outer blank lines. It handles module/class/function/method bodies and decorated definitions, but deliberately does not
project signatures, annotations, default values, type metadata, imports, or a call graph.

Accepted modules publish at `api/python/{module-slug}`. The slug comes from the policy-approved repository-relative
source path; a collision yields `appsurfacedocs.python.slug_collision` and neither colliding page is published. Python
symbol anchors are unique across the entire module, including class members. Case or punctuation normalization uses
deterministic disambiguation, with numeric suffixes when another declaration already occupies a generated suffix.
Readers should use the emitted fragment routes rather than derive anchors from symbol names. Python
symbol fragments carry the same validated generated-API provenance checks used by JavaScript, so search can expose
language, `Public API` lifecycle, and canonical fragment routes without granting custom harvesters a ranking bypass.

### Polyglot ownership link

`AppSurfacePythonModuleAttribute` is the small public C# contract for a host relationship:

```csharp
/// <summary>Hosts the workflow sidecar.</summary>
[AppSurfacePythonModule("sidecar/worker.py")]
public sealed class WorkerHost;
```

The C# harvester reads the attribute syntax; it never constructs the attribute. Its one argument must be a literal,
nonempty repository-relative `.py` path with forward slashes and without `.` or `..` segments. An invalid or duplicate
marker on a type emits `appsurfacedocs.python.ownership_invalid` and creates no relationship.

An internal linker replaces built-in, encoded markers only after the C# and Python harvesters complete. It publishes
reciprocal links only when exactly one accepted Python module and exactly one documented C# owner match the same path.
Unmatched or ambiguous candidates are removed rather than producing a heuristic or dangling link. The relationship is
navigation metadata, not evidence of a runtime call, import, or dependency graph; no generic cross-language field was
added to `DocNode`, `IDocHarvester`, or the search schema.

## Evidence

Focused tests prove:

- native Tree-sitter Python parsing on the local macOS ARM64 host;
- public-boundary success, missing/dynamic boundaries, unsupported/missing exports, package initializers, and input-size
  rejection;
- classes, functions, async functions, methods, normalized docstrings, source anchors, and exclusion of a top-level
  non-exported helper;
- generated Python symbol search entries with `python` language metadata and `Public API` lifecycle; and
- a C# host and an accepted Python module with reciprocal rendered links through the real aggregation pipeline.

The package inspection workflow still records archive size, RID inventory, package identity, and provenance metadata.
It now treats archive size as decision evidence rather than a hard product gate; the separate 64 MiB static inspection
limit remains a safety bound for the inspection command itself.

## Non-goals and follow-up questions

- No runtime Python execution, import-time inspection, virtual-environment discovery, or Python toolchain dependency.
- No support for dynamic/re-exported `__all__`, wildcard imports, package resolution, runtime decorator behavior, or
  Google/NumPy/Sphinx docstring semantics.
- No generic language-harvester framework and no claim that every Tree-sitter grammar is product-supported merely
  because the selected package bundles it.
- No AI answer-quality measurement. The next product research can test whether the newly discoverable, source-linked
  Python surface improves retrieval or answers in a realistic mixed-language application.
- Before broad release adoption, run package-consumer/native-load evidence on the declared Windows, Linux, and macOS
  RIDs and reassess whether a Python-only or per-RID packaging option can reduce the payload without sacrificing the
  broader multi-language product direction.
