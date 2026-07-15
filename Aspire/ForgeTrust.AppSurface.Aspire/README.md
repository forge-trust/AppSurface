# ForgeTrust.AppSurface.Aspire

.NET Aspire integration for the AppSurface ecosystem.

## Overview

`ForgeTrust.AppSurface.Aspire` provides modular local composition and an explicit adapter from an evaluated Aspire resource graph to AppSurface deployment intent. Local profiles remain available, while native AppHosts can attach artifact-only publish and read-only verification steps to Aspire's deployment pipeline.

Use this package when you want:

- `AspireApp<TModule>` as the AppHost entry point.
- `AspireProfile` commands such as `local` or `demo` to choose a resource graph.
- `IAspireComponent<TResource>` classes that can be injected, reused, ordered, and resolved once through `AspireStartupContext`.
- An explicit deployment annotation on one existing `ProjectResource` and one target-level Aspire pipeline integration.

Deployment support does not provision foundations, resolve secret values, apply infrastructure, execute jobs, or change traffic. It publishes deterministic evidence and delegates read-only parity inspection to the selected provider target. Local profiles still do not automatically forward arbitrary Aspire CLI arguments.
It also does not configure application-side OpenTelemetry exporters. Use [`ForgeTrust.AppSurface.Observability`](../../Observability/ForgeTrust.AppSurface.Observability/README.md) in each app project that should publish logs, traces, and metrics to Aspire or another OTLP collector.

For deterministic tests of profile-based AppHosts, use [`ForgeTrust.AppSurface.Aspire.Testing`](../ForgeTrust.AppSurface.Aspire.Testing/README.md). Native AppHosts that build directly from `DistributedApplication.CreateBuilder(args)` should continue using Aspire's native testing API.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

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
The web example registers `ForgeTrust.AppSurface.Observability`, so hitting the web app should produce app-side telemetry in the Aspire dashboard when Aspire supplies `OTEL_EXPORTER_OTLP_ENDPOINT`.

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
  <Sdk Name="Aspire.AppHost.Sdk" Version="13.4.4" />
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

## Native Deployment Pipeline

Deployment AppHosts must preserve Aspire's original arguments. Call `DistributedApplication.CreateBuilder(args)` directly; do not route `aspire publish` or `aspire do` through the local `AspireProfile`/CliFx command path.

This preview supports the coordinated Aspire AppHost SDK, `Aspire.Hosting`, and Aspire CLI **13.4.4** patch line. The deployment-pipeline APIs are experimental and isolated inside the package adapter. Use the matching CLI for publish and named-step proof; advancing Aspire requires a package release with compile, packed-consumer, `--list-steps`, publish, and targeted `do` verification before consumers upgrade.

```csharp
var builder = DistributedApplication.CreateBuilder(args);
var bindings = builder.AddParameter("appsurface-gcp-bindings");
var image = builder.AddParameter("migration-image");
var revision = builder.AddParameter("appsurface-source-revision");
var connection = builder.AddParameter("database", secret: true);

var providerTarget = GcpCloudRunDeploymentTarget.Create();
var target = builder.AddAppSurfaceDeploymentTarget(
    "gcp-staging",
    providerTarget,
    bindings,
    revision);

builder.AddProject<Projects.Migrations>("migrations")
    .WithAppSurfaceMigrationJob(options => options
        .WithImage(image)
        .WithPhase(DeploymentPhase.CandidatePreparation)
        .WithCommand("dotnet", "/app/Migrations.dll")
        .WithConnectionSecret(connection, "ConnectionStrings__database")
        .RequirePrivateNetwork()
        .WithExecutionPolicy(1, 1, 0, TimeSpan.FromMinutes(10)))
    .WithComputeEnvironment(target);

await builder.Build().RunAsync();
```

[`GcpCloudRunDeploymentTarget`](../../Deployment/ForgeTrust.AppSurface.Deployment.GcpCloudRun/README.md) comes from `ForgeTrust.AppSurface.Deployment.GcpCloudRun` and remains Aspire-independent; the Aspire adapter owns the non-secret binding-path and source-revision parameters. The binding path must be relative to and remain beneath `builder.AppHostDirectory`. The connection parameter is checked for secret classification and retained only by logical name; its value is never evaluated.

