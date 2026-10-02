using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Consumes the exact candidate NuGet bundle with the PostgreSQL runtime-preflight controller and
/// checks the public-feed restore against those same package bytes before repeating the proof.
/// </summary>
internal sealed class DurablePreflightArtifactProof
{
    internal const int CandidateProofTimeoutMilliseconds = 25 * 60 * 1000;
    internal const int PublishedProofTimeoutMilliseconds = 65 * 60 * 1000;
    private const string ConsumerProjectRelativePath = "Durable/consumers/PostgreSqlPreflightConsumer/PostgreSqlPreflightConsumer.csproj";
    private const string ProofScriptRelativePath = "Durable/verify-preflight-artifacts.sh";
    private const string CliPackageId = "ForgeTrust.AppSurface.Cli";
    private const string ProviderPackageId = "ForgeTrust.AppSurface.Durable.PostgreSql";
    private const string ProviderRecipePath = "contentFiles/any/any/configure-postgresql-roles.sql";
    private const string ProofKind = "issue-845-exact-package-disposable-postgresql-consumer";
    private const string ProofSuccessMarker = "issue-845-proof=passed";
    private const string PostgresImage = "postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877";
    private const string OwnerRole = "appsurface_durable_owner";
    private const string StoreId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450";
    private const string ActiveEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450";
    private const string RolePairsRelativePath = "examples/durable-postgresql/role-pairs-full-and-work-only.example.json";
    private const string Schema10FixtureRelativePath = "Durable/consumers/PostgreSqlPreflightConsumer/schema10-two-pair.sql";

    private readonly IExternalCommandRunner _commandRunner;
    private readonly Action<string> _deleteOwnedDirectory;

    /// <summary>Creates a carrier with checked deletion of every owned public-feed scratch directory.</summary>
    /// <param name="commandRunner">Bounded runner for the exact-package consumer.</param>
    /// <param name="deleteOwnedDirectory">
    /// Optional internal filesystem seam for deterministic cleanup-failure tests. Production deletes recursively;
    /// every supplied implementation must remove the directory, and the carrier independently checks its absence.
    /// Cleanup failure blocks receipt promotion and triggers another checked cleanup attempt on the failure path.
    /// </param>
    internal DurablePreflightArtifactProof(IExternalCommandRunner commandRunner, Action<string>? deleteOwnedDirectory = null)
    {
        _commandRunner = commandRunner;
        _deleteOwnedDirectory = deleteOwnedDirectory ?? (static path => Directory.Delete(path, recursive: true));
    }

