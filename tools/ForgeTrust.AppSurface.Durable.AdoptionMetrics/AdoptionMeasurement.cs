using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Durable.AdoptionMetrics;

internal static class AdoptionMeasurementEngine
{
    private const int SupportedSchemaVersion = 1;
    private const int ExpectedRegionCount = 6;
    private static readonly string[] ExpectedNames =
    [
        "external-activation-mapping",
        "primary-lifecycle-test",
        "registration",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static async Task<AdoptionMeasurementResult> MeasureAsync(
        string specPath,
        string consumerRoot,
        string repositoryRoot,
        IConsumerRevisionVerifier revisionVerifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(revisionVerifier);

        var normalizedSpecPath = Path.GetFullPath(specPath);
        var normalizedConsumerRoot = Path.GetFullPath(consumerRoot);
        var normalizedRepositoryRoot = Path.GetFullPath(repositoryRoot);

        AdoptionMeasurementSpec spec;
        try
        {
            await using var stream = File.OpenRead(normalizedSpecPath);
            spec = await JsonSerializer.DeserializeAsync<AdoptionMeasurementSpec>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                ?? throw new AdoptionMeasurementException("The measurement specification is empty.");
        }
        catch (AdoptionMeasurementException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new AdoptionMeasurementException(
                $"Could not read measurement specification '{normalizedSpecPath}'.",
                exception);
        }

        ValidateSpec(spec);
        await revisionVerifier.VerifyAsync(
            normalizedConsumerRoot,
            spec.BaselineCommit,
            spec.Regions
                .Where(static region => region.SourceRoot == AdoptionSourceRoot.Consumer)
                .Select(static region => region.RelativePath)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            cancellationToken);

        var measured = new List<AdoptionMeasurementRegionResult>(ExpectedRegionCount);
        foreach (var region in spec.Regions
                     .OrderBy(static region => region.Name, StringComparer.Ordinal)
                     .ThenBy(static region => region.Variant))
        {
            var root = region.SourceRoot switch
            {
                AdoptionSourceRoot.Consumer => normalizedConsumerRoot,
                AdoptionSourceRoot.Repository => normalizedRepositoryRoot,
                _ => throw new AdoptionMeasurementException(
                    $"Region '{region.Name}' has unsupported source root '{region.SourceRoot}'."),
            };

            var path = ResolveSafePath(root, region.RelativePath, region.Name);
            var lineCount = await CountRegionLinesAsync(path, region, cancellationToken);
            var passed = region.Variant == AdoptionVariant.Baseline || lineCount <= region.Limit;

            if (lineCount != region.LineCount)
            {
                throw new AdoptionMeasurementException(
                    $"Region '{region.Name}' ({ToJsonValue(region.Variant)}) expected {region.LineCount} nonblank lines but measured {lineCount}. Refresh the pinned evidence deliberately.");
            }

            if (passed != region.Passed)
            {
                throw new AdoptionMeasurementException(
                    $"Region '{region.Name}' ({ToJsonValue(region.Variant)}) expected passed={region.Passed.ToString().ToLowerInvariant()} but measured passed={passed.ToString().ToLowerInvariant()}.");
            }

            measured.Add(new(
                region.Variant,
                region.Name,
                region.SourceRoot,
                NormalizeRelativePath(region.RelativePath),
                region.StartToken,
                region.EndToken,
                lineCount,
                region.Limit,
                passed));
        }

        var overallPassed = measured.All(static region => region.Passed);
        if (overallPassed != spec.OverallPassed)
        {
            throw new AdoptionMeasurementException(
                $"The specification expected overallPassed={spec.OverallPassed.ToString().ToLowerInvariant()} but measured overallPassed={overallPassed.ToString().ToLowerInvariant()}.");
        }

        return new(
            spec.SchemaVersion,
            spec.ConsumerRepository,
            spec.BaselineCommit,
            measured,
            overallPassed);
    }

    private static void ValidateSpec(AdoptionMeasurementSpec spec)
    {
        if (spec.SchemaVersion != SupportedSchemaVersion)
        {
            throw new AdoptionMeasurementException(
                $"Unsupported schemaVersion {spec.SchemaVersion}; expected {SupportedSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(spec.ConsumerRepository))
        {
            throw new AdoptionMeasurementException("consumerRepository is required.");
        }

        if (spec.BaselineCommit is not { Length: 40 }
            || spec.BaselineCommit.Any(static value => !char.IsAsciiHexDigit(value)))
        {
            throw new AdoptionMeasurementException("baselineCommit must be a full 40-character Git commit.");
        }

        if (spec.Regions is null || spec.Regions.Count != ExpectedRegionCount)
        {
            throw new AdoptionMeasurementException(
                $"The specification must contain exactly {ExpectedRegionCount} regions: baseline and proposed variants for each named touchpoint.");
        }

        foreach (var region in spec.Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Name)
                || string.IsNullOrWhiteSpace(region.RelativePath)
                || string.IsNullOrWhiteSpace(region.StartToken)
                || string.IsNullOrWhiteSpace(region.EndToken))
            {
                throw new AdoptionMeasurementException(
                    "Every region requires name, relativePath, startToken, and endToken.");
            }

            if (region.StartToken.Contains('\n', StringComparison.Ordinal)
                || region.StartToken.Contains('\r', StringComparison.Ordinal)
                || region.EndToken.Contains('\n', StringComparison.Ordinal)
                || region.EndToken.Contains('\r', StringComparison.Ordinal))
            {
                throw new AdoptionMeasurementException(
                    $"Region '{region.Name}' tokens must each identify one physical line.");
            }

            if (region.LineCount < 0)
            {
                throw new AdoptionMeasurementException(
                    $"Region '{region.Name}' lineCount cannot be negative.");
            }

            if (region.Limit <= 0)
            {
                throw new AdoptionMeasurementException(
                    $"Region '{region.Name}' limit must be positive.");
            }
        }

        foreach (var name in ExpectedNames)
        {
            var matching = spec.Regions.Where(region => string.Equals(region.Name, name, StringComparison.Ordinal)).ToArray();
            if (matching.Length != 2
                || matching.Count(static region => region.Variant == AdoptionVariant.Baseline) != 1
                || matching.Count(static region => region.Variant == AdoptionVariant.Proposed) != 1)
            {
                throw new AdoptionMeasurementException(
                    $"Region name '{name}' requires exactly one baseline and one proposed entry.");
            }
        }

        var unexpectedName = spec.Regions.FirstOrDefault(
            region => !ExpectedNames.Contains(region.Name, StringComparer.Ordinal));
        if (unexpectedName is not null)
        {
            throw new AdoptionMeasurementException(
                $"Unexpected region name '{unexpectedName.Name}'.");
        }
    }

    private static string ResolveSafePath(string root, string relativePath, string regionName)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new AdoptionMeasurementException(
                $"Region '{regionName}' relativePath must not be rooted.");
        }

