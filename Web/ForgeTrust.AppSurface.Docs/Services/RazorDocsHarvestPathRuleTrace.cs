namespace ForgeTrust.AppSurface.Docs.Services;

internal sealed record RazorDocsHarvestPathRuleTrace(
    RazorDocsHarvestPathDecisionCode Code,
    string Scope,
    string? Pattern,
    string? DefaultGroup,
    bool Matched);
