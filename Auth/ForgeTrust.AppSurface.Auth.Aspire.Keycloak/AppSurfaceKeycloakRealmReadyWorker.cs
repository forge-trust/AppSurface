using System.Diagnostics.CodeAnalysis;
using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Core;

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
        var useProcessPath = assemblyPath is null;
        if (useProcessPath)
        {
            assemblyPath = typeof(AppSurfaceKeycloakRealmReadyWorker).Assembly.Location;
            if (assemblyPath.Length == 0)
            {
                // Bundled assemblies lack a file path. The publish target leaves the worker beside the AppHost.
                return ResolveBundled(AppContext.BaseDirectory);
            }
        }

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

        var consumerOutputWorker = CreateConsumerOutputInvocation(assemblyDirectory);
        if (consumerOutputWorker is not null)
        {
            return consumerOutputWorker;
        }

        var projectReferenceWorker = CreateInvocation(assemblyDirectory);
        if (projectReferenceWorker is not null)
        {
            return projectReferenceWorker;
        }

        // Self-extracting bundles put both the adapter and AppContext.BaseDirectory in the extraction cache.
        // The shared muxer loads hostfxr from its own host/fxr directory.
        if (useProcessPath && Path.GetDirectoryName(Environment.ProcessPath) is { } executableDirectory
            && !Directory.Exists(PathUtils.PathUnder(executableDirectory, "host", "fxr")))
        {
            return ResolveBundled(executableDirectory);
        }

        throw AppSurfaceKeycloakRealmReadyConfiguration.WorkerUnavailable();
    }

    /// <summary>Resolves the external worker directory beside a bundled AppHost.</summary>
    /// <param name="appDirectory">The base directory of the bundled AppHost executable.</param>
    /// <returns>The worker invocation when all required files are present.</returns>
    /// <exception cref="AppSurfaceKeycloakException">A required worker file is missing.</exception>
    internal static AppSurfaceKeycloakRealmReadyWorkerInvocation ResolveBundled(string appDirectory) =>
        CreateConsumerOutputInvocation(appDirectory)
        ?? throw AppSurfaceKeycloakRealmReadyConfiguration.WorkerUnavailable();

    private static AppSurfaceKeycloakRealmReadyWorkerInvocation? CreateConsumerOutputInvocation(string directory) =>
        CreateInvocation(PathUtils.PathUnder(directory, ConsumerOutputWorkerDirectoryName));

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
            ["exec", "--runtimeconfig", workerRuntimeConfigPath, "--depsfile", workerDepsFilePath, workerAssemblyPath, KeycloakWorkerProtocol.RealmReady]);
    }
}

/// <summary>
/// Holds one resolved command invocation without exposing the local payload path as a public API.
/// </summary>
internal sealed record AppSurfaceKeycloakRealmReadyWorkerInvocation(string WorkingDirectory, string[] Arguments);