        var path = Path.GetFullPath(relativePath, root);
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new AdoptionMeasurementException(
                $"Region '{regionName}' relativePath escapes its declared source root.");
        }

        if (!File.Exists(path))
        {
            throw new AdoptionMeasurementException(
                $"Region '{regionName}' source file '{NormalizeRelativePath(relativePath)}' does not exist.");
        }

        RejectSymbolicLinks(root, path, regionName);
        return path;
    }

    private static void RejectSymbolicLinks(string root, string path, string regionName)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        try
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new AdoptionMeasurementException(
                    $"Region '{regionName}' source path must not contain symbolic links or reparse points.");
            }

            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AdoptionMeasurementException(
                        $"Region '{regionName}' source path must not contain symbolic links or reparse points.");
                }
            }
        }
        catch (AdoptionMeasurementException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AdoptionMeasurementException(
                $"Could not validate region '{regionName}' source path '{path}'.",
                exception);
        }
    }

    private static async Task<int> CountRegionLinesAsync(
        string path,
        AdoptionMeasurementRegion region,
        CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AdoptionMeasurementException(
                $"Could not read region '{region.Name}' source file '{path}'.",
                exception);
        }

        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var startMatches = FindTokenMatches(lines, region.StartToken);
        var endMatches = FindTokenMatches(lines, region.EndToken);

        if (startMatches.Count != 1)
        {
            throw new AdoptionMeasurementException(
                $"Region '{region.Name}' startToken matched {startMatches.Count} lines; expected exactly one.");
        }

        if (endMatches.Count != 1)
        {
            throw new AdoptionMeasurementException(
                $"Region '{region.Name}' endToken matched {endMatches.Count} lines; expected exactly one.");
        }

        var start = startMatches[0];
        var end = endMatches[0];
        if (start >= end)
        {
            throw new AdoptionMeasurementException(
                $"Region '{region.Name}' startToken must occur before endToken.");
        }

        return lines[(start + 1)..end].Count(static line => !string.IsNullOrWhiteSpace(line));
    }

    private static List<int> FindTokenMatches(string[] lines, string token)
    {
        var matches = new List<int>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.Equals(lines[index].Trim(), token, StringComparison.Ordinal))
            {
                matches.Add(index);
            }
        }

        return matches;
    }

    internal static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/');
    }

    internal static string ToJsonValue(AdoptionVariant variant)
    {
        return variant switch
        {
            AdoptionVariant.Baseline => "baseline",
            AdoptionVariant.Proposed => "proposed",
            _ => throw new AdoptionMeasurementException($"Unsupported variant '{variant}'."),
        };
    }
}

