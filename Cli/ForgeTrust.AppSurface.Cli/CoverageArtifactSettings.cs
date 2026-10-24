using System.Text.Json;
#if !EVIDENCE_COVERAGE_CORE
using ForgeTrust.AppSurface.Evidence.Coverage;
#endif

#if EVIDENCE_COVERAGE_CORE
namespace ForgeTrust.AppSurface.Evidence.Coverage;
#else
namespace ForgeTrust.AppSurface.Cli;
#endif

/// <summary>Describes the effective collector-settings inspection for one evaluated project.</summary>
/// <param name="State">Whether XPlat is enabled, disabled, absent, unknown, or not applicable to this evaluation.</param>
/// <param name="Cause">A stable reason that never includes raw arguments, paths, or environment values.</param>
/// <param name="EffectivePath">The resolved settings path for internal use only; never copy it to public diagnostics.</param>
/// <param name="Project">A project file name without its directory path.</param>
/// <param name="Framework">The selected target framework, or <see langword="null"/> for outer evaluation.</param>
internal sealed record CoverageArtifactSettingsResult(string State, string Cause, string? EffectivePath, string Project, string? Framework);

/// <summary>Resolves and inspects runsettings for the actual test invocation without changing its arguments.</summary>
internal static class CoverageArtifactSettings
{
    private const int MaximumCapabilityJsonCharacters = 1024 * 1024;
    private const int MaximumArgumentCount = 1024;
    private const int MaximumArgumentCharacters = 64 * 1024;
    private const int MaximumSingleArgumentCharacters = 16 * 1024;
    private const int MaximumSettingsPathCharacters = 4096;

    /// <summary>Observes effective settings from evaluated MSBuild properties and recognized invocation overrides.</summary>
    /// <param name="project">The selected test project.</param>
    /// <param name="framework">The selected framework, or <see langword="null"/> for outer evaluation.</param>
    /// <param name="capabilityJson">Successful MSBuild capability JSON.</param>
    /// <param name="configuration">The selected build configuration.</param>
    /// <param name="testArguments">Literal test arguments, preserved as supplied.</param>
    /// <returns>A bounded state and internal effective path; paths must never be copied to public diagnostics.</returns>
    public static CoverageArtifactSettingsResult Observe(CoverageRunProject project, string? framework, string capabilityJson, string configuration, IReadOnlyList<string> testArguments)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(capabilityJson);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(testArguments);

        var projectPath = SafeProject(project.RelativePath);
        if (capabilityJson.Length > MaximumCapabilityJsonCharacters)
            return Unknown("capability-json-over-limit");

