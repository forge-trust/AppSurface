namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Exact installed-artifact proof command; arguments choose proof inputs, never template content or SQL policy.</summary>
internal static class DurableTemplateCommand
{
    /// <summary>Runs a source-bound correctness proof from the immutable local producer bundle.</summary>
    /// <param name="arguments">Explicit option/value pairs; unknown, duplicate and incomplete options are rejected.</param>
    /// <param name="currentDirectory">Base for user-supplied relative paths.</param>
    /// <param name="runner">Bounded command runner, optionally deterministic in command-dispatch tests.</param>
    /// <param name="cancellationToken">Cancels work; independently bounded proof cleanup still runs.</param>
    /// <returns>Safe schema-v1 receipt; command callers use its Succeeded flag as the exit signal.</returns>
    internal static async Task<DurableTemplateProofReceipt> RunAsync(string[] arguments, string currentDirectory,
        IExternalCommandRunner runner, CancellationToken cancellationToken)
    {
        var options = DurableTemplateCommandOptions.Parse(arguments, currentDirectory);
        var manifest = await new PackageArtifactManifestReader().ReadAsync(options.ArtifactManifest, cancellationToken);
        if (manifest.PackageVersion != options.PackageVersion)
            throw new PackageIndexException("Template proof version differs from the producer manifest.");
        var rows = new List<PackageArtifactValidationReportEntry>();
        foreach (var entry in manifest.Entries)
        {
            var path = Path.Join(options.Artifacts, entry.ArtifactFileName);
            DurableTemplateStaging.RequireRegularPath(path);
            if (Path.GetFileName(entry.ArtifactFileName) != entry.ArtifactFileName || !File.Exists(path)
                || PackageHash.ComputeSha512(path) != entry.Sha512)
                throw new PackageIndexException("Template proof archive differs from the producer manifest.");
            rows.Add(new(entry.PackageId, entry.ProjectPath, PackagePublishDecision.Publish, [], path));
        }
        var request = new DurableTemplateConsumerProofRequest(options.RepositoryRoot, options.Artifacts,
            options.PackageVersion, options.Report, SourceCommit: options.SourceCommit,
            RunFirstWork: OperatingSystem.IsLinux(), RunNativeSmoke: options.NativePgBin is not null);
        NativePostgreSqlToolIdentity? nativeIdentity = null;
        return await new DurableTemplateConsumerProof(runner).RunAsync(request, rows,
            options.NativePgBin is null ? null : (root, environment, token) =>
                RunNativeSmokeAsync(options.NativePgBin, root, environment, runner, identity => nativeIdentity = identity, token),
            cancellationToken, nativeToolIdentity: () => nativeIdentity);
    }

