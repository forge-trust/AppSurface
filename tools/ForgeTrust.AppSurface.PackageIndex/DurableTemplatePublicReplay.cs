using System.IO.Compression;
using System.Security.Cryptography;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Replays the template from the public feed after promotion, preserving the candidate/public identity distinction.</summary>
internal static class DurableTemplatePublicReplay
{
    /// <summary>Installs the public template through dotnet new and proves its payload against the frozen producer before execution.</summary>
    /// <param name="request">Existing published smoke request with exact candidate preflight bindings.</param>
    /// <param name="manifest">Frozen candidate manifest; raw public archive hashes may differ after signing.</param>
    /// <param name="publicCache">Private cache restored by the existing public-feed smoke.</param>
    /// <param name="runner">Bounded child runner.</param>
    /// <param name="cancellationToken">Cancels work; owned template/roots are still cleaned.</param>
    internal static async Task RunAsync(PackageSmokeInstallRequest request, PackageArtifactManifest manifest,
        string publicCache, IExternalCommandRunner runner, CancellationToken cancellationToken)
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/var/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/tmp/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        var root = Path.Join(temporary, "appsurface-template-public-" + Guid.NewGuid().ToString("N"));
        var cli = Path.Join(root, "cli");
        var feed = Path.Join(root, "public-archives");
        Directory.CreateDirectory(cli); Directory.CreateDirectory(feed);
        var environment = DurableTemplateConsumerProof.EnvironmentFor(root, "public-install");
        environment["DOTNET_CLI_HOME"] = cli;
        Exception? primary = null;
        try
        {
            var install = await runner.RunAsync(new ExternalCommandRequest("dotnet",
                ["new", "install", $"{DurableTemplateStaging.PackageId}@{manifest.PackageVersion}", "--nuget-source", request.Source, "--force"],
                root, "public-template-install", "installing promoted template", 180_000, environment, ExternalCapturePolicy.ReleaseProof), cancellationToken);
            DurableTemplateConsumerProof.RequireResult(install);
            var installed = Directory.EnumerateFiles(cli, "*.nupkg", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Equals($"{DurableTemplateStaging.PackageId}.{manifest.PackageVersion}.nupkg", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (installed.Length != 1) throw new PackageIndexException("Public native template installation did not retain one exact archive.");
            var templateEntry = manifest.Entries.Single(entry => string.Equals(entry.PackageId,
                DurableTemplateStaging.PackageId, StringComparison.OrdinalIgnoreCase));
            var rows = new List<PackageArtifactValidationReportEntry>();
            foreach (var entry in manifest.Entries)
            {
                var id = entry.PackageId.ToLowerInvariant();
                var version = manifest.PackageVersion.ToLowerInvariant();
                var publicArchive = string.Equals(entry.PackageId, templateEntry.PackageId, StringComparison.OrdinalIgnoreCase) ? installed[0]
                    : Path.Join(publicCache, id, version, $"{id}.{version}.nupkg");
                var candidate = Path.Join(Path.GetDirectoryName(request.ArtifactManifestPath)!, entry.ArtifactFileName);
                RequirePayloadIdentity(candidate, publicArchive);
                var copy = Path.Join(feed, entry.ArtifactFileName);
                File.Copy(publicArchive, copy);
                rows.Add(new(entry.PackageId, entry.ProjectPath, PackagePublishDecision.Publish, [], copy));
            }
            var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(new(request.RepositoryRoot, feed,
                manifest.PackageVersion, Path.Join(request.WorkDirectory, "durable-template-public-replay.json"), request.Source,
                request.PreflightProof?.SourceCommit, RunFirstWork: OperatingSystem.IsLinux(), Mode: "promoted-public-feed-replay"), rows,
                cancellationToken: cancellationToken);
            if (!receipt.Succeeded) throw new PackageIndexException("Public Durable template replay failed; retry the safe receipt phase.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            primary = exception;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                try
                {
                    var uninstall = await runner.RunAsync(new ExternalCommandRequest("dotnet", ["new", "uninstall", DurableTemplateStaging.PackageId], root,
                        "public-template-uninstall", "removing owned promoted template", 20_000, environment, ExternalCapturePolicy.ReleaseProof), cleanup.Token);
                    DurableTemplateConsumerProof.RequireResult(uninstall);
                    var list = await runner.RunAsync(new ExternalCommandRequest("dotnet", ["new", "list"], root,
                        "public-template-absence", "checking template removal", 20_000, environment, ExternalCapturePolicy.ReleaseProof), cleanup.Token);
                    DurableTemplateConsumerProof.RequireResult(list);
                    if (list.StandardOutput.Contains(DurableTemplateArtifactContract.ShortName, StringComparison.Ordinal))
                        throw new PackageIndexException("Promoted template remained installed after cleanup.");
                }
                finally
                {
                    RequireRegularTree(root);
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                throw new PackageIndexException("Promoted template replay cleanup failed; publication smoke is unsuccessful.");
            }
        }
        if (primary is not null) throw new PackageIndexException("Promoted Durable template replay failed; inspect its safe receipt and rerun smoke-install.");
    }

    /// <summary>Compares every uncompressed package payload, ignoring only the NuGet signing envelope.</summary>
    internal static void RequirePayloadIdentity(string candidate, string published)
    {
        var expected = Inventory(candidate);
        var actual = Inventory(published);
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
            throw new PackageIndexException("Promoted package payload differs from the frozen candidate.");
    }

    private static Dictionary<string, string> Inventory(string path)
    {
        DurableTemplateStaging.RequireRegularPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 128L * 1024 * 1024)
            throw new PackageIndexException("Published archive is missing or exceeds the replay bound.");
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 4096) throw new PackageIndexException("Published archive inventory exceeds the replay bound.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = DurableTemplateArtifactContract.NormalizeArchivePath(entry.FullName.TrimEnd('/'));
            if (!names.Add(name)) throw new PackageIndexException("Published archive contains duplicate payload/signature paths.");
            if (name == ".signature.p7s")
            {
                if (entry.Length > 1024 * 1024) throw new PackageIndexException("Published signing envelope exceeds its replay bound.");
                continue;
            }
            if (entry.Length > 128L * 1024 * 1024 || (total += entry.Length) > 512L * 1024 * 1024)
                throw new PackageIndexException("Published archive inflation exceeds the replay bound.");
            using var input = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[16 * 1024];
            long count = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                count += read;
                if (count > entry.Length) throw new PackageIndexException("Published archive has inconsistent inflated length.");
                hash.AppendData(buffer.AsSpan(0, read));
            }
            if (count != entry.Length || !values.TryAdd(name, Convert.ToHexStringLower(hash.GetHashAndReset())))
                throw new PackageIndexException("Published archive has inconsistent or duplicate payload entries.");
        }
        return values;
    }

    private static void RequireRegularTree(string root)
    {
        DurableTemplateStaging.RequireRegularPath(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            DurableTemplateStaging.RequireRegularPath(path);
            if (Directory.Exists(path)) RequireRegularTree(path);
        }
    }
}
