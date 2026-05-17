using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Resolves a deterministic development port for AppSurface web hosts in development when the caller has not
/// already supplied explicit ASP.NET Core endpoint configuration.
/// </summary>
internal static class AppSurfaceWebDevelopmentPortDefaults
{
    private const int DevelopmentPortBase = 5600;
    private const int DevelopmentPortRange = 1000;

    /// <summary>
    /// Applies a deterministic localhost <c>--urls</c> fallback in development when command-line arguments,
    /// environment variables, and local appsettings files do not specify where the host should listen.
    /// </summary>
    /// <param name="args">The command-line arguments supplied by the caller.</param>
    /// <param name="currentDirectory">The current working directory for the process.</param>
    /// <param name="applicationBaseDirectory">The application base directory for the host entry assembly.</param>
    /// <param name="environmentReader">Reads environment variables needed to detect the environment and explicit endpoint configuration.</param>
    /// <param name="environmentVariableNames">The available environment variable names, used to detect named Kestrel endpoint variables.</param>
    /// <returns>
    /// A resolution describing the effective arguments. If no fallback was needed, the returned arguments match
    /// the supplied <paramref name="args"/>.
    /// </returns>
    internal static AppSurfaceWebDevelopmentPortResolution Resolve(
        string[] args,
        string currentDirectory,
        string applicationBaseDirectory,
        Func<string, string?> environmentReader,
        IEnumerable<string>? environmentVariableNames = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        ArgumentNullException.ThrowIfNull(applicationBaseDirectory);
        ArgumentNullException.ThrowIfNull(environmentReader);

        var environmentName = ResolveEnvironmentName(args, environmentReader);
        if (!string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
            || HasExplicitEndpointConfiguration(args, currentDirectory, environmentName, environmentReader, environmentVariableNames))
        {
            return new(args, null, null);
        }

        var seedPath = ResolveSeedPath(currentDirectory, applicationBaseDirectory);
        var port = ComputeDeterministicPort(seedPath);
        var url = $"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}";

        return new([.. args, "--urls", url], port, seedPath);
    }

    private static string ResolveEnvironmentName(
        IReadOnlyList<string> args,
        Func<string, string?> environmentReader)
    {
        return ResolveEnvironmentArgument(args)
               ?? environmentReader("ASPNETCORE_ENVIRONMENT")
               ?? environmentReader("DOTNET_ENVIRONMENT")
               ?? Environments.Production;
    }

