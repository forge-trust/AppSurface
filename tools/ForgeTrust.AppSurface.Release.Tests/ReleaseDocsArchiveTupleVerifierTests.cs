using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Release;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseDocsArchiveTupleVerifierTests
{
    [Fact]
    public async Task VerifyAcceptsArchivePlanAndSidecarForExpectedVersion()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();

        var verification = await ReleaseDocsArchiveTupleVerifier.VerifyAsync(
            fixture.Version,
            fixture.ArchivePath,
            fixture.PlanPath,
            fixture.SidecarPath,
            CancellationToken.None);

        Assert.Equal(fixture.Version.ToString(), verification.Version);
        Assert.Equal(fixture.Version.TagName, verification.Tag);
        Assert.Equal(fixture.AssetName, verification.ArchiveAssetName);
        Assert.Equal(fixture.ArchiveSha256, verification.ArchiveSha256);
        Assert.Equal(fixture.ArchiveBytes.Length, verification.ArchiveLengthBytes);
    }

    [Fact]
    public async Task VerifyRejectsArchiveMutationAfterPlanAndSidecarWereWritten()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        await File.WriteAllBytesAsync(fixture.ArchivePath, [.. fixture.ArchiveBytes, 0x42]);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Contains("archive bytes", exception.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRejectsPublicationPlanDigestMismatch()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync(planSha256: new string('a', 64));

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Contains("sidecar does not match", exception.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRejectsSidecarDigestOrFilenameMismatch()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync(sidecarText: $"{new string('a', 64)}  {fixture.AssetName}\n");

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Contains("sidecar does not match", exception.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRejectsPlanForAnotherVersionOrTag()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync(planVersion: "1.2.4", planTag: "v1.2.4");

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Contains("does not bind the expected version", exception.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRejectsArchiveWithUnexpectedVersionedFilename()
    {
        using var fixture = new TupleFixture(archiveFileName: "appsurface-docs-v1.2.4.tar.gz");
        await fixture.WriteValidTupleAsync();

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Contains("expected versioned filename", exception.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRejectsSymlinkedArchiveInput()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        var targetPath = Path.Join(fixture.RootPath, "archive-target.bin");
        await File.WriteAllBytesAsync(targetPath, fixture.ArchiveBytes);
        File.Delete(fixture.ArchivePath);
        File.CreateSymbolicLink(fixture.ArchivePath, targetPath);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Contains("contains a link", exception.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("sidecar")]
    public async Task VerifyRejectsSymlinkedMetadataInput(string input)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        var selectedPath = input == "plan" ? fixture.PlanPath : fixture.SidecarPath;
        var targetPath = Path.Join(fixture.RootPath, $"{input}-target");
        File.Move(selectedPath, targetPath);
        File.CreateSymbolicLink(selectedPath, targetPath);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Contains("contains a link", exception.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRejectsSymlinkedArchiveParentDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        var alias = Path.Join(Path.GetDirectoryName(fixture.RootPath)!, $"appsurface-docs-tuple-alias-{Guid.NewGuid():N}");
        File.CreateSymbolicLink(alias, fixture.RootPath);
        try
        {
            var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => ReleaseDocsArchiveTupleVerifier.VerifyAsync(
                fixture.Version,
                Path.Join(alias, fixture.AssetName),
                fixture.PlanPath,
                fixture.SidecarPath,
                CancellationToken.None));

            Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
            Assert.Contains("contains a link", exception.Diagnostic.Cause, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(alias);
        }
    }

    [Fact]
    public async Task VerifyRejectsOversizedArchiveBeforeHashing()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        await using (var archive = new FileStream(fixture.ArchivePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            archive.SetLength(ReleaseDocsArchiveTupleVerifier.MaximumArchiveBytes + 1);
        }

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Contains("exceeds", exception.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRejectsMalformedPlanJsonWithFixedDiagnostic()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        await File.WriteAllTextAsync(fixture.PlanPath, "{ malformed plan }");

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Equal("The publication plan is malformed or does not use the supported schema.", exception.Diagnostic.Cause);
        Assert.DoesNotContain(fixture.RootPath, exception.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRejectsDuplicatePlanDigestProperty()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        var plan = await File.ReadAllTextAsync(fixture.PlanPath);
        var digestProperty = $"\"archiveSha256\": \"{fixture.ArchiveSha256}\",";
        await File.WriteAllTextAsync(fixture.PlanPath, plan.Replace(
            digestProperty,
            $"{digestProperty}{Environment.NewLine}  \"archiveSha256\": \"{fixture.ArchiveSha256}\",",
            StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Equal("The publication plan is malformed or does not use the supported schema.", exception.Diagnostic.Cause);
    }

    [Fact]
    public async Task VerifyRejectsOversizedPlanAndSidecar()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        await File.WriteAllBytesAsync(fixture.PlanPath, new byte[ReleaseDocsArchiveTupleVerifier.MaximumPlanBytes + 1]);

        var planException = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());
        Assert.Equal("release-docs-archive-tuple-invalid", planException.Diagnostic.Code);
        Assert.Equal("A selected plan or sidecar is empty or exceeds its byte limit.", planException.Diagnostic.Cause);

        await fixture.WriteValidTupleAsync();
        await File.WriteAllBytesAsync(fixture.SidecarPath, new byte[ReleaseDocsArchiveTupleVerifier.MaximumSidecarBytes + 1]);

        var sidecarException = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());
        Assert.Equal("release-docs-archive-tuple-invalid", sidecarException.Diagnostic.Code);
        Assert.Equal("A selected plan or sidecar is empty or exceeds its byte limit.", sidecarException.Diagnostic.Cause);
    }

    [Fact]
    public async Task VerifyRejectsMissingPlanWithFixedIoDiagnostic()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        File.Delete(fixture.PlanPath);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => fixture.VerifyAsync());

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Equal("A selected tuple input could not be read safely.", exception.Diagnostic.Cause);
        Assert.DoesNotContain(fixture.RootPath, exception.Diagnostic.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRejectsCancellationBeforeReadingInputs()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReleaseDocsArchiveTupleVerifier.VerifyAsync(
            fixture.Version,
            fixture.ArchivePath,
            fixture.PlanPath,
            fixture.SidecarPath,
            cancellation.Token));
    }

    [Fact]
    public async Task VerifyRejectsNoncanonicalExpectedVersion()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();
        var invalidVersion = new SemVer(1, 2, 3, "invalid_label");

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => ReleaseDocsArchiveTupleVerifier.VerifyAsync(
            invalidVersion,
            fixture.ArchivePath,
            fixture.PlanPath,
            fixture.SidecarPath,
            CancellationToken.None));

        Assert.Equal("release-docs-archive-tuple-invalid", exception.Diagnostic.Code);
        Assert.Equal("The expected release version is not canonical SemVer.", exception.Diagnostic.Cause);
    }

    [Fact]
    public async Task CliVerifiesAValidHostSelectedTupleAndEmitsPathFreeJson()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();

        var result = await fixture.RunCliAsync(fixture.Version.ToString());

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Contains($"\"archiveSha256\": \"{fixture.ArchiveSha256}\"", result.Stdout, StringComparison.Ordinal);
        Assert.Contains($"\"version\": \"{fixture.Version}\"", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.RootPath, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRejectsInvalidVersionAndTupleWithBoundedPathFreeDiagnostics()
    {
        using var fixture = new TupleFixture();
        await fixture.WriteValidTupleAsync();

        var invalidVersion = await fixture.RunCliAsync("v" + fixture.Version);
        Assert.Equal(1, invalidVersion.ExitCode);
        Assert.Empty(invalidVersion.Stdout);
        Assert.Contains("release-docs-archive-tuple-arguments-invalid", invalidVersion.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.RootPath, invalidVersion.Stderr, StringComparison.Ordinal);

        var missingPlan = await fixture.RunCliAsync(
            fixture.Version.ToString(),
            planPath: Path.Join(fixture.RootPath, "missing-plan.json"));
        Assert.Equal(1, missingPlan.ExitCode);
        Assert.Empty(missingPlan.Stdout);
        Assert.Contains("release-docs-archive-tuple-invalid", missingPlan.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.RootPath, missingPlan.Stderr, StringComparison.Ordinal);
    }

    internal sealed class TupleFixture : IDisposable
    {
        private static readonly byte[] DefaultArchiveBytes = "deterministic docs archive bytes"u8.ToArray();

        internal TupleFixture(string? archiveFileName = null)
        {
            RootPath = Directory.CreateTempSubdirectory("appsurface-docs-tuple-").FullName;
            Version = SemVer.Parse("1.2.3-preview.1");
            AssetName = $"appsurface-docs-v{Version}.tar.gz";
            ArchivePath = TestPathUtils.PathUnder(RootPath, archiveFileName ?? AssetName);
            PlanPath = Path.Join(RootPath, "docs-publication-plan.json");
            SidecarPath = ArchivePath + ".sha256";
            ArchiveBytes = DefaultArchiveBytes;
            ArchiveSha256 = Convert.ToHexString(SHA256.HashData(ArchiveBytes)).ToLowerInvariant();
        }

        internal string RootPath { get; }

        internal SemVer Version { get; }

        internal string AssetName { get; }

        internal string ArchivePath { get; }

        internal string PlanPath { get; }

        internal string SidecarPath { get; }

        internal byte[] ArchiveBytes { get; }

        internal string ArchiveSha256 { get; }

        internal async Task WriteValidTupleAsync(
            string? planSha256 = null,
            string? sidecarText = null,
            string? planVersion = null,
            string? planTag = null)
        {
            await File.WriteAllBytesAsync(ArchivePath, ArchiveBytes);
            var plan = new
            {
                schema = "appsurface-docs-publication-plan-v1",
                version = planVersion ?? Version.ToString(),
                tag = planTag ?? Version.TagName,
                planPath = Path.Join(RootPath, "docs-publication-plan.json"),
                archiveAssetName = AssetName,
                archivePath = Path.Join(RootPath, AssetName),
                archiveSha256 = planSha256 ?? ArchiveSha256,
                sha256Path = Path.Join(RootPath, AssetName + ".sha256"),
                exactTreePath = $"releases/{Version}",
                releaseManifestSha256 = new string('b', 64),
                pagesStagingRoot = Path.Join(RootPath, "pages"),
                catalogPath = Path.Join(RootPath, "pages", "versions.json"),
                recommendedVersion = (string?)null,
                catalogEntry = new
                {
                    version = Version.ToString(),
                    label = Version.ToString(),
                    summary = $"AppSurface {Version}",
                    supportState = "Maintained",
                    visibility = "Public",
                    advisoryState = "None",
                    exactTreePath = $"releases/{Version}",
                    releaseManifestSha256 = new string('b', 64),
                },
                retryPolicy = new { draftAssetReplaceAllowed = true, publicAssetReplaceAllowed = false },
                recovery = new { summaryPath = Path.Join(RootPath, "summary.md") },
            };
            await File.WriteAllBytesAsync(PlanPath, JsonSerializer.SerializeToUtf8Bytes(plan, ReleaseJson.Options));
            await File.WriteAllTextAsync(
                SidecarPath,
                sidecarText ?? $"{ArchiveSha256}  {AssetName}{Environment.NewLine}");
        }

        internal Task<ReleaseDocsArchiveTupleVerification> VerifyAsync() =>
            ReleaseDocsArchiveTupleVerifier.VerifyAsync(Version, ArchivePath, PlanPath, SidecarPath, CancellationToken.None);

        internal async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(string version, string? planPath = null)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = await Program.RunAsync(
                [
                    "verify-docs-archive-tuple",
                    "--version", version,
                    "--archive", ArchivePath,
                    "--plan", planPath ?? PlanPath,
                    "--sha256", SidecarPath,
                ],
                stdout,
                stderr,
                RootPath);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }

        public void Dispose() => Directory.Delete(RootPath, recursive: true);
    }
}
