using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>
/// Resolves the exact ReportGenerator runtime shipped beside this support assembly for fan-in collection.
/// </summary>
/// <remarks>
/// Unlike <see cref="ReportGeneratorPackageLocator"/>, this resolver never searches a NuGet cache,
/// a tool installation, PATH, or a source checkout. It verifies the assembly-relative payload
/// against the hash inventory embedded in this assembly before returning the entry point.
/// </remarks>
internal sealed class StrictReportGeneratorRuntimeLocator : IReportGeneratorPackageLocator
{
    internal const string Version = "5.5.10";
    internal const string TargetFramework = "net10.0";
    internal const string RelativeRuntimeDirectory = "appsurface-coverage-runtime/reportgenerator/5.5.10/net10.0";
    private const string ManifestResourceName = "ForgeTrust.AppSurface.Evidence.Coverage.reportgenerator-runtime-manifest.json";
    private const string RuntimeManifestFileName = "runtime-manifest.json";
    private const string DiagnosticCode = "ASCOV140";

    /// <summary>
    /// Resolves and verifies the payload relative to this assembly's directory.
    /// </summary>
    /// <returns>The verified entry point, dependency manifest, runtime configuration, and payload directory.</returns>
    public string ResolveReportGeneratorDll()
        => ResolveRuntime().EntryPointPath;

