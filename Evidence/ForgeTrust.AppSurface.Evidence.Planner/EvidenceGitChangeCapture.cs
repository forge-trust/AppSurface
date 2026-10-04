using System.Diagnostics;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Planner;

/// <summary>
/// Captures the two byte-exact Git representations used by a revision-bound evidence gate.
/// </summary>
/// <remarks>
/// A source diff binds the plan to exact Git output; a separate NUL-delimited status stream
/// selects paths even when a binary, mode, symlink, or submodule change has no text hunk.
/// Neither representation authenticates the repository or a remote PR by itself.
/// </remarks>
public sealed class EvidenceGitChangeSnapshot
{
    private readonly byte[] _sourceDiff;
    private readonly byte[] _nameStatus;

    internal EvidenceGitChangeSnapshot(
        string baseRevision,
        string headRevision,
        byte[] sourceDiff,
        byte[] nameStatus,
        IReadOnlyList<NormalizedDiffPath> changedPaths)
    {
        BaseRevision = baseRevision;
        HeadRevision = headRevision;
        _sourceDiff = sourceDiff;
        _nameStatus = nameStatus;
        SourceDiffDigest = EvidenceDigest.Sha256(sourceDiff);
        NameStatusDigest = EvidenceDigest.Sha256(nameStatus);
        ChangedPaths = Array.AsReadOnly(changedPaths.ToArray());
    }

    /// <summary>Gets the complete canonical base commit object ID.</summary>
    public string BaseRevision { get; }

    /// <summary>Gets the complete canonical head commit object ID.</summary>
    public string HeadRevision { get; }

    /// <summary>Gets the bounded source diff bytes. Callers must not treat them as executable instructions.</summary>
    public ReadOnlyMemory<byte> SourceDiff => _sourceDiff;

    /// <summary>Gets the bounded NUL-delimited name-status bytes.</summary>
    public ReadOnlyMemory<byte> NameStatus => _nameStatus;

    /// <summary>Gets the SHA-256 of the exact source diff bytes.</summary>
    public string SourceDiffDigest { get; }

    /// <summary>Gets the SHA-256 of the exact name-status bytes.</summary>
    public string NameStatusDigest { get; }

    /// <summary>Gets the normalized changed paths used for policy selection.</summary>
    public IReadOnlyList<NormalizedDiffPath> ChangedPaths { get; }
}

/// <summary>
/// Runs Git with fixed diff options against two complete commit IDs in a trusted object store.
/// </summary>
/// <remarks>
/// Supply a trusted bare or object-only checkout containing both commits. The caller is responsible
/// for fetching the exact objects, pinning the Git version in CI, and independently checking current
/// PR or tag identity before a gate verdict. This type never reads the ambient checkout's HEAD
/// and disables Git replacement refs so those refs cannot reinterpret a supplied object ID.
/// </remarks>
public static class EvidenceGitChangeCapture
{
    /// <summary>Maximum captured source diff size for the first AppSurface gate.</summary>
    public const int MaximumSourceDiffBytes = 20 * 1024 * 1024;

    /// <summary>Maximum number of changed records accepted for policy selection.</summary>
    public const int MaximumChangedRecords = 10_000;

    /// <summary>Maximum UTF-8 byte length of one changed repository path.</summary>
    public const int MaximumPathBytes = 4 * 1024;

    /// <summary>Maximum wall-clock seconds for capture of both Git representations.</summary>
    public const int MaximumCaptureSeconds = 120;

