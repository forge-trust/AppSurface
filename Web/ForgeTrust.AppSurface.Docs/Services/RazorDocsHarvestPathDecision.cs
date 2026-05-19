namespace ForgeTrust.AppSurface.Docs.Services;

internal sealed record RazorDocsHarvestPathDecision(
    bool Included,
    string RelativePath,
    RazorDocsHarvestSourceKind SourceKind,
    RazorDocsHarvestPathDecisionCode Code,
    IReadOnlyList<RazorDocsHarvestPathRuleTrace> Trace,
    string[] MatchedDefaultGroups);
