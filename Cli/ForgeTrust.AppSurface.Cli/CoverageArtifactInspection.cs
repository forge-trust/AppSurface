using System.Runtime.InteropServices;
using System.Xml;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>A bounded, read-only lower-bound observation of known coverage output.</summary>
internal sealed record CoverageArtifactObservation(
    long Bytes,
    int FileCount,
    int Entries,
    bool Complete,
    bool Cancelled,
    DateTimeOffset ObservedAt,
    IReadOnlyList<CoverageArtifactContributor> Largest,
    IReadOnlyList<CoverageArtifactInspectionError> Errors)
{
    /// <summary>Gets bounded relative directories to inspect first on the next observation; these grant no ownership.</summary>
    internal IReadOnlyList<string> ContinuationDirectories { get; init; } = [];
}

/// <summary>A relative path and size observed during coverage inspection.</summary>
internal sealed record CoverageArtifactContributor(string Path, long Bytes);

/// <summary>A sanitized inspection failure, scoped to a repository-relative path.</summary>
internal sealed record CoverageArtifactInspectionError(string Type, string Scope);

/// <summary>Collector settings state and a precise reason for the result.</summary>
internal sealed record CoverageCollectorRisk(string State, string Cause);

/// <summary>Inspects known test output without reading report payloads or modifying files.</summary>
internal static class CoverageArtifactInspector
{
    private const int MaximumErrors = 32;
    private const int MaximumLargest = 5;
    private const int MaximumSettingsBytes = 64 * 1024;
    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".gstack", ".codex",
    };

    /// <summary>Counts regular files below repository TestResults and coverage-output roots.</summary>
    /// <remarks>Budgets include directory enumerations and entry metadata checks. Incomplete results are lower bounds.</remarks>
    public static CoverageArtifactObservation Inspect(
        string repositoryRoot,
        TimeProvider clock,
        TimeSpan budget,
        int maximumEntries,
        CancellationToken token,
        IReadOnlyList<string>? continuationDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(clock);
        if (budget < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
        if (maximumEntries < 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        var observedAt = clock.GetUtcNow();
        var started = clock.GetTimestamp();
        var bytes = 0L;
        var files = 0;
        var entries = 0;
        var complete = true;
        var cancelled = false;
        var errors = new List<CoverageArtifactInspectionError>();
        var largest = new List<CoverageArtifactContributor>();
        var continuation = new List<string>();

        bool WithinBudget() => clock.GetElapsedTime(started) <= budget && !token.IsCancellationRequested;
        void Error(string type, string scope)
        {
            if (errors.Count < MaximumErrors) errors.Add(new(type, SanitizeRelative(scope)));
        }

        string root;
        try
        {
            root = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(repositoryRoot));
            var info = new DirectoryInfo(root);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Error(info.Exists ? "LinkSkipped" : "MissingRoot", ".");
                complete = false;
                return Result();
            }
            for (var ancestor = info.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Error("LinkAncestor", ".");
                    complete = false;
                    return Result();
                }
            }
        }
        catch (Exception ex) when (IsFileSystemException(ex) || ex is ArgumentException)
        {
            Error(ex.GetType().Name, ".");
            complete = false;
            return Result();
        }

        var candidates = new LinkedList<(string Path, bool InScope)>();
        var queued = new HashSet<string>(StringComparer.Ordinal);
        var resumed = continuationDirectories is { Count: > 0 };
        if (continuationDirectories is { Count: > 128 }) throw new InvalidDataException("Inspection continuation exceeds its directory bound.");
        foreach (var relative in continuationDirectories ?? [])
        {
            if (!IsSafeContinuation(relative)) throw new InvalidDataException("Inspection continuation must contain bounded relative directories.");
            if (queued.Add(relative)) candidates.AddLast((relative == "." ? root : Path.Join(root, relative), IsOutputPath(relative)));
        }
        if (candidates.Count == 0) { candidates.AddLast((root, false)); queued.Add("."); }
        while (candidates.Count > 0)
        {
            if (!WithinBudget()) { complete = false; break; }
            var (directory, inScope) = candidates.First!.Value;
            candidates.RemoveFirst();
            var relDir = Relative(root, directory);
            // A persisted observation is untrusted and can become stale. Recheck every ancestor
            // before touching the resumed path; never follow links or enter a nested repository.
            if (!string.Equals(directory, root, PathComparison) && HasRepositoryMarker(directory)) continue;
            if (!IsSafeObservedDirectory(root, directory)) { Error("ContinuationUnavailable", relDir); complete = false; continue; }
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(directory); }
            catch (Exception ex) when (IsFileSystemException(ex)) { Error(ex.GetType().Name, relDir); complete = false; continue; }
            using var iterator = children.GetEnumerator();
            while (true)
            {
                if (!WithinBudget())
                {
                    complete = false;
                    candidates.AddLast((directory, inScope));
                    break;
                }
                string child;
                try { if (!iterator.MoveNext()) break; child = iterator.Current; }
                catch (Exception ex) when (IsFileSystemException(ex)) { Error(ex.GetType().Name, relDir); complete = false; break; }
                if (entries >= maximumEntries)
                {
                    complete = false;
                    candidates.AddLast((directory, inScope));
                    break;
                }
                entries++;
                var name = Path.GetFileName(child);
                var relative = Relative(root, child);
                try
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (SkippedNames.Contains(name)) continue;
                        var childInScope = inScope || name.Equals("TestResults", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("coverage-output", StringComparison.OrdinalIgnoreCase);
                        if (queued.Contains(relative)) continue;
                        if (candidates.Count >= 128)
                        {
                            Error("ContinuationCapacity", relative);
                            complete = false;
                            // Keep the interrupted path and repository root in the bounded
                            // cursor. If trimming is needed, the root scan can rediscover
                            // trimmed paths on a later call.
                            var currentIsPending = candidates.Any(item => item.Path.Equals(directory, PathComparison));
                            var rootIsPending = candidates.Any(item => item.Path.Equals(root, PathComparison));
                            var requiredSlots = (currentIsPending ? 0 : 1) + (rootIsPending ? 0 : 1);
                            while (candidates.Count + requiredSlots > 128)
                            {
                                var removable = candidates.Last;
                                while (removable is not null && (removable.Value.Path.Equals(root, PathComparison)
                                    || removable.Value.Path.Equals(directory, PathComparison))) removable = removable.Previous;
                                if (removable is null) break;
                                queued.Remove(Relative(root, removable.Value.Path));
                                candidates.Remove(removable);
                            }
                            if (!rootIsPending) { queued.Add("."); candidates.AddLast((root, false)); }
                            if (!currentIsPending) { queued.Add(relDir); candidates.AddLast((directory, inScope)); }
                            break;
                        }
                        queued.Add(relative);
                        candidates.AddLast((child, childInScope));
                        continue;
                    }
                    if (!inScope || !TryGetRegularFileLength(child, out var length)) continue;
                    bytes = SaturatingAdd(bytes, length);
                    files++;
                    largest.Add(new(SanitizeRelative(relative), length));
                    largest.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
                    if (largest.Count > MaximumLargest) largest.RemoveAt(largest.Count - 1);
                }
                catch (Exception ex) when (IsFileSystemException(ex)) { Error(ex.GetType().Name, relative); complete = false; }
            }
            // Partial means the observation is a lower bound; it does not mean remaining
            // queued directories should be abandoned while this invocation still has budget.
            if (entries >= maximumEntries && candidates.Count > 0) break;
        }
        if (token.IsCancellationRequested) { cancelled = true; complete = false; }
        else if (candidates.Count > 0 || clock.GetElapsedTime(started) > budget) complete = false;
        continuation.AddRange(candidates.Take(128).Select(candidate => Relative(root, candidate.Path)));
        // A continued observation covers only a portion of the repository. Never promote its
        // per-invocation byte count to a complete repository total or sum overlapping scans.
        if (resumed) complete = false;
        return Result();

        CoverageArtifactObservation Result() => new(bytes, files, entries, complete, cancelled, observedAt, largest.ToArray(), errors.ToArray())
        { ContinuationDirectories = continuationDirectories is null && complete ? [] : continuation.Distinct(StringComparer.Ordinal).Take(128).ToArray() };
    }

    private static bool IsSafeContinuation(string path) => path == "." || path is { Length: > 0 and <= 4096 }
        && !Path.IsPathRooted(path) && !path.Contains('\\')
        && path.Split('/').All(component => component is not ("" or "." or "..") && !SkippedNames.Contains(component));

    private static bool IsOutputPath(string relative) => relative.Split('/').Any(component =>
        component.Equals("TestResults", StringComparison.OrdinalIgnoreCase) || component.Equals("coverage-output", StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeObservedDirectory(string root, string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (current.FullName.Equals(root, PathComparison)) return true;
            if (SkippedNames.Contains(current.Name) || HasRepositoryMarker(current.FullName)) return false;
        }
        return false;
    }

    /// <summary>Reads a supplied effective settings file safely; this reports configured risk, not execution.</summary>
    public static CoverageCollectorRisk ReadCollectorRisk(string settingsPath)
    {
        ArgumentNullException.ThrowIfNull(settingsPath);
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists) return new("absent", "settings-file-missing");
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return new("unknown", "settings-file-link");
            if (!TryOpenRegularFile(settingsPath, out var stream, out var length)) return new("unknown", "settings-file-not-regular-or-unavailable");
            using (var validatedStream = stream!)
            {
                if (length > MaximumSettingsBytes) return new("unknown", "settings-file-over-64KiB");
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumSettingsBytes };
                using var reader = XmlReader.Create(validatedStream, settings);
                var enabled = false;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || !reader.LocalName.Equals("DataCollector", StringComparison.OrdinalIgnoreCase)) continue;
                    var friendlyName = reader.GetAttribute("friendlyName");
                    if (!string.Equals(friendlyName, "XPlat Code Coverage", StringComparison.OrdinalIgnoreCase)) continue;
                    if (reader.GetAttribute("isEnabled") is not null)
                        return new("unknown", "settings-collector-enablement-invalid");
                    var enablement = reader.GetAttribute("enabled");
                    if (enablement is not null && !bool.TryParse(enablement, out _))
                        return new("unknown", "settings-collector-enablement-invalid");
                    enabled |= enablement is null || bool.Parse(enablement);
                }
                return enabled ? new("enabled", "xplat-collector-enabled") : new("disabled", "xplat-collector-not-configured-or-disabled");
            }
        }
        catch (FileNotFoundException) { return new("absent", "settings-file-missing"); }
        catch (DirectoryNotFoundException) { return new("absent", "settings-file-missing"); }
        catch (XmlException) { return new("unknown", "settings-xml-invalid-or-dtd-prohibited"); }
        catch (Exception ex) when (IsFileSystemException(ex)) { return new("unknown", "settings-file-" + ex.GetType().Name); }
    }

    private static bool HasRepositoryMarker(string path) => Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static string SanitizeRelative(string relative)
    {
        var segments = relative.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (new[] { "secret", "token", "password", "credential", "privatekey", "api_key", "apikey" }
                .Any(marker => segment.Contains(marker, StringComparison.OrdinalIgnoreCase))) segments[index] = "<redacted>";
        }
        return CoverageArtifactDisplay.Bound(string.Join('/', segments));
    }

    private static bool TryGetRegularFileLength(string path, out long length)
    {
        using var stream = TryOpenRegularFile(path, out var opened, out length) ? opened : null;
        return stream is not null;
    }

    /// <summary>Opens a candidate without following links and validates native regular-file facts.</summary>
    /// <param name="path">Observed local path inside the caller's bounded scan.</param>
    /// <param name="stream">A caller-owned stream on success; null when native facts reject the candidate.</param>
    /// <param name="length">Observed nonnegative byte length on success.</param>
    /// <param name="nativeCalls">Optional per-call native observations for decoding and cleanup tests.</param>
    /// <remarks>Injected observations verify managed decisions; native platform acceptance is separate.</remarks>
    internal static bool TryOpenRegularFile(string path, out FileStream? stream, out long length,
        CoverageArtifactCandidateCalls? nativeCalls = null)
    {
        var calls = nativeCalls ?? CoverageArtifactCandidateCalls.Default;
        stream = null;
        length = 0;
        if (calls.IsWindows)
        {
            var handle = calls.WindowsOpen(path);
            try
            {
                if (handle.IsInvalid) throw new IOException("Unable to inspect candidate handle.");
                if (calls.WindowsType(handle) != 1
                    || !calls.WindowsInformation(handle, out var info)
                    || (info.FileAttributes & (0x10u | 0x400u)) != 0) return false;
                var nativeLength = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
                if (nativeLength > long.MaxValue) return false;
                length = (long)nativeLength;
                stream = new FileStream(handle, FileAccess.Read);
                return true;
            }
            finally { if (stream is null) handle.Dispose(); }
        }

        if (!calls.IsLinux && !calls.IsMacOS) return false;
        if (!calls.IsMacOS && calls.Architecture is not (Architecture.X64 or Architecture.Arm64)) return false;
        var flags = (calls.IsMacOS ? 0x00000004 : 0x00000800)
            | CoverageFileSystemInterop.UnixTraversalFlags(calls.IsMacOS, calls.Architecture).NoFollow;
        var descriptor = calls.UnixOpen(path, flags);
        if (descriptor < 0) throw new IOException("Unable to inspect candidate handle.");
        var safeHandle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        nint buffer = 0;
        try
        {
            buffer = Marshal.AllocHGlobal(512);
            if (calls.UnixStat(descriptor, buffer) != 0) throw new IOException("Unable to inspect candidate handle.");
            var mode = calls.IsMacOS
                ? unchecked((ushort)Marshal.ReadInt16(buffer, 4))
                : unchecked((uint)Marshal.ReadInt32(buffer, calls.Architecture == Architecture.X64 ? 24 : 16));
            if ((mode & 0xF000) != 0x8000) return false;
            length = Marshal.ReadInt64(buffer, calls.IsMacOS ? 96 : 48);
            if (length < 0) return false;
            stream = new FileStream(safeHandle, FileAccess.Read);
            return true;
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            if (stream is null) safeHandle.Dispose();
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
    private static bool IsFileSystemException(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;
}

/// <summary>Per-call native candidate-file observations; defaults always inspect the real host.</summary>
/// <remarks>Fake opens must return caller-owned native handles. Tests do not establish another OS's ABI or containment.</remarks>
internal sealed class CoverageArtifactCandidateCalls
{
    /// <summary>Gets the real host's immutable observation set.</summary>
    internal static CoverageArtifactCandidateCalls Default { get; } = new();
    /// <summary>Gets whether Windows handle metadata is selected.</summary>
    internal bool IsWindows { get; init; } = OperatingSystem.IsWindows();
    /// <summary>Gets whether Darwin stat decoding is selected.</summary>
    internal bool IsMacOS { get; init; } = OperatingSystem.IsMacOS();
    /// <summary>Gets whether Linux stat decoding is supported.</summary>
    internal bool IsLinux { get; init; } = OperatingSystem.IsLinux();
    /// <summary>Gets the process architecture used to decode Linux stat mode.</summary>
    internal Architecture Architecture { get; init; } = RuntimeInformation.ProcessArchitecture;
    /// <summary>Gets the Windows no-follow regular-candidate open operation.</summary>
    internal Func<string, SafeFileHandle> WindowsOpen { get; init; } = path => CoverageFileSystemInterop.WindowsCreateFile(path, 0x80000000, 0x7, 0, 3, 0x00200000, 0);
    /// <summary>Gets the Windows object-type observation.</summary>
    internal Func<SafeFileHandle, uint> WindowsType { get; init; } = CoverageFileSystemInterop.WindowsGetFileType;
    /// <summary>Gets the Windows native size and attribute observation.</summary>
    internal CoverageArtifactStateCalls.ReadWindowsInformation WindowsInformation { get; init; } = CoverageFileSystemInterop.WindowsGetFileInformationByHandle;
    /// <summary>Gets the Unix no-follow candidate open operation.</summary>
    internal Func<string, int, int> UnixOpen { get; init; } = CoverageFileSystemInterop.UnixOpen;
    /// <summary>Gets the Unix native stat operation.</summary>
    internal Func<int, nint, int> UnixStat { get; init; } = CoverageFileSystemInterop.UnixFStat;
}
