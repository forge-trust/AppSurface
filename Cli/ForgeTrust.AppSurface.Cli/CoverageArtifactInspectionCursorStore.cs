using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Persists bounded, non-authoritative directory cursors for incremental coverage inspection.</summary>
internal static partial class CoverageArtifactInspectionCursorStore
{
    private const string StagingName = "staging";
    private const string InspectionName = "inspection";
    private const string StateName = "state.json";
    private const int MaxStateBytes = 64 * 1024;
    private const int MaxScopes = 32;
    private const int MaxPaths = 128;
    private const int MaxPathLength = 4096;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Reads the cursor for one scope without creating or modifying any store entry.</summary>
    /// <param name="store">Existing private domain, or null when optional management is unavailable.</param>
    /// <param name="scopeId">A 64-character hexadecimal repository observation fingerprint.</param>
    /// <param name="clock">Clock used to expire observations after seven days.</param>
    /// <returns>Ordered pending directories, or null when no unexpired observation exists.</returns>
    /// <remarks>Only observation hints are returned. Callers must revalidate every path and must not infer ownership or sum overlapping scans.</remarks>
    internal static IReadOnlyList<string>? Read(CoverageArtifactStore? store, string scopeId, TimeProvider clock)
    {
        if (store is null) return null;
        ArgumentNullException.ThrowIfNull(clock);
        ValidateScope(scopeId);
        using var inspection = OpenInspection(store, create: false);
        if (inspection is null) return null;
        if (!inspection.EnumerateArtifactEntries(2).Any(entry => entry.Name == StateName)) return null;
        using var operation = CoverageArtifactOperationLease.Acquire(inspection.ArtifactDirectoryPath, exclusive: false);
        operation.VerifyBinding();
        FileStream stream;
        try { stream = inspection.OpenArtifactFile(StateName, createNew: false); }
        catch (FileNotFoundException) { return null; }
        using (stream)
        {
            var state = CoverageArtifactJson.Read<CursorState>(stream, MaxStateBytes, JsonOptions);
            ValidateState(state);
            var now = clock.GetUtcNow();
            var entry = state.Scopes.SingleOrDefault(item => item.ScopeId == scopeId);
            if (entry is null || now - entry.UpdatedAtUtc > MaxAge) return null;
            return entry.Paths.ToArray();
        }
    }

    /// <summary>Writes a bounded cursor for one scope into the existing store's private staging tree.</summary>
    /// <param name="store">Existing private domain, or null to skip persistence.</param>
    /// <param name="scopeId">A 64-character hexadecimal repository observation fingerprint.</param>
    /// <param name="paths">Up to 128 portable relative directories, in next-scan order.</param>
    /// <param name="clock">Clock used for observation timestamps and expiry.</param>
    /// <remarks>State is private and atomically replaced. At most 32 scopes and 64 KiB are retained; older scopes and trailing paths are dropped before publication.</remarks>
    internal static void Write(CoverageArtifactStore? store, string scopeId, IReadOnlyList<string> paths, TimeProvider clock)
    {
        if (store is null) return;
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ValidateScope(scopeId);
        if (paths.Count > MaxPaths) throw new InvalidDataException("Inspection cursor contains too many paths.");
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var normalizedPaths = paths.Where(seenPaths.Add).ToArray();
        foreach (var path in normalizedPaths) ValidatePath(path);

        using var inspection = OpenInspection(store, create: true)!;
        using var operation = CoverageArtifactOperationLease.Acquire(inspection.ArtifactDirectoryPath, exclusive: true);
        operation.VerifyBinding();
        var now = clock.GetUtcNow();
        var scopes = ReadState(inspection).Scopes
            .Where(item => now - item.UpdatedAtUtc <= MaxAge && item.UpdatedAtUtc <= now)
            .Where(item => item.ScopeId != scopeId)
            .Append(new CursorScope(scopeId, now, normalizedPaths))
            .OrderBy(item => item.UpdatedAtUtc)
            .ToList();
        while (scopes.Count > MaxScopes) scopes.RemoveAt(0);

        var state = new CursorState(1, scopes);
        while (EncodedSize(state) > MaxStateBytes && scopes.Count > 1)
        {
            var oldest = scopes.FindIndex(item => item.ScopeId != scopeId);
            if (oldest < 0) break;
            scopes.RemoveAt(oldest);
            state = new CursorState(1, scopes);
        }
        if (EncodedSize(state) > MaxStateBytes)
        {
            while (EncodedSize(state) > MaxStateBytes && normalizedPaths.Length > 0)
            {
                normalizedPaths = normalizedPaths[..^1];
                scopes[^1] = new CursorScope(scopeId, now, normalizedPaths);
                state = new CursorState(1, scopes);
            }
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (bytes.Length > MaxStateBytes) throw new InvalidDataException("Inspection cursor state exceeds its encoded-byte limit.");
        Publish(inspection, bytes);
    }

    private static CoverageRunOutputLease? OpenInspection(CoverageArtifactStore store, bool create)
    {
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(store.RootPath, false, true)
            ?? throw new DirectoryNotFoundException("The artifact store must already exist.");
        using var staging = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(store.RootPath, StagingName), false, true)
            ?? throw new InvalidDataException("The artifact store staging directory is missing.");
        var path = Path.Join(store.RootPath, StagingName, InspectionName);
        var existing = CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true);
        if (existing is not null) return existing;
        if (!create) return null;
        return staging.CreateArtifactDirectory(InspectionName);
    }

    private static CursorState ReadState(CoverageRunOutputLease inspection)
    {
        try
        {
            using var stream = inspection.OpenArtifactFile(StateName, false);
            var state = CoverageArtifactJson.Read<CursorState>(stream, MaxStateBytes, JsonOptions);
            ValidateState(state);
            return state;
        }
        catch (FileNotFoundException) { return new CursorState(1, []); }
    }

    private static void Publish(CoverageRunOutputLease directory, byte[] bytes)
        => CoverageArtifactMetadataPublication.Write(directory, StateName, bytes, MaxStateBytes);

    private static int EncodedSize(CursorState state) => JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions).Length;

    private static void ValidateState(CursorState state)
    {
        if (state.SchemaVersion != 1 || state.Scopes is null || state.Scopes.Count > MaxScopes)
            throw new InvalidDataException("Inspection cursor schema or scope count is invalid.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in state.Scopes)
        {
            if (scope is null || !ids.Add(scope.ScopeId) || scope.Paths is null || scope.Paths.Length > MaxPaths)
                throw new InvalidDataException("Inspection cursor scope is invalid.");
            ValidateScope(scope.ScopeId);
            foreach (var path in scope.Paths) ValidatePath(path);
        }
        if (EncodedSize(state) > MaxStateBytes) throw new InvalidDataException("Inspection cursor state exceeds its encoded-byte limit.");
    }

    private static void ValidateScope(string scopeId)
    {
        if (scopeId is null || !ScopePattern().IsMatch(scopeId)) throw new InvalidDataException("Inspection scope ID must be a 64-character hexadecimal hash.");
    }

    private static void ValidatePath(string path)
    {
        if (path is null || path.Length is 0 or > MaxPathLength) throw new InvalidDataException("Inspection cursor path length is invalid.");
        if (path == ".") return;
        if (path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
            || path.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("Inspection cursor path must be a portable relative path without traversal components.");
    }

    [GeneratedRegex("\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ScopePattern();

    private sealed record CursorState(int SchemaVersion, List<CursorScope> Scopes);
    private sealed record CursorScope(string ScopeId, DateTimeOffset UpdatedAtUtc, string[] Paths);
}
