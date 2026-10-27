using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Collects explicitly writer-owned paths without adopting pre-existing output contents.</summary>
/// <remarks>
/// Writers register each attempted file publication and only invocation-unique subtrees. Hashing occurs after
/// producers drain, under the caller's output lease. An incomplete inventory cannot publish complete history.
/// </remarks>
internal sealed class CoverageArtifactInventory(string outputDirectory, TimeProvider clock)
{
    private readonly object _sync = new();
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _freshTrees = new(StringComparer.Ordinal);
    private bool _limited;

    /// <summary>Gets the absolute output root whose writers own this inventory.</summary>
    internal string OutputDirectory { get; } = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(outputDirectory));

    /// <summary>Registers a path that this invocation creates or overwrites; absent failed writes are not invented.</summary>
    /// <param name="path">A known writer destination within this output.</param>
    internal void RecordFile(string path)
    {
        string relative;
        try { relative = Relative(path); }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            lock (_sync) _limited = true;
            return;
        }
        lock (_sync)
        {
            if (_files.Count >= CoverageArtifactLimits.MaximumFiles && !_files.Contains(relative)) _limited = true;
            else _files.Add(relative);
        }
    }

    /// <summary>Registers a newly created invocation-unique subtree, never a reused project or output directory.</summary>
    /// <param name="path">A newly created raw collector, diagnostic or generator directory.</param>
    internal void RecordFreshTree(string path)
    {
        string relative;
        try { relative = Relative(path); }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            lock (_sync) _limited = true;
            return;
        }
        lock (_sync)
        {
            if (_freshTrees.Count >= CoverageArtifactLimits.MaximumFiles) _limited = true;
            else _freshTrees.Add(relative);
        }
    }

    /// <summary>Captures the pre-write state of a fixed-name artifact so a retained stale file is not adopted.</summary>
    /// <param name="path">A writer destination under this output.</param>
    /// <returns>The prior length and write timestamp, or null when the destination did not exist.</returns>
    internal FileObservation? ObserveBeforeWrite(string path)
    {
        try
        {
            _ = Relative(path);
            var file = new FileInfo(path);
            file.Refresh();
            return file.Exists ? new FileObservation(file.Length, file.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception exception) when (CoverageArtifactSession.IsOptionalFailure(exception))
        {
            lock (_sync) _limited = true;
            return null;
        }
    }

    /// <summary>Records a newly created producer file; ambiguous retained destinations make the inventory incomplete.</summary>
    /// <param name="path">Writer destination.</param>
    /// <param name="before">State immediately before the participating producer.</param>
    internal void RecordAfterWrite(string path, FileObservation? before)
    {
        try
        {
            _ = Relative(path);
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists) return;
            if (before is null) RecordFile(path);
            else
            {
                // The external producer has no publication callback for a retained fixed-name file.
                // A timestamp/length change cannot establish writer authority, so preserve uncertainty.
                lock (_sync) _limited = true;
            }
        }
        catch (Exception exception) when (CoverageArtifactSession.IsOptionalFailure(exception))
        {
            lock (_sync) _limited = true;
        }
    }

    /// <summary>Hashes the writer-owned regular files after producers stop, preserving exact original bytes.</summary>
    /// <param name="token">Caller cancellation; terminal failure inventory may use its independent bounded budget.</param>
    /// <returns>A bounded complete inventory, or an exception that prevents optional complete publication.</returns>
    internal IReadOnlyList<CoverageArtifactInventoryEntry> Capture(CancellationToken token)
    {
        string[] trees;
        HashSet<string> files;
        lock (_sync)
        {
            if (_limited) throw new InvalidDataException("Writer inventory exceeded its file limit.");
            trees = _freshTrees.ToArray();
            files = new(_files, StringComparer.Ordinal);
        }
        var deadline = new CoverageArtifactWorkBudget(clock, TimeSpan.FromSeconds(1));
        foreach (var tree in trees)
        {
            var pending = new Stack<string>();
            pending.Push(Path.Join(OutputDirectory, tree));
            while (pending.Count > 0)
            {
                deadline.Check(token);
                using var directory = CoverageRunOutputLease.AcquireArtifactDirectory(pending.Pop(), false, false);
                if (directory is null) continue;
                foreach (var entry in directory.EnumerateArtifactEntries(CoverageArtifactLimits.MaximumFiles))
                {
                    deadline.Check(token);
                    if (files.Count >= CoverageArtifactLimits.MaximumFiles) throw new InvalidDataException("Writer inventory exceeded its file limit.");
                    var path = Path.Join(directory.ArtifactDirectoryPath, entry.Name);
                    if (entry.IsDirectory) pending.Push(path);
                    else files.Add(Relative(path));
                }
            }
        }
        var result = new List<CoverageArtifactInventoryEntry>(files.Count);
        var buffer = new byte[CoverageArtifactLimits.OperationBufferMaxBytes];
        foreach (var relative in files.Order(StringComparer.Ordinal))
        {
            deadline.Check(token);
            var path = Path.Join(OutputDirectory, relative);
            using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetDirectoryName(path)!, false, false);
            if (parent is null) continue;
            FileStream stream;
            try { stream = parent.OpenArtifactFile(Path.GetFileName(path), false, requirePrivate: false); }
            catch (IOException) when (!File.Exists(path) && !Directory.Exists(path)) { continue; }
            using (stream)
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var length = stream.Length;
                long observed = 0;
                int read;
                while ((read = stream.Read(buffer)) > 0)
                {
                    deadline.Check(token);
                    observed = checked(observed + read);
                    hash.AppendData(buffer, 0, read);
                }
                if (observed != length || stream.Length != length) throw new IOException("Writer-owned artifact changed during inventory.");
                result.Add(new(relative.Replace('\\', '/'), length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
            }
        }
        if (JsonSerializer.SerializeToUtf8Bytes(result).LongLength > CoverageArtifactLimits.RunInventoryMaxBytes)
            throw new InvalidDataException("Writer inventory exceeds its encoded metadata limit.");
        return result;
    }

    /// <summary>Creates bounded informational provenance from an invocation's declared policy.</summary>
    /// <param name="driver">Selected collector integration.</param>
    /// <param name="configuration">Selected build configuration.</param>
    /// <param name="projects">Ordered project identities.</param>
    /// <param name="include">Coverage include policy.</param>
    /// <param name="exclude">Coverage exclude policy.</param>
    /// <returns>Fingerprints contain no test arguments, environment values or source contents.</returns>
    internal static CoverageArtifactProvenance Provenance(string driver, string configuration, IEnumerable<string> projects, string? include, string? exclude)
    {
        var policy = string.Join('\n', new[] { driver, configuration, include ?? string.Empty, exclude ?? string.Empty }.Concat(projects));
        return new(null, "unknown", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policy))).ToLowerInvariant());
    }

    /// <summary>Gets the exact producing assembly version without reading environment or process arguments.</summary>
    internal static string ProducingVersion => typeof(CoverageArtifactInventory).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CoverageArtifactInventory).Assembly.GetName().Version?.ToString() ?? "unknown";

    private string Relative(string path)
    {
        var normalizedOutput = CoverageRunOutputLease.NormalizePlatformPath(OutputDirectory);
        var normalizedPath = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(path));
        var relative = Path.GetRelativePath(normalizedOutput, normalizedPath);
        if (Path.IsPathRooted(relative) || relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Writer inventory path leaves its output root.");
        if (relative.Length > 4096) throw new InvalidDataException("Writer inventory path is too long.");
        var components = relative.Split(Path.DirectorySeparatorChar);
        if (components.Length > 32) throw new InvalidDataException("Writer inventory path is too deep.");
        foreach (var component in components) CoverageRunOutputLease.ValidateArtifactComponent(component);
        return relative;
    }
}

/// <summary>Length and timestamp used to distinguish a writer's output from a retained fixed-name file.</summary>
internal readonly record struct FileObservation(long Length, long LastWriteUtcTicks);
