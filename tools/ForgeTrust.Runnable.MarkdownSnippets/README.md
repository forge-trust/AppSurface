# ForgeTrust.Runnable.MarkdownSnippets

`ForgeTrust.Runnable.MarkdownSnippets` keeps checked-in Markdown code fences synchronized with source-owned samples. It is intentionally a generator and verifier only: rendered documentation consumes normal Markdown after the generated blocks have been materialized.

## Commands

Generate the default RazorWire README snippets:

```bash
# From the repository root:
dotnet run --project tools/ForgeTrust.Runnable.MarkdownSnippets/ForgeTrust.Runnable.MarkdownSnippets.csproj -- generate
```

Verify that generated snippets are current:

```bash
# From the repository root:
dotnet run --project tools/ForgeTrust.Runnable.MarkdownSnippets/ForgeTrust.Runnable.MarkdownSnippets.csproj -- verify
```

Both commands accept:

- `--repo-root <path>`: repository root. Defaults to the current directory.
- `--document <path>`: Markdown file to generate or verify. Defaults to `Web/ForgeTrust.Runnable.Web.RazorWire/README.md`.

The documented `dotnet run --project tools/...` commands are intentionally
repository-root commands because the project path and default `--repo-root`
are both resolved from the current directory. From another directory, pass
rooted paths for both `--project` and `--repo-root`, or `cd` to the repository
root before running them.

## Managed Blocks

Managed Markdown blocks use an opening directive, a generated fenced code block, and a closing directive:

````md
<!-- runnable:snippet id="razorwire-counter" file="examples/razorwire-mvc/Views/Shared/Components/Counter/Default.cshtml" marker="razorwire-counter" lang="cshtml" -->
```cshtml
Generated content goes here.
```
<!-- /runnable:snippet -->
````

Supported directive attributes are:

- `id`: stable block identifier for error messages.
- `file`: repository-relative source file path.
- `marker`: source marker identifier to extract.
- `lang`: Markdown fence language.
- `dedent`: optional boolean. Defaults to `true`; use `false` when leading indentation is meaningful.

Unknown attributes, duplicate attributes, rooted paths, and paths that escape the repository root fail verification.

When an opening directive is indented or quoted, generated fence, content, and
closing directive lines keep the same Markdown line prefix. Use that form when
a snippet block belongs inside a nested Markdown structure such as a list item
or blockquote.

## Source Markers

Sources own snippets with matching start and end markers:

```csharp
// docs:snippet razorwire-counter:start
Console.WriteLine("hello");
// docs:snippet razorwire-counter:end
```

Razor and HTML comments are also supported:

```cshtml
@* docs:snippet razorwire-counter:start *@
<rw:scripts/>
@* docs:snippet razorwire-counter:end *@
```

```html
<!-- docs:snippet razorwire-counter:start -->
<div></div>
<!-- docs:snippet razorwire-counter:end -->
```

Each marker pair must be unique, ordered, and contain non-empty content. Marker-like text inside strings does not count because markers must occupy the whole trimmed line.

## Pitfalls

Run `generate` after changing marked source files, then commit both the source change and the generated Markdown change. CI runs `verify` and fails when the checked-in Markdown drifts from the source markers.

Do not use this for render-time includes. RazorDocs and other Markdown renderers should receive ordinary generated Markdown so package documentation stays portable across GitHub, NuGet, and static export surfaces.
