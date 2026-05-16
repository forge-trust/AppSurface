using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ForgeTrust.AppSurface.Core.Defaults;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// Provides context and configuration during the application startup process.
/// </summary>
/// <param name="Args">Command-line arguments provided to the application.</param>
/// <param name="RootModule">The root module of the application.</param>
/// <param name="ApplicationName">
/// Optional user-facing display label for product surfaces.
/// Defaults to the root module assembly name when not provided.
/// This value does not control Generic Host/static web asset manifest identity.
/// </param>
/// <param name="EnvironmentProvider">Optional provider for environment information.</param>
/// <param name="CustomRegistrations">Optional custom service registrations.</param>
public record StartupContext(
    string[] Args,
    IAppSurfaceHostModule RootModule,
    string? ApplicationName = null,
    IEnvironmentProvider? EnvironmentProvider = null,
    params List<Action<IServiceCollection>> CustomRegistrations)
{
    internal ModuleDependencyBuilder Dependencies { get; } = new();

    /// <summary>
    /// Gets or sets whether startup dependency registration has already run for this context.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. <see cref="AppSurfaceStartup{TRootModule}"/> sets this to
    /// <see langword="true"/> after framework and root-module dependencies are registered so repeated startup-time calls
    /// are no-ops for the same <see cref="StartupContext"/>. This state is intended for single-threaded startup
    /// composition and is not safe for concurrent writes.
    /// </remarks>
    internal bool DependenciesRegistered { get; set; }

    private string? _applicationName = ApplicationName;

    /// <summary>
    /// Gets or sets an assembly that should override both application discovery and host manifest identity.
    /// </summary>
    /// <remarks>
    /// Set this when a test or custom host needs AppSurface to scan a different assembly for commands, MVC parts,
    /// or Aspire components, or when static web asset manifest identity must be pinned to a specific assembly.
    /// </remarks>
    public Assembly? OverrideEntryPointAssembly { get; set; } = null;

    /// <summary>
    /// Gets or sets how console-oriented apps should present command output relative to ambient host lifecycle diagnostics.
    /// </summary>
    /// <remarks>
    /// The default value is <see cref="ConsoleOutputMode.Default"/>, which preserves the standard Generic Host behavior.
    /// Console startups can switch to <see cref="ConsoleOutputMode.CommandFirst"/> when command output should remain the
    /// primary user-facing console experience. Non-console apps can ignore this setting.
    /// </remarks>
    public ConsoleOutputMode ConsoleOutputMode { get; set; } = ConsoleOutputMode.Default;

    /// <summary>
    /// Gets the assembly that contains the root module.
    /// </summary>
    public Assembly RootModuleAssembly { get; } = RootModule.GetType().Assembly;

    /// <summary>
    /// Gets the assembly used for application-owned type discovery.
    /// </summary>
    /// <remarks>
    /// AppSurface uses this assembly to discover application-owned commands, MVC application parts, Aspire components,
    /// and similar extensibility hooks. By default it stays aligned with the root module assembly so cross-assembly
    /// test runners and shared hosts do not accidentally scan the outer process entry assembly. Set
    /// <see cref="OverrideEntryPointAssembly"/> when a host intentionally wants discovery to come from a different
    /// assembly.
    /// </remarks>
    public Assembly EntryPointAssembly => OverrideEntryPointAssembly ?? RootModuleAssembly;

    /// <summary>
    /// Gets the assembly whose name should be written into the Generic Host environment.
    /// </summary>
    /// <remarks>
    /// ASP.NET static web assets resolve runtime manifests through <see cref="Microsoft.Extensions.Hosting.IHostEnvironment.ApplicationName"/>.
    /// When <see cref="OverrideEntryPointAssembly"/> is not provided, AppSurface falls back to
    /// <see cref="Assembly.GetEntryAssembly()"/> here so the host environment tracks the real executable surface
    /// rather than the root module's assembly. When no process entry assembly is available, the root module assembly
    /// remains the defensive fallback.
    /// </remarks>
    internal Assembly HostIdentityAssembly => OverrideEntryPointAssembly ?? Assembly.GetEntryAssembly() ?? RootModuleAssembly;

    /// <summary>
    /// Gets the environment provider for the application.
    /// </summary>
    public IEnvironmentProvider EnvironmentProvider { get; } = EnvironmentProvider ?? new DefaultEnvironmentProvider(Args);

    /// <summary>
    /// Gets a value indicating whether the current environment is development.
    /// </summary>
    public bool IsDevelopment => EnvironmentProvider.IsDevelopment;

    /// <summary>
    /// Gets the user-facing name of the application.
    /// </summary>
    /// <remarks>
    /// This value is intended for product surfaces such as generated OpenAPI titles, command output, and other places
    /// where callers need a readable application label. It is intentionally separate from <see cref="HostApplicationName"/>,
    /// which must stay aligned with the assembly identity used by the .NET Generic Host and ASP.NET static web asset
    /// manifest discovery.
    /// </remarks>
    public string ApplicationName
    {
        get => _applicationName ?? GetAssemblyNameOrDefault(RootModuleAssembly);
        init => _applicationName = value;
    }

    /// <summary>
    /// Gets the assembly-backed application identity assigned to <see cref="Microsoft.Extensions.Hosting.IHostEnvironment.ApplicationName"/>.
    /// </summary>
    /// <remarks>
    /// ASP.NET static web assets resolve runtime manifests by host application name. Custom display names should not be
    /// written into the host environment because they can point static web asset discovery at a non-existent manifest.
    /// By default AppSurface uses the process entry assembly when one is available; override
    /// <see cref="OverrideEntryPointAssembly"/> when a test or custom host needs to select a different manifest identity.
    /// This host identity is intentionally separate from <see cref="EntryPointAssembly"/>, which still drives
    /// application-owned type discovery.
    /// </remarks>
    public string HostApplicationName => GetAssemblyNameOrDefault(HostIdentityAssembly);

    /// <summary>
    /// Gets the list of modules that the application depends on.
    /// </summary>
    /// <returns>A read-only list of dependent modules.</returns>
    public IReadOnlyList<IAppSurfaceModule> GetDependencies() =>
        Dependencies.Modules
            .ToList();

    [ExcludeFromCodeCoverage(
        Justification = "Loaded assemblies normally expose a name; the fallback preserves legacy defensive behavior.")]
    private static string GetAssemblyNameOrDefault(Assembly assembly) =>
        assembly.GetName().Name ?? "AppSurfaceApp";
}
