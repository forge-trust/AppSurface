using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;
using ForgeTrust.AppSurface.Core;

var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath)
    ?? throw new InvalidOperationException("The published AppHost executable path is unavailable.");
var adapterPath = typeof(AppSurfaceKeycloakRealmReadyWorker).Assembly.Location;
var executableWorkerDirectory = PathUtils.PathUnder(executableDirectory, "appsurface-keycloak-realm-ready");
string[] workerExtensions = ["dll", "deps.json", "runtimeconfig.json"];

if (args is ["--expect-muxer-rejection"])
{
    if (string.IsNullOrWhiteSpace(adapterPath) || !File.Exists(adapterPath))
        throw new InvalidOperationException("The muxer smoke did not load the adapter from a physical assembly.");

    var managedDirectory = Path.GetDirectoryName(adapterPath);
    if (managedDirectory != Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        || managedDirectory == executableDirectory)
        throw new InvalidOperationException("The muxer smoke must load the adapter from the separate managed output.");

    if (workerExtensions.Any(extension => !File.Exists(PathUtils.PathUnder(executableWorkerDirectory,
            $"{AppSurfaceKeycloakRealmReadyWorker.WorkerAssemblyName}.{extension}"))))
        throw new InvalidOperationException("The muxer smoke requires the complete worker triplet beside the executable.");

    AssertWorkerResolutionRejected("The resolver accepted the complete worker beside the shared muxer.");
    Console.WriteLine($"muxer: ignored the complete worker beside the shared muxer and returned {AppSurfaceKeycloakDiagnosticCodes.RealmReadyWorkerUnavailable}.");
    return;
}

var layout = args[0];
var extracted = adapterPath.Length != 0
    && Path.GetDirectoryName(adapterPath) != executableDirectory
    && Path.GetDirectoryName(adapterPath) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
if (layout == "bundled" ? adapterPath.Length != 0 : layout != "self-extract" || !extracted)
    throw new InvalidOperationException($"The AppHost did not use the {layout} assembly layout.");
if (Directory.GetCurrentDirectory() == executableDirectory)
    throw new InvalidOperationException("The smoke must run outside the executable directory.");

if (args is [_, "--expect-missing-runtimeconfig"])
{
    AssertWorkerResolutionRejected("The AppHost accepted an incomplete external worker.");
    Console.WriteLine($"{layout}: missing external runtimeconfig rejected with {AppSurfaceKeycloakDiagnosticCodes.RealmReadyWorkerUnavailable}.");
    return;
}

var worker = AppSurfaceKeycloakRealmReadyWorker.Resolve();
if (worker.WorkingDirectory != executableWorkerDirectory)
    throw new InvalidOperationException("The AppHost selected the wrong external worker directory.");

foreach (var extension in workerExtensions)
{
    var path = PathUtils.PathUnder(executableWorkerDirectory, $"{AppSurfaceKeycloakRealmReadyWorker.WorkerAssemblyName}.{extension}");
    if (!File.Exists(path) || !worker.Arguments.Contains(path, StringComparer.Ordinal))
        throw new InvalidOperationException($"The invocation lacks the external worker {extension}.");
}

Console.WriteLine($"{layout}: the native AppHost resolved the packed worker beside its executable from another directory.");

static void AssertWorkerResolutionRejected(string failureMessage)
{
    try
    {
        AppSurfaceKeycloakRealmReadyWorker.Resolve();
    }
    catch (AppSurfaceKeycloakException error) when (error.Code == AppSurfaceKeycloakDiagnosticCodes.RealmReadyWorkerUnavailable)
    {
        return;
    }

    throw new InvalidOperationException(failureMessage);
}
