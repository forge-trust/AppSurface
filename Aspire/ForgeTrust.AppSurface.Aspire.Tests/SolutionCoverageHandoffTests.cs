using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class SolutionCoverageHandoffTests
{
    [Fact]
    public async Task ImportPreservesCollectionAndDiagnosticsForTheSameSourceAndAttempt()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync();

        await fixture.ImportAsync();

        Assert.Equal("<coverage />", await File.ReadAllTextAsync(Path.Join(fixture.Destination, "coverage.cobertura.xml")));
        Assert.Equal("{}", await File.ReadAllTextAsync(Path.Join(fixture.Destination, "timings.json")));
        Assert.Equal("diagnostic", await File.ReadAllTextAsync(Path.Join(fixture.Destination, "projects", "test.log")));
        Assert.True(File.Exists(Path.Join(fixture.Coverage, "collection-receipt.json")));
    }

    [Theory]
    [InlineData("Commit")]
    [InlineData("Tree")]
    [InlineData("Sdk")]
    [InlineData("Configuration")]
    [InlineData("SchemaVersion")]
    [InlineData("GITHUB_REPOSITORY")]
    [InlineData("GITHUB_RUN_ID")]
    [InlineData("GITHUB_RUN_ATTEMPT")]
    [InlineData("missing-run")]
    [InlineData("extra-run")]
    public async Task ImportRejectsMismatchedIdentityBeforeCopying(string field)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync();
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Receipt))!;
        if (field is "Commit" or "Tree" or "Sdk") receipt["Identity"]![field] = "different";
        else if (field == "Configuration") receipt[field] = "Debug";
        else if (field == "SchemaVersion") receipt[field] = 2;
        else if (field == "missing-run") receipt["Run"]!.AsObject().Remove("GITHUB_RUN_ID");
        else receipt["Run"]![field == "extra-run" ? "extra" : field] = "different";
        await File.WriteAllTextAsync(fixture.Receipt, receipt.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(fixture.ImportAsync);

        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Theory]
    [InlineData("coverage.cobertura.xml")]
    [InlineData("timings.json")]
    public async Task ImportRejectsAlteredReportAndTimings(string artifact)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync();
        await File.AppendAllTextAsync(Path.Join(fixture.Coverage, artifact), "altered");

        await Assert.ThrowsAsync<InvalidDataException>(fixture.ImportAsync);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("null")]
    [InlineData("json-syntax")]
    [InlineData("json-type")]
    public async Task ImportRejectsInvalidReceipt(string defect)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync();
        await File.WriteAllTextAsync(fixture.Receipt, defect switch
        {
            "empty" => "",
            "null" => "null",
            "json-syntax" => "{",
            "json-type" => "{\"SchemaVersion\":\"invalid\"}",
            _ => new string(' ', 16385),
        });

        await Assert.ThrowsAsync<InvalidDataException>(fixture.ImportAsync);
    }

    [Theory]
    [InlineData("collection")]
    [InlineData("coverage")]
    [InlineData("collection-receipt.json")]
    [InlineData("coverage.cobertura.xml")]
    [InlineData("timings.json")]
    public async Task ImportRejectsMissingInputsAsInvalidData(string missing)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync();
        var collection = fixture.Root;
        if (missing == "collection") collection = Path.Join(fixture.Root, "absent", "collection");
        else if (missing == "coverage") Directory.Delete(fixture.Coverage, recursive: true);
        else File.Delete(Path.Join(fixture.Coverage, missing));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => SolutionCoverageHandoff.ImportAsync(
            collection, fixture.Destination, fixture.Identity, fixture.Run, CancellationToken.None));

        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        if (missing is "collection" or "coverage" or "collection-receipt.json")
            Assert.False(Directory.Exists(fixture.Destination));
    }

    [Theory]
    [InlineData("collection-receipt.json")]
    [InlineData("coverage.cobertura.xml")]
    [InlineData("timings.json")]
    public async Task ImportRejectsDirectoriesInPlaceOfFiles(string artifact)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync();
        var path = Path.Join(fixture.Coverage, artifact);
        File.Delete(path);
        Directory.CreateDirectory(path);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(fixture.ImportAsync);

        Assert.Contains("must be a file", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("coverage.cobertura.xml")]
    [InlineData("timings.json")]
    public async Task WriteRejectsDirectoriesWithoutPublishingReceipt(string artifact)
    {
        using var fixture = new Fixture();
        var path = Path.Join(fixture.Coverage, artifact);
        File.Delete(path);
        Directory.CreateDirectory(path);

        await Assert.ThrowsAsync<InvalidDataException>(fixture.WriteAsync);

        Assert.False(File.Exists(fixture.Receipt));
    }

    [Theory]
    [InlineData("coverage.cobertura.xml")]
    [InlineData("timings.json")]
    public async Task WriteRejectsMissingInputsWithoutPublishingReceipt(string missing)
    {
        using var fixture = new Fixture();
        File.Delete(Path.Join(fixture.Coverage, missing));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(fixture.WriteAsync);

        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.False(File.Exists(fixture.Receipt));
    }

    [Fact]
    public async Task ImportRejectsOccupiedAndOverlappingDestinations()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync();
        Directory.CreateDirectory(fixture.Destination);
        var sentinel = Path.Join(fixture.Destination, "caller.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");

        await Assert.ThrowsAsync<InvalidDataException>(fixture.ImportAsync);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ImportAsync(fixture.Coverage));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ImportAsync(Path.Join(fixture.Coverage, "nested")));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ImportAsync(fixture.Root));

        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
    }

    [Theory]
    [InlineData("COVERAGE")]
    [InlineData("COVERAGE/nested")]
    public async Task ImportRejectsCaseAliasedDestinationsBeforeCopying(string relativeDestination)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync();
        var destination = TestPathUtils.PathUnder(fixture.Root, relativeDestination.Replace('/', Path.DirectorySeparatorChar));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ImportAsync(destination));

        Assert.Contains("disjoint fresh output", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(fixture.Coverage, "nested")));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    public async Task ImportRejectsLinkedCollectionEntries(string kind)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync();
        if (kind == "file") File.CreateSymbolicLink(Path.Join(fixture.Coverage, "linked.log"), Path.Join(fixture.Coverage, "timings.json"));
        else Directory.CreateSymbolicLink(Path.Join(fixture.Coverage, "linked-directory"), Path.Join(fixture.Coverage, "projects"));

        await Assert.ThrowsAsync<InvalidDataException>(fixture.ImportAsync);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("tree")]
    [InlineData("sdk")]
    [InlineData("provenance")]
    [InlineData("empty-report")]
    public async Task WriteRejectsInvalidIdentityOrIncompleteCollection(string defect)
    {
        using var fixture = new Fixture();
        var identity = fixture.Identity with
        {
            Commit = defect == "commit" ? new string('x', 40) : fixture.Identity.Commit,
            Tree = defect == "tree" ? "short" : fixture.Identity.Tree,
            Sdk = defect == "sdk" ? "" : fixture.Identity.Sdk,
        };
        var run = new Dictionary<string, string?>(fixture.Run);
        if (defect == "provenance") run.Remove("GITHUB_RUN_ATTEMPT");
        if (defect == "empty-report") await File.WriteAllTextAsync(Path.Join(fixture.Coverage, "coverage.cobertura.xml"), "");

        await Assert.ThrowsAsync<InvalidDataException>(() => SolutionCoverageHandoff.WriteAsync(fixture.Coverage, identity, run, CancellationToken.None));

        Assert.False(File.Exists(fixture.Receipt));
    }

    [Fact]
    public async Task ImportHonorsCancellationAndReceiptPublicationDoesNotClobber()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SolutionCoverageHandoff.ImportAsync(
            fixture.Root, fixture.Destination, fixture.Identity, fixture.Run, cancellation.Token));
        await Assert.ThrowsAsync<IOException>(fixture.WriteAsync);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = CreateRoot();

        private static string CreateRoot()
        {
            var root = Directory.CreateTempSubdirectory("coverage-handoff-").FullName;
            // macOS temp aliases contain linked ancestors; the handoff deliberately rejects those paths.
            return OperatingSystem.IsMacOS() && (root.StartsWith("/var/", StringComparison.Ordinal) || root.StartsWith("/tmp/", StringComparison.Ordinal))
                ? "/private" + root : root;
        }
        public string Coverage => Path.Join(Root, "coverage");
        public string Destination => Path.Join(Root, "accepted", "coverage");
        public string Receipt => Path.Join(Coverage, "collection-receipt.json");
        public SolutionCoverageIdentity Identity { get; } = new(new string('a', 40), new string('b', 40), "10.0.402");
        public IReadOnlyDictionary<string, string?> Run { get; } = new Dictionary<string, string?>
        { ["GITHUB_REPOSITORY"] = "forge-trust/AppSurface", ["GITHUB_RUN_ID"] = "123", ["GITHUB_RUN_ATTEMPT"] = "2" };

        public Fixture()
        {
            Directory.CreateDirectory(Path.Join(Coverage, "projects"));
            File.WriteAllText(Path.Join(Coverage, "coverage.cobertura.xml"), "<coverage />");
            File.WriteAllText(Path.Join(Coverage, "timings.json"), "{}");
            File.WriteAllText(Path.Join(Coverage, "projects", "test.log"), "diagnostic");
        }

        public Task WriteAsync() => SolutionCoverageHandoff.WriteAsync(Coverage, Identity, Run, CancellationToken.None);
        public Task ImportAsync() => ImportAsync(Destination);
        public Task ImportAsync(string destination) => SolutionCoverageHandoff.ImportAsync(Root, destination, Identity, Run, CancellationToken.None);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