    private static string? ResolveEnvironmentArgument(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            const string environmentPrefix = "--environment=";
            if (arg.StartsWith(environmentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var inlineValue = arg[environmentPrefix.Length..];
                if (string.IsNullOrWhiteSpace(inlineValue))
                {
                    continue;
                }

                return inlineValue;
            }

            if (!string.Equals(arg, "--environment", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                continue;
            }

            return args[index + 1];
        }

        return null;
    }

    private static bool HasExplicitEndpointConfiguration(
        IReadOnlyList<string> args,
        string currentDirectory,
        string environmentName,
        Func<string, string?> environmentReader,
        IEnumerable<string>? environmentVariableNames)
    {
        return HasExplicitUrlArgument(args)
               || HasExplicitEndpointEnvironmentVariable(environmentReader, environmentVariableNames)
               || HasExplicitEndpointConfigurationSource(args, currentDirectory, environmentName);
    }

    private static bool HasExplicitUrlArgument(IReadOnlyList<string> args)
    {
        return args.Any(arg =>
            string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--urls", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExplicitEndpointEnvironmentVariable(
        Func<string, string?> environmentReader,
        IEnumerable<string>? environmentVariableNames)
    {
        return HasEnvironmentValue(environmentReader, "ASPNETCORE_URLS")
               || HasEnvironmentValue(environmentReader, "URLS")
               || HasEnvironmentValue(environmentReader, "ASPNETCORE_HTTP_PORTS")
               || HasEnvironmentValue(environmentReader, "DOTNET_HTTP_PORTS")
               || HasEnvironmentValue(environmentReader, "HTTP_PORTS")
               || HasEnvironmentValue(environmentReader, "ASPNETCORE_HTTPS_PORTS")
               || HasEnvironmentValue(environmentReader, "DOTNET_HTTPS_PORTS")
               || HasEnvironmentValue(environmentReader, "HTTPS_PORTS")
               || HasKestrelEndpointEnvironmentVariable(environmentReader, environmentVariableNames);
    }

    private static bool HasKestrelEndpointEnvironmentVariable(
        Func<string, string?> environmentReader,
        IEnumerable<string>? environmentVariableNames)
    {
        return environmentVariableNames?.Any(variableName =>
            variableName.StartsWith("Kestrel__Endpoints__", StringComparison.OrdinalIgnoreCase)
            && variableName.EndsWith("__Url", StringComparison.OrdinalIgnoreCase)
            && HasEnvironmentValue(environmentReader, variableName)) == true;
    }

    private static bool HasEnvironmentValue(
        Func<string, string?> environmentReader,
        string variableName)
    {
        return !string.IsNullOrWhiteSpace(environmentReader(variableName));
    }

    private static bool HasExplicitEndpointConfigurationSource(
        IReadOnlyList<string> args,
        string currentDirectory,
        string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(currentDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false)
            .AddCommandLine(args.ToArray())
            .Build();

        return HasConfigurationValue(configuration, "urls")
               || HasConfigurationValue(configuration, "http_ports")
               || HasConfigurationValue(configuration, "https_ports")
               || configuration
                   .GetSection("Kestrel:Endpoints")
                   .GetChildren()
                   .Any(endpoint => HasConfigurationValue(endpoint, "Url"));
    }

    private static bool HasConfigurationValue(
        IConfiguration configuration,
        string key)
    {
        return !string.IsNullOrWhiteSpace(configuration[key]);
    }

    private static string ResolveSeedPath(string currentDirectory, string applicationBaseDirectory)
    {
        var repoRoot = FindAncestorContainingMarker(
            currentDirectory,
            directoryPath =>
                Directory.Exists(Path.Combine(directoryPath, ".git"))
                || File.Exists(Path.Combine(directoryPath, ".git")));
        if (repoRoot is not null)
        {
            return NormalizePath(repoRoot);
        }

        var projectRoot = FindAncestorContainingMarker(
            applicationBaseDirectory,
            directoryPath => Directory.EnumerateFiles(directoryPath, "*.csproj").Any());
        if (projectRoot is not null)
        {
            return NormalizePath(projectRoot);
        }

        return NormalizePath(currentDirectory);
    }

    private static int ComputeDeterministicPort(string seedPath)
    {
        var hash = ComputeFnv1aHash(seedPath);
        return DevelopmentPortBase + (int)(hash % DevelopmentPortRange);
    }

    private static string? FindAncestorContainingMarker(
        string startPath,
        Func<string, bool> predicate)
    {
        var fullPath = Path.GetFullPath(startPath);
        var current = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : new DirectoryInfo(Path.GetDirectoryName(fullPath) ?? fullPath);

        while (current is not null)
        {
            if (predicate(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static uint ComputeFnv1aHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var currentByte in Encoding.UTF8.GetBytes(value))
        {
            hash ^= currentByte;
            hash *= prime;
        }

        return hash;
    }
}

/// <summary>
/// Describes the effective command-line arguments after AppSurface web development defaults have been resolved.
/// </summary>
/// <param name="Args">The effective arguments that should be passed into host startup.</param>
/// <param name="AppliedPort">The fallback port applied by the resolver, if any.</param>
/// <param name="SeedPath">The normalized workspace or project path used to compute the fallback port.</param>
internal readonly record struct AppSurfaceWebDevelopmentPortResolution(
    string[] Args,
    int? AppliedPort,
    string? SeedPath);