```bash
aspire publish --environment Staging --output-path ./artifacts/appsurface
aspire do appsurface-gcp-verify --environment Staging --output-path ./artifacts/appsurface
```

The artifact step is required by Aspire's standard `Publish` aggregation step. The named verification step depends on artifact publication, so targeted verification regenerates the same complete bundle before read-only inspection. Ordinary publish never runs verification.

The native `appsurface-gcp-verify` step always uses [`Shadow` parity](../../Deployment/reference.md#parity-modes) because it is the pre-cutover adoption proof. After state import and the single-writer cutover, the application-owned release workflow can call `IDeploymentTarget.VerifyAsync` with `Owned` parity to require AppSurface provenance labels; the native step does not infer that authority change.

Pitfalls:

- Every annotated migration project must be assigned with `WithComputeEnvironment`; AppSurface does not guess a target.
- Image and source parameters must be non-secret, canonical evidence. Images must include the full repository and SHA-256 digest, and source revisions must be full lowercase commits.
- Output must be an empty directory or an AppSurface-owned directory containing exactly the generated bundle. Unexpected or non-owned files are rejected.
- `aspire publish` makes no cloud calls and changes no infrastructure. CI remains responsible for credentials, state, apply, job execution, canaries, approval, and promotion.
- `appsurface-gcp-verify` is intentionally shadow-only. Do not treat it as proof that the generated writer is authoritative after cutover.

## Discovery Rules

AppSurface discovers concrete `IAspireComponent` classes from the AppHost entry assembly and registers each concrete type as a singleton. It does not register components by interface and does not discover components from every referenced assembly.

For reusable package-owned components, expose them through an app-local profile or module path until broader discovery support exists.

## Troubleshooting

| What you see | Likely cause | Fix |
| --- | --- | --- |
| `aspire: command not found` | The Aspire CLI is not installed or not on `PATH`. | Install the Aspire CLI, then rerun the `aspire run --apphost ... -- local` command. |
| Aspire stops at `Checking certificates...` or `Trusting certificates...` | The local ASP.NET Core development certificate has not been trusted yet. | Run `aspire certs trust` from an interactive shell, or fall back to `dotnet dev-certs https --trust`, and approve the OS trust prompt. |
| `The type or namespace name 'Projects' could not be found` | The AppHost SDK did not generate project metadata, or the project reference is marked as `IsAspireProjectResource=false`. | Build the AppHost project and keep `IsAspireProjectResource=false` only on library references such as `ForgeTrust.AppSurface.Aspire`. |
| Duplicate resource name error | Two different component instances generated the same Aspire resource name. | Constructor-inject shared concrete components and resolve dependencies through `AspireStartupContext.Resolve(...)` instead of manually creating components. |
| Unknown deployment argument does not reach Aspire | The AppHost routed deployment through the local `AspireProfile`/CliFx command path. | Use a native deployment entry point that calls `DistributedApplication.CreateBuilder(args)`; keep profiles for local compatibility. |
| Aspire's entry-point testing factory reports that the AppHost exited without building | The AppHost builds only after asynchronous AppSurface/CliFx profile dispatch. | Select the profile by type with [`ForgeTrust.AppSurface.Aspire.Testing`](../ForgeTrust.AppSurface.Aspire.Testing/README.md) instead of invoking the entry point. |

## Pitfalls

- Keep component classes in the AppHost assembly when relying on automatic discovery.
- Resolve component dependencies through `AspireStartupContext` so the same component instance is generated once.
- Use stable Aspire resource names such as `web`; duplicate names fail in Aspire's own resource model.
- Keep Aspire in charge of environment selection, pipeline ordering, publish, and named steps; AppSurface supplies the intent/compiler adapter rather than a second deployment command hierarchy.

---
[📂 Back to Aspire List](../README.md) | [🏠 Back to Root](../../README.md)