    /// <summary>
    /// Resolves and verifies the payload relative to this assembly's directory.
    /// </summary>
    /// <returns>The verified entry point, dependency manifest, runtime configuration, and payload directory.</returns>
    internal ReportGeneratorRuntimePayload ResolveRuntime()
    {
        var assemblyLocation = typeof(StrictReportGeneratorRuntimeLocator).Assembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyLocation))
        {
            throw CreateFailure("The coverage assembly has no filesystem location.");
        }

        return ResolveRuntime(Path.GetDirectoryName(assemblyLocation)!);
    }

    /// <summary>
    /// Resolves and verifies the assembly-relative payload under the supplied assembly directory.
    /// </summary>
    /// <param name="assemblyDirectory">Directory containing this assembly and its runtime subdirectory.</param>
    /// <returns>The verified entry point, dependency manifest, runtime configuration, and payload directory.</returns>
    internal ReportGeneratorRuntimePayload ResolveRuntime(string assemblyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyDirectory);

        try
        {
            var expectedManifest = ReadEmbeddedManifest();
            var root = Path.GetFullPath(Path.Join(assemblyDirectory, RelativeRuntimeDirectory));
            var manifestPath = Path.Join(root, RuntimeManifestFileName);
            EnsureRegularDirectory(root);
            EnsureRegularFile(manifestPath);

            var deployedManifest = ReadBoundedFile(manifestPath, expectedManifest.Length);
            if (!deployedManifest.AsSpan().SequenceEqual(expectedManifest))
            {
                throw new InvalidDataException("The deployed runtime manifest differs from the assembly's pinned manifest.");
            }

            var inventory = ParseManifest(expectedManifest);
            ValidateExactFileSet(root, inventory);

            var entryPoint = Path.Join(root, "ReportGenerator.dll");
            var deps = Path.Join(root, "ReportGenerator.deps.json");
            var runtimeConfig = Path.Join(root, "ReportGenerator.runtimeconfig.json");
            return new ReportGeneratorRuntimePayload(Version, TargetFramework, root, entryPoint, deps, runtimeConfig);
        }
        catch (CoverageExecutionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                                           or ArgumentException or NotSupportedException or InvalidOperationException
                                           or FormatException)
        {
            throw CreateFailure(exception.Message);
        }
    }

    private static byte[] ReadEmbeddedManifest()
    {
        using var stream = typeof(StrictReportGeneratorRuntimeLocator).Assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new InvalidDataException("The pinned runtime manifest resource is missing from the coverage assembly.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] ReadBoundedFile(string path, int expectedLength)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[expectedLength + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read != expectedLength)
        {
            throw new InvalidDataException("The deployed runtime manifest has an unexpected size.");
        }

        return buffer[..expectedLength];
    }

    /// <summary>Validates the pinned manifest format and returns its exact runtime inventory.</summary>
    /// <remarks>Intentionally internal so package-format regressions can be tested without modifying assembly resources.</remarks>
    internal static IReadOnlyDictionary<string, string> ParseManifest(byte[] manifestBytes)
    {
        using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        RequireObjectProperties(root, "schemaVersion", "component", "version", "targetFramework", "entryPoint", "files");
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || root.GetProperty("component").GetString() != "ReportGenerator"
            || root.GetProperty("version").GetString() != Version
            || root.GetProperty("targetFramework").GetString() != TargetFramework
            || root.GetProperty("entryPoint").GetString() != "ReportGenerator.dll")
        {
            throw new InvalidDataException("The pinned runtime manifest has an unsupported identity or version.");
        }

        var inventory = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            RequireObjectProperties(file, "path", "sha256");
            var path = file.GetProperty("path").GetString();
            var hash = file.GetProperty("sha256").GetString();
            if (string.IsNullOrWhiteSpace(path) || !IsSafeRelativePath(path)
                || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)
                || !inventory.TryAdd(path, hash.ToLowerInvariant()))
            {
                throw new InvalidDataException("The pinned runtime manifest contains an invalid or duplicate file entry.");
            }
        }

        foreach (var required in new[] { "ReportGenerator.dll", "ReportGenerator.deps.json", "ReportGenerator.runtimeconfig.json" })
        {
            if (!inventory.ContainsKey(required))
            {
                throw new InvalidDataException($"The pinned runtime manifest does not contain required file '{required}'.");
            }
        }

        if (inventory.Count == 0)
        {
            throw new InvalidDataException("The pinned runtime manifest contains no runtime files.");
        }

        return inventory;
    }

    private static void RequireObjectProperties(JsonElement element, params string[] expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A pinned runtime manifest object has an invalid JSON shape.");
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!actual.Add(property.Name))
            {
                throw new InvalidDataException($"The pinned runtime manifest repeats property '{property.Name}'.");
            }
        }

        if (!actual.SetEquals(expectedNames))
        {
            throw new InvalidDataException("The pinned runtime manifest has missing or unknown properties.");
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static void ValidateExactFileSet(string root, IReadOnlyDictionary<string, string> inventory)
    {
        var expectedFiles = new HashSet<string>(inventory.Keys, StringComparer.Ordinal)
        {
            RuntimeManifestFileName,
        };
        var actualFiles = EnumerateRegularFiles(root);
        if (!actualFiles.SetEquals(expectedFiles))
        {
            var missing = expectedFiles.Except(actualFiles, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
            var extra = actualFiles.Except(expectedFiles, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
            throw new InvalidDataException(missing is not null
                ? $"A required runtime file is missing: '{missing}'."
                : $"An unlisted runtime file is present: '{extra}'.");
        }

        foreach (var (relativePath, expectedHash) in inventory)
        {
            var path = Path.Join(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            EnsureRegularFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"A runtime file does not match its pinned hash: '{relativePath}'.");
            }
        }
    }

    private static HashSet<string> EnumerateRegularFiles(string root)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            EnsureRegularDirectory(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The runtime payload contains a link or reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                if (!files.Add(relative))
                {
                    throw new InvalidDataException("The runtime payload contains duplicate file paths.");
                }
            }
        }

        return files;
    }

    private static void EnsureRegularDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The runtime payload directory is missing or is not a regular directory.");
        }
    }

    private static void EnsureRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A runtime payload file is missing or is not a regular file.");
        }
    }

    private static CoverageExecutionException CreateFailure(string detail)
        => new($"{DiagnosticCode} ReportGenerator runtime payload is unavailable or invalid. Problem: the pinned package-owned runtime failed validation. Cause: {detail} Fix: restore the exact ForgeTrust.AppSurface.Evidence.Coverage package payload beside the application assembly. Docs: Evidence/ForgeTrust.AppSurface.Evidence.Coverage/README.md#reportgenerator-runtime-payload.");
}

/// <summary>
/// Verified filesystem paths for the pinned ReportGenerator runtime.
/// </summary>
/// <param name="Version">Pinned ReportGenerator version.</param>
/// <param name="TargetFramework">Runtime target framework.</param>
/// <param name="RootDirectory">Assembly-relative directory containing the verified payload.</param>
/// <param name="EntryPointPath">ReportGenerator entry assembly path.</param>
/// <param name="DependencyManifestPath">ReportGenerator dependency manifest path.</param>
/// <param name="RuntimeConfigurationPath">ReportGenerator runtime configuration path.</param>
internal sealed record ReportGeneratorRuntimePayload(
    string Version,
    string TargetFramework,
    string RootDirectory,
    string EntryPointPath,
    string DependencyManifestPath,
    string RuntimeConfigurationPath);
