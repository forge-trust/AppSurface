# AppSurface test path helpers

`ForgeTrust.AppSurface.Testing` is test-only infrastructure for building fixture paths with clear containment intent. It is not a public package and it is not a production filesystem sandbox.

## Use `PathUnder` for full fixture paths

Use `TestPathUtils.PathUnder(basePath, segments)` whenever a test builds a full path under a repository root, temp workspace, project directory, output directory, or other trusted base.

```csharp
var projectPath = TestPathUtils.PathUnder(repoRoot, "Flow", "ForgeTrust.AppSurface.Flow", "ForgeTrust.AppSurface.Flow.csproj");
```

For dynamic relative values, pass the value through `PathUnder` instead of joining it directly:

```csharp
var outputPath = TestPathUtils.PathUnder(projectDirectory, outputRelativePath);
```

`PathUnder` rejects rooted segments, Windows absolute-looking segments, blank segments, and parent traversal before returning the normalized full path.

## Use `RelativePath` only for relative strings

Use `TestPathUtils.RelativePath` when the test needs a validated relative string, for example an expected manifest value or a path-shape assertion.

```csharp
var manifestPath = TestPathUtils.RelativePath("packages", "package-index.yml");
```

`RelativePath` does not prove a full path is under a base. If the next operation reads, writes, creates, deletes, or probes a file under a base, use `PathUnder`.

## Common migrations

Repo-root file read:

```csharp
var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
var workflow = File.ReadAllText(TestPathUtils.PathUnder(repoRoot, ".github", "workflows", "build.yml"));
```

Temp workspace child file:

```csharp
var readmePath = TestPathUtils.PathUnder(tempRoot, "docs", "README.md");
Directory.CreateDirectory(Path.GetDirectoryName(readmePath)!);
File.WriteAllText(readmePath, "# Docs");
```

Dynamic relative output:

```csharp
var outputPath = TestPathUtils.PathUnder(projectDirectory, outputRelativePath);
```

Relative path returned to production code:

```csharp
var relativePath = TestPathUtils.RelativePath("docs", "README.md");
```

Intentional platform-path behavior tests may still use `Path.Join` or `Path.Combine` directly when the point of the test is the platform API behavior or the arguments are literal expected-path segments.

## Policy failures

The policy test reports the violation kind, file, line and column, risky argument, reason, replacement guidance, and allowlist path. If a direct `Path.Join` or `Path.Combine` call is intentional, add a reasoned entry to `tests/ForgeTrust.AppSurface.Testing/path-policy-allowlist.yml`.
