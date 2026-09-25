using System.Reflection;
using System.Runtime.Loader;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: CandidateBootstrap <guarded|unguarded|startup|dependencies|host-builder|guarded-package> <assembly-path>");
    return 2;
}

var mode = args[0];
var pluginPath = Path.GetFullPath(args[1]);
if (args.Length != 2 || mode is not ("guarded" or "unguarded" or "startup" or "dependencies" or "host-builder" or "guarded-package"))
{
    Console.Error.WriteLine("Invalid compatibility fixture arguments.");
    return 2;
}

// Verify the candidate itself before adding an incompatible assembly to the process.
AppSurfacePackageCompatibility.ValidateAssemblies(AppDomain.CurrentDomain.GetAssemblies());

// Loading the file into an isolated load context reads assembly metadata without resolving its old contract.
// The guard must see this Assembly before any GetTypes/DI activation operation can resolve legacy interfaces.
var plugin = new AssemblyLoadContext("legacy-provider-metadata", isCollectible: true)
    .LoadFromAssemblyPath(pluginPath);

if (mode is "startup" or "dependencies" or "host-builder")
{
    var startup = new ProbeStartup(plugin);
    var root = new ProbeRootModule();
    try
    {
        if (mode == "startup")
        {
            await startup.RunAsync(Array.Empty<string>());
        }
        else if (mode == "dependencies")
        {
            startup.PrepareDependencies(new StartupContext([], root));
        }
        else
        {
            _ = ((IAppSurfaceStartup)startup).CreateHostBuilder(new StartupContext([], root));
        }

        Console.Error.WriteLine("Startup boundary did not reject the previous provider binary.");
        return 1;
    }
    catch (AppSurfacePackageCompatibilityException ex)
    {
        if (startup.RootFactoryCalls != 0 || root.DependencyCalls != 0)
        {
            Console.Error.WriteLine("Compatibility rejection happened after a module callback.");
            return 1;
        }

        Console.WriteLine($"{mode.ToUpperInvariant()} REJECTED: {ex.Message}");
        Console.WriteLine("CALLBACKS: root-factory=0 dependency-registration=0");
        return 0;
    }
}

if (mode is "guarded" or "guarded-package")
{
    try
    {
        // In guarded-package mode this is the old package itself; no old plugin is loaded or passed as a confounder.
        ConfigPackageCompatibility.ValidateAssemblies([plugin]);
        Console.Error.WriteLine("Guard did not reject the previous assembly contract.");
        return 1;
    }
    catch (AppSurfacePackageCompatibilityException ex) when (ex.Code == "config-package-version-mismatch")
    {
        Console.WriteLine($"GUARDED REJECTED: {ex.Message}");
        return 0;
    }
}

if (string.Equals(mode, "unguarded", StringComparison.Ordinal))
{
    try
    {
        var activatedType = plugin.GetTypes().Single(t => !t.IsAbstract && t.Name == "LegacyProviderPlugin");
        _ = Activator.CreateInstance(activatedType);
        Console.Error.WriteLine("Unguarded control unexpectedly activated the previous provider.");
        return 1;
    }
    catch (Exception ex) when (ex is TypeLoadException or FileLoadException or FileNotFoundException or ReflectionTypeLoadException)
    {
        Console.WriteLine($"UNGUARDED RUNTIME FAILURE: {ex.GetType().Name}");
        return 0;
    }
}

return 2;

// This factory deliberately attempts the actual legacy type scan if it is ever invoked. The startup proof therefore
// fails with a real loader error (or a nonzero callback count) when pre-factory validation is missing.
sealed class ProbeStartup : AppSurfaceStartup<ProbeRootModule>
{
    private readonly Assembly _plugin;

    public ProbeStartup(Assembly plugin)
    {
        _plugin = plugin;
    }

    public int RootFactoryCalls { get; private set; }

    protected override ProbeRootModule CreateRootModule()
    {
        RootFactoryCalls++;
        var type = _plugin.GetTypes().Single(t => !t.IsAbstract && t.Name == "LegacyProviderPlugin");
        _ = Activator.CreateInstance(type);
        return new ProbeRootModule();
    }

    public void PrepareDependencies(StartupContext context) => RegisterDependencies(context);

    protected override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services) =>
        throw new InvalidOperationException("The host must not reach service configuration with an incompatible plugin.");
}

sealed class ProbeRootModule : IAppSurfaceHostModule
{
    public int DependencyCalls { get; private set; }

    public void RegisterDependentModules(ModuleDependencyBuilder builder) => DependencyCalls++;

    public void ConfigureServices(StartupContext context, IServiceCollection services) =>
        throw new InvalidOperationException("The host must not configure an incompatible module graph.");

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) =>
        throw new InvalidOperationException("The host must not configure an incompatible module graph.");

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) =>
        throw new InvalidOperationException("The host must not configure an incompatible module graph.");
}
