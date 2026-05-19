namespace ForgeTrust.AppSurface.Docs.Services;

internal enum RazorDocsHarvestPathDecisionCode
{
    IncludedByDefaultCandidate,
    IncludedByGlobalInclude,
    IncludedBySourceInclude,
    IncludedByDefaultGroupAllow,
    ExcludedByInvalidPath,
    ExcludedByBaseCandidate,
    ExcludedByGlobalIncludeMiss,
    ExcludedBySourceIncludeMiss,
    ExcludedByDefaultGroup,
    ExcludedByGlobalExclude,
    ExcludedBySourceExclude
}
