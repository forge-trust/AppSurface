namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Captures one rule evaluation step for internal harvest path diagnostics.
/// </summary>
/// <param name="Code">The decision code represented by the trace step.</param>
/// <param name="Scope">The rule scope, such as <c>global</c>, <c>Markdown</c>, <c>CSharp</c>, or <c>default-allow</c>.</param>
/// <param name="Pattern">The configured pattern that matched, or null when the step has no pattern match.</param>
/// <param name="DefaultGroup">The default exclusion group involved in the step, or null when the step is not group-based.</param>
/// <param name="Matched">Whether the rule matched the candidate path.</param>
internal sealed record RazorDocsHarvestPathRuleTrace(
    RazorDocsHarvestPathDecisionCode Code,
    string Scope,
    string? Pattern,
    string? DefaultGroup,
    bool Matched);
