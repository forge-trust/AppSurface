namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Describes the include, exclude, or prune outcome for one normalized harvest path.
/// </summary>
/// <param name="Included">Indicates whether the path remains eligible for harvesting.</param>
/// <param name="RelativePath">The normalized repository-relative path that was evaluated.</param>
/// <param name="SourceKind">The source type whose harvest policy was applied.</param>
/// <param name="Code">The policy decision code explaining the outcome.</param>
/// <param name="Trace">The ordered rule matches that contributed to the decision.</param>
/// <param name="MatchedDefaultGroups">The default exclusion groups that matched the path.</param>
internal sealed record RazorDocsHarvestPathDecision(
    bool Included,
    string RelativePath,
    RazorDocsHarvestSourceKind SourceKind,
    RazorDocsHarvestPathDecisionCode Code,
    IReadOnlyList<RazorDocsHarvestPathRuleTrace> Trace,
    string[] MatchedDefaultGroups);
