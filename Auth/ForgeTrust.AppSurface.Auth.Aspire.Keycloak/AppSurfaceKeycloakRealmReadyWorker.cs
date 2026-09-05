using System.Diagnostics.CodeAnalysis;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Resolves the framework-dependent executable payload for a package or project-reference AppHost.
/// </summary>
[ExcludeFromCodeCoverage(
    Justification = "The worker resolver is executable-payload plumbing; graph and resolver tests exercise package, consumer-output, and project-reference invocations through the public realm-ready flow.")]
internal static class AppSurfaceKeycloakRealmReadyWorker
{
    private const string ConsumerOutputWorkerDirectoryName = "appsurface-keycloak-realm-ready";
    private const string WorkerDirectoryName = "realm-ready";
    internal const string WorkerAssemblyName = "ForgeTrust.AppSurface.Auth.Aspire.Keycloak.RealmReadyWorker";

    internal static AppSurfaceKeycloakRealmReadyWorkerInvocation Resolve(string? assemblyPath = null)
    {
        assemblyPath ??= typeof(AppSurfaceKeycloakRealmReadyWorker).Assembly.Location;
        var assemblyDirectory = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrWhiteSpace(assemblyPath) || string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            throw AppSurfaceKeycloakRealmReadyConfiguration.WorkerUnavailable();
        }

        var packageWorkerDirectory = Path.GetFullPath(Path.Combine(
            assemblyDirectory,
            "..",
            "..",
            "tools",
            "net10.0",
            "any",
            WorkerDirectoryName));
        var packageWorker = CreateInvocation(packageWorkerDirectory);
        if (packageWorker is not null)
        {
            return packageWorker;
        }

        var consumerOutputWorker = CreateInvocation(
            Path.Join(assemblyDirectory, ConsumerOutputWorkerDirectoryName));
        if (consumerOutputWorker is not null)
        {
            return consumerOutputWorker;
        }

        var projectReferenceWorker = CreateInvocation(assemblyDirectory);
        if (projectReferenceWorker is not null)
        {
            return projectReferenceWorker;
        }

        throw AppSurfaceKeycloakRealmReadyConfiguration.WorkerUnavailable();
    }

    private static AppSurfaceKeycloakRealmReadyWorkerInvocation? CreateInvocation(string workingDirectory)
    {
        var workerAssemblyPath = Path.Join(workingDirectory, $"{WorkerAssemblyName}.dll");
        var workerRuntimeConfigPath = Path.Join(workingDirectory, $"{WorkerAssemblyName}.runtimeconfig.json");
        var workerDepsFilePath = Path.Join(workingDirectory, $"{WorkerAssemblyName}.deps.json");
        if (!File.Exists(workerAssemblyPath) || !File.Exists(workerRuntimeConfigPath) || !File.Exists(workerDepsFilePath))
        {
            return null;
        }

        return new AppSurfaceKeycloakRealmReadyWorkerInvocation(
            workingDirectory,
            ["exec", "--runtimeconfig", workerRuntimeConfigPath, "--depsfile", workerDepsFilePath, workerAssemblyPath, "--appsurface-keycloak-realm-ready"]);
    }
}

/// <summary>
/// Holds one resolved command invocation without exposing the local payload path as a public API.
/// </summary>
internal sealed record AppSurfaceKeycloakRealmReadyWorkerInvocation(string WorkingDirectory, string[] Arguments);