    /// <summary>Runs the generated ordinary-startup smoke against one privately owned native PostgreSQL cluster.</summary>
    /// <param name="bin">Absolute PostgreSQL tool directory selected for the smoke runner.</param>
    /// <param name="root">Generated test project to run.</param>
    /// <param name="environment">Environment shared with the generated proof, copied before adding cluster credentials.</param>
    /// <param name="runner">Bounded runner used for both cluster operations and the generated test command.</param>
    /// <param name="identitySink">Receives the validated tool and server identity for the proof receipt.</param>
    /// <param name="cancellationToken">Cancels setup and smoke execution; bounded cluster cleanup still runs.</param>
    /// <param name="startClusterAsync">Optional test seam that returns an owned cluster. Production uses
    /// <see cref="DurableTemplateNativePostgreSql.StartAsync(string, IExternalCommandRunner, CancellationToken, INativePostgreSqlToolDirectoryResolver?, INativePostgreSqlBootstrapRunner?, INativePostgreSqlRuntime?, NativePostgreSqlBudgets?)"/>
    /// with the fixed resolver for <paramref name="bin"/>.</param>
    /// <exception cref="PackageIndexException">Thrown when the generated smoke or its required output markers fail validation.</exception>
    internal static async Task RunNativeSmokeAsync(string bin, string root, IReadOnlyDictionary<string, string?> environment,
        IExternalCommandRunner runner, Action<NativePostgreSqlToolIdentity> identitySink, CancellationToken cancellationToken,
        Func<string, IExternalCommandRunner, CancellationToken, Task<DurableTemplateNativePostgreSql>>? startClusterAsync = null)
    {
        // macOS user temp paths can exceed PostgreSQL's Unix-domain socket path limit.
        var temporary = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetFullPath(Path.GetTempPath());
        var ownedRoot = Path.Join(temporary, "appsurface-native-template-" + Guid.NewGuid().ToString("N"));
        var cluster = startClusterAsync is null
            ? await DurableTemplateNativePostgreSql.StartAsync(ownedRoot, runner, cancellationToken,
                toolDirectoryResolver: new FixedNativeToolDirectory(bin))
            : await startClusterAsync(ownedRoot, runner, cancellationToken);
        ExternalCommandResult? result = null;
        var cleanupRemaining = 20_000;
        try
        {
            identitySink(cluster.ToolIdentity);
            var childEnvironment = environment.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            childEnvironment["APPSURFACE_TEMPLATE_NATIVE_ADMIN_CONNECTION"] = cluster.ConnectionString;
            childEnvironment["APPSURFACE_TEMPLATE_NATIVE_PG_BIN"] = bin;
            childEnvironment["APPSURFACE_TEMPLATE_NATIVE_SETUP_REMAINING_MS"] = cluster.SetupBudgetRemainingMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            childEnvironment["APPSURFACE_TEMPLATE_NATIVE_PORT_MIN"] = "50000";
            childEnvironment["APPSURFACE_TEMPLATE_NATIVE_PORT_MAX"] = "60000";
            result = await runner.RunAsync(new ExternalCommandRequest("dotnet",
                ["test", root, "--no-build", "--no-restore", "--filter", "FullyQualifiedName~NativePostgreSqlSmoke", "--logger", "console;verbosity=detailed"],
                root, "native-smoke", "verifying ordinary generated host startup", cluster.SetupBudgetRemainingMilliseconds + 35_000, childEnvironment,
                ExternalCapturePolicy.ReleaseProof), cancellationToken);
            cleanupRemaining = ReadCleanupRemaining(result.StandardOutput);
            DurableTemplateConsumerProof.RequireResult(result);
            if (!result.StandardOutput.Contains("[native-smoke] read-only ordinary startup passed", StringComparison.Ordinal))
                throw new PackageIndexException("Native ordinary-startup assertion checkpoint is missing.");
        }
        finally
        {
            await cluster.DisposeWithBudgetAsync(Math.Max(1, cleanupRemaining));
        }
    }

    /// <summary>Requires the generated child's bounded cleanup observation before sharing the remaining cluster allowance.</summary>
    internal static int ReadCleanupRemaining(string output)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(output,
            @"\[native-cleanup\] elapsed-ms=([0-9]+)(?:\r?\n|$)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (matches.Count != 1 || !int.TryParse(matches[0].Groups[1].Value,
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var elapsed)
            || elapsed >= 20_000)
            throw new PackageIndexException("Native smoke cleanup observation is missing, ambiguous or exhausted.");
        return 20_000 - elapsed;
    }

    private sealed class FixedNativeToolDirectory(string directory) : INativePostgreSqlToolDirectoryResolver
    {
        public Task<string> ResolveAsync(IExternalCommandRunner commandRunner, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(directory);
        }
    }

}

/// <summary>Resolved immutable inputs to verify-durable-template; parsing performs no IO or tool launch.</summary>
internal sealed record DurableTemplateCommandOptions(string RepositoryRoot, string Artifacts, string ArtifactManifest,
    string PackageVersion, string SourceCommit, string Report, string? NativePgBin)
{
    /// <summary>Requires exact version/source/archive/receipt inputs and confines no mutable authority to the generated app.</summary>
    internal static DurableTemplateCommandOptions Parse(string[] args, string currentDirectory)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(["--repo-root", "--artifacts-input", "--artifact-manifest", "--package-version",
            "--source-commit", "--report", "--native-pg-bin"], StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var key = args[index];
            if (!allowed.Contains(key) || !values.TryAdd(key, string.Empty) || index + 1 == args.Length
                || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new PackageIndexException("Template proof options are unknown, duplicated or missing a value.");
            values[key] = args[index + 1];
        }
        string Required(string key) => values.TryGetValue(key, out var value) ? value
            : throw new PackageIndexException($"Template proof requires {key}.");
        string PathValue(string value) => Path.GetFullPath(value, currentDirectory);
        var version = Required("--package-version");
        PackageVersionValidator.Require(version, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        var source = Required("--source-commit");
        if (source.Length != 40 || !source.All(Uri.IsHexDigit)) throw new PackageIndexException("Template proof requires a full source revision.");
        var root = values.GetValueOrDefault("--repo-root", currentDirectory);
        return new(PathValue(root), PathValue(Required("--artifacts-input")), PathValue(Required("--artifact-manifest")),
            version, source, PathValue(Required("--report")), values.TryGetValue("--native-pg-bin", out var bin) ? PathValue(bin) : null);
    }
}