internal static class AdoptionMeasurementWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static async Task WriteAsync(
        string outputPath,
        AdoptionMeasurementResult result,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(result);

        var normalizedOutputPath = Path.GetFullPath(outputPath);
        try
        {
            var outputDirectory = Path.GetDirectoryName(normalizedOutputPath);
            if (outputDirectory is not null)
            {
                Directory.CreateDirectory(outputDirectory);
                RejectSymbolicLink(outputDirectory, "output directory");
            }

            RejectSymbolicLink(normalizedOutputPath, "output file");
            var json = JsonSerializer.Serialize(result, SerializerOptions).ReplaceLineEndings("\n") + "\n";
            var temporaryPath = Path.Combine(
                outputDirectory ?? Directory.GetCurrentDirectory(),
                $".{Path.GetFileName(normalizedOutputPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken);
                File.Move(temporaryPath, normalizedOutputPath, overwrite: true);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
        catch (AdoptionMeasurementException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AdoptionMeasurementException(
                $"Could not write adoption measurement output '{normalizedOutputPath}'.",
                exception);
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the write or move failure. A later run uses a unique temporary name.
        }
    }

    private static void RejectSymbolicLink(string path, string description)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new AdoptionMeasurementException(
                    $"The {description} must not be a symbolic link or reparse point.");
            }
        }
        catch (AdoptionMeasurementException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AdoptionMeasurementException(
                $"Could not validate the {description} '{path}'.",
                exception);
        }
    }
}

internal interface IConsumerRevisionVerifier
{
    Task VerifyAsync(
        string consumerRoot,
        string expectedCommit,
        IReadOnlyList<string> selectedRelativePaths,
        CancellationToken cancellationToken);
}

internal sealed class GitConsumerRevisionVerifier : IConsumerRevisionVerifier
{
    private readonly string _gitExecutable;
    private readonly TimeSpan _commandTimeout;

    internal static GitConsumerRevisionVerifier Instance { get; } = new(
        "git",
        TimeSpan.FromSeconds(30));

