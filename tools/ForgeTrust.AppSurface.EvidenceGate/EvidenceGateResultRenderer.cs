using System.Text;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.EvidenceGate;

/// <summary>Renders one independently verified PR verdict for terminal and GitHub summary consumers.</summary>
/// <remarks>
/// The renderer accepts only the bounded verifier result. It never reads changed paths, producer logs,
/// artifact contents, or the untrusted manifest when preparing user-facing output.
/// </remarks>
internal static class EvidenceGateResultRenderer
{
    /// <summary>Returns one secret-safe terminal line with the verified claim and bounded obligation counts.</summary>
    /// <param name="result">Independent PR verdict; an absent plan summary never yields a verified claim.</param>
    internal static string RenderTerminal(EvidencePullRequestGateVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var status = result.IsEligible ? "eligible" : "ineligible";
        var claim = VerifiedClaim(result);
        if (result.Summary is not { } summary)
        {
            return $"gate={status}; code={result.Code}; claim={claim}";
        }

        return $"gate={status}; code={result.Code}; claim={claim}; profile={SafeTerminal(summary.ProfileId)}; "
            + $"base={SafeTerminal(summary.BaseRevisionShort)}; head={SafeTerminal(summary.HeadRevisionShort)}; "
            + $"selected={summary.SelectedObligationIds.Count + summary.OmittedSelectedObligationCount}; "
            + $"closed={summary.ClosedObligationIds.Count + summary.OmittedClosedObligationCount}; "
            + $"missing={summary.MissingObligationIds.Count + summary.OmittedMissingObligationCount}";
    }

    /// <summary>Returns the bounded GitHub step summary for the same independent PR verdict.</summary>
    /// <param name="result">Independent PR verdict; only its trusted bounded summary is rendered.</param>
    internal static string RenderMarkdown(EvidencePullRequestGateVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var output = new StringBuilder("## AppSurface evidence gate\n\n");
        output.Append("- Verdict: **").Append(result.IsEligible ? "eligible" : "ineligible").Append("** (`")
            .Append(EscapeInline(result.Code)).AppendLine("`)");
        output.Append("- Verified claim: `").Append(VerifiedClaim(result)).AppendLine("`");
        output.Append("- Diagnostic: ").Append(EscapeInline(result.Diagnostic)).AppendLine();

        if (result.Summary is not { } summary)
        {
            output.AppendLine("- Plan summary: unavailable because trusted verification did not reach the plan summary stage.");
            return output.ToString();
        }

        output.Append("- Profile: `").Append(EscapeInline(summary.ProfileId)).AppendLine("`");
        output.Append("- Selection: `").Append(EscapeInline(summary.SelectionRationale)).AppendLine("`");
        output.Append("- Revisions: base `").Append(EscapeInline(summary.BaseRevisionShort))
            .Append("`, head `").Append(EscapeInline(summary.HeadRevisionShort)).AppendLine("`");
        AppendIdentifiers(output, "Selected obligations", summary.SelectedObligationIds, summary.OmittedSelectedObligationCount);
        AppendIdentifiers(output, "Closed obligations", summary.ClosedObligationIds, summary.OmittedClosedObligationCount);
        AppendIdentifiers(output, "Missing obligations", summary.MissingObligationIds, summary.OmittedMissingObligationCount);
        output.AppendLine();
        output.AppendLine("### Policy selection");
        foreach (var rule in summary.MatchedRules)
        {
            output.Append("- `").Append(EscapeInline(rule.Id)).Append("` selects `")
                .Append(EscapeInline(rule.ProfileId)).Append("` for `")
                .Append(EscapeInline(rule.Pattern)).AppendLine("`.");
        }

        if (summary.OmittedMatchedRuleCount > 0)
        {
            output.Append("- ").Append(summary.OmittedMatchedRuleCount).AppendLine(" more matched rules omitted.");
        }

        output.AppendLine();
        output.AppendLine("### Obligation rationale");
        foreach (var obligation in summary.ObligationRationales)
        {
            output.Append("- `").Append(EscapeInline(obligation.Id)).Append("` (`")
                .Append(EscapeInline(obligation.RiskClass)).Append("`): ")
                .Append(EscapeInline(obligation.Rationale)).AppendLine();
        }

        if (summary.OmittedObligationRationaleCount > 0)
        {
            output.Append("- ").Append(summary.OmittedObligationRationaleCount).AppendLine(" more rationales omitted.");
        }

        return output.ToString();
    }

    private static void AppendIdentifiers(StringBuilder output, string label, IReadOnlyList<string> identifiers, int omittedCount)
    {
        output.Append("- ").Append(label).Append(" (").Append(identifiers.Count + omittedCount).Append("): ");
        output.Append(identifiers.Count == 0
            ? "none"
            : string.Join(", ", identifiers.Select(static identifier => $"`{EscapeInline(identifier)}`")));
        if (omittedCount > 0)
        {
            output.Append("; ").Append(omittedCount).Append(" more omitted");
        }

        output.AppendLine();
    }

    private static string VerifiedClaim(EvidencePullRequestGateVerificationResult result) =>
        !result.IsEligible || result.Summary is null ? "None"
        : result.Summary.ProfileHasEvidence ? "TargetedComplete" : "NoEvidenceRequired";

    private static string SafeTerminal(string value) =>
        new(value.Take(256).Select(static character => char.IsControl(character) || char.IsWhiteSpace(character) || character is ';' or '=' ? '_' : character).ToArray());

    private static string EscapeInline(string value)
    {
        var escaped = new StringBuilder();
        foreach (var character in value.Take(256))
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                escaped.Append(' ');
            }
            else if (character is '\\' or '`' or '[' or ']' or '<' or '>' or '*' or '_' or '|')
            {
                escaped.Append('\\').Append(character);
            }
            else
            {
                escaped.Append(character);
            }
        }

        return escaped.ToString();
    }
}
