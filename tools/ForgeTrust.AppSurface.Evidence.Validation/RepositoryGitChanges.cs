using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>A pinned repository snapshot and its canonical changed-path inventory.</summary>
internal sealed record RepositoryChangeSet(string HeadCommit, string BaseCommit, IReadOnlyList<NormalizedDiffPath> Paths, string SourceDigest);

/// <summary>Collects a deterministic, bounded Git change inventory for local development and workflow events.</summary>
internal sealed class RepositoryGitChanges
{
    private const int MaximumOutputBytes = 16 * 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Binds committed, staged, unstaged and nonignored untracked changes to a local source digest.
    /// Git observes executable bits on Unix and uses its configured file-mode behavior on Windows.
    /// </summary>
    internal async Task<RepositoryChangeSet> CollectLocalAsync(string repository, string baseRevision, CancellationToken ct)
    {
        ValidateBaseRevision(baseRevision);
        var root = Path.GetFullPath(repository);
        // These flags can make diff/status omit tracked bytes that a local build still reads.
        var indexEntries = ParseFields(await GitBytes(root, ct, "ls-files", "-v", "-z"));
        if (indexEntries.Any(entry => entry.Length > 0 && (char.IsAsciiLetterLower(entry[0]) || entry[0] == 'S')))
            throw new InvalidOperationException("Local evidence cannot bind tracked source with assume-unchanged or skip-worktree flags. Clear those flags explicitly before collecting evidence.");
        var head = await GitText(root, ct, "rev-parse", "--verify", "HEAD^{commit}");
        var baseSha = await GitText(root, ct, "rev-parse", "--verify", baseRevision + "^{commit}");
        var merge = await GitText(root, ct, "merge-base", baseSha, head);
        var paths = new List<NormalizedDiffPath>();
        paths.AddRange(ParseNameStatus(await GitBytes(root, ct, "diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--find-renames", "--find-copies", "--find-copies-harder", merge, head, "--")));
        paths.AddRange(ParseNameStatus(await GitBytes(root, ct, "diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--find-renames", "--find-copies", "--find-copies-harder", "--cached", head, "--")));
        paths.AddRange(ParseNameStatus(await GitBytes(root, ct, "diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--find-renames", "--find-copies", "--find-copies-harder", "--")));
        var untracked = await GitBytes(root, ct, "ls-files", "--others", "--exclude-standard", "-z");
        foreach (var path in ParsePathList(untracked)) paths.Add(new NormalizedDiffPath(path, "added"));
        var normalized = Canonicalize(paths);
        var digest = await ComputeLocalDigest(root, head, merge, normalized, ct);
        return new(head, merge, normalized, digest);
    }