    private const int MaximumNameStatusBytes = 85 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Captures and validates a byte-exact diff and NUL status stream for the same commit pair.
    /// </summary>
    /// <param name="repositoryPath">Trusted Git object store containing both commits.</param>
    /// <param name="baseRevision">Full 40- or 64-character lower-case commit ID.</param>
    /// <param name="headRevision">Full commit ID in the same object format.</param>
    /// <param name="cancellationToken">Cancellation for Git execution and bounded reads.</param>
    /// <returns>A bounded snapshot suitable for trusted policy planning.</returns>
    public static async Task<EvidenceGitChangeSnapshot> CaptureAsync(
        string repositoryPath,
        string baseRevision,
        string headRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ValidateRevision(baseRevision);
        ValidateRevision(headRevision);
        if (baseRevision.Length != headRevision.Length)
        {
            throw new EvidencePlanningException("ASEVD130", "Git revisions use different object ID formats.", "Supply two complete commit IDs from the same repository.");
        }

        var repository = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(repository))
        {
            throw new EvidencePlanningException("ASEVD131", "The trusted Git object store is unavailable.", "Fetch the exact base and head commits into a trusted object-only checkout.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(MaximumCaptureSeconds));
        try
        {
            return await CaptureWithinDeadlineAsync(repository, baseRevision, headRevision, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EvidencePlanningException("ASEVD137", "Exact Git change capture exceeded its deadline.", "Retry in the pinned runner or reduce the change before a reviewed deadline increase.");
        }
    }

    private static async Task<EvidenceGitChangeSnapshot> CaptureWithinDeadlineAsync(
        string repository,
        string baseRevision,
        string headRevision,
        CancellationToken cancellationToken)
    {
        await RequireCommitAsync(repository, baseRevision, cancellationToken).ConfigureAwait(false);
        await RequireCommitAsync(repository, headRevision, cancellationToken).ConfigureAwait(false);

        var sourceDiff = await RunGitAsync(
            repository,
            ["-c", "diff.external=", "-c", "diff.noprefix=false", "-c", "diff.mnemonicprefix=false", "-c", "diff.algorithm=myers", "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--binary", "--full-index", "--unified=3", "--find-renames=50%", "--find-copies=50%", "--submodule=short", "--ignore-submodules=none", "--no-relative", "--src-prefix=a/", "--dst-prefix=b/", baseRevision, headRevision, "--"],
            MaximumSourceDiffBytes,
            cancellationToken).ConfigureAwait(false);
        var nameStatus = await RunGitAsync(
            repository,
            ["-c", "diff.external=", "-c", "diff.noprefix=false", "-c", "diff.mnemonicprefix=false", "-c", "diff.algorithm=myers", "diff", "--name-status", "-z", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames=50%", "--find-copies=50%", "--submodule=short", "--ignore-submodules=none", "--no-relative", baseRevision, headRevision, "--"],
            MaximumNameStatusBytes,
            cancellationToken).ConfigureAwait(false);
        if (sourceDiff.Length == 0 && nameStatus.Length != 0)
        {
            throw new EvidencePlanningException("ASEVD133", "Git source diff is empty while changed paths are reported.", "Recapture both representations from the same exact commit pair.");
        }

        var paths = ParseNameStatus(nameStatus);
        if (paths.Count == 0)
        {
            throw new EvidencePlanningException("ASEVD132", "The exact Git revisions have no changed paths.", "Use a commit pair with an explicit policy-selected change; never infer a no-evidence claim from an empty diff.");
        }

        return new EvidenceGitChangeSnapshot(baseRevision, headRevision, sourceDiff, nameStatus, paths);
    }

    /// <summary>
    /// Regenerates both Git representations and rejects a changed or substituted snapshot.
    /// </summary>
    /// <param name="repositoryPath">The same trusted object store used for capture.</param>
    /// <param name="snapshot">Snapshot whose exact bytes and normalized paths must still bind.</param>
    /// <param name="cancellationToken">Cancellation for Git execution.</param>
    /// <returns>A task that completes only when both representations match.</returns>
    public static async Task VerifyAsync(
        string repositoryPath,
        EvidenceGitChangeSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var regenerated = await CaptureAsync(repositoryPath, snapshot.BaseRevision, snapshot.HeadRevision, cancellationToken).ConfigureAwait(false);
        if (!regenerated.SourceDiff.Span.SequenceEqual(snapshot.SourceDiff.Span)
            || !regenerated.NameStatus.Span.SequenceEqual(snapshot.NameStatus.Span)
            || !regenerated.ChangedPaths.SequenceEqual(snapshot.ChangedPaths))
        {
            throw new EvidencePlanningException("ASEVD133", "Git change bytes or selected paths differ from the captured snapshot.", "Discard the stale plan and recapture both representations from the exact commit objects.");
        }
    }

    /// <summary>
    /// Strictly parses <c>git diff --name-status -z</c> bytes, including both rename or copy paths.
    /// </summary>
    /// <param name="bytes">Complete NUL-delimited Git stdout.</param>
    /// <returns>Changed paths in Git's order.</returns>
    public static IReadOnlyList<NormalizedDiffPath> ParseNameStatus(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumNameStatusBytes)
        {
            throw MalformedStatus();
        }

        var changes = new List<NormalizedDiffPath>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (changes.Count >= MaximumChangedRecords)
            {
                throw new EvidencePlanningException("ASEVD134", "Git reported too many changed records.", "Reduce the change or revise the reviewed gate limit and its tests.");
            }

            var status = ReadField(bytes, ref offset);
            var isPair = status.Length > 0 && (status[0] == 'R' || status[0] == 'C');
            if (!IsSupportedStatus(status))
            {
                throw MalformedStatus();
            }

            var firstPath = ReadPath(bytes, ref offset);
            var path = isPair ? ReadPath(bytes, ref offset) : firstPath;
            if (!seen.Add(path))
            {
                throw MalformedStatus();
            }

            changes.Add(new NormalizedDiffPath(path, StatusKind(status[0]), isPair ? firstPath : null));
        }

        return changes;
    }

    private static string ReadPath(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var field = ReadFieldBytes(bytes, ref offset);
        if (field.Length is 0 or > MaximumPathBytes)
        {
            throw MalformedStatus();
        }

        string path;
        try
        {
            path = StrictUtf8.GetString(field);
        }
        catch (DecoderFallbackException)
        {
            throw MalformedStatus();
        }

        if (path.StartsWith('/')
            || path.EndsWith('/')
            || path.Contains('\\')
            || path.Any(char.IsControl)
            || path.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw MalformedStatus();
        }

        return path;
    }

    private static string ReadField(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var field = ReadFieldBytes(bytes, ref offset);
        if (field.Length is 0 or > 4)
        {
            throw MalformedStatus();
        }

        try
        {
            return StrictUtf8.GetString(field);
        }
        catch (DecoderFallbackException)
        {
            throw MalformedStatus();
        }
    }

    private static ReadOnlySpan<byte> ReadFieldBytes(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var terminator = bytes[offset..].IndexOf((byte)0);
        if (terminator < 0)
        {
            throw MalformedStatus();
        }

        var field = bytes.Slice(offset, terminator);
        offset += terminator + 1;
        return field;
    }

    private static bool IsSupportedStatus(string status)
    {
        if (status is "A" or "D" or "M" or "T")
        {
            return true;
        }

        return status.Length is >= 2 and <= 4
            && status[0] is 'R' or 'C'
            && int.TryParse(status.AsSpan(1), out var score)
            && score is >= 0 and <= 100;
    }

    private static string StatusKind(char status) => status switch
    {
        'A' => "added",
        'D' => "deleted",
        'M' => "modified",
        'T' => "typechanged",
        'R' => "renamed",
        'C' => "copied",
        _ => throw MalformedStatus(),
    };

    private static EvidencePlanningException MalformedStatus() =>
        new("ASEVD135", "Git name-status output is malformed or contains an unsafe path.", "Recapture the exact commit pair with the pinned Git runner; do not fall back to text-hunk path selection.");

    private static void ValidateRevision(string revision)
    {
        if (string.IsNullOrWhiteSpace(revision)
            || revision.Length is not (40 or 64)
            || revision.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new EvidencePlanningException("ASEVD130", "A Git revision is not a complete lower-case commit ID.", "Supply the complete base and head object IDs, not refs or abbreviated SHAs.");
        }
    }

    private static async Task RequireCommitAsync(string repository, string revision, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repository, ["cat-file", "-t", revision], 32, cancellationToken).ConfigureAwait(false);
        if (!result.AsSpan().SequenceEqual("commit\n"u8))
        {
            throw new EvidencePlanningException("ASEVD131", "An exact Git revision is not a commit object.", "Fetch the complete base and head commits into the trusted object store.");
        }
    }

