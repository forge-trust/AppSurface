using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceIdentityInputValidationCoverageTests
{
    [Theory]
    [InlineData("missing-inputs")]
    [InlineData("missing-implementation")]
    [InlineData("missing-includes")]
    [InlineData("empty-includes")]
    public async Task IncompleteIdentityDeclarationFailsBeforeAnyRepositoryQuery(string defect)
    {
        var producer = Producer();
        producer = defect switch
        {
            "missing-inputs" => producer with { Inputs = null },
            "missing-includes" => producer with { Inputs = new(null!, []) },
            "empty-includes" => producer with { Inputs = new([], []) },
            _ => producer
        };
        var request = Request(Path.GetTempPath(), producer) with
        { ImplementationIdentity = defect == "missing-implementation" ? " " : "implementation" };

        var result = await EvidenceProducerIdentityBuilder.BuildAsync(request, CancellationToken.None);

        Assert.Null(result.Identity);
        Assert.Equal("reuse.identity.unavailable", result.Reason);
        Assert.Contains("during validation (InvalidOperationException)", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/absolute")]
    [InlineData("src\\input")]
    [InlineData("src//input")]
    [InlineData("src/./input")]
    [InlineData("src/../input")]
    [InlineData("src/bad**")]
    [InlineData("src/in?ut")]
    [InlineData("src/[input")]
    [InlineData("src/input]")]
    [InlineData("src/in\0put")]
    public async Task InvalidSelectionPatternFailsBeforeAnyRepositoryQuery(string? pattern)
    {
        var producer = Producer() with { Inputs = new([pattern!], []) };

        var result = await EvidenceProducerIdentityBuilder.BuildAsync(Request(Path.GetTempPath(), producer), CancellationToken.None);

        Assert.Null(result.Identity);
        Assert.Equal("reuse.identity.unavailable", result.Reason);
        Assert.Contains("during validation (ArgumentException)", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmittedExcludeListHasTheSameIdentityAsAnEmptyList()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("input.txt", "input bytes");
        var producer = Producer();
        var empty = await EvidenceProducerIdentityBuilder.BuildAsync(Request(repository.Root, producer), CancellationToken.None);
        var omitted = await EvidenceProducerIdentityBuilder.BuildAsync(Request(repository.Root,
            producer with { Inputs = new(["input.txt"], null!) }), CancellationToken.None);

        Assert.True(empty.IsEligible, empty.Diagnostic);
        Assert.True(omitted.IsEligible, omitted.Diagnostic);
        Assert.Equal(empty.Identity!.Key, omitted.Identity!.Key);
    }

    [Fact]
    public async Task MissingTrackedInputAncestorIsExcludedFromInventoryAndCannotProduceIdentity()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/nested/input.txt", "tracked input");
        await repository.CommitAsync();
        Directory.Delete(TestPathUtils.PathUnder(repository.Root, "src"), recursive: true);

        var producer = Producer() with { Inputs = new(["src/**"], []) };
        var result = await EvidenceProducerIdentityBuilder.BuildAsync(
            Request(repository.Root, producer), CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Null(result.Identity);
        Assert.Equal("reuse.identity.empty", result.Reason);
        Assert.Contains("select no files", result.Diagnostic, StringComparison.Ordinal);
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(repository.Root, "src")));
    }

    [Fact]
    public void CanonicalDeclarationHashPreservesMeaningWhileIgnoringDeclarationListOrder()
    {
        var declaration = Producer() with
        {
            RequiredResources = ["z", "a"],
            AssertionIds = ["z", "a"],
            ArtifactSlots = [new("z", "z", "text/plain", false, 1), new("a", "a", "text/plain", false, 1)],
            Inputs = new(["z/**", "a/**"], ["z/omit", "a/omit"])
        };
        var reordered = declaration with
        {
            RequiredResources = ["a", "z"],
            AssertionIds = ["a", "z"],
            ArtifactSlots = declaration.ArtifactSlots.Reverse().ToArray(),
            Inputs = new(["a/**", "z/**"], ["a/omit", "z/omit"])
        };

        Assert.Equal(EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(declaration),
            EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(reordered));
        Assert.NotEqual(EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(declaration),
            EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(declaration with { Inputs = null }));
        Assert.Throws<ArgumentNullException>(() => EvidenceProducerIdentityBuilder.CanonicalDeclarationHash(null!));
    }

    private static EvidenceProducerDeclaration Producer() => new("producer", "check", "1", [], [], [], 10)
    { Inputs = new(["input.txt"], []) };

    private static EvidenceProducerIdentityRequest Request(string root, EvidenceProducerDeclaration producer)
        => new(root, new("1.1", "policy", "", "", new("local", EvidenceProfileScope.Targeted, [], [producer], []), [], [], ""),
            producer, "implementation", new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, string>(), null);
}
