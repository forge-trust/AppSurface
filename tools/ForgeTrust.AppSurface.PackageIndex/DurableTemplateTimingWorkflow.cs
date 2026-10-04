using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Executes five serial exact install/create/test workloads with positively owned NuGet roots.</summary>
/// <remarks>
/// Candidate preparation is outside primed clocks. Cold jobs supply five independently created private Docker daemons;
/// their fresh identity and absent pinned image are verified before each sample. This runner never purges shared caches
/// or images. A failed attempt stays in its series, and corrected runs require a new series identity.
/// </remarks>
internal sealed class DurableTemplateTimingWorkflow(IExternalCommandRunner runner, Func<bool>? isSupportedRunner = null)
{
    internal const int MaximumCacheTreeEntries = 16_384;
    internal const long MaximumCacheEntryBytes = 256L * 1024 * 1024;
    internal const long MaximumCacheTreeBytes = 1024L * 1024 * 1024;
    private const int CacheReadBufferBytes = 64 * 1024;

    private static readonly string[] Checkpoints =
    [
        "[first-work] authorized activation accepted", "[first-work] Work reached terminal completion",
        "[first-work] readiness: NotStarted -> Healthy", "[first-work] exported appsurface.durable.runtime.activation"
    ];

    /// <summary>Measures one complete series and writes only bounded safe observations after owned cleanup.</summary>
    /// <param name="input">Exact producer/archive/source inputs; the report is the timing receipt destination.</param>
    /// <param name="mode">Primed performance gate or cold diagnostic protocol.</param>
    /// <param name="coldDockerHosts">Five private workflow-owned daemon endpoints, one per cold sample; empty for primed.</param>
    /// <param name="cancellationToken">Stops workload commands; independently bounded cleanup is still attempted.</param>
    /// <returns>All five attempts and their validated series result.</returns>
    internal async Task<DurableTemplateTimingProofReceipt> RunAsync(DurableTemplateCommandOptions input,
        DurableTemplateTimingMode mode, IReadOnlyList<string> coldDockerHosts, CancellationToken cancellationToken)
    {
        if (!(isSupportedRunner?.Invoke() ?? OperatingSystem.IsLinux())) throw new PackageIndexException("Release timing requires its Linux runner.");
        if (!Enum.IsDefined(mode)) throw new PackageIndexException("Timing mode is unsupported.");
        if (mode == DurableTemplateTimingMode.Cold && coldDockerHosts.Count != 5
            || mode == DurableTemplateTimingMode.Primed && coldDockerHosts.Count != 0)
            throw new PackageIndexException("Cold timing requires five private workflow-owned Docker endpoints.");
        var manifest = await new PackageArtifactManifestReader().ReadAsync(input.ArtifactManifest, cancellationToken);
        if (manifest.PackageVersion != input.PackageVersion) throw new PackageIndexException("Timing version differs from its producer.");
        var identities = new List<DurableTemplateTimingArtifact>();
        foreach (var entry in manifest.Entries)
        {
            var archive = Path.Join(input.Artifacts, entry.ArtifactFileName);
            DurableTemplateStaging.RequireRegularPath(archive);
            if (Path.GetFileName(entry.ArtifactFileName) != entry.ArtifactFileName || PackageHash.ComputeSha512(archive) != entry.Sha512)
                throw new PackageIndexException("Timing archive differs from its producer.");
            identities.Add(new(entry.PackageId, input.PackageVersion, entry.Sha512));
        }
        var template = manifest.Entries.Single(entry => string.Equals(entry.PackageId, DurableTemplateStaging.PackageId, StringComparison.OrdinalIgnoreCase));
        DurableTemplateArtifactContract.ValidateArchive(Path.Join(input.Artifacts, template.ArtifactFileName), input.PackageVersion);
        var source = await Command("git", ["rev-parse", "HEAD"], input.RepositoryRoot, null, 5_000, cancellationToken);
        if (source.StandardOutput.Trim() != input.SourceCommit) throw new PackageIndexException("Timing source differs from the producer.");
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/var/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/tmp/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        var root = Path.Join(temporary, "appsurface-template-timing-" + Guid.NewGuid().ToString("N"));
        DurableTemplateStaging.RequireRegularPath(root);
        Directory.CreateDirectory(root);
        var samples = new List<DurableTemplateTimingSample>();
        var series = Guid.NewGuid().ToString("N");
        var cacheHash = (string?)null;
        var sdk = string.Empty;
        try
        {
            var preparation = Path.Join(root, "preparation");
            Directory.CreateDirectory(preparation);
            WriteConfiguration(preparation, input.Artifacts);
            var prepared = DurableTemplateConsumerProof.EnvironmentFor(root, "preparation");
            prepared["NUGET_HTTP_CACHE_PATH"] = Path.Join(root, "preparation-http");
            var observedSdk = (await Command("dotnet", ["--version"], preparation, prepared, 5_000, cancellationToken)).StandardOutput.Trim();
            if (!Regex.IsMatch(observedSdk, @"\A10\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?\z", RegexOptions.CultureInvariant))
                throw new PackageIndexException("Timing SDK identity is missing or unsupported.");
            sdk = observedSdk;
            if (mode == DurableTemplateTimingMode.Primed)
            {
                await Command("dotnet", ["new", "install", $"{DurableTemplateStaging.PackageId}@{input.PackageVersion}"], preparation, prepared, 180_000, cancellationToken);
                await Command("dotnet", ["new", DurableTemplateArtifactContract.ShortName, "-n", "FirstDurableWorker"], preparation, prepared, 180_000, cancellationToken);
                await Command("dotnet", ["restore", "FirstDurableWorker", "--configfile", "NuGet.config"], preparation, prepared, 300_000, cancellationToken);
                DurableTemplateConsumerProof.VerifyRestoredGraph(Path.Join(preparation, "FirstDurableWorker"), prepared["NUGET_PACKAGES"]!, input.Artifacts, input.PackageVersion);
                cacheHash = HashCache(prepared["NUGET_PACKAGES"]!);
                await Command("docker", ["pull", DurableTemplateConsumerProof.PostgreSqlImage], preparation, prepared, 300_000, cancellationToken);
                await Command("dotnet", ["new", "uninstall", DurableTemplateStaging.PackageId], preparation, prepared, 20_000, cancellationToken);
            }
            for (var ordinal = 1; ordinal <= 5; ordinal++)
            {
                var sampleRoot = Path.Join(root, "sample-" + ordinal);
                Directory.CreateDirectory(sampleRoot);
                WriteConfiguration(sampleRoot, input.Artifacts);
                var environment = DurableTemplateConsumerProof.EnvironmentFor(root, "sample-" + ordinal);
                environment["NUGET_HTTP_CACHE_PATH"] = Path.Join(root, "sample-" + ordinal + "-http");
                Directory.CreateDirectory(environment["NUGET_HTTP_CACHE_PATH"]!);
                if (mode == DurableTemplateTimingMode.Primed) CopyTree(prepared["NUGET_PACKAGES"]!, environment["NUGET_PACKAGES"]!);
                if (mode == DurableTemplateTimingMode.Cold)
                {
                    var endpoint = coldDockerHosts[ordinal - 1];
                    if (!Regex.IsMatch(endpoint, @"\Atcp://172\.(?:1[6-9]|2[0-9]|3[01])\.[0-9]{1,3}\.[0-9]{1,3}:2375\z", RegexOptions.CultureInvariant))
                        throw new PackageIndexException("Cold daemon must be a private workflow-owned Docker bridge endpoint.");
                    environment["DOCKER_HOST"] = endpoint;
                    environment["DOCKER_CONTEXT"] = null;
                    environment["DOCKER_TLS_VERIFY"] = null;
                    environment["DOCKER_CERT_PATH"] = null;
                    environment["TESTCONTAINERS_HOST_OVERRIDE"] = new Uri(endpoint).Host;
                    environment["TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE"] = "/var/run/docker.sock";
                }
                samples.Add(await MeasureSample(input, mode, ordinal, series, sdk, sampleRoot, environment,
                    identities, template.ArtifactFileName, cancellationToken));
            }
        }
        finally
        {
            RequireTree(root);
            Directory.Delete(root, recursive: true);
        }
        var request = new DurableTemplateTimingProofRequest(series, mode, DurableTemplateTimingFeedKind.CandidateLocal,
            "candidate-local-feed", input.SourceCommit, input.PackageVersion, RuntimeInformation.RuntimeIdentifier,
            Environment.GetEnvironmentVariable("ImageVersion") ?? "linux-owned-runner", sdk,
            DurableTemplateConsumerProof.PostgreSqlImage, cacheHash, Stopwatch.Frequency, identities);
        var receipt = DurableTemplateTimingProof.Evaluate(request, samples);
        WriteReceipt(input.Report, receipt);
        return receipt;
    }

