namespace EvidenceHost.CompletedIsolation;

/// <summary>Exact protected caller context; feature-branch controls cannot synthesize a completed-run entitlement.</summary>
internal static class OwnerCompletionPolicy
{
    internal static readonly string[] ContextNames = ["GITHUB_ACTIONS", "GITHUB_EVENT_NAME", "GITHUB_REPOSITORY",
        "GITHUB_REF", "GITHUB_REF_PROTECTED", "GITHUB_WORKFLOW_REF", "GITHUB_REPOSITORY_ID", "GITHUB_SHA", "GITHUB_WORKFLOW_SHA"];

    internal static Dictionary<string, string> Validate(IReadOnlyDictionary<string, string> supplied)
    {
        var context = ContextNames.ToDictionary(name => name,
            name => supplied.TryGetValue(name, out var value) ? value : throw new IsolationException("protected-context"));
        if (context.Values.Any(value => value.Length > 4096 || value.Any(char.IsControl))
            || context["GITHUB_ACTIONS"] != "true" || context["GITHUB_EVENT_NAME"] != "workflow_run"
            || context["GITHUB_REPOSITORY"] != "forge-trust/AppSurface" || context["GITHUB_REF"] != "refs/heads/main"
            || context["GITHUB_REF_PROTECTED"] != "true"
            || context["GITHUB_WORKFLOW_REF"] != "forge-trust/AppSurface/.github/workflows/evidencehost-completed-validation.yml@refs/heads/main"
            || !IsSha(context["GITHUB_SHA"]) || context["GITHUB_SHA"] != context["GITHUB_WORKFLOW_SHA"]
            || !ulong.TryParse(context["GITHUB_REPOSITORY_ID"], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var repositoryId) || repositoryId == 0)
            throw new IsolationException("protected-context");
        return context;
    }

    private static bool IsSha(string value) => value.Length == 40 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
