namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Resolves the framework-dependent executable payload for a package or project-reference AppHost.
/// </summary>
internal static class AppSurfaceKeycloakRealmReadyWorker
{
    private const string ConsumerOutputWorkerDirectoryName = "appsurface-keycloak-realm-ready";
    private const string WorkerDirectoryName = "realm-ready";

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
        var packageWorker = CreateInvocation(packageWorkerDirectory, assemblyPath: null, runtimeConfigPath: null, depsFilePath: null);
        if (packageWorker is not null)
        {
            return packageWorker;
        }

        var consumerOutputWorker = CreateInvocation(
            Path.Combine(assemblyDirectory, ConsumerOutputWorkerDirectoryName),
            assemblyPath: null,
            runtimeConfigPath: null,
            depsFilePath: null);
        if (consumerOutputWorker is not null)
        {
            return consumerOutputWorker;
        }

        var projectReferenceWorker = CreateInvocation(
            assemblyDirectory,
            assemblyPath,
            runtimeConfigPath: null,
            depsFilePath: null);
        if (projectReferenceWorker is not null)
        {
            return projectReferenceWorker;
        }

        throw AppSurfaceKeycloakRealmReadyConfiguration.WorkerUnavailable();
    }

    private static AppSurfaceKeycloakRealmReadyWorkerInvocation? CreateInvocation(
        string workingDirectory,
        string? assemblyPath,
        string? runtimeConfigPath,
        string? depsFilePath)
    {
        var workerAssemblyPath = assemblyPath ?? Path.Combine(workingDirectory, $"{typeof(AppSurfaceKeycloakRealmReadyWorker).Assembly.GetName().Name}.dll");
        var workerRuntimeConfigPath = runtimeConfigPath ?? Path.Combine(workingDirectory, $"{typeof(AppSurfaceKeycloakRealmReadyWorker).Assembly.GetName().Name}.runtimeconfig.json");
        var workerDepsFilePath = depsFilePath ?? Path.Combine(workingDirectory, $"{typeof(AppSurfaceKeycloakRealmReadyWorker).Assembly.GetName().Name}.deps.json");
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
