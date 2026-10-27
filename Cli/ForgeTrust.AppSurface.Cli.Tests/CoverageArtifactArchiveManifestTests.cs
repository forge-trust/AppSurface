using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactArchiveManifestTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void WriteManifest_AndReadManifest_RoundTripACompleteManifestAndKeepStreamsOpen()
    {
        var completePath = string.Join('/', Enumerable.Repeat(new string('p', 240), 17));
        Assert.Equal(4096, completePath.Length);
        var valid = ValidManifest();
        var expected = valid with
        {
            Provenance = new(new string('r', 128), new string('s', 128), new string('c', 128)),
            Files = [valid.Files[0] with { Path = completePath }, valid.Files[1]]
        };
        using var stream = new MemoryStream();

        CoverageArtifactArchive.WriteManifest(stream, expected);

        Assert.True(stream.CanWrite);
        stream.Position = 0;
        var actual = CoverageArtifactArchive.ReadManifest(stream);
        Assert.True(stream.CanRead);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.OutputIdentity, actual.OutputIdentity);
        Assert.Equal(expected.RepositoryIdentity, actual.RepositoryIdentity);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.ArchivedAtUtc, actual.ArchivedAtUtc);
        Assert.Equal(expected.Provenance, actual.Provenance);
        Assert.Equal(expected.Complete, actual.Complete);
        Assert.Equal(expected.Files, actual.Files);
    }

    [Theory]
    [InlineData("null-files-and-entries")]
    [InlineData("duplicate-paths-and-payloads")]
    [InlineData("non-ascii-payload-id")]
    [InlineData("invalid-manifest-fields")]
    [InlineData("incomplete-entry")]
    [InlineData("negative-and-excessive-lengths")]
    [InlineData("invalid-paths")]
    [InlineData("invalid-hashes")]
    [InlineData("oversized-provenance")]
    public void WriteManifest_AndReadManifest_RejectMutatedInvalidManifestsWithoutChangingSentinel(string invalidCase)
    {
        var valid = ValidManifest();
        var sentinel = Encoding.UTF8.GetBytes("existing manifest must remain intact");

        foreach (var (invalid, index) in Mutate(valid, invalidCase).Select((manifest, index) => (manifest, index)))
        {
            var mutation = $"{invalidCase} mutation {index}";
            using var destination = new MemoryStream();
            destination.Write(sentinel);
            destination.Position = 0;
            AssertManifestRejected(() => CoverageArtifactArchive.WriteManifest(destination, invalid), mutation);
            Assert.Equal(sentinel, destination.ToArray());
            Assert.Equal(0, destination.Position);

            using var source = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(invalid, SerializerOptions));
            AssertManifestRejected(() => CoverageArtifactArchive.ReadManifest(source), mutation);
            Assert.True(source.CanRead);
        }
    }

    [Fact]
    public void ReadManifest_RejectsDuplicateKeysUnknownFieldsAndMalformedJson()
    {
        using var valid = new MemoryStream();
        CoverageArtifactArchive.WriteManifest(valid, ValidManifest());
        var json = Encoding.UTF8.GetString(valid.ToArray());

        var duplicateKey = json.Insert(1, "\"runId\":\"00000000000000000000000000000000\",");
        var unknownField = json.Insert(1, "\"unexpected\":true,");
        foreach (var invalidJson in new[] { duplicateKey, unknownField, "{\"schemaVersion\":1" })
        {
            using var source = new MemoryStream(Encoding.UTF8.GetBytes(invalidJson));
            AssertManifestRejected(() => CoverageArtifactArchive.ReadManifest(source), invalidJson);
            Assert.True(source.CanRead);
        }
    }

    private static void AssertManifestRejected(Action action, string scenario)
    {
        var exception = Record.Exception(action);
        Assert.True(exception is InvalidDataException,
            $"Expected manifest validation to reject {scenario} with InvalidDataException, but received {exception?.GetType().Name ?? "no exception"}.");
    }

    private static CoverageArtifactRunManifest ValidManifest()
    {
        var files = new[]
        {
            new CoverageArtifactFileEntry("résumé/report.xml", "00000001", 7, Hash("original-1"), 11, Hash("stored-1"), true),
            new CoverageArtifactFileEntry("summary.json", "00000002", 3, Hash("original-2"), 9, Hash("stored-2"), true)
        };

        return new CoverageArtifactRunManifest
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid().ToString("N"),
            OutputIdentity = "output-id",
            RepositoryIdentity = "repository-id",
            Version = "test-version",
            Outcome = "success",
            CreatedAtUtc = DateTimeOffset.Parse("2026-10-21T12:00:00Z"),
            ArchivedAtUtc = DateTimeOffset.Parse("2026-10-21T12:05:00Z"),
            Provenance = new("revision", "clean", "configuration"),
            Files = files,
            Complete = true
        };
    }

    private static IReadOnlyList<CoverageArtifactRunManifest> Mutate(CoverageArtifactRunManifest valid, string invalidCase)
    {
        var first = valid.Files[0];
        var second = valid.Files[1];
        return invalidCase switch
        {
            "null-files-and-entries" =>
            [
                valid with { Files = null! },
                valid with { Files = [null!] },
                valid with { Files = [first with { PayloadId = null! }] }
            ],
            "duplicate-paths-and-payloads" =>
            [
                valid with { Files = [first, second with { Path = first.Path }] },
                valid with { Files = [first, second with { Path = "RÉSUMÉ/report.xml" }] },
                valid with { Files = [first, second with { Path = "re\u0301sumé/report.xml" }] },
                valid with { Files = [first, second with { PayloadId = first.PayloadId }] },
                valid with { Files = [first with { Path = "reports" }, second with { Path = "reports/summary.json" }] }
            ],
            "non-ascii-payload-id" =>
            [
                valid with { Files = [first with { PayloadId = "１２３４５６７８" }, second] },
                valid with { Files = [first with { PayloadId = "0000001" }, second] },
                valid with { Files = [first with { PayloadId = "0000000x" }, second] }
            ],
            "invalid-manifest-fields" =>
            [
                valid with { SchemaVersion = 2 },
                valid with { Complete = false },
                valid with { RunId = "not-a-guid" },
                valid with { Outcome = "passed" },
                valid with { OutputIdentity = string.Empty },
                valid with { OutputIdentity = new string('o', 257) },
                valid with { RepositoryIdentity = string.Empty },
                valid with { RepositoryIdentity = new string('r', 257) },
                valid with { Version = null! },
                valid with { Version = new string('v', 257) }
            ],
            "incomplete-entry" => [valid with { Files = [first with { Complete = false }, second] }],
            "negative-and-excessive-lengths" =>
            [
                valid with { Files = [first with { OriginalLength = -1 }, second] },
                valid with { Files = [first with { StoredLength = -1 }, second] },
                valid with { Files = [first with { OriginalLength = CoverageArtifactLimits.ExplicitExpandedMaxBytes + 1 }, second] }
            ],
            "invalid-paths" =>
            [
                WithPath(valid, first, second, null!),
                WithPath(valid, first, second, string.Empty),
                WithPath(valid, first, second, "/rooted.txt"),
                WithPath(valid, first, second, "nested\\file.txt"),
                WithPath(valid, first, second, new string('p', 4097)),
                WithPath(valid, first, second, "../escape.txt"),
                WithPath(valid, first, second, string.Join('/', Enumerable.Repeat("part", 33)))
            ],
            "invalid-hashes" =>
            [
                valid with { Files = [first with { OriginalSha256 = null! }, second] },
                valid with { Files = [first with { OriginalSha256 = new string('a', 63) }, second] },
                valid with { Files = [first with { OriginalSha256 = new string('g', 64) }, second] },
                valid with { Files = [first with { StoredSha256 = null! }, second] },
                valid with { Files = [first with { StoredSha256 = new string('a', 63) }, second] },
                valid with { Files = [first with { StoredSha256 = new string('g', 64) }, second] }
            ],
            "oversized-provenance" =>
            [
                valid with { Provenance = new(new string('r', 129), "clean", "configuration") },
                valid with { Provenance = new("revision", new string('s', 129), "configuration") },
                valid with { Provenance = new("revision", "clean", new string('c', 129)) }
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase), invalidCase, "Unknown manifest mutation.")
        };
    }

    private static CoverageArtifactRunManifest WithPath(
        CoverageArtifactRunManifest manifest,
        CoverageArtifactFileEntry first,
        CoverageArtifactFileEntry second,
        string path) => manifest with { Files = [first with { Path = path }, second] };

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