    private async Task<DurableTemplateTimingSample> MeasureSample(DurableTemplateCommandOptions input,
        DurableTemplateTimingMode mode, int ordinal, string series, string sdk, string root,
        Dictionary<string, string?> environment, IReadOnlyList<DurableTemplateTimingArtifact> artifacts,
        string templateArtifactFileName, CancellationToken cancellationToken)
    {
        var daemonHash = string.Empty;
        var before = string.Empty;
        var after = string.Empty;
        var cache = environment["NUGET_PACKAGES"]!;
        var cacheHash = HashCache(cache);
        var cacheEmpty = !Directory.EnumerateFileSystemEntries(cache).Any();
        var commands = new List<DurableTemplateTimingCommand>();
        var flags = new bool[4];
        var database = string.Empty;
        var setupSeconds = 0d;
        var cleanup = false;
        var failure = string.Empty;
        var childCleanupMs = 0;
        var started = Stopwatch.GetTimestamp();
        var maximumMs = mode == DurableTemplateTimingMode.Primed ? 180_000 : 900_000;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(maximumMs);
        using var group = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        if (mode == DurableTemplateTimingMode.Primed) group.CancelAfter(TimeSpan.FromSeconds(55));
        var readyFile = Path.Join(root, "fixture-ready-ticks");
        environment["APPSURFACE_TEMPLATE_TIMING_READY_FILE"] = readyFile;
        try
        {
            var daemon = await Command("docker", ["info", "--format", "{{.ID}}"], root, environment, 5_000, cancellationToken);
            if (string.IsNullOrWhiteSpace(daemon.StandardOutput)) throw new PackageIndexException("Docker daemon identity is missing.");
            daemonHash = Hash(daemon.StandardOutput.Trim());
            before = await InspectImage(root, environment, cancellationToken);
            // The observation commands are outside the three-command workload clock.
            started = Stopwatch.GetTimestamp();
            deadline.CancelAfter(maximumMs);
            if (mode == DurableTemplateTimingMode.Primed) group.CancelAfter(TimeSpan.FromSeconds(55));
            await Timed(DurableTemplateTimingCommandPhase.TemplateInstall, ["new", "install", $"{DurableTemplateStaging.PackageId}@{input.PackageVersion}"]);
            await Timed(DurableTemplateTimingCommandPhase.ProjectCreate, ["new", DurableTemplateArtifactContract.ShortName, "-n", "FirstDurableWorker"]);
            var result = await Timed(DurableTemplateTimingCommandPhase.FirstDurableWorkTest,
                ["test", "FirstDurableWorker", "--filter", "FullyQualifiedName~FirstDurableWork", "--logger", "console;verbosity=detailed"]);
            for (var index = 0; index < flags.Length; index++) flags[index] = result.StandardOutput.Contains(Checkpoints[index], StringComparison.Ordinal);
            var fixtureTicks = ReadMarker(result.StandardOutput, "started-ticks");
            if (!long.TryParse(fixtureTicks, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks < started || ticks > Stopwatch.GetTimestamp())
                throw new PackageIndexException("Timing fixture did not expose its monotonic build completion boundary.");
            setupSeconds = (ticks - started) / (double)Stopwatch.Frequency;
            database = ReadMarker(result.StandardOutput, "database-sha256");
            if (!Regex.IsMatch(database, @"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant))
                throw new PackageIndexException("Timing fixture database identity is missing.");
            if (mode == DurableTemplateTimingMode.Primed && setupSeconds > DurableTemplateTimingProof.PrimedSetupRestoreBuildLimitSeconds)
                throw new PackageIndexException("Primed install/create/restore/build group exceeded its budget.");
            var generated = Path.Join(root, "FirstDurableWorker");
            DurableTemplateArtifactContract.ValidateGenerated(generated, "FirstDurableWorker", input.PackageVersion);
            var template = artifacts.Single(artifact => string.Equals(artifact.PackageId, DurableTemplateStaging.PackageId, StringComparison.OrdinalIgnoreCase));
            var archive = Path.Join(input.Artifacts, templateArtifactFileName);
            if (DurableTemplateConsumerProof.HashGeneratedContent(generated) != DurableTemplateArtifactContract.ComputeGeneratedContentSha256(archive, "FirstDurableWorker"))
                throw new PackageIndexException("Timed generated payload differs from its candidate.");
            DurableTemplateConsumerProof.VerifyRestoredGraph(generated, cache, input.Artifacts, input.PackageVersion);
            after = await InspectImage(root, environment, deadline.Token);
            var cleanupMatch = Regex.Matches(result.StandardOutput, @"\[first-work-cleanup\] elapsed-ms=([0-9]+)(?:\r?\n|$)", RegexOptions.CultureInvariant);
            if (cleanupMatch.Count != 1 || !int.TryParse(cleanupMatch[0].Groups[1].Value, out childCleanupMs) || childCleanupMs >= 20_000)
                throw new PackageIndexException("Timing fixture cleanup observation is missing or exhausted.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            failure = deadline.IsCancellationRequested ? "sample-deadline" : group.IsCancellationRequested ? "setup-group-deadline" : "sample-failed";
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, 20_000 - childCleanupMs)));
            try
            {
                await Command("dotnet", ["new", "uninstall", DurableTemplateStaging.PackageId], root, environment,
                    Math.Max(1, 20_000 - childCleanupMs), cleanupDeadline.Token);
                var absence = await Command("dotnet", ["new", "list"], root, environment,
                    Math.Max(1, 20_000 - childCleanupMs), cleanupDeadline.Token);
                if (absence.StandardOutput.Contains(DurableTemplateArtifactContract.ShortName, StringComparison.Ordinal))
                    throw new PackageIndexException("Timed template remained installed after cleanup.");
                foreach (var owned in new[] { root, environment["NUGET_PACKAGES"]!, environment["DOTNET_CLI_HOME"]!, environment["NUGET_HTTP_CACHE_PATH"]! })
                {
                    RequireTree(owned);
                    Directory.Delete(owned, recursive: true);
                }
                cleanup = !cleanupDeadline.IsCancellationRequested;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { failure = "sample-cleanup-failed"; }
        }
        var ended = Stopwatch.GetTimestamp();
        return new(ordinal, Guid.NewGuid().ToString("N"), series, mode, DurableTemplateTimingFeedKind.CandidateLocal,
            "candidate-local-feed", input.SourceCommit, input.PackageVersion, DurableTemplateTimingProof.ComputeArtifactSetSha256(artifacts),
            RuntimeInformation.RuntimeIdentifier, Environment.GetEnvironmentVariable("ImageVersion") ?? "linux-owned-runner", sdk,
            daemonHash, before.Length > 0, before, after, Hash(cache), cacheHash, cacheEmpty, true, false,
            Hash(Path.Join(root, "FirstDurableWorker")), database, started, ended, setupSeconds, commands,
            new(flags[0], flags[1], flags[2], flags[3]), cleanup, failure.Length == 0 && flags.All(flag => flag), failure);

        async Task<ExternalCommandResult> Timed(DurableTemplateTimingCommandPhase phase, string[] args)
        {
            var phaseStarted = Stopwatch.GetTimestamp();
            var remaining = maximumMs - (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (remaining <= 0) throw new TimeoutException();
            var execution = runner.RunAsync(new ExternalCommandRequest("dotnet", args, root, phase.ToString(),
                "running the three-command Durable timing workload", remaining, environment, ExternalCapturePolicy.ReleaseProof), group.Token);
            try
            {
                if (mode == DurableTemplateTimingMode.Primed && phase == DurableTemplateTimingCommandPhase.FirstDurableWorkTest)
                {
                    while (!execution.IsCompleted && !group.IsCancellationRequested)
                    {
                        if (File.Exists(readyFile))
                        {
                            DurableTemplateStaging.RequireRegularPath(readyFile);
                            if (new FileInfo(readyFile).Length > 32 || !long.TryParse(await File.ReadAllTextAsync(readyFile, deadline.Token),
                                    NumberStyles.None, CultureInfo.InvariantCulture, out var boundary)
                                || boundary < started || boundary > Stopwatch.GetTimestamp()
                                || (boundary - started) / (double)Stopwatch.Frequency > 55)
                            {
                                group.Cancel();
                                try { await execution; } catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { }
                                throw new PackageIndexException("Primed fixture boundary is invalid or late.");
                            }
                            group.CancelAfter(Timeout.InfiniteTimeSpan);
                            break;
                        }
                        await Task.WhenAny(execution, Task.Delay(25, CancellationToken.None));
                    }
                }
                var result = await execution;
                commands.Add(new(phase, DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", args),
                    Stopwatch.GetElapsedTime(phaseStarted).TotalSeconds, result.ExitCode, result.StandardOutputTruncated || result.StandardErrorTruncated));
                DurableTemplateConsumerProof.RequireResult(result);
                return result;
            }
            catch
            {
                group.Cancel();
                try { await execution; } catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { }
                throw;
            }
        }
    }

    private async Task<string> InspectImage(string root, IReadOnlyDictionary<string, string?> environment, CancellationToken token)
    {
        var result = await runner.RunAsync(new ExternalCommandRequest("docker",
            ["image", "inspect", "--format", "{{json .RepoDigests}}", DurableTemplateConsumerProof.PostgreSqlImage],
            root, "image-identity", "observing pinned image cache", 5_000, environment, ExternalCapturePolicy.ReleaseProof), token);
        if (result.StandardOutputTruncated || result.StandardErrorTruncated) throw new PackageIndexException("Pinned image observation was truncated.");
        if (result.ExitCode == 1 && (result.StandardError.Contains("No such image", StringComparison.OrdinalIgnoreCase)
                || result.StandardError.Contains("No such object", StringComparison.OrdinalIgnoreCase))) return string.Empty;
        DurableTemplateConsumerProof.RequireResult(result);
        var digest = DurableTemplateConsumerProof.PostgreSqlImage.Split('@')[1];
        if (!result.StandardOutput.Contains("@" + digest, StringComparison.Ordinal))
            throw new PackageIndexException("Pinned image digest observation differs.");
        return digest;
    }

    private async Task<ExternalCommandResult> Command(string executable, IReadOnlyList<string> args, string root,
        IReadOnlyDictionary<string, string?>? environment, int timeout, CancellationToken token)
    {
        var result = await runner.RunAsync(new ExternalCommandRequest(executable, args, root, "timing-preparation",
            "preparing or cleaning the owned timing proof", timeout, environment, ExternalCapturePolicy.ReleaseProof), token);
        DurableTemplateConsumerProof.RequireResult(result);
        return result;
    }

    /// <summary>Hashes every relative tree path and exact regular-file byte in a bounded NuGet cache closure.</summary>
    internal static string HashCache(string cache)
    {
        var entries = ReadCacheTree(cache);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytesRead = 0;
        foreach (var entry in entries)
        {
            var relativePath = Encoding.UTF8.GetBytes(entry.RelativePath);
            hash.AppendData([entry.IsDirectory ? (byte)0 : (byte)1]);
            AppendCacheInteger(hash, relativePath.Length);
            hash.AppendData(relativePath);
            if (entry.IsDirectory) continue;

            AppendCacheLong(hash, entry.Length);
            DurableTemplateStaging.RequireRegularPath(entry.FullPath);
            using var stream = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                CacheReadBufferBytes, FileOptions.SequentialScan);
            if (stream.Length != entry.Length) throw new PackageIndexException("NuGet cache changed while its closure was being hashed.");
            var attributes = File.GetAttributes(entry.FullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new PackageIndexException("NuGet cache contains a non-regular or linked entry.");

            var buffer = new byte[CacheReadBufferBytes];
            long entryBytesRead = 0;
            int bytesRead;
            while (entryBytesRead < entry.Length
                && (bytesRead = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, entry.Length - entryBytesRead))) > 0)
            {
                entryBytesRead += bytesRead;
                totalBytesRead += bytesRead;
                if (entryBytesRead > MaximumCacheEntryBytes || totalBytesRead > MaximumCacheTreeBytes)
                    throw new PackageIndexException("NuGet cache closure exceeds its bounded file-read policy.");
                hash.AppendData(buffer, 0, bytesRead);
            }
            if (entryBytesRead != entry.Length || stream.Length != entry.Length)
                throw new PackageIndexException("NuGet cache changed while its closure was being hashed.");
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void WriteConfiguration(string root, string feed) => File.WriteAllText(Path.Join(root, "NuGet.config"),
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", feed)),
            new XElement("add", new XAttribute("key", "third-party"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
            new XElement("packageSourceMapping", new XElement("packageSource", new XAttribute("key", "candidate"), new XElement("package", new XAttribute("pattern", "ForgeTrust.*"))),
                new XElement("packageSource", new XAttribute("key", "third-party"), new XElement("package", new XAttribute("pattern", "*")))),
            new XElement("fallbackPackageFolders", new XElement("clear")))).ToString());

    private static string ReadMarker(string output, string marker)
    {
        var matches = Regex.Matches(output, @"\[timing-fixture\] " + Regex.Escape(marker) + @"=([^\r\n]+)", RegexOptions.CultureInvariant);
        if (matches.Count != 1) throw new PackageIndexException("Timing fixture marker is missing or ambiguous.");
        return matches[0].Groups[1].Value;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    /// <summary>Copies a validated bounded regular tree while preserving every relative directory and file path.</summary>
    internal static void CopyTree(string source, string destination)
    {
        var entries = ReadCacheTree(source);
        DurableTemplateStaging.RequireRegularPath(destination);
        Directory.CreateDirectory(destination);
        foreach (var entry in entries.Where(entry => entry.IsDirectory))
        {
            var output = Path.Join(destination, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(output);
        }
        foreach (var entry in entries.Where(entry => !entry.IsDirectory))
        {
            var output = Path.Join(destination, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(entry.FullPath, output);
        }
    }

    private static IReadOnlyList<CacheTreeEntry> ReadCacheTree(string root)
    {
        DurableTemplateStaging.RequireRegularPath(root);
        if (!Directory.Exists(root)) throw new PackageIndexException("NuGet cache root is missing or is not a directory.");
        var entries = new List<CacheTreeEntry>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        long totalBytes = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                DurableTemplateStaging.RequireRegularPath(path);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PackageIndexException("NuGet cache contains a linked or reparse-point entry.");

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var length = isDirectory ? 0 : new FileInfo(path).Length;
                if (!isDirectory)
                {
                    if (length < 0 || length > MaximumCacheEntryBytes || totalBytes > MaximumCacheTreeBytes - length)
                        throw new PackageIndexException("NuGet cache closure exceeds its bounded file-read policy.");
                    totalBytes += length;
                }

                var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relativePath.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
                    throw new PackageIndexException("NuGet cache entry escaped its root.");
                entries.Add(new(path, relativePath, isDirectory, length));
                if (entries.Count > MaximumCacheTreeEntries)
                    throw new PackageIndexException("NuGet cache closure exceeds its entry-count limit.");
                if (isDirectory) pending.Push(path);
            }
        }

        return entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static void AppendCacheInteger(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendCacheLong(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private sealed record CacheTreeEntry(string FullPath, string RelativePath, bool IsDirectory, long Length);

    private static void RequireTree(string root)
    {
        DurableTemplateStaging.RequireRegularPath(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            DurableTemplateStaging.RequireRegularPath(path);
            if (Directory.Exists(path)) RequireTree(path);
        }
    }

    /// <summary>Writes a bounded safe timing receipt; raw outputs and credential-bearing arguments are excluded.</summary>
    internal static void WriteReceipt(string path, DurableTemplateTimingProofReceipt receipt)
    {
        DurableTemplateStaging.RequireRegularPath(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, PackageArtifactJson.Options);
        if (bytes.Length > 64 * 1024) throw new PackageIndexException("Timing receipt exceeds its safe retention bound.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, bytes);
    }
}
