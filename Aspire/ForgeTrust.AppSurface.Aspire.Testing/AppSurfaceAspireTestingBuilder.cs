using System.Reflection;
using Aspire.Hosting;
using CliFx.Binding;
using ForgeTrust.AppSurface.Core;
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

            var options = new DistributedApplicationOptions
            {
                Args = profile.PassThroughArgs,
                AssemblyName = typeof(TAppHost).Assembly.GetName().Name,
                ProjectDirectory = projectDirectory,
                DisableDashboard = true
            };

            var innerBuilder = DistributedApplication.CreateBuilder(options);

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
            cleanupLogger = activation.Services.GetService(typeof(ILoggerFactory)) is ILoggerFactory loggerFactory
                ? loggerFactory.CreateLogger(typeof(AppSurfaceAspireTestingBuilder))
                : null;
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