    /// <summary>
    /// Runs the shared proof over a downloaded immutable candidate bundle. The manifest is promoted only after
    /// the controller exits successfully and its combined receipt binds the expected run, artifact, and bytes.
    /// </summary>
    internal async Task<DurablePreflightArtifactProofResult> RunCandidateAsync(
        DurablePreflightArtifactProofRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        if (File.Exists(request.ApprovedManifestPath) || Directory.Exists(request.ApprovedManifestPath)
            || File.Exists(request.ReceiptPath) || Directory.Exists(request.ReceiptPath))
        {
            throw new PackageIndexException("Candidate approval or receipt destination already exists.");
        }

        var receiptTemporaryPath = CreateTemporarySiblingPath(request.ReceiptPath, "candidate");
        var totalStart = Stopwatch.GetTimestamp();
        var approvedManifestPromoted = false;
        var receiptPromoted = false;

        try
        {
            var bundle = await ValidateBundleAsync(
                request.ArtifactDirectory,
                request.ArtifactManifestPath,
                request.SourceCommit,
                cancellationToken);
            var fixture = await ReadExpectedFixtureAsync(request.RepositoryRoot, cancellationToken);
            await RunControllerAsync(request, request.ArtifactDirectory, request.ArtifactManifestPath,
                receiptTemporaryPath, CandidateProofTimeoutMilliseconds, cancellationToken);
            var receipt = await ReadCompleteReceiptAsync(
                receiptTemporaryPath,
                request,
                bundle,
                fixture,
                cancellationToken);
            var postProofBundle = await ValidateBundleAsync(
                request.ArtifactDirectory,
                request.ArtifactManifestPath,
                request.SourceCommit,
                cancellationToken);
            if (!BundleIdentityMatches(bundle, postProofBundle))
            {
                throw new PackageIndexException("The exact candidate package bundle changed while the consumer proof was running.");
            }

            if (File.Exists(request.ApprovedManifestPath) || Directory.Exists(request.ApprovedManifestPath))
            {
                throw new PackageIndexException("Approved artifact manifest destination already exists.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.ApprovedManifestPath))!);
            File.Move(request.ArtifactManifestPath, request.ApprovedManifestPath);
            approvedManifestPromoted = true;
            MoveReceipt(receiptTemporaryPath, request.ReceiptPath);
            receiptPromoted = true;
            return new DurablePreflightArtifactProofResult(
                bundle.ManifestSha256,
                bundle.ManifestSha512,
                bundle.CliSha256,
                bundle.ProviderSha256,
                bundle.RecipeSha256,
                receipt.TotalDurationMilliseconds,
                Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds,
                receipt.ScenarioCount);
        }
        catch
        {
            DeleteIfPresent(receiptTemporaryPath);
            if (approvedManifestPromoted)
            {
                if (!File.Exists(request.ArtifactManifestPath) && !Directory.Exists(request.ArtifactManifestPath))
                {
                    // The approved path contains the caller's input until the entire proof succeeds.
                    // Restore it on a later promotion failure instead of deleting the only copy.
                    File.Move(request.ApprovedManifestPath, request.ArtifactManifestPath);
                }
                else
                {
                    // A caller recreated the input path during promotion. Keep that path untouched and
                    // withhold the approved output created by this operation.
                    DeleteIfPresent(request.ApprovedManifestPath);
                }
            }

            if (receiptPromoted) DeleteIfPresent(request.ReceiptPath);
            throw;
        }
    }

    /// <summary>
    /// Revalidates a retained candidate receipt and promotes its approved manifest beside the immutable downloaded
    /// package bundle. Used by protected release jobs before they consume the bundle.
    /// </summary>
    internal static async Task ValidateAndPromoteCandidateManifestAsync(
        DurablePreflightArtifactProofRequest request,
        string candidateReceiptPath,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateReceiptPath);
        var destination = Path.Combine(request.ArtifactDirectory, "package-artifact-manifest.json");
        if (!IsRegularFile(request.ApprovedManifestPath) || !File.Exists(candidateReceiptPath)
            || File.Exists(destination) || Directory.Exists(destination))
        {
            throw new PackageIndexException("Retained candidate approval inputs are missing, stale, or already promoted.");
        }

        if (!string.Equals(Path.GetFullPath(repositoryRoot), Path.GetFullPath(request.RepositoryRoot), PackageIndexGenerator.RepositoryPathComparison))
        {
            throw new PackageIndexException("Candidate promotion repository root must match the receipt-bound request root.");
        }

        var bundle = await ValidateBundleAsync(request.ArtifactDirectory, request.ArtifactManifestPath, request.SourceCommit, cancellationToken);
        var fixture = await ReadExpectedFixtureAsync(request.RepositoryRoot, cancellationToken);
        _ = await ReadCompleteReceiptAsync(candidateReceiptPath, request, bundle, fixture, cancellationToken);
        var unapprovedBytes = await File.ReadAllBytesAsync(request.ArtifactManifestPath, cancellationToken);
        var approvedBytes = await File.ReadAllBytesAsync(request.ApprovedManifestPath, cancellationToken);
        if (!unapprovedBytes.AsSpan().SequenceEqual(approvedBytes))
        {
            throw new PackageIndexException("Retained approved manifest does not byte-match the proof-bound unapproved manifest.");
        }

        var temporary = CreateTemporarySiblingPath(destination, "promote");
        try
        {
            await File.WriteAllBytesAsync(temporary, approvedBytes, cancellationToken);
            File.Move(temporary, destination);
        }
        catch
        {
            DeleteIfPresent(temporary);
            DeleteIfPresent(destination);
            throw;
        }
    }

    /// <summary>
    /// Compares clean public-feed restore results byte-for-byte with the approved candidate package bundle, then
    /// invokes the same disposable proof against those restored CLI/provider archives.
    /// </summary>
    internal async Task<DurablePreflightArtifactProofResult> RunPublishedAsync(
        DurablePreflightArtifactProofRequest request,
        string candidateReceiptPath,
        string restoredPackagesPath,
        string publicFeedReceiptPath,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateReceiptPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoredPackagesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicFeedReceiptPath);
        if (File.Exists(publicFeedReceiptPath) || Directory.Exists(publicFeedReceiptPath)
            || File.Exists(publicFeedReceiptPath + ".carrier.json") || Directory.Exists(publicFeedReceiptPath + ".carrier.json"))
        {
            throw new PackageIndexException("Public-feed receipt destination already exists.");
        }

        if (!File.Exists(candidateReceiptPath) || !Directory.Exists(restoredPackagesPath))
        {
            throw new PackageIndexException("Candidate preflight receipt is missing; public proof is closed.");
        }

        var candidateBundle = await ValidateBundleAsync(
            request.ArtifactDirectory,
            request.ArtifactManifestPath,
            request.SourceCommit,
            cancellationToken);
        var fixture = await ReadExpectedFixtureAsync(request.RepositoryRoot, cancellationToken);
        _ = await ReadCompleteReceiptAsync(
            candidateReceiptPath,
            request,
            candidateBundle,
            fixture,
            cancellationToken);

        var totalStart = Stopwatch.GetTimestamp();
        var compareStart = Stopwatch.GetTimestamp();
        var receiptTemporaryPath = CreateTemporarySiblingPath(publicFeedReceiptPath, "published");
        var carrierReceiptTemporaryPath = CreateTemporarySiblingPath(publicFeedReceiptPath + ".carrier.json", "published");
        var publicArtifactsDirectory = CreateTemporaryDirectory("appsurface-preflight-public-feed");
        var publicArtifactsCleaned = false;
        var proofReceiptPromoted = false;
        var carrierReceiptWritten = false;
        try
        {
            var publicPackagePaths = await CompareRestoredPackageBytesAsync(
                request.ArtifactDirectory,
                request.ArtifactManifestPath,
                restoredPackagesPath,
                publicArtifactsDirectory,
                cancellationToken);
            var comparisonMilliseconds = Stopwatch.GetElapsedTime(compareStart).TotalMilliseconds;

            await RunControllerAsync(request, publicArtifactsDirectory, request.ArtifactManifestPath,
                receiptTemporaryPath, PublishedProofTimeoutMilliseconds, cancellationToken);
            var publishedReceipt = await ReadCompleteReceiptAsync(
                receiptTemporaryPath,
                request,
                candidateBundle,
                fixture,
                cancellationToken);

            DeleteOwnedDirectory(publicArtifactsDirectory);
            publicArtifactsCleaned = true;

            var outerReceipt = new DurablePreflightPublicFeedReceipt(
                SchemaVersion: 1,
                ProofKind: "issue845-public-feed-byte-identity",
                SourceCommit: request.SourceCommit,
                RunId: request.RunId,
                ArtifactId: request.ArtifactId,
                CandidateReceiptSha256: await ComputeSha256Async(candidateReceiptPath, cancellationToken),
                ArtifactManifestSha256: candidateBundle.ManifestSha256,
                ArtifactManifestSha512: candidateBundle.ManifestSha512,
                PublicPackageSha256: publicPackagePaths,
                ExtractedRecipeSha256: candidateBundle.RecipeSha256,
                PublicRestoreMilliseconds: comparisonMilliseconds,
                SharedProofMilliseconds: publishedReceipt.TotalDurationMilliseconds,
                TotalMilliseconds: Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds,
                SharedProofReceiptSha256: await ComputeSha256Async(receiptTemporaryPath, cancellationToken));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publicFeedReceiptPath))!);
            await WritePublicFeedReceiptAsync(carrierReceiptTemporaryPath, outerReceipt, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            MoveReceipt(receiptTemporaryPath, publicFeedReceiptPath);
            proofReceiptPromoted = true;
            MoveReceipt(carrierReceiptTemporaryPath, publicFeedReceiptPath + ".carrier.json");
            carrierReceiptWritten = true;
            return new DurablePreflightArtifactProofResult(
                candidateBundle.ManifestSha256,
                candidateBundle.ManifestSha512,
                publicPackagePaths[CliPackageId],
                publicPackagePaths[ProviderPackageId],
                candidateBundle.RecipeSha256,
                publishedReceipt.TotalDurationMilliseconds,
                outerReceipt.TotalMilliseconds,
                publishedReceipt.ScenarioCount);
        }
        catch
        {
            DeleteIfPresent(receiptTemporaryPath);
            DeleteIfPresent(carrierReceiptTemporaryPath);
            if (proofReceiptPromoted) DeleteIfPresent(publicFeedReceiptPath);
            if (carrierReceiptWritten) DeleteIfPresent(publicFeedReceiptPath + ".carrier.json");
            throw;
        }
        finally
        {
            if (!publicArtifactsCleaned) DeleteOwnedDirectory(publicArtifactsDirectory);
        }
    }

    internal static async Task<DurablePreflightBundleIdentity> ValidateBundleAsync(
        string artifactDirectory,
        string artifactManifestPath,
        string sourceCommit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactManifestPath);
        if (!Directory.Exists(artifactDirectory) || !File.Exists(artifactManifestPath))
        {
            throw new PackageIndexException("Exact candidate package bundle or manifest is missing.");
        }

        var manifestBytes = await File.ReadAllBytesAsync(artifactManifestPath, cancellationToken);
        var manifest = await new PackageArtifactManifestReader().ReadAsync(artifactManifestPath, cancellationToken);
        var entries = manifest.Entries.ToDictionary(entry => entry.PackageId, StringComparer.OrdinalIgnoreCase);
        if (!entries.TryGetValue(CliPackageId, out var cli) || !cli.IsTool
            || !entries.TryGetValue(ProviderPackageId, out var provider))
        {
            throw new PackageIndexException("Candidate bundle must contain the AppSurface CLI tool and PostgreSQL provider packages.");
        }

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(artifactDirectory, entry.ArtifactFileName);
            if (!IsRegularFile(path))
            {
                throw new PackageIndexException($"Candidate package '{entry.PackageId}' is missing or is not a regular file.");
            }

            var actualSha512 = await PackageHash.ComputeSha512Async(path, cancellationToken);
            if (!string.Equals(actualSha512, entry.Sha512, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageIndexException($"Candidate package '{entry.PackageId}' no longer matches its unapproved manifest.");
            }

            await ValidatePackageNuspecIdentityAsync(path, entry.PackageId, manifest.PackageVersion, sourceCommit, cancellationToken);

            hashes.Add(entry.PackageId, await ComputeSha256Async(path, cancellationToken));
        }

        var recipeSha256 = await ComputePackagedRecipeSha256Async(
            Path.Combine(artifactDirectory, provider.ArtifactFileName), cancellationToken);
        return new DurablePreflightBundleIdentity(
            Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            Convert.ToHexStringLower(SHA512.HashData(manifestBytes)),
            manifest.PackageVersion,
            hashes[CliPackageId],
            hashes[ProviderPackageId],
            recipeSha256,
            manifest.Entries.Count,
            hashes);
    }

    private static async Task ValidatePackageNuspecIdentityAsync(
        string packagePath,
        string expectedPackageId,
        string expectedVersion,
        string expectedSourceCommit,
        CancellationToken cancellationToken)
    {
        if (!expectedPackageId.StartsWith("ForgeTrust.", StringComparison.Ordinal))
        {
            throw new PackageIndexException($"Candidate bundle package '{expectedPackageId}' is outside the ForgeTrust package closure.");
        }

        try
        {
            await using var packageStream = File.OpenRead(packagePath);
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
            var nuspecs = archive.Entries.Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (nuspecs.Length != 1 || nuspecs[0].Length is <= 0 or > 1024 * 1024)
            {
                throw new PackageIndexException($"Candidate package '{expectedPackageId}' must contain one bounded nuspec.");
            }

            await using var nuspecStream = nuspecs[0].Open();
            using var reader = XmlReader.Create(nuspecStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true });
            var nuspec = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
            var metadata = nuspec.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata");
            var packageId = metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == "id")?.Value;
            var packageVersion = metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == "version")?.Value;
            var repository = metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == "repository");
            var repositoryCommit = repository?.Attribute("commit")?.Value;
            if (!string.Equals(packageId, expectedPackageId, StringComparison.Ordinal)
                || !string.Equals(packageVersion, expectedVersion, StringComparison.Ordinal)
                || !string.Equals(repository?.Attribute("type")?.Value, "git", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(repository?.Attribute("url")?.Value, "https://github.com/forge-trust/AppSurface", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(repositoryCommit, expectedSourceCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageIndexException($"Candidate package '{expectedPackageId}' nuspec id, version, or source commit does not match the receipt-bound request.");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException or InvalidOperationException)
        {
            throw new PackageIndexException($"Candidate package '{expectedPackageId}' has invalid nuspec identity metadata.", exception);
        }
    }

    internal static async Task<IReadOnlyDictionary<string, string>> CompareRestoredPackageBytesAsync(
        string candidateDirectory,
        string artifactManifestPath,
        string restoredPackagesPath,
        string publicArtifactsDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(restoredPackagesPath))
        {
            throw new PackageIndexException("Public NuGet restore did not create its isolated package cache.");
        }

        var manifest = await new PackageArtifactManifestReader().ReadAsync(artifactManifestPath, cancellationToken);
        var entries = manifest.Entries.ToDictionary(entry => entry.PackageId, StringComparer.OrdinalIgnoreCase);
        if (!entries.TryGetValue(CliPackageId, out var cli) || !entries.TryGetValue(ProviderPackageId, out var provider))
        {
            throw new PackageIndexException("Candidate manifest is missing the matching CLI or PostgreSQL provider package.");
        }

        Directory.CreateDirectory(publicArtifactsDirectory);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidatePath = Path.Combine(candidateDirectory, entry.ArtifactFileName);
            var restoredPath = ResolveRestoredPackagePath(restoredPackagesPath, entry, manifest.PackageVersion);
            if (!IsRegularFile(candidatePath) || !IsRegularFile(restoredPath)
                || !await FilesAreByteIdenticalAsync(candidatePath, restoredPath, cancellationToken))
            {
                throw new PackageIndexException($"Public-feed restored '{entry.PackageId}' archive does not byte-match the exact candidate package.");
            }

            var destination = Path.Combine(publicArtifactsDirectory, entry.ArtifactFileName);
            File.Copy(restoredPath, destination, overwrite: false);
            hashes.Add(entry.PackageId, await ComputeSha256Async(destination, cancellationToken));
        }

        var recipeEntry = provider;
        var candidateRecipe = await ReadPackagedRecipeAsync(
            Path.Combine(candidateDirectory, recipeEntry.ArtifactFileName), cancellationToken);
        var restoredRecipePath = Path.Combine(
            restoredPackagesPath,
            provider.PackageId.ToLowerInvariant(),
            manifest.PackageVersion.ToLowerInvariant(),
            ProviderRecipePath.Replace('/', Path.DirectorySeparatorChar));
        if (!IsRegularFile(restoredRecipePath))
        {
            throw new PackageIndexException("Public-feed restored provider package does not contain the packaged PostgreSQL role recipe.");
        }

        var restoredRecipe = await File.ReadAllBytesAsync(restoredRecipePath, cancellationToken);
        if (!candidateRecipe.AsSpan().SequenceEqual(restoredRecipe))
        {
            throw new PackageIndexException("Public-feed extracted PostgreSQL role recipe does not byte-match the exact candidate recipe.");
        }

        File.Copy(artifactManifestPath, Path.Combine(publicArtifactsDirectory, Path.GetFileName(artifactManifestPath)), overwrite: false);
        return hashes;
    }

    private static async Task<DurablePreflightExpectedFixture> ReadExpectedFixtureAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var rolePairsPath = Path.Combine(repositoryRoot, RolePairsRelativePath);
        var schema10FixturePath = Path.Combine(repositoryRoot, Schema10FixtureRelativePath);
        if (!IsRegularFile(rolePairsPath) || !IsRegularFile(schema10FixturePath))
        {
            throw new PackageIndexException("The checked-in disposable preflight fixtures are missing or unsafe.");
        }

        var rolePairsBytes = await File.ReadAllBytesAsync(rolePairsPath, cancellationToken);
        var schema10Bytes = await File.ReadAllBytesAsync(schema10FixturePath, cancellationToken);
        using var roleDocument = JsonDocument.Parse(rolePairsBytes, new JsonDocumentOptions { MaxDepth = 8 });
        var roleRoot = roleDocument.RootElement;
        if (!roleRoot.TryGetProperty("version", out var version) || version.GetInt32() != 1
            || !roleRoot.TryGetProperty("pairs", out var pairs) || pairs.ValueKind != JsonValueKind.Array
            || pairs.GetArrayLength() != 2)
        {
            throw new PackageIndexException("The disposable preflight role fixture must be version 1 with exactly two role pairs.");
        }

        var roles = new List<string>(2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var expectedProfiles = new[] { "full", "work_only" };
        var index = 0;
        foreach (var pair in pairs.EnumerateArray())
        {
            var runtime = RequireStringProperty(pair, "runtime");
            _ = RequireStringProperty(pair, "dispatcher");
            if (!pair.TryGetProperty("dispatcher_profile", out var profile)
                || profile.ValueKind != JsonValueKind.String || profile.GetString() != expectedProfiles[index++]
                || !seen.Add(runtime))
            {
                throw new PackageIndexException("The disposable preflight role fixture has invalid or duplicate role identities.");
            }

            roles.Add(runtime);
        }

        var pairEntries = pairs.EnumerateArray().ToArray();
        var onePair = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            pairs = new[]
            {
                new
                {
                    dispatcher = RequireStringProperty(pairEntries[0], "dispatcher"),
                    runtime = RequireStringProperty(pairEntries[0], "runtime"),
                    dispatcher_profile = "full"
                }
            }
        });
        return new DurablePreflightExpectedFixture(
            Convert.ToHexStringLower(SHA256.HashData(rolePairsBytes)),
            Convert.ToHexStringLower(SHA256.HashData(schema10Bytes)),
            Convert.ToHexStringLower(SHA256.HashData(onePair)),
            roles,
            OwnerRole,
            StoreId,
            ActiveEpoch);
    }

    private async Task RunControllerAsync(
        DurablePreflightArtifactProofRequest request,
        string artifactDirectory,
        string artifactManifestPath,
        string receiptPath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var script = Path.Combine(request.RepositoryRoot, ProofScriptRelativePath);
        var consumer = Path.Combine(request.RepositoryRoot, ConsumerProjectRelativePath);
        if (!File.Exists(script) || !File.Exists(consumer))
        {
            throw new PackageIndexException("The PostgreSQL preflight artifact consumer entrypoint or project is missing.");
        }

        var result = await _commandRunner.RunAsync(
            new ExternalCommandRequest(
                "bash",
                [
                    script,
                    "--artifact-dir", artifactDirectory,
                    "--artifact-manifest", artifactManifestPath,
                    "--source-commit", request.SourceCommit,
                    "--run-id", request.RunId,
                    "--artifact-id", request.ArtifactId,
                    "--consumer-project", consumer,
                    "--receipt", receiptPath
                ],
                request.RepositoryRoot,
                "PostgreSQL preflight artifact proof",
                "running the exact-bundle disposable PostgreSQL scenarios",
                timeoutMilliseconds,
                new Dictionary<string, string?>
                {
                    ["CI"] = "true",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
                    ["DOTNET_NOLOGO"] = "1",
                    ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                    ["PREFLIGHT_ROLE_PAIRS_FILE"] = Path.Combine(request.RepositoryRoot, RolePairsRelativePath),
                    ["PREFLIGHT_MIGRATION_OWNER_ROLE"] = OwnerRole,
                    ["PREFLIGHT_STORE_ID"] = StoreId,
                    ["PREFLIGHT_ACTIVE_EPOCH"] = ActiveEpoch
                },
                ExternalCapturePolicy.ReleaseProof),
            cancellationToken);

        if (result.ExitCode != 0 || result.StandardOutputTruncated || result.StandardErrorTruncated
            || !result.StandardOutput.Split('\n').Any(line => string.Equals(line.TrimEnd('\r'), ProofSuccessMarker, StringComparison.Ordinal)))
        {
            throw new PackageIndexException("PostgreSQL preflight artifact proof failed, omitted its success marker, or returned incomplete output.");
        }
    }

    private static async Task<ValidatedDurablePreflightReceipt> ReadCompleteReceiptAsync(
        string receiptPath,
        DurablePreflightArtifactProofRequest request,
        DurablePreflightBundleIdentity expectedBundle,
        DurablePreflightExpectedFixture expectedFixture,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(receiptPath))
        {
            throw new PackageIndexException("The PostgreSQL preflight consumer exited without a complete receipt.");
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(receiptPath, cancellationToken);
            if (bytes.Length is 0 or > 2 * 1024 * 1024)
            {
                throw new PackageIndexException("The PostgreSQL preflight receipt is empty or exceeds its bounded size.");
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            RejectDuplicateProperties(root);
            RequireInteger(root, "schemaVersion", 1);
            RequireString(root, "proofKind", ProofKind);
            RequireString(root, "sourceCommit", request.SourceCommit);
            RequireString(root, "runId", request.RunId);
            RequireString(root, "artifactId", request.ArtifactId);
            RequireString(root, "packageVersion", expectedBundle.PackageVersion);
            RequireString(root, "artifactManifestSha256", expectedBundle.ManifestSha256);
            RequireString(root, "exactCliPackageSha256", expectedBundle.CliSha256);
            RequireString(root, "exactProviderPackageSha256", expectedBundle.ProviderSha256);
            RequireString(root, "extractedRoleRecipeSha256", expectedBundle.RecipeSha256);
            RequireString(root, "schema10FixtureSha256", expectedFixture.Schema10FixtureSha256);
            RequireString(root, "completeRoleManifestSha256", expectedFixture.RolePairsSha256);
            RequireString(root, "migrationOwnerRole", expectedFixture.OwnerRole);
            RequireString(root, "storeId", expectedFixture.StoreId);
            RequireString(root, "activeEpoch", expectedFixture.ActiveEpoch);
            RequireString(root, "postgresImage", PostgresImage);
            RequireString(root, "onePairManifestSha256", expectedFixture.OnePairManifestSha256);
            RequireSha256(root, "schema10BaselineSnapshotSha256");
            ValidatePackages(root, expectedBundle);
            RequireMigrationChecksums(root);
            var scenarioCount = ValidateScenarios(root, expectedFixture);
            ValidateRuntimeEvidence(root, expectedBundle, expectedFixture);
            ValidateGuardedCompletion(root, expectedFixture);
            ValidateGuardLossNegativeProof(root, expectedFixture);
            var totalDuration = RequirePositiveNumber(root, "totalDurationMilliseconds");
            ValidateStageDurations(root);

            if (root.TryGetProperty("limitations", out var limitations)
                && (limitations.ValueKind != JsonValueKind.Array || limitations.GetArrayLength() != 0))
            {
                throw new PackageIndexException("A receipt with limitations cannot satisfy the complete release proof gate.");
            }

            return new ValidatedDurablePreflightReceipt(scenarioCount, totalDuration);
        }
        catch (JsonException exception)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt is malformed or exceeds the bounded JSON shape.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt has malformed field types or structure.", exception);
        }
    }

    private static int ValidateScenarios(JsonElement root, DurablePreflightExpectedFixture fixture)
    {
        if (!root.TryGetProperty("scenarios", out var scenarios) || scenarios.ValueKind != JsonValueKind.Array)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt does not include the four required scenario outcomes.");
        }

        var expected = new Dictionary<string, (int RuntimeCount, bool Schema10, bool SourceDenials)>(StringComparer.Ordinal)
        {
            ["one-pair"] = (1, false, false),
            ["pair-enrollment"] = (2, false, true),
            ["identical-rerun-stable-catalog"] = (2, false, true),
            ["schema10-to-11-two-pair"] = (2, true, true)
        };
        foreach (var scenario in scenarios.EnumerateArray())
        {
            var name = RequireStringProperty(scenario, "name");
            if (!expected.TryGetValue(name, out var expectation)
                || RequireStringProperty(scenario, "ownerGuard") != "continuous-shared-session-lock"
                || !scenario.TryGetProperty("fixtureActivationCallbackCompleted", out var activated)
                || activated.ValueKind != JsonValueKind.True
                || RequirePositiveInteger(scenario, "runtimePreflightCount") != expectation.RuntimeCount
                || RequirePositiveInteger(scenario, "guardBackendPid") <= 0
                || RequireStringProperty(scenario, "ownerDiagnostic") != "separate-and-never-counted"
                || RequireStringProperty(scenario, "storeId") != fixture.StoreId
                || RequireStringProperty(scenario, "activeEpoch") != fixture.ActiveEpoch
                || RequireStringProperty(scenario, "laneEvidence") != LaneSummary(expectation.SourceDenials)
                || !scenario.TryGetProperty("modeledSchema10BaselinePresent", out var schema10)
                || schema10.ValueKind != (expectation.Schema10 ? JsonValueKind.True : JsonValueKind.False))
            {
                throw new PackageIndexException("The PostgreSQL preflight receipt contains a missing, duplicate, or failed scenario.");
            }

            ValidateLaneEvidence(scenario, name, fixture, expectation.SourceDenials, "laneEvidenceBefore");
            ValidateLaneEvidence(scenario, name, fixture, expectation.SourceDenials, "laneEvidenceAfter");
            expected.Remove(name);
        }

        if (expected.Count != 0 || scenarios.GetArrayLength() != 4)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt is missing one or more required scenarios.");
        }

        if (!root.TryGetProperty("guardWindow", out var guardWindow) || guardWindow.ValueKind != JsonValueKind.Object
            || !IsTrue(guardWindow, "intact")
            || !guardWindow.TryGetProperty("backendPids", out var backendPids) || backendPids.ValueKind != JsonValueKind.Array
            || backendPids.GetArrayLength() != 4
            || backendPids.EnumerateArray().Any(static pid => pid.ValueKind != JsonValueKind.Number
                || !pid.TryGetInt32(out var value) || value <= 0)
            || !IsTrue(root, "activationCallbackCompleted"))
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt does not prove an intact guard window and completed activations.");
        }

        return scenarios.GetArrayLength();
    }

    private static void ValidateRuntimeEvidence(
        JsonElement root,
        DurablePreflightBundleIdentity bundle,
        DurablePreflightExpectedFixture fixture)
    {
        if (!root.TryGetProperty("cliResults", out var results) || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt contains no runtime credential evidence.");
        }

        var expectedRoles = fixture.RuntimeRoles;
        var runtimeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var ownerDiagnosticCounts = new Dictionary<int, int>();
        var onePairHash = fixture.OnePairManifestSha256;
        if (!root.TryGetProperty("queuedWriter", out var writerEvidence)
            || RequireStringProperty(writerEvidence, "outcome") is not ("completed-before-writer" or "bounded-failure-then-rerun"))
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt does not identify the queued-writer completion branch.");
        }

        var writerScenario = RequireStringProperty(writerEvidence, "authoritativeScenario");
        var expectedWriterScenario = RequireStringProperty(writerEvidence, "outcome") == "completed-before-writer"
            ? "queued-writer-prewriter-complete"
            : "queued-writer-postwriter-rerun";
        if (writerScenario != expectedWriterScenario)
        {
            throw new PackageIndexException("The queued-writer authoritative scenario does not match its completion branch.");
        }

        var expectedResults = new List<(string Scenario, string Caller, int? Pair, string Role, int Count, string Hash)>
        {
            ("one-pair", "runtime", 1, fixture.RuntimeRoles[0], 1, onePairHash),
            ("one-pair", "owner-diagnostic", null, fixture.OwnerRole, 1, onePairHash)
        };
        foreach (var scenario in new[] { "pair-enrollment", "identical-rerun-stable-catalog", "schema10-to-11-two-pair" })
        {
            expectedResults.Add((scenario, "runtime", 1, fixture.RuntimeRoles[0], 2, fixture.RolePairsSha256));
            expectedResults.Add((scenario, "runtime", 2, fixture.RuntimeRoles[1], 2, fixture.RolePairsSha256));
            expectedResults.Add((scenario, "owner-diagnostic", null, fixture.OwnerRole, 2, fixture.RolePairsSha256));
        }

        expectedResults.Add((writerScenario, "runtime", 1, fixture.RuntimeRoles[0], 2, fixture.RolePairsSha256));
        expectedResults.Add((writerScenario, "runtime", 2, fixture.RuntimeRoles[1], 2, fixture.RolePairsSha256));
        expectedResults.Add((writerScenario, "owner-diagnostic", null, fixture.OwnerRole, 2, fixture.RolePairsSha256));
        if (results.GetArrayLength() != expectedResults.Count)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt has an incomplete per-scenario CLI result sequence.");
        }

        var resultIndex = 0;
        foreach (var result in results.EnumerateArray())
        {
            RejectDuplicateProperties(result);
            var expected = expectedResults[resultIndex++];
            RequireString(result, "Scenario", expected.Scenario);
            var caller = RequireStringProperty(result, "Caller");
            var role = RequireStringProperty(result, "Role");
            RequireString(result, "Owner", fixture.OwnerRole);
            var runtimeCount = RequirePositiveInteger(result, "RuntimeCount");
            RequireString(result, "StoreId", fixture.StoreId);
            RequireString(result, "ActiveEpoch", fixture.ActiveEpoch);
            _ = RequireNonNegativeNumber(result, "ElapsedMilliseconds");
            RequireString(result, "ManifestSha256", expected.Hash);
            if (caller != expected.Caller || role != expected.Role || runtimeCount != expected.Count
                || (result.GetProperty("Pair").ValueKind == JsonValueKind.Null ? null : result.GetProperty("Pair").GetInt32()) != expected.Pair)
            {
                throw new PackageIndexException("The PostgreSQL preflight receipt CLI result is out of expected scenario order or identity.");
            }

            if (caller == "owner-diagnostic")
            {
                ownerDiagnosticCounts[runtimeCount] = ownerDiagnosticCounts.GetValueOrDefault(runtimeCount) + 1;
                if (result.GetProperty("Pair").ValueKind != JsonValueKind.Null || runtimeCount is not (1 or 2))
                {
                    throw new PackageIndexException("Owner-diagnostic evidence cannot identify a runtime pair.");
                }

                continue;
            }

            var pair = RequirePositiveInteger(result, "Pair");
            var expectedRole = pair is >= 1 and <= 2 ? expectedRoles[pair - 1] : null;
            if (caller != "runtime" || expectedRole is null || role != expectedRole || runtimeCount is not (1 or 2)
                || (pair == 1 && runtimeCount == 1 && !string.Equals(RequireStringProperty(result, "ManifestSha256"), onePairHash, StringComparison.Ordinal))
                || (runtimeCount == 2 && !string.Equals(RequireStringProperty(result, "ManifestSha256"), fixture.RolePairsSha256, StringComparison.Ordinal))
                || (pair == 2 && runtimeCount != 2))
            {
                throw new PackageIndexException("The PostgreSQL preflight receipt contains invalid runtime role/pair bindings.");
            }

            var identity = $"{pair}:{runtimeCount}";
            runtimeCounts[identity] = runtimeCounts.GetValueOrDefault(identity) + 1;
        }

        if (ownerDiagnosticCounts.GetValueOrDefault(1) != 1 || ownerDiagnosticCounts.GetValueOrDefault(2) != 4
            || runtimeCounts.GetValueOrDefault("1:1") != 1 || runtimeCounts.GetValueOrDefault("1:2") != 4
            || runtimeCounts.GetValueOrDefault("2:2") != 4 || results.GetArrayLength() != 14)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt is missing or duplicates expected per-scenario runtime/owner evidence.");
        }
    }

    private static void ValidateGuardedCompletion(JsonElement root, DurablePreflightExpectedFixture fixture)
    {
        if (!root.TryGetProperty("queuedWriter", out var writer) || writer.ValueKind != JsonValueKind.Object
            || RequireStringProperty(writer, "outcome") is not ("completed-before-writer" or "bounded-failure-then-rerun")
            || RequireStringProperty(writer, "authoritativeScenario") != (RequireStringProperty(writer, "outcome") == "completed-before-writer"
                ? "queued-writer-prewriter-complete" : "queued-writer-postwriter-rerun")
            || !writer.TryGetProperty("queuedWriterObserved", out var observed) || observed.ValueKind != JsonValueKind.True
            || !writer.TryGetProperty("writerFinished", out var finished) || finished.ValueKind != JsonValueKind.True
            || !writer.TryGetProperty("authoritativeWindow", out var window) || window.ValueKind != JsonValueKind.Object
            || RequireStringProperty(window, "scenario") != RequireStringProperty(writer, "authoritativeScenario")
            || RequireStringProperty(window, "ownerGuard") != "continuous-shared-session-lock"
            || !IsTrue(window, "fixtureActivationCallbackCompleted")
            || RequireStringProperty(window, "laneEvidence") != LaneSummary(sourceDenials: true)
            || RequirePositiveInteger(window, "runtimePreflightCount") != 2
            || RequirePositiveInteger(window, "guardBackendPid") <= 0
            || RequireStringProperty(window, "storeId") != fixture.StoreId
            || RequireStringProperty(window, "activeEpoch") != fixture.ActiveEpoch
            || RequireStringProperty(window, "ownerDiagnostic") != "separate-and-never-counted"
            || !window.TryGetProperty("modeledSchema10BaselinePresent", out var schema10) || schema10.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("catalogSnapshotSha256BeforeAndAfterIdenticalRerun", out var catalog) || catalog.ValueKind != JsonValueKind.Array
            || catalog.GetArrayLength() != 2 || catalog[0].GetString() != catalog[1].GetString()
            || !IsSha256(catalog[0].GetString()))
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt does not prove guarded lane and writer-race completion.");
        }

        if (RequireStringProperty(writer, "outcome") == "completed-before-writer")
        {
            if (!IsTrue(writer, "authoritativeWindowCompletedBeforeWriter")
                || !IsTrue(writer, "writerAcquiredAfterWindow")
                || (writer.TryGetProperty("boundedFailure", out var notFailed) && notFailed.ValueKind != JsonValueKind.Null))
            {
                throw new PackageIndexException("The queued-writer pre-completion branch is incomplete or contradictory.");
            }
        }
        else if (!writer.TryGetProperty("boundedFailure", out var boundedFailure)
            || boundedFailure.ValueKind != JsonValueKind.Object
            || RequireStringProperty(boundedFailure, "failureKind") is not ("runtime-child" or "lane-proof" or "fixture-activation" or "guard-loss" or "deadline" or "cleanup")
            || string.IsNullOrWhiteSpace(RequireStringProperty(boundedFailure, "stage"))
            || RequirePositiveNumber(boundedFailure, "durationMilliseconds") > 20_000
            || !IsTrue(writer, "writerReleasedAfterFailure")
            || !IsTrue(writer, "writerFinishedBeforeRerun"))
        {
            throw new PackageIndexException("The queued-writer recovery branch lacks bounded-failure, release, or finish evidence.");
        }

        ValidateLaneEvidence(window, RequireStringProperty(writer, "authoritativeScenario"), fixture, sourceDenials: true, propertyName: "laneEvidenceBefore");
        ValidateLaneEvidence(window, RequireStringProperty(writer, "authoritativeScenario"), fixture, sourceDenials: true, propertyName: "laneEvidenceAfter");
    }

    private static string LaneSummary(bool sourceDenials)
        => sourceDenials
            ? "forwarder-work-flow-schedule-and-source-work-only-denials-passed"
            : "forwarder-work-flow-schedule-passed";

    /// <summary>
    /// Requires independently observed failed lane and activation windows. A successful scenario receipt
    /// cannot replace backend termination, cancellation, drained children/host, closed admission,
    /// released sessions, and the withheld receipts from the controller's real guard-loss probes.
    /// </summary>
    private static void ValidateGuardLossNegativeProof(JsonElement root, DurablePreflightExpectedFixture fixture)
    {
        if (!root.TryGetProperty("guardLossNegativeProof", out var evidence) || evidence.ValueKind != JsonValueKind.Object)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt is missing its actual guard-loss negative evidence.");
        }

        foreach (var propertyName in new[]
        {
            "lanePassBackendTerminated", "lanePassCancellationObserved", "lanePassChildDrained", "lanePassReceiptWithheld",
            "activationBackendTerminated", "activationCancellationObserved", "activationHostDrainPersisted",
            "activationAdmissionClosed", "activationSessionsReleased", "activationReceiptWithheld",
            "lanePassStarted", "lanePassObservedExecuting", "activationWorkInvocationStartedBeforeCompletion",
            "activationDrainVerifiedCheckpointObserved", "activationHostedServicesStopped", "activationChildDrained",
            "activationIdentityUnchanged", "finalReceiptAbsentBeforeAndAfter"
        })
        {
            if (!IsTrue(evidence, propertyName))
            {
                throw new PackageIndexException($"The PostgreSQL preflight guard-loss proof lacks observed '{propertyName}' evidence.");
            }
        }

        var laneGuardBackendPid = RequirePositiveInteger(evidence, "lanePassBackendPid");
        var executingBackendPid = RequirePositiveInteger(evidence, "lanePassExecutingBackendPid");
        if (laneGuardBackendPid == executingBackendPid)
        {
            throw new PackageIndexException("The PostgreSQL preflight guard-loss proof must bind a distinct lanePassExecutingBackendPid.");
        }

        _ = RequirePositiveInteger(evidence, "activationBackendPid");
        RequireString(evidence, "lanePassStoreId", fixture.StoreId);
        RequireString(evidence, "lanePassActiveEpoch", fixture.ActiveEpoch);
        RequireString(evidence, "activationStoreId", fixture.StoreId);
        RequireString(evidence, "activationActiveEpoch", fixture.ActiveEpoch);
    }

    private static void ValidateLaneEvidence(
        JsonElement scenario,
        string expectedScenario,
        DurablePreflightExpectedFixture fixture,
        bool sourceDenials,
        string propertyName)
    {
        if (!scenario.TryGetProperty(propertyName, out var evidence) || evidence.ValueKind != JsonValueKind.Object
            || RequireStringProperty(evidence, "scenario") != expectedScenario
            || RequireStringProperty(evidence, "storeId") != fixture.StoreId
            || RequireStringProperty(evidence, "activeEpoch") != fixture.ActiveEpoch
            || RequirePositiveInteger(evidence, "guardBackendPid") <= 0
            || !evidence.TryGetProperty("forwarder", out var forwarder) || forwarder.ValueKind != JsonValueKind.Object
            || RequireStringProperty(forwarder, "runtimeRole") != fixture.RuntimeRoles[0]
            || RequireStringProperty(forwarder, "workResult") != "completed"
            || RequireStringProperty(forwarder, "flowResult") != "completed"
            || RequireStringProperty(forwarder, "scheduleResult") != "completed")
        {
            throw new PackageIndexException($"The PostgreSQL preflight receipt lacks bound {propertyName} forwarder evidence for '{expectedScenario}'.");
        }

        if (sourceDenials)
        {
            if (!evidence.TryGetProperty("sourceWorkOnly", out var source) || source.ValueKind != JsonValueKind.Object
                || RequireStringProperty(source, "runtimeRole") != fixture.RuntimeRoles[1]
                || RequireStringProperty(source, "workResult") != "completed"
                || RequireStringProperty(source, "flowResult") != "denied"
                || RequireStringProperty(source, "scheduleResult") != "denied"
                || RequireStringProperty(source, "allResult") != "denied")
            {
                throw new PackageIndexException($"The PostgreSQL preflight receipt lacks source work-only denial evidence for '{expectedScenario}'.");
            }
        }
        else if (evidence.TryGetProperty("sourceWorkOnly", out var absentSource) && absentSource.ValueKind != JsonValueKind.Null)
        {
            throw new PackageIndexException($"The one-pair scenario cannot claim source-role denial evidence that it did not install.");
        }
    }

    private static bool IsTrue(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool BundleIdentityMatches(
        DurablePreflightBundleIdentity expected,
        DurablePreflightBundleIdentity actual)
        => expected.ManifestSha256 == actual.ManifestSha256
            && expected.ManifestSha512 == actual.ManifestSha512
            && expected.PackageVersion == actual.PackageVersion
            && expected.CliSha256 == actual.CliSha256
            && expected.ProviderSha256 == actual.ProviderSha256
            && expected.RecipeSha256 == actual.RecipeSha256
            && expected.PackageCount == actual.PackageCount
            && expected.PackageSha256.Count == actual.PackageSha256.Count
            && expected.PackageSha256.All(package => actual.PackageSha256.TryGetValue(package.Key, out var hash)
                && string.Equals(package.Value, hash, StringComparison.Ordinal));

    private static void ValidateStageDurations(JsonElement root)
    {
        if (!root.TryGetProperty("stageDurationsMilliseconds", out var durations)
            || durations.ValueKind != JsonValueKind.Object
            || !durations.EnumerateObject().Any())
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt is missing stage duration evidence.");
        }

        foreach (var property in durations.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetDouble(out var value)
                || !double.IsFinite(value)
                || value < 0)
            {
                throw new PackageIndexException("The PostgreSQL preflight receipt contains invalid stage duration evidence.");
            }
        }

        var requiredStages = new[]
        {
            "one_pair_bootstrap_ms", "pair_enrollment_recipe_ms", "identical_rerun_ms",
            "schema10_to_11_upgrade_ms", "queued_writer_fresh_rerun_ms"
        };
        if (requiredStages.Any(stage => !durations.TryGetProperty(stage, out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var milliseconds)
            || !double.IsFinite(milliseconds) || milliseconds <= 0))
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt omits one or more required stage timings.");
        }
    }

    private static void RequireMigrationChecksums(JsonElement root)
    {
        if (!root.TryGetProperty("migrationChecksums", out var checksums)
            || checksums.ValueKind != JsonValueKind.Array || checksums.GetArrayLength() == 0)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt is missing normalized migration checksums.");
        }

        if (checksums.GetArrayLength() != 10)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt must retain all ten normalized schema-10 migration checksums.");
        }

        var expectedVersion = 1;
        foreach (var checksum in checksums.EnumerateArray())
        {
            if (checksum.ValueKind != JsonValueKind.Object
                || !checksum.TryGetProperty("sha256", out var hash)
                || hash.ValueKind != JsonValueKind.String || !IsSha256(hash.GetString())
                || !checksum.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber) || versionNumber != expectedVersion++
                || !checksum.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
            {
                throw new PackageIndexException("The PostgreSQL preflight receipt contains an invalid migration checksum.");
            }
        }
    }

    private static void ValidatePackages(JsonElement root, DurablePreflightBundleIdentity expectedBundle)
    {
        if (!root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array
            || packages.GetArrayLength() != expectedBundle.PackageCount)
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt package set does not match the frozen bundle.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages.EnumerateArray())
        {
            var id = RequireStringProperty(package, "packageId");
            var version = RequireStringProperty(package, "version");
            var hash = RequireStringProperty(package, "sha256");
            if (!seen.Add(id) || version != expectedBundle.PackageVersion || !IsSha256(hash)
                || !expectedBundle.PackageSha256.TryGetValue(id, out var expectedHash)
                || !string.Equals(hash, expectedHash, StringComparison.Ordinal))
            {
                throw new PackageIndexException("The PostgreSQL preflight receipt contains invalid package identities or hashes.");
            }
        }

        if (!seen.Contains(CliPackageId) || !seen.Contains(ProviderPackageId))
        {
            throw new PackageIndexException("The PostgreSQL preflight receipt omits a required proof package.");
        }
    }

    private static double RequirePositiveNumber(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number <= 0)
        {
            throw new PackageIndexException($"The PostgreSQL preflight receipt is missing valid '{propertyName}' evidence.");
        }

        return number;
    }

    private static double RequireNonNegativeNumber(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0)
        {
            throw new PackageIndexException($"The PostgreSQL preflight receipt is missing valid '{propertyName}' evidence.");
        }

        return number;
    }

    private static void RequireInteger(JsonElement root, string propertyName, int expected)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var actual) || actual != expected)
        {
            throw new PackageIndexException($"The PostgreSQL preflight receipt does not match expected '{propertyName}'.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new PackageIndexException($"The PostgreSQL preflight receipt contains duplicate property '{property.Name}'.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static int RequirePositiveInteger(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number) || number <= 0)
        {
            throw new PackageIndexException($"The PostgreSQL preflight receipt is missing valid '{propertyName}' evidence.");
        }

        return number;
    }

    private static string RequireStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new PackageIndexException($"The PostgreSQL preflight receipt is missing valid '{propertyName}' evidence.");
        }

        return value.GetString()!;
    }

    private static void RequireString(JsonElement element, string propertyName, string expected)
    {
        if (!string.Equals(RequireStringProperty(element, propertyName), expected, StringComparison.Ordinal))
        {
            throw new PackageIndexException($"The PostgreSQL preflight receipt does not match expected '{propertyName}'.");
        }
    }

    private static void RequireSha256(JsonElement element, string propertyName)
    {
        if (!IsSha256(RequireStringProperty(element, propertyName)))
        {
            throw new PackageIndexException($"The PostgreSQL preflight receipt contains an invalid '{propertyName}'.");
        }
    }

    private static void RequireNonEmptyGuid(JsonElement element, string propertyName)
    {
        if (!Guid.TryParseExact(RequireStringProperty(element, propertyName), "D", out var value) || value == Guid.Empty)
        {
            throw new PackageIndexException($"The PostgreSQL preflight receipt contains an invalid '{propertyName}'.");
        }
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit) && value == value.ToLowerInvariant();

    private static void ValidateRequest(DurablePreflightArtifactProofRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArtifactDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArtifactManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApprovedManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReceiptPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArtifactId);
        if (!Directory.Exists(request.RepositoryRoot) || !Directory.Exists(request.ArtifactDirectory)
            || !File.Exists(request.ArtifactManifestPath)
            || !Regex.IsMatch(request.SourceCommit, "\\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\\z", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(request.RunId, "\\A[A-Za-z0-9._-]{1,100}\\z", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(request.ArtifactId, "\\A[A-Za-z0-9._-]{1,160}\\z", RegexOptions.CultureInvariant))
        {
            throw new PackageIndexException("Preflight proof inputs are missing or have invalid safe identities.");
        }

    }

    private static string ResolveRestoredPackagePath(string packagesRoot, PackageArtifactManifestEntry entry, string version)
    {
        var id = entry.PackageId.ToLowerInvariant();
        var normalizedVersion = version.ToLowerInvariant();
        return Path.Combine(packagesRoot, id, normalizedVersion, $"{id}.{normalizedVersion}.nupkg");
    }

    private static async Task<bool> FilesAreByteIdenticalAsync(string left, string right, CancellationToken cancellationToken)
    {
        const int BufferSize = 64 * 1024;
        await using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (leftStream.Length != rightStream.Length)
        {
            return false;
        }

        var leftBuffer = new byte[BufferSize];
        var rightBuffer = new byte[BufferSize];
        while (true)
        {
            var leftRead = await leftStream.ReadAsync(leftBuffer, cancellationToken);
            var rightRead = await rightStream.ReadAsync(rightBuffer, cancellationToken);
            if (leftRead != rightRead || !leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }
        }
    }

    private static async Task<string> ComputePackagedRecipeSha256Async(string packagePath, CancellationToken cancellationToken)
        => Convert.ToHexStringLower(SHA256.HashData(await ReadPackagedRecipeAsync(packagePath, cancellationToken)));

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<byte[]> ReadPackagedRecipeAsync(string packagePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(packagePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var matches = archive.Entries.Where(entry => string.Equals(entry.FullName, ProviderRecipePath, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].Length > 4 * 1024 * 1024)
            {
                throw new PackageIndexException("The exact PostgreSQL provider archive must contain one bounded packaged role recipe.");
            }

            await using var recipeStream = matches[0].Open();
            using var output = new MemoryStream(checked((int)matches[0].Length));
            await recipeStream.CopyToAsync(output, cancellationToken);
            return output.ToArray();
        }
        catch (InvalidDataException exception)
        {
            throw new PackageIndexException("The exact PostgreSQL provider package is not a valid NuGet archive.", exception);
        }
    }

    private static bool IsRegularFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static string CreateTemporarySiblingPath(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return $"{fullPath}.{label}.{Guid.NewGuid():N}.tmp";
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void MoveReceipt(string source, string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new PackageIndexException("Preflight proof receipt destination already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.Move(source, destination);
    }

    private static async Task WritePublicFeedReceiptAsync(
        string path,
        DurablePreflightPublicFeedReceipt receipt,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, receipt, PackageArtifactJson.Options, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void DeleteOwnedDirectory(string path)
    {
        try
        {
            try
            {
                _deleteOwnedDirectory(path);
            }
            catch (DirectoryNotFoundException) { }

            // Directory.Exists suppresses access errors, so inspect attributes to distinguish absence
            // from an inaccessible or replaced path before accepting the cleanup as complete.
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            throw new PackageIndexException("The owned public-feed proof scratch path remained after checked cleanup.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PackageIndexException("The owned public-feed proof scratch cleanup failed; success receipts are withheld.", exception);
        }
    }
}

/// <summary>One exact candidate/public feed proof invocation.</summary>
internal sealed record DurablePreflightArtifactProofRequest(
    string RepositoryRoot,
    string ArtifactDirectory,
    string ArtifactManifestPath,
    string ApprovedManifestPath,
    string ReceiptPath,
    string SourceCommit,
    string RunId,
    string ArtifactId);

/// <summary>Validated package and manifest hashes observed before invoking the combined proof.</summary>
internal sealed record DurablePreflightBundleIdentity(
    string ManifestSha256,
    string ManifestSha512,
    string PackageVersion,
    string CliSha256,
    string ProviderSha256,
    string RecipeSha256,
    int PackageCount,
    IReadOnlyDictionary<string, string> PackageSha256);

/// <summary>Expected immutable fixture identities used by the controller and strict receipt validator.</summary>
internal sealed record DurablePreflightExpectedFixture(
    string RolePairsSha256,
    string Schema10FixtureSha256,
    string OnePairManifestSha256,
    IReadOnlyList<string> RuntimeRoles,
    string OwnerRole,
    string StoreId,
    string ActiveEpoch);

/// <summary>Validated final combined receipt summary returned by the exact-package controller.</summary>
internal sealed record ValidatedDurablePreflightReceipt(int ScenarioCount, double TotalDurationMilliseconds);

/// <summary>Carrier result for receipt rendering and workflow summaries.</summary>
internal sealed record DurablePreflightArtifactProofResult(
    string ArtifactManifestSha256,
    string ArtifactManifestSha512,
    string CliPackageSha256,
    string ProviderPackageSha256,
    string ExtractedRecipeSha256,
    double SharedProofMilliseconds,
    double TotalMilliseconds,
    int ScenarioCount);

/// <summary>Public-feed evidence that the restored package bytes matched the candidate bundle before proof.</summary>
internal sealed record DurablePreflightPublicFeedReceipt(
    int SchemaVersion,
    string ProofKind,
    string SourceCommit,
    string RunId,
    string ArtifactId,
    string CandidateReceiptSha256,
    string ArtifactManifestSha256,
    string ArtifactManifestSha512,
    IReadOnlyDictionary<string, string> PublicPackageSha256,
    string ExtractedRecipeSha256,
    double PublicRestoreMilliseconds,
    double SharedProofMilliseconds,
    double TotalMilliseconds,
    string SharedProofReceiptSha256);

/// <summary>Strict command-line request for candidate or published-feed preflight artifact consumption.</summary>
internal sealed record DurablePreflightArtifactProofCommandOptions(
    string Mode,
    DurablePreflightArtifactProofRequest Request,
    string? CandidateReceiptPath,
    string? RestoredPackagesPath,
    string? PublishedReceiptPath)
{
    internal static DurablePreflightArtifactProofCommandOptions Parse(string[] args, string currentDirectory)
    {
        string? mode = null;
        string? repositoryRoot = null;
        string? artifactDirectory = null;
        string? artifactManifest = null;
        string? approvedManifest = null;
        string? receipt = null;
        string? sourceCommit = null;
        string? runId = null;
        string? artifactId = null;
        string? candidateReceipt = null;
        string? restoredPackages = null;
        string? publishedReceipt = null;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new PackageIndexException($"Option '{option}' requires a value.");
            }

            var value = args[++index];
            switch (option)
            {
                case "--mode": mode = value; break;
                case "--repo-root": repositoryRoot = value; break;
                case "--artifact-dir": artifactDirectory = value; break;
                case "--artifact-manifest": artifactManifest = value; break;
                case "--approved-manifest": approvedManifest = value; break;
                case "--receipt": receipt = value; break;
                case "--source-commit": sourceCommit = value; break;
                case "--run-id": runId = value; break;
                case "--artifact-id": artifactId = value; break;
                case "--candidate-receipt": candidateReceipt = value; break;
                case "--restored-packages": restoredPackages = value; break;
                case "--published-receipt": publishedReceipt = value; break;
                default: throw new PackageIndexException($"Unknown preflight artifact proof option '{option}'.");
            }
        }

        if (mode is not ("candidate" or "promote" or "published"))
        {
            throw new PackageIndexException("verify-preflight-artifact requires --mode candidate, promote, or published.");
        }

        var root = Path.GetFullPath(repositoryRoot ?? currentDirectory);
        string Resolve(string value) => Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
        var request = new DurablePreflightArtifactProofRequest(
            root,
            Resolve(Require(artifactDirectory, "--artifact-dir")),
            Resolve(Require(artifactManifest, "--artifact-manifest")),
            Resolve(Require(approvedManifest, "--approved-manifest")),
            Resolve(Require(receipt, "--receipt")),
            Require(sourceCommit, "--source-commit"),
            Require(runId, "--run-id"),
            Require(artifactId, "--artifact-id"));

        if (mode == "published" && (string.IsNullOrWhiteSpace(candidateReceipt)
            || string.IsNullOrWhiteSpace(restoredPackages)
            || string.IsNullOrWhiteSpace(publishedReceipt)))
        {
            throw new PackageIndexException(
                "Published preflight proof requires --candidate-receipt, --restored-packages, and --published-receipt.");
        }

        return new DurablePreflightArtifactProofCommandOptions(
            mode,
            request,
            candidateReceipt is null ? null : Resolve(candidateReceipt),
            restoredPackages is null ? null : Resolve(restoredPackages),
            publishedReceipt is null ? null : Resolve(publishedReceipt));
    }

    private static string Require(string? value, string option)
        => string.IsNullOrWhiteSpace(value) ? throw new PackageIndexException($"verify-preflight-artifact requires '{option}'.") : value;
}
