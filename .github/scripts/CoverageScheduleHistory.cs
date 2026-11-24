using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text.Json;

/// <summary>Restores the newest valid coverage scheduling artifact from successful default-branch builds.</summary>
internal static class CoverageScheduleHistory
{
    // Preserve Python's 4,300-decimal-digit conversion bound for consumed GitHub integer metadata.
    private const int MaxConsumedIntegerDigits = 4300;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(45);
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFzzz",
        "yyyy-MM-dd'T'HH:mmzzz"
    ];

    /// <summary>Runs the restore command through injectable environment, API, clock, and output boundaries.</summary>
    internal static async Task<int> RunAsync(
        string[] args,
        Func<string, string?> environment,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, int, TimeSpan, Task<byte[]>> request,
        TimeProvider clock,
        Action<Stream, byte[]>? writeBytes = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(clock);

        var parsed = ParseArguments(args);
        if (parsed.ShowHelp)
        {
            stdout.WriteLine("Usage: coverage-schedule-history restore --output <absolute-path>");
            return 0;
        }

        if (parsed.Error is not null)
        {
            WriteError(stderr, parsed.Error);
            return 2;
        }

        string outputPath;
        try
        {
            if (parsed.OutputPath is null ||
                parsed.OutputPath.IndexOf('\0') >= 0 ||
                !Path.IsPathFullyQualified(parsed.OutputPath))
            {
                WriteError(stderr, "--output must be an absolute path without NUL characters");
                return 2;
            }

            outputPath = Path.TrimEndingDirectorySeparator(parsed.OutputPath);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            WriteError(stderr, "--output is not a valid path");
            return 2;
        }

        try
        {
            var inspectedPath = ResolveOutputPath(outputPath, createParents: false);
            if (inspectedPath is not null && PathEntryExists(inspectedPath))
            {
                WriteError(stderr, $"--output already exists: {outputPath}");
                return 2;
            }
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            WriteError(stderr, "--output could not be inspected");
            return 2;
        }

        var repository = environment("GITHUB_REPOSITORY");
        if (string.IsNullOrEmpty(repository))
        {
            WriteError(stderr, "GITHUB_REPOSITORY is required");
            return 2;
        }

        var defaultBranch = environment("GITHUB_DEFAULT_BRANCH");
        if (string.IsNullOrEmpty(defaultBranch))
        {
            WriteError(stderr, "GITHUB_DEFAULT_BRANCH is required");
            return 2;
        }

        if (!IsValidRepository(repository))
        {
            WriteError(stderr, "GITHUB_REPOSITORY must be an owner/repository pair");
            return 2;
        }

        if (!IsValidDefaultBranch(defaultBranch))
        {
            WriteError(stderr, "GITHUB_DEFAULT_BRANCH is invalid");
            return 2;
        }

        var (content, warning) = await RestoreAsync(repository, defaultBranch, request, clock);

        if (content is null)
        {
            WriteWarning(stderr, warning);
            return 0;
        }

        try
        {
            CreateOutput(outputPath, content, writeBytes);
            return 0;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            WriteError(stderr, "could not create --output");
            return 2;
        }
    }

    private static ParsedArguments ParseArguments(string[] args)
    {
        if (args is ["--help"] or ["-h"] or ["restore", "--help"] or ["restore", "-h"])
        {
            return new ParsedArguments(null, null, ShowHelp: true);
        }

        if (args.Length == 0)
        {
            return new ParsedArguments(null, "a command is required", ShowHelp: false);
        }

        if (!string.Equals(args[0], "restore", StringComparison.Ordinal))
        {
            return new ParsedArguments(null, $"unknown command: {args[0]}", ShowHelp: false);
        }

        string? outputPath = null;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--output", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Length)
                {
                    return new ParsedArguments(null, "argument --output requires a value", ShowHelp: false);
                }

                outputPath = args[++index];
                continue;
            }

            if (argument.StartsWith("--output=", StringComparison.Ordinal))
            {
                outputPath = argument["--output=".Length..];
                continue;
            }

            return new ParsedArguments(null, $"unrecognized argument: {argument}", ShowHelp: false);
        }

        return outputPath is null
            ? new ParsedArguments(null, "argument --output is required", ShowHelp: false)
            : new ParsedArguments(outputPath, null, ShowHelp: false);
    }

    private static async Task<(byte[]? Content, string Warning)> RestoreAsync(
        string repository,
        string defaultBranch,
        Func<string, int, TimeSpan, Task<byte[]>> request,
        TimeProvider clock)
    {
        var startTimestamp = clock.GetTimestamp();
        List<WorkflowRun> runs;
        try
        {
            runs = await ReadWorkflowRunsAsync(repository, defaultBranch, RequestWithinBudgetAsync);
        }
        catch (IOException)
        {
            return (null, "GitHub workflow history is unavailable");
        }

        if (runs.Count == 0)
        {
            return (null, "no successful completed build runs found on the default branch");
        }

        foreach (var run in runs)
        {
            var runId = run.Id.ToString(CultureInfo.InvariantCulture);
            var endpoint = $"repos/{repository}/actions/runs/{runId}/artifacts?per_page=100";
            List<BigInteger> artifactIds;
            try
            {
                artifactIds = await ReadArtifactIdsAsync(endpoint, RequestWithinBudgetAsync);
            }
            catch (IOException)
            {
                return (null, "GitHub artifact history is unavailable");
            }

            foreach (var artifactId in artifactIds)
            {
                var artifactIdText = artifactId.ToString(CultureInfo.InvariantCulture);
                var downloadEndpoint = $"repos/{repository}/actions/artifacts/{artifactIdText}/zip";
                byte[] archiveBytes;
                try
                {
                    archiveBytes = await RequestWithinBudgetAsync(downloadEndpoint, CoverageHistoryData.MaxArchiveBytes);
                }
                catch (IOException)
                {
                    return (null, "GitHub artifact download is unavailable");
                }

                try
                {
                    return (CoverageHistoryData.ReadArchive(archiveBytes), string.Empty);
                }
                catch (InvalidDataException)
                {
                    // A damaged candidate does not prevent trying an older artifact.
                }
            }
        }

        return (null, "no valid coverage schedule timings artifact found");

        async Task<byte[]> RequestWithinBudgetAsync(string endpoint, int maxBytes)
        {
            var remaining = RestoreTimeout - clock.GetElapsedTime(startTimestamp);
            if (remaining <= TimeSpan.Zero)
            {
                throw new IOException("GitHub API time budget exhausted.");
            }

            var timeout = remaining < RequestTimeout ? remaining : RequestTimeout;
            var response = await request(endpoint, maxBytes, timeout);
            if (response.Length > maxBytes)
            {
                throw new IOException("GitHub API response exceeded its size limit.");
            }

            return response;
        }
    }

    private static async Task<List<WorkflowRun>> ReadWorkflowRunsAsync(
        string repository,
        string defaultBranch,
        Func<string, int, Task<byte[]>> boundedRequest)
    {
        var endpoint = $"repos/{repository}/actions/workflows/build.yml/runs?" +
            $"branch={FormEncode(defaultBranch)}&event=push&status=success&per_page=10";
        using var document = await ReadApiDocumentAsync(endpoint, boundedRequest);
        if (!TryGetArray(document.RootElement, "workflow_runs", out var workflowRuns))
        {
            throw new IOException("GitHub workflow runs response had an invalid shape.");
        }

        var runs = new List<WorkflowRun>();
        foreach (var item in workflowRuns.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetPositiveInteger(item, "id", out var id) ||
                !TryGetString(item, "head_branch", out var branch) ||
                !string.Equals(branch, defaultBranch, StringComparison.Ordinal) ||
                !TryGetString(item, "event", out var eventName) ||
                !string.Equals(eventName, "push", StringComparison.Ordinal) ||
                !TryGetProperty(item, "head_repository", out var headRepository) ||
                headRepository.ValueKind != JsonValueKind.Object ||
                !TryGetString(headRepository, "full_name", out var fullName) ||
                !string.Equals(fullName, repository, StringComparison.OrdinalIgnoreCase) ||
                !TryGetString(item, "status", out var status) ||
                !string.Equals(status, "completed", StringComparison.Ordinal) ||
                !TryGetString(item, "conclusion", out var conclusion) ||
                !string.Equals(conclusion, "success", StringComparison.Ordinal) ||
                !TryGetString(item, "created_at", out var createdAt) ||
                !TryParseTimestamp(createdAt, out var timestamp))
            {
                continue;
            }

            runs.Add(new WorkflowRun(timestamp, id));
        }

        return runs
            .OrderByDescending(run => run.CreatedAt)
            .ThenByDescending(run => run.Id)
            .ToList();
    }

    private static async Task<List<BigInteger>> ReadArtifactIdsAsync(
        string endpoint,
        Func<string, int, Task<byte[]>> boundedRequest)
    {
        using var document = await ReadApiDocumentAsync(endpoint, boundedRequest);
        if (!TryGetArray(document.RootElement, "artifacts", out var artifacts))
        {
            throw new IOException("GitHub artifacts response had an invalid shape.");
        }

        var ids = new List<BigInteger>();
        foreach (var artifact in artifacts.EnumerateArray())
        {
            if (IsEligibleArtifact(artifact, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static async Task<JsonDocument> ReadApiDocumentAsync(
        string endpoint,
        Func<string, int, Task<byte[]>> boundedRequest)
    {
        var response = await boundedRequest(endpoint, CoverageHistoryData.MaxJsonBytes);
        try
        {
            return CoverageHistoryData.ReadJson(response);
        }
        catch (InvalidDataException exception)
        {
            throw new IOException("GitHub API response was unavailable or invalid.", exception);
        }
    }

    private static bool IsEligibleArtifact(JsonElement artifact, out BigInteger artifactId)
    {
        artifactId = BigInteger.Zero;
        return artifact.ValueKind == JsonValueKind.Object &&
            TryGetString(artifact, "name", out var name) &&
            string.Equals(name, "coverage-schedule-timings", StringComparison.Ordinal) &&
            TryGetProperty(artifact, "expired", out var expired) &&
            expired.ValueKind == JsonValueKind.False &&
            TryGetInteger(artifact, "size_in_bytes", out var size) &&
            size >= BigInteger.Zero &&
            size <= CoverageHistoryData.MaxArchiveBytes &&
            TryGetPositiveInteger(artifact, "id", out artifactId);
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return TryGetProperty(element, name, out value) && value.ValueKind == JsonValueKind.Array;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static bool TryGetPositiveInteger(JsonElement element, string name, out BigInteger value) =>
        TryGetInteger(element, name, out value) && value > BigInteger.Zero;

    private static bool TryGetInteger(JsonElement element, string name, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (!TryGetProperty(element, name, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var rawValue = property.GetRawText();
        if (rawValue.AsSpan().ContainsAny('.', 'e', 'E'))
        {
            return false;
        }

        var digitCount = rawValue.Length - (rawValue.StartsWith("-", StringComparison.Ordinal) ? 1 : 0);
        if (digitCount > MaxConsumedIntegerDigits)
        {
            throw new IOException("GitHub API response was unavailable or invalid.");
        }

        return BigInteger.TryParse(
            rawValue,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var normalized = value.EndsWith('Z') ? value[..^1] + "+00:00" : value;
        return DateTimeOffset.TryParseExact(
            normalized,
            TimestampFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out timestamp);
    }

    private static bool IsValidRepository(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 &&
            IsValidRepositoryPart(parts[0]) &&
            IsValidRepositoryPart(parts[1]);
    }

    private static bool IsValidRepositoryPart(string value)
    {
        return value.Length > 0 &&
            value is not "." and not ".." &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static bool IsValidDefaultBranch(string value)
    {
        var characterCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            characterCount++;
            if (characterCount > 255 || rune.Value is <= 0x1f or 0x7f)
            {
                return false;
            }
        }

        return characterCount > 0;
    }

    private static string FormEncode(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    private static bool PathEntryExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return true;
        }

        return new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;
    }

    // BCL file operations collapse ".." before following symlinks. Walk such paths in
    // filesystem order, preserving the distinction between inspection and creation.
    private static string? ResolveOutputPath(string path, bool createParents)
    {
        var root = Path.GetPathRoot(path)!; // Fully qualified at argument validation.
        var components = path[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Where(component => component != ".")
            .ToArray();
        if (!components.Contains("..", StringComparer.Ordinal))
        {
            if (components.Length == 0) return root;
            var output = Path.Combine(root, Path.Combine(components));
            if (createParents) Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            return output;
        }

        var currentDirectory = root;
        foreach (var component in components[..^1])
        {
            if (component == "..")
            {
                currentDirectory = Directory.GetParent(currentDirectory)?.FullName ?? currentDirectory;
                continue;
            }

            var candidate = Path.Combine(currentDirectory, component);
            if (createParents) Directory.CreateDirectory(candidate);
            else if (!Directory.Exists(candidate)) return null;
            var directory = new DirectoryInfo(candidate);
            currentDirectory = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }

        var lastComponent = components[^1];
        return lastComponent == ".."
            ? Directory.GetParent(currentDirectory)?.FullName ?? currentDirectory
            : Path.Combine(currentDirectory, lastComponent);
    }

    private static void CreateOutput(string path, byte[] content, Action<Stream, byte[]>? writeBytes)
    {
        var createdByInvocation = false;
        var outputPath = path;
        try
        {
            outputPath = ResolveOutputPath(path, createParents: true)!;
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            createdByInvocation = true;
            if (writeBytes is null)
            {
                output.Write(content, 0, content.Length);
            }
            else
            {
                writeBytes(output, content);
            }

            output.Flush();
        }
        catch
        {
            if (createdByInvocation)
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch (Exception cleanupException) when (IsFileSystemFailure(cleanupException))
                {
                    // Keep the original write failure as the reported outcome.
                }
            }

            throw;
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is ArgumentException or IOException or NotSupportedException or
            UnauthorizedAccessException or SecurityException;

    private static void WriteWarning(TextWriter stderr, string message) =>
        stderr.WriteLine($"warning: {message}");

    private static void WriteError(TextWriter stderr, string message) =>
        stderr.WriteLine($"error: {message}");

    private sealed record ParsedArguments(string? OutputPath, string? Error, bool ShowHelp);

    private sealed record WorkflowRun(DateTimeOffset CreatedAt, BigInteger Id);
}
