# Aspire AppHost Example

This example proves the local `ForgeTrust.AppSurface.Aspire` adoption path. It starts an Aspire AppHost through `AspireApp<TModule>`, selects a profile command with CliFx, and composes the existing web example through reusable Aspire components.

## Run It

Build the AppHost from the repository root:

```bash
dotnet build examples/aspire-apphost/AspireAppHostExample.csproj
```

If the Aspire CLI is installed, run the local profile:

```bash
aspire run --apphost examples/aspire-apphost/AspireAppHostExample.csproj -- local
```

The `-- local` token selects the AppSurface `LocalProfile` command. Aspire opens the dashboard and shows the `web` project resource for `examples/web-app`.

## What This Example Shows

- `Program.cs` delegates AppHost startup to `AspireApp<ExampleModule>.RunAsync(args)`.
- `LocalProfile` is a CliFx command that chooses which Aspire components participate in the resource graph.
- `WebAppProjectComponent` registers the web example with `AddProject<Projects.WebAppExample>("web")`.
- `WebAppEnvironmentComponent` depends on `WebAppProjectComponent`, resolves it through `AspireStartupContext.Resolve(...)`, and applies an environment variable.
- The AppSurface project reference is marked `IsAspireProjectResource=false` so only the web app becomes an Aspire resource.

## What This Example Does Not Show

- Deployment support.
- Automatic forwarding of arbitrary Aspire or deployment arguments.
- Cross-assembly component discovery.
- Secret management, hosting policy, or production orchestration.

## Project Reference Shape

Aspire AppHost projects generate the `Projects` namespace from project references. Keep library references out of the Aspire resource graph:

```xml
<ProjectReference Include="../../Aspire/ForgeTrust.AppSurface.Aspire/ForgeTrust.AppSurface.Aspire.csproj" IsAspireProjectResource="false" />
<ProjectReference Include="../web-app/WebAppExample.csproj" AspireProjectMetadataTypeName="WebAppExample" />
```

The web app reference intentionally does not set `IsAspireProjectResource=false`; it must remain a resource so `Projects.WebAppExample` is generated.

## Argument Forwarding

`AspireProfile.PassThroughArgs` defaults to an empty array. The example uses:

```bash
aspire run --apphost examples/aspire-apphost/AspireAppHostExample.csproj -- local
```

Everything after `--` is passed to the AppHost process as AppSurface command arguments. `local` selects the profile. Unknown command-line arguments are not automatically forwarded to Aspire's `DistributedApplicationBuilder`; a profile must override `PassThroughArgs` when it intentionally supports known AppHost arguments.

## Troubleshooting

| What you see | Likely cause | Fix |
| --- | --- | --- |
| `aspire: command not found` | Aspire CLI is not installed or not on `PATH`. | Install the Aspire CLI, then rerun the `aspire run` command. |
| `Projects.WebAppExample` does not compile | The AppHost SDK did not generate project metadata, or the web app reference was marked non-resource. | Rebuild the AppHost and keep `IsAspireProjectResource=false` only on the AppSurface library reference. |
| Duplicate resource name error | Multiple component instances created the same resource name. | Constructor-inject concrete components and use `AspireStartupContext.Resolve(...)` for dependencies. |
| Extra args are rejected | `LocalProfile` is a CliFx command and does not automatically forward unknown args. | Keep the command to `-- local`, or add an explicit `PassThroughArgs` override for known AppHost args. |

---
[📂 Back to Examples](../README.md) | [🏠 Back to Root](../../README.md)