        string? inheritedPath;
        try
        {
            using var document = JsonDocument.Parse(capabilityJson, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetUniqueProperty(root, "Properties", out var properties, out var hasProperties)
                || !hasProperties
                || properties.ValueKind != JsonValueKind.Object)
            {
                return Unknown("capability-properties-unavailable");
            }

            if (!TryReadUniqueString(properties, "Configuration", required: true, out var evaluatedConfiguration)
                || string.IsNullOrWhiteSpace(evaluatedConfiguration))
            {
                return Unknown("configuration-evaluation-unavailable");
            }
            if (!string.Equals(evaluatedConfiguration, configuration, StringComparison.OrdinalIgnoreCase))
                return Unknown("configuration-evaluation-mismatch");

            if (!TryReadUniqueString(properties, "TargetFrameworks", required: false, out var targetFrameworks))
                return Unknown("framework-evaluation-unavailable");
            var evaluatedFrameworks = targetFrameworks?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

            if (framework is null && evaluatedFrameworks.Length > 0)
            {
                // The preflight reports both the outer evaluation and each selected inner framework.
                // The outer RunSettingsFilePath can be a global default that no selected test uses.
                return new("not-applicable", "framework-specific-evaluation-required", null, projectPath, null);
            }

            if (framework is not null)
            {
                if (string.IsNullOrWhiteSpace(framework)
                    || !TryReadUniqueString(properties, "TargetFramework", required: true, out var evaluatedFramework)
                    || string.IsNullOrWhiteSpace(evaluatedFramework))
                {
                    return Unknown("framework-evaluation-unavailable");
                }
                if (!string.Equals(evaluatedFramework, framework, StringComparison.OrdinalIgnoreCase)
                    || evaluatedFrameworks.Length > 0 && !evaluatedFrameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
                {
                    return Unknown("framework-evaluation-mismatch");
                }
            }
            else if (!TryReadUniqueString(properties, "TargetFramework", required: true, out var singleFramework)
                || string.IsNullOrWhiteSpace(singleFramework))
            {
                return Unknown("framework-evaluation-unavailable");
            }

            if (!TryReadUniqueString(properties, "RunSettingsFilePath", required: false, out inheritedPath))
                return Unknown("settings-property-unusable");
            if (string.IsNullOrWhiteSpace(inheritedPath) && inheritedPath is not null)
            {
                if (inheritedPath.Length == 0) inheritedPath = null;
                else return Unknown("settings-property-unusable");
            }
        }
        catch (JsonException)
        {
            return Unknown("capability-json-invalid");
        }

        if (!TryReadOverrides(testArguments, out var overrides, out var overrideCause))
            return Unknown(overrideCause);

        string? effective = inheritedPath;
        if (overrides.Count > 0)
        {
            var resolvedOverrides = new List<string?>(overrides.Count);
            foreach (var value in overrides)
            {
                if (value is null)
                {
                    resolvedOverrides.Add(null);
                    continue;
                }
                if (!TryResolveSettingsPath(project, value, out var resolvedOverride))
                    return Unknown("settings-path-unusable");
                resolvedOverrides.Add(resolvedOverride);
            }

            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (resolvedOverrides.Skip(1).Any(value => !comparer.Equals(value, resolvedOverrides[0])))
                return Unknown("settings-overrides-conflict");
            effective = overrides[0] is null ? null : resolvedOverrides[0];
        }

        if (effective is null)
            return new("absent", "settings-not-configured", null, projectPath, framework);
        if (effective.Length > MaximumSettingsPathCharacters || string.IsNullOrWhiteSpace(effective))
            return Unknown("settings-path-unusable");

        if (!TryResolveSettingsPath(project, effective, out var absolutePath))
            return Unknown("settings-path-unusable");

        var risk = CoverageArtifactInspector.ReadCollectorRisk(absolutePath);
        // A configured but missing file is not evidence that the invocation has no collector.
        return risk.State == "absent"
            ? new("unknown", risk.Cause, absolutePath, projectPath, framework)
            : new(risk.State, risk.Cause, absolutePath, projectPath, framework);

        CoverageArtifactSettingsResult Unknown(string cause) => new("unknown", cause, null, projectPath, framework);
    }

    private static bool TryReadOverrides(IReadOnlyList<string> arguments, out List<string?> overrides, out string cause)
    {
        overrides = [];
        cause = "settings-override-unusable";
        if (arguments.Count > MaximumArgumentCount)
        {
            cause = "settings-arguments-over-limit";
            return false;
        }

        var totalCharacters = 0;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is null || argument.Length > MaximumSingleArgumentCharacters
                || totalCharacters > MaximumArgumentCharacters - argument.Length)
            {
                cause = "settings-arguments-over-limit";
                return false;
            }
            totalCharacters += argument.Length;

            // After this delimiter tokens belong to the test host, not dotnet test/MSBuild.
            if (argument == "--") break;

