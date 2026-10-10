using System.Reflection;
using Aspire.Hosting;
using CliFx.Binding;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Aspire.Testing;

/// <summary>
/// Creates deterministic Aspire testing builders from AppSurface profile types.
/// </summary>
/// <remarks>
/// This factory activates the selected profile through AppSurface dependency injection but never starts the activation
/// host or invokes the AppHost entry point. The selected profile must not declare CliFx option or positional-parameter
/// bindings because this typed path intentionally has no command-line binding phase.
/// </remarks>
public static class AppSurfaceAspireTestingBuilder
{
    /// <summary>
    /// Creates a configurable Aspire testing builder for a typed AppSurface profile.
    /// </summary>
    /// <typeparam name="TAppHost">The generated public <c>Projects.*</c> marker for the AppHost project.</typeparam>
    /// <typeparam name="TModule">The public AppSurface root module in the AppHost assembly.</typeparam>
    /// <typeparam name="TProfile">The public AppSurface Aspire profile selected for the test graph.</typeparam>
    /// <param name="cancellationToken">A token checked between synchronous activation and composition steps.</param>
    /// <returns>A builder that the caller may customize once before calling <c>BuildAsync</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The types or generated marker are invalid, the profile uses CliFx member binding, or activation or composition
    /// fails. The exception identifies the affected type and preserves the original failure as its inner exception.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public static async Task<AppSurfaceAspireProfileTestingBuilder> CreateAsync<TAppHost, TModule, TProfile>(
        CancellationToken cancellationToken = default)
        where TAppHost : class
        where TModule : IAppSurfaceHostModule, new()
        where TProfile : AspireProfile
    {
        AspireProfileActivationLease<TProfile>? activation = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectDirectory = ValidateTypesAndGetProjectDirectory<TAppHost, TModule, TProfile>();

            try
            {
                activation = await AspireProfileActivator.ActivateAsync<TAppHost, TModule, TProfile>(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!AspireExceptionUtilities.IsProcessFatal(ex))
            {
                throw new InvalidOperationException(
                    $"Profile activation failed for '{typeof(TProfile).FullName}' using module '{typeof(TModule).FullName}'. " +
                    "Ensure the profile and its constructor dependencies are registered by the AppHost module.",
                    ex);
            }

            var profile = activation.Profile;

            var innerBuilder = CreateAspireBuilder<TAppHost>(profile, projectDirectory);

            try
            {
                profile.Compose(innerBuilder, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!AspireExceptionUtilities.IsProcessFatal(ex))
            {
                throw new InvalidOperationException(
                    $"Profile composition failed for '{typeof(TProfile).FullName}'. Inspect its dependencies and components.",
                    ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = new AppSurfaceAspireProfileTestingBuilder(innerBuilder, activation);
            activation = null;
            return result;
        }
        finally
        {
            await DisposeAfterFailureAsync(activation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a typed Aspire testing graph with per-call customization before profile activation and composition.
    /// </summary>
    /// <typeparam name="TAppHost">The generated public AppHost marker in the module/profile assembly.</typeparam>
    /// <typeparam name="TModule">The public root module activated with its ordinary parameterless constructor.</typeparam>
    /// <typeparam name="TProfile">The public, command-decorated profile resolved through activation services.</typeparam>
    /// <param name="options">Required options; all three callbacks default to null.</param>
    /// <param name="cancellationToken">A token checked around each synchronous callback, activation and composition.</param>
    /// <returns>An Active testing builder with the completed graph, ready for one explicit BuildAsync call.</returns>
    /// <remarks>
    /// Delegate references are snapshotted at entry. Final module configuration precedes the activation configuration
    /// hook, all module/framework service registrations precede the activation services hook, and the selected
    /// provider creates the profile before the Aspire hook. The Aspire hook and profile composition receive the same
    /// Preparing wrapper: access and mutations are permitted, but build and disposal are rejected without changing
    /// ownership. Escaping errors abort unbuilt Aspire resources before activation services. The factory never starts
    /// a host or resource. Synchronous callbacks are not preempted by cancellation. Use immutable captures and keep
    /// activation services separate from Aspire's application services; later container registrations retain their
    /// own precedence. Existing CreateAsync remains available with its original composition behavior.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Options are null, before cancellation or type validation is checked.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed, or a callback throws this exception.</exception>
    /// <exception cref="InvalidOperationException">
    /// Marker/module/profile validation fails, or configuration, services, profile activation, Aspire customization
    /// or composition fails. Phase diagnostics identify all three types, preserve the original inner exception and
    /// provide corrective advice without including configuration values.
    /// </exception>
    public static async Task<AppSurfaceAspireProfileTestingBuilder> CreateConfiguredAsync<TAppHost, TModule, TProfile>(
        AppSurfaceAspireTestingOptions options,
        CancellationToken cancellationToken = default)
        where TAppHost : class
        where TModule : IAppSurfaceHostModule, new()
        where TProfile : AspireProfile
    {
        ArgumentNullException.ThrowIfNull(options);
        var configurationHook = options.ConfigureActivationConfiguration;
        var servicesHook = options.ConfigureActivationServices;
        var aspireHook = options.ConfigureAspireBuilder;
        cancellationToken.ThrowIfCancellationRequested();
        var projectDirectory = ValidateTypesAndGetProjectDirectory<TAppHost, TModule, TProfile>();
        AspireProfileActivationLease<TProfile>? activation = null;
        AppSurfaceAspireProfileTestingBuilder? preparing = null;
        Exception? hookFailure = null;

        void InvokeHook<TArgument>(Action<TArgument> callback, TArgument argument, string phase, string advice)
        {
            try
            {
                callback(argument);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (!AspireExceptionUtilities.IsProcessFatal(error))
            {
                hookFailure = CreateConfiguredFailure<TAppHost, TModule, TProfile>(phase, advice, error);
                throw hookFailure;
            }
        }

        try
        {
            try
            {
                activation = await AspireProfileActivator.ActivateConfiguredAsync<TAppHost, TModule, TProfile>(
                    configurationHook is null ? null : configuration => InvokeHook(
                        configurationHook, configuration, "Activation configuration",
                        "Add constructor-visible configuration overlays in ConfigureActivationConfiguration."),
                    servicesHook is null ? null : services => InvokeHook(
                        servicesHook, services, "Activation services",
                        "Register constructor dependencies in ConfigureActivationServices; do not build a separate provider."),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (!AspireExceptionUtilities.IsProcessFatal(error))
            {
                if (ReferenceEquals(error, hookFailure))
                {
                    throw;
                }

                throw CreateConfiguredFailure<TAppHost, TModule, TProfile>("Profile activation",
                    "Register the profile and constructor dependencies in the AppHost module or activation services hook.", error);
            }

            var profile = activation.Profile;
            cancellationToken.ThrowIfCancellationRequested();
            IDistributedApplicationBuilder innerBuilder;
            try
            {
                innerBuilder = CreateAspireBuilder<TAppHost>(profile, projectDirectory);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (!AspireExceptionUtilities.IsProcessFatal(error))
            {
                throw CreateConfiguredFailure<TAppHost, TModule, TProfile>("Aspire builder",
                    "Check profile PassThroughArgs and the generated AppHost project directory before customizing the graph.", error);
            }

            preparing = new AppSurfaceAspireProfileTestingBuilder(innerBuilder, activation, isPreparing: true);
            activation = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (aspireHook is not null)
            {
                InvokeHook(aspireHook, preparing, "Aspire builder",
                    "Customize configuration, services and resources; build and disposal are unavailable during preparation.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                profile.Compose(preparing, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (!AspireExceptionUtilities.IsProcessFatal(error))
            {
                throw CreateConfiguredFailure<TAppHost, TModule, TProfile>("Profile composition",
                    "Inspect profile dependencies and components; select the graph through constructor services.", error);
            }

            cancellationToken.ThrowIfCancellationRequested();
            preparing.CompletePreparation();
            var result = preparing;
            preparing = null;
            return result;
        }
        finally
        {
            if (preparing is not null)
            {
                try
                {
                    await preparing.AbortPreparationAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Factory cleanup, including fatal cleanup errors, cannot replace its primary creation failure.
                }
            }

            await DisposeAfterFailureAsync(activation).ConfigureAwait(false);
        }
    }

    private static IDistributedApplicationBuilder CreateAspireBuilder<TAppHost>(AspireProfile profile, string projectDirectory)
        where TAppHost : class =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = profile.PassThroughArgs,
            AssemblyName = typeof(TAppHost).Assembly.GetName().Name,
            ProjectDirectory = projectDirectory,
            DisableDashboard = true
        });

    private static InvalidOperationException CreateConfiguredFailure<TAppHost, TModule, TProfile>(
        string phase, string advice, Exception error) =>
        new($"{phase} failed for AppHost '{typeof(TAppHost).FullName}', module '{typeof(TModule).FullName}', " +
            $"profile '{typeof(TProfile).FullName}'. {advice}", error);

    private static string ValidateTypesAndGetProjectDirectory<TAppHost, TModule, TProfile>()
        where TAppHost : class
        where TModule : IAppSurfaceHostModule, new()
        where TProfile : AspireProfile
    {
        var markerType = typeof(TAppHost);
        var moduleType = typeof(TModule);
        var profileType = typeof(TProfile);

        ValidatePublicClosedType(markerType, "AppHost marker");
        ValidatePublicClosedType(moduleType, "module");
        ValidatePublicClosedType(profileType, "profile");

        if (markerType.Assembly != moduleType.Assembly || markerType.Assembly != profileType.Assembly)
        {
            throw new InvalidOperationException(
                $"Type validation failed: AppHost marker '{markerType.FullName}', module '{moduleType.FullName}', and " +
                $"profile '{profileType.FullName}' must be public types in the same AppHost assembly.");
        }

        if (profileType.GetCustomAttribute<CommandAttribute>(inherit: false) is null)
        {
            throw new InvalidOperationException(
                $"Type validation failed: profile '{profileType.FullName}' must declare CliFx [Command] metadata.");
        }

        var boundMembers = GetProfileProperties(profileType)
            .Where(property => property.IsDefined(typeof(CommandOptionAttribute), inherit: true) ||
                               property.IsDefined(typeof(CommandParameterAttribute), inherit: true))
            .Select(property => property.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (boundMembers.Length > 0)
        {
            throw new InvalidOperationException(
                $"Profile '{profileType.FullName}' cannot use typed Aspire testing because command-bound member(s) " +
                $"{string.Join(", ", boundMembers)} require CliFx option or positional-parameter binding. " +
                "Move graph selection to constructor services or PassThroughArgs.");
        }

        var properties = markerType.GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.Name == "ProjectPath")
            .ToArray();
        if (properties.Length != 1 || properties[0].PropertyType != typeof(string) ||
            properties[0].GetMethod is null || properties[0].GetIndexParameters().Length != 0)
        {
            throw new InvalidOperationException(
                $"AppHost marker validation failed for '{markerType.FullName}'. Pass the generated Projects.* type with " +
                "exactly one public static readable string ProjectPath property.");
        }

        string? projectPath;
        try
        {
            projectPath = (string?)properties[0].GetValue(null);
        }
        catch (Exception ex) when (!AspireExceptionUtilities.IsProcessFatal(ex))
        {
            throw new InvalidOperationException(
                $"AppHost marker validation failed for '{markerType.FullName}' because ProjectPath could not be read.",
                ex is TargetInvocationException { InnerException: not null } ? ex.InnerException : ex);
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidOperationException(
                $"AppHost marker validation failed for '{markerType.FullName}': ProjectPath must not be empty.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(projectPath.Trim());
        }
        catch (Exception ex) when (!AspireExceptionUtilities.IsProcessFatal(ex))
        {
            throw new InvalidOperationException(
                $"AppHost marker validation failed for '{markerType.FullName}': ProjectPath is invalid.",
                ex);
        }

        if (!Path.IsPathRooted(fullPath) || !Directory.Exists(fullPath))
        {
            throw new InvalidOperationException(
                $"AppHost marker validation failed for '{markerType.FullName}': ProjectPath must name an existing directory.");
        }

        return fullPath;
    }

    private static void ValidatePublicClosedType(Type type, string role)
    {
        var isPublic = type.IsPublic || type.IsNestedPublic;
        if (!isPublic || type.ContainsGenericParameters || type.IsAbstract || !type.IsClass)
        {
            throw new InvalidOperationException(
                $"Type validation failed: {role} '{type.FullName}' must be a public, closed, concrete class in the AppHost assembly.");
        }
    }

    private static IEnumerable<PropertyInfo> GetProfileProperties(Type profileType)
    {
        for (var current = profileType; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var property in current.GetProperties(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                yield return property;
            }
        }
    }

    private static async ValueTask DisposeAfterFailureAsync<TProfile>(
        AspireProfileActivationLease<TProfile>? activation)
        where TProfile : AspireProfile
    {
        if (activation is null)
        {
            return;
        }

        ILogger? cleanupLogger = null;
        try
        {
            cleanupLogger = activation.Services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AppSurfaceAspireTestingBuilder));
        }
        catch (Exception)
        {
            // Logging is best-effort and must not interfere with cleanup.
        }

        try
        {
            await activation.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupException)
        {
            try
            {
                cleanupLogger?.LogWarning(
                    cleanupException,
                    "Aspire profile activation cleanup failed while preserving the primary factory failure.");
            }
            catch (Exception)
            {
                // Cleanup failures never replace the primary activation or composition failure.
            }
        }
    }
}