    private static async Task<byte[]> RunGitAsync(
        string repository,
        IReadOnlyList<string> arguments,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--no-pager");
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repository);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // A trusted object-store path must not be redirected by inherited Git environment
        // variables such as GIT_DIR or GIT_ALTERNATE_OBJECT_DIRECTORIES. Keep only the
        // fixed settings below, independent of the caller's ambient checkout.
        foreach (var key in start.Environment.Keys.Where(static key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_CONFIG_COUNT"] = "0";
        start.Environment["GIT_CONFIG_PARAMETERS"] = string.Empty;
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_EXTERNAL_DIFF"] = string.Empty;
        start.Environment["GIT_DIFF_OPTS"] = string.Empty;
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["LC_ALL"] = "C";

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new EvidencePlanningException("ASEVD136", "The trusted Git process did not start.", "Install the pinned Git version in the gate runner.");
            }

            var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumBytes, cancellationToken);
            var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, 4096, cancellationToken);
            try
            {
                var stdout = await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new EvidencePlanningException("ASEVD136", "Git could not read the exact commit pair.", "Fetch both complete commits into the trusted object store and recapture the change.");
                }

                return stdout;
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                throw;
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            throw new EvidencePlanningException("ASEVD136", "The trusted Git process is unavailable.", "Install the pinned Git version and ensure its object store is readable.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var captured = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var request = (int)Math.Min(buffer.Length, maximumBytes - captured.Length + 1);
            var count = await stream.ReadAsync(buffer.AsMemory(0, request), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return captured.ToArray();
            }

            if (captured.Length + count > maximumBytes)
            {
                throw new EvidencePlanningException("ASEVD134", "Git output exceeds the reviewed gate byte limit.", "Reduce the change or revise the reviewed gate limit and its tests.");
            }

            await captured.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }
}