    /// <summary>Initializes a verifier with an explicit executable and per-command deadline.</summary>
    /// <remarks>The configurable seam keeps timeout and cancellation behavior deterministic in tests.</remarks>
    internal GitConsumerRevisionVerifier(string gitExecutable, TimeSpan commandTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutable);
        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                commandTimeout,
                "The Git command timeout must be positive.");
        }

        _gitExecutable = gitExecutable;
        _commandTimeout = commandTimeout;
    }

    public async Task VerifyAsync(
        string consumerRoot,
        string expectedCommit,
        IReadOnlyList<string> selectedRelativePaths,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(consumerRoot))
        {
            throw new AdoptionMeasurementException(
                $"Consumer root '{consumerRoot}' does not exist.");
        }

        var actualCommit = (await RunGitAsync(
                consumerRoot,
                ["rev-parse", "HEAD"],
                cancellationToken))
            .Trim();
        if (!string.Equals(actualCommit, expectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new AdoptionMeasurementException(
                $"Consumer checkout is at '{actualCommit}', expected baseline commit '{expectedCommit}'.");
        }

        foreach (var relativePath in selectedRelativePaths)
        {
            var normalizedRelativePath = AdoptionMeasurementEngine.NormalizeRelativePath(relativePath);
            var existsAtCommit = await RunGitExitCodeAsync(
                consumerRoot,
                ["cat-file", "-e", $"{expectedCommit}:{normalizedRelativePath}"],
                cancellationToken);
            if (existsAtCommit != 0)
            {
                throw new AdoptionMeasurementException(
                    $"Consumer source '{normalizedRelativePath}' is not present at pinned commit '{expectedCommit}'.");
            }

            var exitCode = await RunGitExitCodeAsync(
                consumerRoot,
                ["diff", "--quiet", expectedCommit, "--", normalizedRelativePath],
                cancellationToken);
            if (exitCode != 0)
            {
                throw new AdoptionMeasurementException(
                    $"Consumer source '{normalizedRelativePath}' differs from pinned commit '{expectedCommit}'.");
            }
        }
    }

    private async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = CreateGitProcess(workingDirectory, arguments, redirectOutput: true);
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new AdoptionMeasurementException("Could not start Git to verify the consumer checkout.", exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_commandTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw new AdoptionMeasurementException(
                $"Git did not complete consumer checkout verification within {_commandTimeout.TotalSeconds:0.###} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        if (process.ExitCode != 0)
        {
            throw new AdoptionMeasurementException(
                $"Git could not verify the consumer checkout: {error.Result.Trim()}");
        }

        return output.Result;
    }

    private async Task<int> RunGitExitCodeAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        // These fixed probes are quiet and only consume the exit code. Keep diagnostics out of the
        // caller's console without allowing inherited pipe handles to extend the command deadline.
        using var process = CreateGitProbeProcess(workingDirectory, arguments);
        try
        {
            process.Start();
            using var ownership = ProcessOwnership.Attach(process);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_commandTimeout);
            using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var standardOutputDrain = process.StandardOutput.BaseStream.CopyToAsync(
                Stream.Null,
                drainCancellation.Token);
            var standardErrorDrain = process.StandardError.BaseStream.CopyToAsync(
                Stream.Null,
                drainCancellation.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                throw new AdoptionMeasurementException(
                    $"Git did not complete consumer source verification within {_commandTimeout.TotalSeconds:0.###} seconds.");
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                drainCancellation.Cancel();
                process.StandardOutput.BaseStream.Dispose();
                process.StandardError.BaseStream.Dispose();
                ObserveDrainCompletion(standardOutputDrain, standardErrorDrain);
            }

            return process.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            TryKillProcessTree(process);
            throw new AdoptionMeasurementException("Could not start Git to verify consumer source files.", exception);
        }
    }

    private static void ObserveDrainCompletion(Task standardOutputDrain, Task standardErrorDrain)
    {
        var completion = Task.WhenAll(standardOutputDrain, standardErrorDrain);
        _ = completion.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process raced to completion after cancellation.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort: preserve cancellation/timeout as the caller-visible outcome.
        }
    }

    private Process CreateGitProcess(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = redirectOutput,
            RedirectStandardOutput = redirectOutput,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }

    private Process CreateGitProbeProcess(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateGitProcess(workingDirectory, arguments, redirectOutput: true);
        }

        // A POSIX child can outlive Git while retaining its stdout/stderr descriptors. Give the
        // bootstrap process a short window for the parent to assign its process group, then exec
        // Git so its exit code remains unchanged. ProcessOwnership kills the group after Git exits.
        // /bin/sh is commonly dash on Linux, where set -m is disabled without a TTY. Bash is
        // required here because its non-interactive job control gives the Git child a private
        // process group. macOS and mainstream Linux images provide /bin/bash; minimal images
        // without Bash cannot provide this descendant-ownership guarantee with POSIX sh alone.
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "set -m; \"$0\" \"$@\" & child=$!; wait \"$child\"; status=$?; kill -KILL \"-$child\" 2>/dev/null; exit \"$status\"");
        startInfo.ArgumentList.Add(_gitExecutable);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }

    private static class ProcessOwnership
    {
        internal static IDisposable Attach(Process process)
        {
            return OperatingSystem.IsWindows()
                ? WindowsJob.Attach(process)
                : Noop.Instance;
        }

        private sealed class Noop : IDisposable
        {
            internal static Noop Instance { get; } = new();

            public void Dispose()
            {
            }
        }

        private sealed class WindowsJob : IDisposable
        {
            private const uint JobObjectLimitKillOnJobClose = 0x00002000;
            private const int JobObjectExtendedLimitInformationClass = 9;
            private IntPtr _handle;

            private WindowsJob(IntPtr handle)
            {
                _handle = handle;
            }

            internal static WindowsJob Attach(Process process)
            {
                // Windows 8+ permits this job to nest under the host/test job when that job allows
                // nesting. If the host forbids nesting (or is an older Windows version), fail
                // closed: retaining an unowned descendant is worse than rejecting the probe.
                var handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    var limits = new JobObjectExtendedLimitInformation
                    {
                        BasicLimitInformation = new JobObjectBasicLimitInformation
                        {
                            LimitFlags = JobObjectLimitKillOnJobClose,
                        },
                    };
                    if (!SetInformationJobObject(
                            handle,
                            JobObjectExtendedLimitInformationClass,
                            ref limits,
                            (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    }

                    if (!AssignProcessToJobObject(handle, process.Handle))
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    }

                    return new WindowsJob(handle);
                }
                catch
                {
                    TerminateJobObject(handle, 1);
                    CloseHandle(handle);
                    throw;
                }
            }

            public void Dispose()
            {
                var handle = _handle;
                _handle = IntPtr.Zero;
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                TerminateJobObject(handle, 1);
                CloseHandle(handle);
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(
                IntPtr job,
                int informationClass,
                ref JobObjectExtendedLimitInformation information,
                uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectBasicLimitInformation
            {
                internal long PerProcessUserTimeLimit;
                internal long PerJobUserTimeLimit;
                internal uint LimitFlags;
                internal nuint MinimumWorkingSetSize;
                internal nuint MaximumWorkingSetSize;
                internal uint ActiveProcessLimit;
                internal nuint Affinity;
                internal uint PriorityClass;
                internal uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                internal ulong ReadOperationCount;
                internal ulong WriteOperationCount;
                internal ulong OtherOperationCount;
                internal ulong ReadTransferCount;
                internal ulong WriteTransferCount;
                internal ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectExtendedLimitInformation
            {
                internal JobObjectBasicLimitInformation BasicLimitInformation;
                internal IoCounters IoInfo;
                internal nuint ProcessMemoryLimit;
                internal nuint JobMemoryLimit;
                internal nuint PeakProcessMemoryUsed;
                internal nuint PeakJobMemoryUsed;
            }
        }
    }
}

internal sealed record AdoptionMeasurementSpec(
    int SchemaVersion,
    string ConsumerRepository,
    string BaselineCommit,
    IReadOnlyList<AdoptionMeasurementRegion> Regions,
    bool OverallPassed);

internal sealed record AdoptionMeasurementRegion(
    AdoptionVariant Variant,
    string Name,
    AdoptionSourceRoot SourceRoot,
    string RelativePath,
    string StartToken,
    string EndToken,
    int LineCount,
    int Limit,
    bool Passed);

internal sealed record AdoptionMeasurementResult(
    int SchemaVersion,
    string ConsumerRepository,
    string BaselineCommit,
    IReadOnlyList<AdoptionMeasurementRegionResult> Regions,
    bool OverallPassed);

internal sealed record AdoptionMeasurementRegionResult(
    AdoptionVariant Variant,
    string Name,
    AdoptionSourceRoot SourceRoot,
    string RelativePath,
    string StartToken,
    string EndToken,
    int LineCount,
    int Limit,
    bool Passed);

internal enum AdoptionVariant
{
    Baseline,
    Proposed,
}

internal enum AdoptionSourceRoot
{
    Consumer,
    Repository,
}
