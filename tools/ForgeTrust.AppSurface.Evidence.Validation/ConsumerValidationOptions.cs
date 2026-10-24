namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Overrides the checked-in consumer fixture used by the installed-package validation producer.</summary>
/// <param name="SourceDirectory">Optional source repository or fixture directory to copy into the owned workspace.</param>
/// <param name="ExpectedCommit">Optional commit that the source repository must currently contain at its HEAD.</param>
/// <param name="SolutionPath">Optional source-relative solution path.</param>
/// <param name="TestProjects">Optional source-relative test project paths. Supplying these skips solution discovery.</param>
/// <param name="ExpectedRequiredTestFragments">Test-case name or class fragments that must appear in passing JUnit cases.</param>
/// <param name="PermittedSkipPatterns">Optional case-insensitive testcase attribute fragments permitted to be skipped, mapped to a nonempty rationale. Omitted means zero skips are permitted.</param>
/// <param name="DiffBase">Optional git base used for the patch coverage gate.</param>
/// <param name="Environment">Optional child-process environment overrides.</param>
internal sealed record ConsumerValidationOptions(
    string? SourceDirectory = null,
    string? ExpectedCommit = null,
    string? SolutionPath = null,
    IReadOnlyList<string>? TestProjects = null,
    IReadOnlyList<string>? ExpectedRequiredTestFragments = null,
    IReadOnlyDictionary<string, string>? PermittedSkipPatterns = null,
    string? DiffBase = null,
    IReadOnlyDictionary<string, string>? Environment = null);
