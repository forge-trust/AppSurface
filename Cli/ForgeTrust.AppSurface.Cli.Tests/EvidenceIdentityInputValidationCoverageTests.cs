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
    [InlineData("patch-line-mode")]
    [InlineData("configuration")]
    [InlineData("artifact-retention")]
    [InlineData("resource-identity")]
    [InlineData("resource-identity-value")]
    [InlineData("unpaired-low-surrogate")]
    [InlineData("producer-id")]
    [InlineData("producer-kind")]
    [InlineData("producer-version")]
    [InlineData("required-resource")]
    [InlineData("assertion-id")]
    [InlineData("artifact-logical-name")]
    [InlineData("artifact-relative-root")]
    [InlineData("artifact-media-type")]
    [InlineData("resource-id")]
    [InlineData("resource-readiness")]
    [InlineData("resource-dependency")]
    public async Task MalformedUnicodeInExecutionIdentityIsRejectedDuringValidation(string defect)
    {
        var producer = Producer();
        var request = Request(Path.GetTempPath(), producer);
        request = defect switch
        {
            "patch-line-mode" => request with
            {
                Producer = producer with { CoverageGate = new(95, 85, PatchLineMode: "\uD800") }
            },
            "configuration" => request with
            {
                Producer = producer with { CoverageExecution = new EvidenceCoverageExecutionOptions(Configuration: "\uD800") }
            },
            "artifact-retention" => request with { Producer = producer with { CoverageArtifactRetention = "\uD800" } },
            "resource-identity" => request with { ResourceIdentities = new Dictionary<string, string> { ["\uD800"] = "value" } },
            "resource-identity-value" => request with { ResourceIdentities = new Dictionary<string, string> { ["resource"] = "\uD800" } },
            "unpaired-low-surrogate" => request with { ResourceIdentities = new Dictionary<string, string> { ["resource"] = "\uDC00" } },
            "producer-id" => request with { Producer = producer with { Id = "\uD800" } },
            "producer-kind" => request with { Producer = producer with { Kind = "\uD800" } },
            "producer-version" => request with { Producer = producer with { Version = "\uD800" } },
            "required-resource" => request with { Producer = producer with { RequiredResources = ["\uD800"] } },
            "assertion-id" => request with { Producer = producer with { AssertionIds = ["\uD800"] } },
            "artifact-logical-name" => request with
            {
                Producer = producer with { ArtifactSlots = [new("\uD800", "reports", "text/plain", false, 1)] }
            },
            "artifact-relative-root" => request with
            {
                Producer = producer with { ArtifactSlots = [new("report", "\uD800", "text/plain", false, 1)] }
            },
            "artifact-media-type" => request with
            {
                Producer = producer with { ArtifactSlots = [new("report", "reports", "\uD800", false, 1)] }
            },
            "resource-id" => request with
            {
                Plan = WithResource(request.Plan, new("\uD800", "completion", 10, []))
            },
            "resource-readiness" => request with
            {
                Plan = WithResource(request.Plan, new("resource", "\uD800", 10, []))
            },
            _ => request with
            {
                Plan = WithResource(request.Plan, new("resource", "completion", 10, ["\uD800"]))
            }
        };

        var result = await EvidenceProducerIdentityBuilder.BuildAsync(request, CancellationToken.None);

        Assert.Null(result.Identity);
        Assert.Equal("reuse.identity.unavailable", result.Reason);
        Assert.Contains("during validation (ArgumentException)", result.Diagnostic, StringComparison.Ordinal);
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

    private static EvidencePlan WithResource(EvidencePlan plan, EvidenceResourceDeclaration resource)
        => plan with { Profile = plan.Profile with { Resources = [resource] } };

    private static EvidenceProducerIdentityRequest Request(string root, EvidenceProducerDeclaration producer)
        => new(root, new("1.1", "policy", "", "", new("local", EvidenceProfileScope.Targeted, [], [producer], []), [], [], ""),
            producer, "implementation", new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, string>(), null);
}
