# ForgeTrust.AppSurface.Aspire

.NET Aspire integration for the AppSurface ecosystem.

## Overview

`ForgeTrust.AppSurface.Aspire` provides a modular way to define local .NET Aspire AppHosts. It lets an AppHost use AppSurface modules, CLI-selectable profiles, and reusable Aspire components instead of placing every resource registration directly in `Program.cs`.

Use this package when you want:

- `AspireApp<TModule>` as the AppHost entry point.
- `AspireProfile` commands such as `local` or `demo` to choose a resource graph.
- `IAspireComponent<TResource>` classes that can be injected, reused, ordered, and resolved once through `AspireStartupContext`.

This package is a local AppHost composition surface. It does not add deployment support, secret management, or automatic forwarding of arbitrary Aspire CLI arguments.

## Release Guidance

AppSurface has cut the first coordinated `v0.1.0` release candidate. Before installing this package from a prerelease feed, read the [v0.1.0 RC 2 release note](../../releases/v0.1.0-rc.2.md) for current release risk, migration guidance, and package readiness.

## Installation

```bash
dotnet add package ForgeTrust.AppSurface.Aspire
```

## Working Example

The repository includes a local Aspire AppHost proof at [`examples/aspire-apphost`](../../examples/aspire-apphost/README.md). It composes the existing web example through two Aspire components so the resource graph is not just a direct `AddProject` call.

From the repository root:

```bash
dotnet build examples/aspire-apphost/AspireAppHostExample.csproj
```

If the Aspire CLI is installed, run the local profile:

```bash
aspire run --apphost examples/aspire-apphost/AspireAppHostExample.csproj -- local
```

The `-- local` token selects the AppSurface `AspireProfile` command. Aspire opens the dashboard and shows the `web` project resource.

## Minimal Shape

Use `AspireApp<TModule>` to start your AppHost:

```csharp
using AspireAppHostExample;
using ForgeTrust.AppSurface.Aspire;

await AspireApp<ExampleModule>.RunAsync(args);
```

Define a profile command. Constructor-inject concrete components and return the top-level component from `GetComponents()`:

```csharp
using CliFx.Binding;
using ForgeTrust.AppSurface.Aspire;
using Microsoft.Extensions.Logging;

[Command("local", Description = "Run the local Aspire graph.")]
public sealed partial class LocalProfile : AspireProfile
{
    private readonly WebAppEnvironmentComponent _webApp;

    public LocalProfile(WebAppEnvironmentComponent webApp, ILogger<LocalProfile> logger)
        : base(logger)
    {
        _webApp = webApp;
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _webApp;
    }
}
```

Define a project component:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Aspire;

public sealed class WebAppProjectComponent : IAspireComponent<ProjectResource>
{
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        appBuilder.AddProject<Projects.WebAppExample>("web");
}
```

Define a second component that depends on the first one. `Resolve` generates the dependency only once for the same component instance:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Aspire;

public sealed class WebAppEnvironmentComponent : IAspireComponent<ProjectResource>
{
    private readonly WebAppProjectComponent _webApp;

    public WebAppEnvironmentComponent(WebAppProjectComponent webApp)
    {
        _webApp = webApp;
    }

    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        context.Resolve(_webApp)
            .WithEnvironment("APPSURFACE_ASPIRE_EXAMPLE", "local");
}
```

## AppHost Project References

Aspire AppHost projects treat most `ProjectReference` entries as resources and generate typed classes in the `Projects` namespace. Keep the AppSurface library reference out of the resource graph and leave the app project as a resource:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="Aspire.AppHost.Sdk" Version="9.4.2" />
  <ItemGroup>
    <ProjectReference Include="../../Aspire/ForgeTrust.AppSurface.Aspire/ForgeTrust.AppSurface.Aspire.csproj" IsAspireProjectResource="false" />
    <ProjectReference Include="../web-app/WebAppExample.csproj" AspireProjectMetadataTypeName="WebAppExample" />
  </ItemGroup>
</Project>
```

## Argument Forwarding

`AspireProfile.PassThroughArgs` defaults to an empty array:

```csharp
public override string[] PassThroughArgs => [];
```

Override it only when a profile intentionally passes known AppHost arguments into `DistributedApplicationBuilder`. AppSurface command parsing still owns the profile command, so unknown command-line arguments are not forwarded automatically.

For example, `aspire run --apphost examples/aspire-apphost/AspireAppHostExample.csproj -- local` selects the `local` AppSurface profile. It is not a deployment command and does not make arbitrary Aspire deployment arguments pass through the profile.

## Discovery Rules

AppSurface discovers concrete `IAspireComponent` classes from the AppHost entry assembly and registers each concrete type as a singleton. It does not register components by interface and does not discover components from every referenced assembly.

For reusable package-owned components, expose them through an app-local profile or module path until broader discovery support exists.

## Troubleshooting

| What you see | Likely cause | Fix |
| --- | --- | --- |
| `aspire: command not found` | The Aspire CLI is not installed or not on `PATH`. | Install the Aspire CLI, then rerun the `aspire run --apphost ... -- local` command. |
| `The type or namespace name 'Projects' could not be found` | The AppHost SDK did not generate project metadata, or the project reference is marked as `IsAspireProjectResource=false`. | Build the AppHost project and keep `IsAspireProjectResource=false` only on library references such as `ForgeTrust.AppSurface.Aspire`. |
| Duplicate resource name error | Two different component instances generated the same Aspire resource name. | Constructor-inject shared concrete components and resolve dependencies through `AspireStartupContext.Resolve(...)` instead of manually creating components. |
| Unknown argument or deployment argument does not reach Aspire | `AspireProfile` is also a CliFx command, and `PassThroughArgs` defaults to empty. | Keep local profile selection to `-- local`, or override `PassThroughArgs` for known AppHost arguments in that profile. Deployment support is not shipped in this preview. |

## Pitfalls

- Keep component classes in the AppHost assembly when relying on automatic discovery.
- Resolve component dependencies through `AspireStartupContext` so the same component instance is generated once.
- Use stable Aspire resource names such as `web`; duplicate names fail in Aspire's own resource model.
- Treat this package as a local AppHost composition layer. It does not replace Aspire deployment tooling.

---
[📂 Back to Aspire List](../README.md) | [🏠 Back to Root](../../README.md)