    internal async Task<RepositoryChangeSet> CollectEventAsync(string repository, string eventFile, string eventName, string? workflowSha, CancellationToken ct)
    {
        var root = Path.GetFullPath(repository);
        using var doc = ValidationProof.ReadJsonFile(eventFile, MaximumOutputBytes, ct);
        var data = doc.RootElement;
        string head, @base;
        IReadOnlyList<NormalizedDiffPath> paths;
        if (eventName == "pull_request")
        {
            var pr = data.GetProperty("pull_request");
            var targetBase = Commit(pr.GetProperty("base").GetProperty("sha").GetString());
            head = Commit(pr.GetProperty("head").GetProperty("sha").GetString());
            await GitText(root, ct, "cat-file", "-e", targetBase + "^{commit}");
            @base = await GitText(root, ct, "merge-base", targetBase, head);
            paths = await Diff(root, @base, head, ct);
        }
        else if (eventName == "push")
        {
            @base = Commit(data.GetProperty("before").GetString());
            head = Commit(data.TryGetProperty("after", out var after) ? after.GetString() : workflowSha);
            if (IsZero(@base))
            {
                head = ResolveFallbackHead(head, workflowSha);
                paths = await Inventory(root, head, ct);
                @base = await EmptyTree(root, ct);
            }
            else paths = await Diff(root, @base, head, ct);
        }
        else if (eventName == "workflow_dispatch")
        {
            head = Commit(workflowSha ?? data.GetProperty("after").GetString());
            @base = head;
            paths = await Inventory(root, head, ct);
        }
        else throw new InvalidOperationException("Unsupported GitHub event for repository change collection.");

        var actualHead = await GitText(root, ct, "rev-parse", "--verify", "HEAD^{commit}");
        if (!string.Equals(actualHead, head, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Checked-out HEAD does not match the workflow event head.");
        var canonical = Canonicalize(paths);
        if (canonical.Count == 0) throw new InvalidOperationException("Workflow event produced an empty changed-path inventory.");
        return new(head, @base, canonical, DigestInventory(head, @base, canonical));
    }

    internal static IReadOnlyList<NormalizedDiffPath> ParseNameStatus(ReadOnlySpan<byte> data)
    {
        var fields = ParseFields(data);
        var result = new List<NormalizedDiffPath>();
        for (var i = 0; i < fields.Count;)
        {
            var status = fields[i++];
            if (status.Length == 0 || status[0] is not ('A' or 'C' or 'D' or 'M' or 'R' or 'T' or 'U')) throw new FormatException("Malformed Git name-status record.");
            if (status[0] == 'U') throw new FormatException("Git inventory contains an unresolved merge conflict.");
            if (status.Length > 1 && (!int.TryParse(status.AsSpan(1), out var score) || score is < 0 or > 100 || status[0] is not ('R' or 'C'))) throw new FormatException("Malformed Git rename/copy score.");
            if (status.Length == 1 && status[0] is 'R' or 'C') throw new FormatException("Git rename/copy status requires a score.");
            var rename = status[0] is 'R' or 'C';
            if (i + (rename ? 2 : 1) > fields.Count) throw new FormatException("Truncated Git name-status record.");
            if (rename)
            {
                var oldPath = ValidatePath(fields[i++]); var newPath = ValidatePath(fields[i++]);
                result.Add(new(newPath, status[0] == 'C' ? "copied" : "renamed", oldPath));
            }
            else
            {
                var path = ValidatePath(fields[i++]);
                result.Add(new(path, status[0] switch { 'A' => "added", 'D' => "deleted", 'T' => "type-changed", 'U' => "unmerged", _ => "modified" }));
            }
        }
        return Canonicalize(result);
    }

    /// <summary>Writes a bounded patch with fixed a/ and b/ prefixes for local changed-line gating.</summary>
    internal async Task WriteLocalPatchAsync(string repository, string baseCommit, IReadOnlyList<NormalizedDiffPath> paths,
        string output, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repository);
        var patch = await GitBytes(root, cancellationToken, "diff", "--no-ext-diff", "--no-textconv", "--unified=0", baseCommit, "--");
        using var bytes = new MemoryStream();
        bytes.Write(patch);
        foreach (var relative in ParsePathList(await GitBytes(root, cancellationToken, "ls-files", "--others", "--exclude-standard", "-z")))
        {
            if (!paths.Any(path => path.Path == relative)) throw new InvalidOperationException("Local source changed before patch capture.");
            var full = Path.Join(root, relative);
            if (new FileInfo(full).LinkTarget is not null) continue;
            var info = new FileInfo(full);
            if (info.Length > MaximumOutputBytes) throw new InvalidOperationException("Untracked patch input exceeds its byte limit.");
            var content = await File.ReadAllBytesAsync(full, cancellationToken);
            var header = $"diff --git a/{relative} b/{relative}\nnew file mode 100644\n--- /dev/null\n+++ b/{relative}\n";
            string addition;
            try
            {
                var text = StrictUtf8.GetString(content);
                if (text.Contains('\0')) throw new DecoderFallbackException();
                var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                var count = text.Length == 0 ? 0 : lines.Length - (text.EndsWith('\n') ? 1 : 0);
                addition = count == 0 ? header : header + $"@@ -0,0 +1,{count} @@\n" + string.Concat(lines.Take(count).Select(line => "+" + line + "\n"));
            }
            catch (DecoderFallbackException) { addition = header + "Binary files /dev/null and b/" + relative + " differ\n"; }
            bytes.Write(Encoding.UTF8.GetBytes(addition));
            if (bytes.Length > MaximumOutputBytes) throw new InvalidOperationException("Local patch exceeds its byte limit.");
        }
        await File.WriteAllBytesAsync(output, bytes.ToArray(), cancellationToken);
    }

    /// <summary>Reads a bounded scalar identity from Git without a shell.</summary>
    internal static Task<string> ReadIdentityAsync(string repository, CancellationToken cancellationToken, params string[] arguments) =>
        GitText(Path.GetFullPath(repository), cancellationToken, arguments);

    private static async Task<IReadOnlyList<NormalizedDiffPath>> Diff(string root, string @base, string head, CancellationToken ct)
    {
        var actual = await GitText(root, ct, "rev-parse", "--verify", head + "^{commit}");
        if (!actual.Equals(head, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Event head cannot be resolved to the requested commit.");
        await GitText(root, ct, "cat-file", "-e", @base + "^{commit}");
        return ParseNameStatus(await GitBytes(root, ct, "diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--find-renames", "--find-copies", "--find-copies-harder", @base, head, "--"));
    }

    private static async Task<IReadOnlyList<NormalizedDiffPath>> Inventory(string root, string head, CancellationToken ct)
    {
        await GitText(root, ct, "cat-file", "-e", head + "^{commit}");
        return ParsePathList(await GitBytes(root, ct, "ls-tree", "-r", "--name-only", "-z", head)).Select(path => new NormalizedDiffPath(path, "added")).ToArray();
    }

    private static async Task<string> ComputeLocalDigest(string root, string head, string @base, IReadOnlyList<NormalizedDiffPath> paths, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, head);
        Add(hash, @base);
        // Include the complete staged and unstaged patch bytes so mode-only and index-only
        // state changes remain visible even when the checked-out file content is identical.
        Add(hash, EvidenceDigest.Sha256(await GitBytes(root, ct, "diff", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--cached", head, "--")));
        Add(hash, EvidenceDigest.Sha256(await GitBytes(root, ct, "diff", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--")));
        Add(hash, EvidenceDigest.Sha256(await GitBytes(root, ct, "ls-files", "--others", "--exclude-standard", "-z")));
        foreach (var p in paths)
        {
            Add(hash, p.Kind);
            Add(hash, p.Path);
            Add(hash, p.PreviousPath ?? string.Empty);
            foreach (var path in p.PreviousPath is null ? new[] { p.Path } : new[] { p.PreviousPath, p.Path })
            {
                var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { Add(hash, "outside-root"); continue; }
                var cursor = root;
                var symlink = false;
                foreach (var component in path.Split('/'))
                {
                    cursor = Path.Combine(cursor, component);
                    var linkTarget = new FileInfo(cursor).LinkTarget;
                    if (linkTarget is not null)
                    {
                        Add(hash, "symlink");
                        Add(hash, linkTarget);
                        symlink = true;
                        break;
                    }
                    if (!File.Exists(cursor) && !Directory.Exists(cursor))
                    {
                        Add(hash, "missing");
                        symlink = true;
                        break;
                    }
                }
                if (symlink) continue;
                Add(hash, Convert.ToHexString(await HashFile(full, ct)));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<byte[]> HashFile(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, ct);
    }

    private static string DigestInventory(string head, string @base, IReadOnlyList<NormalizedDiffPath> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, head);
        Add(hash, @base);
        foreach (var path in paths)
        {
            Add(hash, path.Kind);
            Add(hash, path.Path);
            Add(hash, path.PreviousPath ?? string.Empty);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Add(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static IReadOnlyList<NormalizedDiffPath> Canonicalize(IEnumerable<NormalizedDiffPath> paths) => paths
        .Distinct()
        .OrderBy(path => path.Path, StringComparer.Ordinal)
        .ThenBy(path => path.PreviousPath, StringComparer.Ordinal)
        .ThenBy(path => path.Kind, StringComparer.Ordinal)
        .ToArray();

    private static string Commit(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit)
        ? value.ToLowerInvariant()
        : throw new FormatException("Event commit SHA must contain exactly 40 or 64 hexadecimal characters.");
    private static bool IsZero(string sha) => sha.All(c => c == '0');
    private static string ResolveFallbackHead(string head, string? workflowSha) => IsZero(head) ? Commit(workflowSha) : head;
    private static void ValidateBaseRevision(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal) || value.Any(char.IsControl))
            throw new ArgumentException("Base revision is invalid.", nameof(value));
    }
    private static Task<string> EmptyTree(string root, CancellationToken ct) => GitText(root, ct, "hash-object", "-t", "tree", "--stdin");
    private static string ValidatePath(string path)
    {
        if (path.Length == 0 || path[0] == '/' || path.Contains('\\') || path.Trim() != path || path.Any(char.IsControl) || path.Split('/').Any(s => s is "" or "." or "..")) throw new FormatException("Git path is not a safe normalized repository-relative path.");
        return path;
    }
    private static List<string> ParseFields(ReadOnlySpan<byte> data)
    {
        var fields = new List<string>();
        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != 0) continue;
            try { fields.Add(StrictUtf8.GetString(data[start..i])); }
            catch (DecoderFallbackException exception) { throw new FormatException("Git path is not valid UTF-8.", exception); }
            start = i + 1;
        }
        if (start != data.Length) throw new FormatException("Git NUL-delimited output is truncated.");
        return fields;
    }
    private static List<string> ParsePathList(ReadOnlySpan<byte> data) => ParseFields(data).Select(ValidatePath).ToList();
    private static async Task<string> GitText(string root, CancellationToken ct, params string[] args) => StrictUtf8.GetString(await GitBytes(root, ct, args)).Trim();
    private static async Task<byte[]> GitBytes(string root, CancellationToken ct, params string[] args)
    {
        ct.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        // Machine-readable evidence must not inherit presentation or fsmonitor settings from dotfiles.
        foreach (var setting in new[]
        {
            "core.fsmonitor=false", "color.diff=false",
            "diff.mnemonicPrefix=false", "diff.noprefix=false", "diff.srcPrefix=a/", "diff.dstPrefix=b/",
        })
        {
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(setting);
        }
        if (!OperatingSystem.IsWindows() && args is ["diff", ..])
        {
            // Honor the actual executable bit on Unix; Windows does not expose Unix modes.
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("core.filemode=true");
        }
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (!process.Start()) throw new InvalidOperationException("Could not start Git.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = ReadBounded(process.StandardOutput.BaseStream, MaximumOutputBytes, timeout);
        var error = ReadBounded(process.StandardError.BaseStream, MaximumErrorBytes, timeout);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var bytes = await output;
            var stderr = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Git command failed: " + StrictUtf8.GetString(stderr));
            return bytes;
        }
        catch (Exception failure)
        {
            var deadlineExpired = timeout.IsCancellationRequested;
            timeout.Cancel();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The owned process exited between HasExited and Kill; no process remains to terminate.
                }
            }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            // Observe both readers even if one exceeded its budget. The original capture failure
            // takes precedence over the cancellation used to terminate the owned Git process.
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // WhenAll observes both readers. Below, surface their original faults or the capture
                // deadline rather than replacing that failure with the cancellation used for cleanup.
            }
            ct.ThrowIfCancellationRequested();
            if (output.IsFaulted) await output;
            if (error.IsFaulted) await error;
            if (failure is OperationCanceledException && deadlineExpired)
                throw new TimeoutException("Git collection exceeded its 30-second deadline.", failure);
            throw;
        }
    }
    private static async Task<byte[]> ReadBounded(Stream stream, int maximumBytes, CancellationTokenSource lifetime)
    {
        using var bytes = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var length = Math.Min(buffer.Length, maximumBytes + 1 - (int)bytes.Length);
                var read = await stream.ReadAsync(buffer.AsMemory(0, length), lifetime.Token);
                if (read == 0) return bytes.ToArray();
                if (bytes.Length + read > maximumBytes) throw new InvalidOperationException("Git output exceeded its configured size limit.");
                bytes.Write(buffer, 0, read);
            }
        }
        catch
        {
            lifetime.Cancel();
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
