# First Success Path

Run the smallest web proof first. Do not install optional packages yet.

Use the repo-first path when you already cloned AppSurface. Use the package-first path when you are evaluating AppSurface from your own app or a fresh project.

## Repo-First Path

From the AppSurface repository root:

```bash
dotnet run --project examples/web-app -- --port 5055
```

Then open <http://127.0.0.1:5055> or run:

```bash
curl http://127.0.0.1:5055
```

Expected response:

```text
Hello World from the root!
```

That proves the base `ForgeTrust.AppSurface.Web` path: a root module, the AppSurface startup pipeline, and one mapped endpoint.

## Package-First Path

From any working folder outside the AppSurface repository, create a small ASP.NET Core app:

```bash
dotnet new web -n AppSurfaceQuickstart
cd AppSurfaceQuickstart
dotnet package add ForgeTrust.AppSurface.Web
```

Replace `Program.cs` with:

```csharp
using ForgeTrust.AppSurface.Core.Defaults;
using ForgeTrust.AppSurface.Web;

await WebApp<QuickstartModule>.RunAsync(
    args,
    options =>
    {
        options.MapEndpoints = endpoints =>
        {
            endpoints.MapGet("/", () => "Hello from AppSurface!");
        };
    });

public sealed class QuickstartModule : NoHostModule, IAppSurfaceWebModule
{
}
```

Run the app on the same stable port used by the repo example:

```bash
dotnet run -- --port 5055
```

Then open <http://127.0.0.1:5055> or run:

```bash
curl http://127.0.0.1:5055
```

Expected response:

```text
Hello from AppSurface!
```

That proves the package-consumer path: a fresh ASP.NET Core app can install `ForgeTrust.AppSurface.Web`, start through `WebApp<TModule>`, and map a first endpoint without cloning this repository.

## What This Example Shows

Both paths use `WebApp<TModule>.RunAsync(...)` and map the root endpoint from web options. The repo example also contributes its own module endpoint at `/module`.

If you are using the repo-first path, try it:

```bash
curl http://127.0.0.1:5055/module
```

Expected response:

```text
Hello from the example module!
```

## What This Example Does Not Prove

This first run does not prove the status-page behavior used later in the evaluator path. It only proves the base web host starts and the module contributes behavior.

For the status-page proof, read [From Program.cs to an AppSurface Module](../guides/from-program-cs-to-module.md). That page points at the Web package behavior and the tests that verify browser status pages and production exception pages.

## Pitfalls

- Pass `--port 5055` when following docs. Without it, AppSurface may choose a deterministic development port for your worktree.
- Start with `ForgeTrust.AppSurface.Web` for a normal web app. Add optional modules only when the [package chooser](../packages/README.md) points to them.
- In .NET 10, `dotnet package add` and `dotnet add package` are equivalent. Use whichever form your SDK and team already prefer.
- If you are using a private package feed or prerelease feed, configure that source before running the package-first install command.
- Treat the startup log as the source of truth if you choose a different port.

Next: [From Program.cs to an AppSurface Module](../guides/from-program-cs-to-module.md)