            if (IsSettingsSplitOption(argument))
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                    return false;
                if (!TryCountArgument(arguments[index], ref totalCharacters, out cause)) return false;
                if (!TryAddSettingsPath(arguments[index], overrides)) return false;
                continue;
            }

            if (TryGetJoinedSettingsPath(argument, out var settingsPath))
            {
                if (!TryAddSettingsPath(settingsPath, overrides)) return false;
                continue;
            }

            if (TryGetPropertyPayload(argument, out var propertyPayload))
            {
                if (!TryAddPropertyOverrides(propertyPayload, overrides))
                    return false;
                continue;
            }

            if (IsSplitPropertyOption(argument))
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index])) return false;
                if (!TryCountArgument(arguments[index], ref totalCharacters, out cause)) return false;
                if (!TryAddPropertyOverrides(arguments[index], overrides))
                    return false;
            }
        }

        return true;
    }

    private static bool IsSettingsSplitOption(string argument) => argument.Equals("--settings", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("-s", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetJoinedSettingsPath(string argument, out string path)
    {
        path = string.Empty;
        foreach (var prefix in new[] { "--settings=", "--settings:", "-s=", "-s:" })
        {
            if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            path = argument[prefix.Length..];
            return true;
        }

        return false;
    }

    private static bool TryCountArgument(string argument, ref int totalCharacters, out string cause)
    {
        cause = "settings-override-unusable";
        if (argument.Length > MaximumSingleArgumentCharacters
            || totalCharacters > MaximumArgumentCharacters - argument.Length)
        {
            cause = "settings-arguments-over-limit";
            return false;
        }
        totalCharacters += argument.Length;
        return true;
    }

    private static bool TryGetPropertyPayload(string argument, out string payload)
    {
        foreach (var prefix in new[]
        {
            "--property:", "--property=", "/property:", "/property=", "-property:", "-property=",
            "/p:", "/p=", "-p:", "-p=",
        })
        {
            if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            payload = argument[prefix.Length..];
            return true;
        }

        payload = string.Empty;
        return false;
    }

    private static bool IsSplitPropertyOption(string argument) => argument.Equals("--property", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("/property", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("-property", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("/p", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("-p", StringComparison.OrdinalIgnoreCase);

    private static bool TryAddSettingsPath(string value, List<string?> overrides)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumSettingsPathCharacters) return false;
        overrides.Add(value);
        return true;
    }

    private static bool TryAddPropertyOverrides(string payload, List<string?> overrides)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        var assignments = payload.Split(';', StringSplitOptions.None);
        foreach (var assignment in assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment)) return false;
            var separator = assignment.IndexOf('=');
            if (separator <= 0) return false;
            var name = assignment[..separator].Trim();
            if (name.Length == 0) return false;
            if (!name.Equals("RunSettingsFilePath", StringComparison.OrdinalIgnoreCase)) continue;

            var value = assignment[(separator + 1)..];
            if (value.Length > MaximumSettingsPathCharacters || value.Length > 0 && string.IsNullOrWhiteSpace(value))
                return false;
            // An explicit empty MSBuild property clears an inherited project default.
            overrides.Add(value.Length == 0 ? null : value);
        }
        return true;
    }

    private static bool TryResolveSettingsPath(CoverageRunProject project, string path, out string absolutePath)
    {
        absolutePath = string.Empty;
        try
        {
            if (Path.IsPathRooted(path))
            {
                absolutePath = Path.GetFullPath(path);
                return true;
            }

            if (!TryGetInvocationDirectory(project, out var invocationDirectory)) return false;
            absolutePath = Path.GetFullPath(path, invocationDirectory);
            return true;
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            return false;
        }
    }

    private static bool TryGetInvocationDirectory(CoverageRunProject project, out string directory)
    {
        directory = string.Empty;
        try
        {
            var relativeProjectPath = project.RelativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativeProjectPath) || Path.IsPathRooted(relativeProjectPath)) return false;
            var segments = relativeProjectPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(segment => segment == "..")) return false;

            var fullProjectPath = Path.GetFullPath(project.FullPath);
            var parent = Path.GetDirectoryName(fullProjectPath);
            if (parent is null) return false;
            var parentSegments = segments.Take(segments.Length - 1).Count(segment => segment != ".");
            for (var index = 0; index < parentSegments; index++)
            {
                parent = Path.GetDirectoryName(parent);
                if (parent is null) return false;
            }

            var expectedProjectPath = Path.GetFullPath(relativeProjectPath, parent);
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(expectedProjectPath, fullProjectPath, pathComparison)) return false;
            directory = parent;
            return true;
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            return false;
        }
    }

    private static bool TryReadUniqueString(JsonElement parent, string name, bool required, out string? value)
    {
        value = null;
        if (!TryGetUniqueProperty(parent, name, out var property, out var found)) return false;
        if (!found) return !required;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return true;
    }

    private static bool TryGetUniqueProperty(JsonElement parent, string name, out JsonElement value, out bool found)
    {
        value = default;
        found = false;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) return false;
            found = true;
            value = property.Value;
        }
        return true;
    }

    private static bool IsPathException(Exception exception) => exception is ArgumentException
        or IOException
        or UnauthorizedAccessException
        or NotSupportedException
        or System.Security.SecurityException;

    private static string SafeProject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "<project>";
        var name = Path.GetFileName(path.Replace('\\', '/'));
        return name.Length is > 0 and <= 128 && !name.Any(char.IsControl) ? name : "<project>";
    }
}
