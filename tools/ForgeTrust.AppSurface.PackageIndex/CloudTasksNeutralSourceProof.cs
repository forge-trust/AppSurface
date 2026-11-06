using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Verifies that a pinned baseline and candidate contain identical authored inputs for the neutral Durable packages.
/// </summary>
internal static class CloudTasksNeutralSourceProof
{
    internal static IReadOnlyList<string> SourceDirectories { get; } =
    [
        "ForgeTrust.AppSurface.Core",
        "Flow/ForgeTrust.AppSurface.Flow",
        "Flow/ForgeTrust.AppSurface.Flow.Generators",
        "Workers/ForgeTrust.AppSurface.Workers",
        "Durable/ForgeTrust.AppSurface.Durable"
    ];

    internal static IReadOnlyList<string> RequiredSharedFiles { get; } =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "NuGet.package-gate.config",
        "LICENSE"
    ];

    internal static IReadOnlyList<string> OptionalSharedFiles { get; } =
    [
        ".editorconfig",
        "global.json",
        "Directory.Build.rsp"
    ];

    internal static IReadOnlyList<string> SharedFiles { get; } =
        [.. RequiredSharedFiles, "Directory.Packages.props", .. OptionalSharedFiles];

    private static readonly IReadOnlyList<string> ApprovedSdkRootIds = ["Google.Cloud.Tasks.V2", "Grpc.Net.Client"];
    private const int MaximumCloudTasksLockBytes = 1024 * 1024;
    private static readonly Regex CanonicalVersion = new(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Compares source files and shared build inputs, allowing only the two exact Cloud Tasks central package pins.
    /// </summary>
    /// <param name="baselineRoot">Root of the source archive pinned for the neutral package baseline.</param>
    /// <param name="candidateRoot">Root of the candidate repository source.</param>
    /// <returns>Sorted baseline and candidate SHA-256 evidence keyed by repository-relative file path.</returns>
    /// <exception cref="PackageIndexException">A source file, shared input, or unapproved central pin differs.</exception>
    internal static IReadOnlyDictionary<string, string> Verify(string baselineRoot, string candidateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRoot);

        baselineRoot = Path.GetFullPath(baselineRoot);
        candidateRoot = Path.GetFullPath(candidateRoot);
        RequireDirectory(baselineRoot, "baseline source root");
        RequireDirectory(candidateRoot, "candidate source root");

        var comparableFiles = new SortedDictionary<string, (string BaselinePath, string CandidatePath)>(StringComparer.Ordinal);
        foreach (var relativeDirectory in SourceDirectories)
        {
            var baselineDirectory = Resolve(baselineRoot, relativeDirectory);
            var candidateDirectory = Resolve(candidateRoot, relativeDirectory);
            RequireDirectory(baselineDirectory, $"baseline source directory '{relativeDirectory}'", baselineRoot);
            RequireDirectory(candidateDirectory, $"candidate source directory '{relativeDirectory}'", candidateRoot);

            var baselineFiles = EnumerateInputFiles(baselineRoot, baselineDirectory);
            var candidateFiles = EnumerateInputFiles(candidateRoot, candidateDirectory);
            RequireSameKeys(baselineFiles.Keys, candidateFiles.Keys, relativeDirectory, "authored source files");
            foreach (var relativePath in baselineFiles.Keys)
            {
                comparableFiles.Add(relativePath, (baselineFiles[relativePath], candidateFiles[relativePath]));
            }
        }

        foreach (var relativePath in RequiredSharedFiles)
        {
            var baselinePath = Resolve(baselineRoot, relativePath);
            var candidatePath = Resolve(candidateRoot, relativePath);
            RequireFile(baselinePath, $"baseline shared build input '{relativePath}'");
            RequireFile(candidatePath, $"candidate shared build input '{relativePath}'");
            comparableFiles.Add(relativePath, (baselinePath, candidatePath));
        }

        foreach (var relativePath in OptionalSharedFiles)
        {
            var baselinePath = Resolve(baselineRoot, relativePath);
            var candidatePath = Resolve(candidateRoot, relativePath);
            var baselineExists = File.Exists(baselinePath);
            var candidateExists = File.Exists(candidatePath);
            if (baselineExists != candidateExists)
            {
                throw new PackageIndexException($"Neutral source parity failed: optional build input '{relativePath}' exists in only one source tree.");
            }

            if (!baselineExists) continue;
            RequireRegularFile(baselinePath, $"baseline optional build input '{relativePath}'");
            RequireRegularFile(candidatePath, $"candidate optional build input '{relativePath}'");
            comparableFiles.Add(relativePath, (baselinePath, candidatePath));
        }

        foreach (var relativePath in DiscoverAncestorBuildInputs(baselineRoot, candidateRoot))
        {
            var baselinePath = Resolve(baselineRoot, relativePath);
            var candidatePath = Resolve(candidateRoot, relativePath);
            if (!File.Exists(baselinePath) || !File.Exists(candidatePath))
            {
                throw new PackageIndexException($"Neutral source parity failed: ancestor build input '{relativePath}' exists in only one source tree.");
            }

            RequireRegularFile(baselinePath, $"baseline ancestor build input '{relativePath}'");
            RequireRegularFile(candidatePath, $"candidate ancestor build input '{relativePath}'");
            comparableFiles.TryAdd(relativePath, (baselinePath, candidatePath));
        }

        var sdkPins = ReadCloudTasksLockPins(candidateRoot);
        var baselinePackagesPath = Resolve(baselineRoot, "Directory.Packages.props");
        var candidatePackagesPath = Resolve(candidateRoot, "Directory.Packages.props");
        RequireFile(baselinePackagesPath, "baseline Directory.Packages.props");
        RequireFile(candidatePackagesPath, "candidate Directory.Packages.props");
        var normalizedBaselinePackages = NormalizeCentralPackages(baselinePackagesPath, sdkPins, allowMatchingOrNoPins: true);
        var normalizedCandidatePackages = NormalizeCentralPackages(candidatePackagesPath, sdkPins, allowMatchingOrNoPins: false);
        if (!XNode.DeepEquals(normalizedBaselinePackages, normalizedCandidatePackages))
        {
            throw new PackageIndexException("Neutral source parity failed: Directory.Packages.props changed beyond the two approved Cloud Tasks pins.");
        }

        comparableFiles.Add("Directory.Packages.props", (baselinePackagesPath, candidatePackagesPath));
        var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relativePath, paths) in comparableFiles)
        {
            if (!string.Equals(relativePath, "Directory.Packages.props", StringComparison.Ordinal))
                CompareBytes(paths.BaselinePath, paths.CandidatePath, relativePath);
            evidence.Add($"baseline/{relativePath}", HashFile(paths.BaselinePath));
            evidence.Add($"candidate/{relativePath}", HashFile(paths.CandidatePath));
        }
        evidence.Add("candidate/Durable/ForgeTrust.AppSurface.Durable.CloudTasks/packages.lock.json",
            HashFile(Resolve(candidateRoot, "Durable/ForgeTrust.AppSurface.Durable.CloudTasks/packages.lock.json")));

        return evidence;
    }

    private static IReadOnlyDictionary<string, string> ReadCloudTasksLockPins(string candidateRoot)
    {
        const string relativePath = "Durable/ForgeTrust.AppSurface.Durable.CloudTasks/packages.lock.json";
        var directory = Resolve(candidateRoot, "Durable/ForgeTrust.AppSurface.Durable.CloudTasks");
        RequireDirectory(directory, "candidate Cloud Tasks SDK lock directory", candidateRoot);
        var path = Resolve(candidateRoot, relativePath);
        RequireFile(path, "candidate Cloud Tasks SDK packages.lock.json");
        byte[] lockBytes;
        try
        {
            using var stream = File.OpenRead(path);
            var length = stream.Length;
            if (length is <= 0 or > MaximumCloudTasksLockBytes)
                throw new PackageIndexException($"Neutral source parity failed: candidate Cloud Tasks SDK lock file is empty or exceeds the {MaximumCloudTasksLockBytes}-byte inspection limit.");
            lockBytes = new byte[(int)length];
            stream.ReadExactly(lockBytes);
            if (stream.ReadByte() != -1)
                throw new PackageIndexException($"Neutral source parity failed: candidate Cloud Tasks SDK lock file exceeds the {MaximumCloudTasksLockBytes}-byte inspection limit.");
        }
        catch (PackageIndexException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PackageIndexException("Neutral source parity failed: candidate Cloud Tasks SDK lock file is unreadable.", exception);
        }

        JsonDocument document;
        try
        {
            var offset = lockBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            document = JsonDocument.Parse(lockBytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            throw new PackageIndexException("Neutral source parity failed: candidate Cloud Tasks SDK lock file is malformed or unreadable.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidSdkLock("root must be a JSON object");
            var dependenciesProperties = root.EnumerateObject().Where(static property => property.NameEquals("dependencies")).ToArray();
            if (dependenciesProperties.Length != 1 || dependenciesProperties[0].Value.ValueKind != JsonValueKind.Object)
                throw InvalidSdkLock("the dependencies object is missing or duplicated");

            var targetProperties = dependenciesProperties[0].Value.EnumerateObject().Where(static property => property.NameEquals("net10.0")).ToArray();
            if (targetProperties.Length != 1 || targetProperties[0].Value.ValueKind != JsonValueKind.Object)
                throw InvalidSdkLock("the net10.0 target closure is missing or duplicated");

            var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var approved in ApprovedSdkRootIds)
            {
                var roots = targetProperties[0].Value.EnumerateObject()
                    .Where(package => string.Equals(package.Name, approved, StringComparison.Ordinal)).ToArray();
                if (roots.Length != 1 || roots[0].Value.ValueKind != JsonValueKind.Object)
                    throw InvalidSdkLock($"direct root '{approved}' is missing or duplicated");
                var fields = roots[0].Value;
                if (!ReadStringProperty(fields, "type", out var type) || !string.Equals(type, "Direct", StringComparison.Ordinal)
                    || !ReadStringProperty(fields, "resolved", out var version) || !IsCanonicalVersion(version))
                    throw InvalidSdkLock($"direct root '{approved}' must have type Direct and a canonical resolved version");
                pins.Add(approved, version);
            }
            return pins;
        }
    }

    private static bool ReadStringProperty(JsonElement element, string name, out string value)
    {
        var matches = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (matches.Length == 1 && matches[0].Value.ValueKind == JsonValueKind.String)
        {
            value = matches[0].Value.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static PackageIndexException InvalidSdkLock(string detail) =>
        new($"Neutral source parity failed: candidate Cloud Tasks SDK lock file {detail}.");

    private static bool IsCanonicalVersion(string value)
    {
        if (!CanonicalVersion.IsMatch(value)) return false;
        var separator = value.IndexOf('-');
        if (separator < 0) return true;
        return value[(separator + 1)..].Split('.').All(static identifier =>
            identifier.Any(static character => !char.IsAsciiDigit(character))
            || identifier.Length == 1 || identifier[0] != '0');
    }

    private static IReadOnlyList<string> DiscoverAncestorBuildInputs(string baselineRoot, string candidateRoot)
    {
        var relativeDirectories = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        foreach (var packageDirectory in SourceDirectories)
        {
            var current = packageDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                relativeDirectories.Add(current);
                var separator = current.LastIndexOf('/');
                current = separator < 0 ? string.Empty : current[..separator];
            }
        }

        var policyNames = new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", ".editorconfig" };
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relativeDirectory in relativeDirectories)
        {
            foreach (var fileName in policyNames)
            {
                var relativePath = string.IsNullOrEmpty(relativeDirectory) ? fileName : $"{relativeDirectory}/{fileName}";
                if (string.Equals(relativePath, "Directory.Packages.props", StringComparison.Ordinal)) continue;
                if (File.Exists(Resolve(baselineRoot, relativePath)) || File.Exists(Resolve(candidateRoot, relativePath)))
                    paths.Add(relativePath);
            }
        }

        return paths.ToArray();
    }

    private static Dictionary<string, string> EnumerateInputFiles(string root, string packageDirectory)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(packageDirectory);
        while (pendingDirectories.TryPop(out var currentDirectory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(currentDirectory).OrderBy(static path => path, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (Directory.Exists(path) && IsGeneratedDirectory(name)) continue;
                RejectReparsePoint(path);
                if (Directory.Exists(path))
                {
                    pendingDirectories.Push(path);
                    continue;
                }

                if (!File.Exists(path))
                {
                    throw new PackageIndexException($"Neutral source parity failed: unsupported filesystem entry '{Path.GetRelativePath(root, path)}'.");
                }

                var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, path));
                files.Add(relativePath, path);
            }
        }

        return files;
    }

    private static XDocument NormalizeCentralPackages(
        string path,
        IReadOnlyDictionary<string, string> sdkPins,
        bool allowMatchingOrNoPins)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new PackageIndexException($"Neutral source parity failed: cannot parse central package file '{Path.GetFileName(path)}': {ex.Message}");
        }

        if (document.Root is null)
        {
            throw new PackageIndexException("Neutral source parity failed: Directory.Packages.props has no XML root element.");
        }

        var packageNodes = document.Descendants()
            .Where(static element => element.Name.LocalName == "PackageVersion")
            .ToArray();
        var presentPins = new Dictionary<string, XElement[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var approved in ApprovedSdkRootIds)
        {
            var matches = packageNodes.Where(node => string.Equals(
                (string?)node.Attribute("Include"), approved, StringComparison.OrdinalIgnoreCase)).ToArray();
            presentPins.Add(approved, matches);
            if (matches.Length > 1)
                throw new PackageIndexException($"Neutral source parity failed: Directory.Packages.props pin '{approved}' is duplicated.");
        }

        if (allowMatchingOrNoPins && presentPins.Values.All(static matches => matches.Length == 0))
            return document;

        foreach (var approved in ApprovedSdkRootIds)
        {
            var matches = presentPins[approved];
            if (matches.Length != 1 || !HasExactApprovedPin(matches[0], approved, sdkPins[approved]))
            {
                var tree = allowMatchingOrNoPins ? "baseline" : "candidate";
                throw new PackageIndexException($"Neutral source parity failed: Directory.Packages.props {tree} pin '{approved}' is missing, partial, or differs from its Cloud Tasks SDK lock version/condition.");
            }

            RemovePackagePinWithIndent(matches[0]);
        }

        return document;
    }

    private static bool HasExactApprovedPin(XElement node, string packageId, string version)
    {
        if (node.Attributes().Count() != (packageId == "Grpc.Net.Client" ? 3 : 2)) return false;
        if (!string.Equals((string?)node.Attribute("Include"), packageId, StringComparison.Ordinal)) return false;
        if (!string.Equals((string?)node.Attribute("Version"), version, StringComparison.Ordinal)) return false;
        if (node.HasElements || node.Nodes().Any(static child => child is not XText text || !string.IsNullOrWhiteSpace(text.Value))) return false;

        if (packageId == "Google.Cloud.Tasks.V2") return node.Attribute("Condition") is null;
        return string.Equals(
            (string?)node.Attribute("Condition"),
            "'$(MSBuildProjectName)' == 'ForgeTrust.AppSurface.Durable.CloudTasks' OR '$(MSBuildProjectName)' == 'ForgeTrust.AppSurface.Durable.CloudTasks.Tests'",
            StringComparison.Ordinal);
    }

    private static void RemovePackagePinWithIndent(XElement node)
    {
        if (node.PreviousNode is XText previous && string.IsNullOrWhiteSpace(previous.Value)) previous.Remove();
        node.Remove();
    }

    private static void CompareBytes(string baselinePath, string candidatePath, string relativePath)
    {
        var baselineBytes = File.ReadAllBytes(baselinePath);
        var candidateBytes = File.ReadAllBytes(candidatePath);
        if (!baselineBytes.AsSpan().SequenceEqual(candidateBytes))
        {
            throw new PackageIndexException($"Neutral source parity failed: authored file '{relativePath}' differs between the pinned baseline and candidate.");
        }
    }

    private static void RequireSameKeys(IEnumerable<string> baselineKeys, IEnumerable<string> candidateKeys, string directory, string description)
    {
        var baselineSet = baselineKeys.ToHashSet(StringComparer.Ordinal);
        var candidateSet = candidateKeys.ToHashSet(StringComparer.Ordinal);
        var differences = baselineSet.Except(candidateSet, StringComparer.Ordinal)
            .Select(path => $"missing candidate {path}")
            .Concat(candidateSet.Except(baselineSet, StringComparer.Ordinal).Select(path => $"candidate-only {path}"))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        if (differences.Length != 0)
        {
            throw new PackageIndexException($"Neutral source parity failed: {description} differ under '{directory}': {string.Join(", ", differences)}.");
        }
    }

    private static void RequireDirectory(string path, string description, string? trustedRoot = null)
    {
        if (!Directory.Exists(path)) throw new PackageIndexException($"Neutral source parity failed: {description} is missing.");
        var fullPath = Path.GetFullPath(path);
        if (trustedRoot is null)
        {
            RejectReparsePoint(fullPath);
            return;
        }

        var fullRoot = Path.GetFullPath(trustedRoot);
        var relativePath = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new PackageIndexException($"Neutral source parity failed: {description} resolves outside its source root.");
        }

        RejectReparsePoint(fullRoot);
        var current = fullRoot;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            if (Directory.Exists(current) || File.Exists(current)) RejectReparsePoint(current);
        }
    }

    private static void RequireFile(string path, string description)
    {
        if (!File.Exists(path)) throw new PackageIndexException($"Neutral source parity failed: {description} is missing.");
        RequireRegularFile(path, description);
    }

    private static void RequireRegularFile(string path, string description)
    {
        if (!File.Exists(path)) throw new PackageIndexException($"Neutral source parity failed: {description} is missing.");
        RejectReparsePoint(path);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PackageIndexException($"Neutral source parity failed: symbolic link or reparse point '{path}' is not an accepted build input.");
        }
    }

    private static bool IsGeneratedDirectory(string name) =>
        name.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string NormalizeRelativePath(string path) => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string Resolve(string root, string relativePath) => Path.GetFullPath(Path.Join(root, relativePath));
}
