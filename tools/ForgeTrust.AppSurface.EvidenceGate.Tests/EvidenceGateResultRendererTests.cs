using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.EvidenceGate.Tests;

public sealed class EvidenceGateResultRendererTests
{
    [Fact]
    public void RendererKeepsTerminalMachineSummaryAndMarkdownOnOneBoundedVerdict()
    {
        var summary = new EvidencePullRequestGateSummary(
            "code;coverage", "matched-policy-rules",
            [new EvidencePullRequestGateRuleSummary("code-rule", "src/[malicious]`<script>", "code;coverage")],
            ["coverage"], [], ["coverage"],
            [new EvidencePullRequestGateObligationRationale("coverage", "behavioral-code", "Verify coverage | gate")],
            "aaaaaaaaaaaa", "bbbbbbbbbbbb",
            2, 1, 0, 1, 0, true);
        var result = new EvidencePullRequestGateVerificationResult(false, "ASEVG004", "Evidence is incomplete.", summary);

        var terminal = EvidenceGateResultRenderer.RenderTerminal(result);
        var markdown = EvidenceGateResultRenderer.RenderMarkdown(result);

        Assert.Equal("gate=ineligible; code=ASEVG004; claim=None; profile=code_coverage; base=aaaaaaaaaaaa; head=bbbbbbbbbbbb; selected=2; closed=0; missing=2", terminal);
        Assert.Contains("Verdict: **ineligible** (`ASEVG004`)", markdown, StringComparison.Ordinal);
        Assert.Contains("Verified claim: `None`", markdown, StringComparison.Ordinal);
        Assert.Contains("Selected obligations (2): `coverage`; 1 more omitted", markdown, StringComparison.Ordinal);
        Assert.Contains("Missing obligations (2): `coverage`; 1 more omitted", markdown, StringComparison.Ordinal);
        Assert.Contains("src/\\[malicious\\]\\`\\<script\\>", markdown, StringComparison.Ordinal);
        Assert.Contains("Verify coverage \\| gate", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PrePlanFailureDoesNotInventProfileOrClaim()
    {
        var result = new EvidencePullRequestGateVerificationResult(false, "ASEGG103", "Malformed plan.");

        Assert.Equal("gate=ineligible; code=ASEGG103; claim=None", EvidenceGateResultRenderer.RenderTerminal(result));
        var markdown = EvidenceGateResultRenderer.RenderMarkdown(result);
        Assert.Contains("Plan summary: unavailable", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Profile:", markdown, StringComparison.Ordinal);

        var inconsistentSuccess = new EvidencePullRequestGateVerificationResult(true, "ASEVG000", "Eligible without a summary.");
        Assert.Contains("claim=None", EvidenceGateResultRenderer.RenderTerminal(inconsistentSuccess), StringComparison.Ordinal);
    }

    [Fact]
    public void EligibleNonemptySummaryReportsTargetedClaim()
    {
        var summary = new EvidencePullRequestGateSummary(
            "code-coverage", "matched-policy-rules", [], ["coverage"], ["coverage"], [], [],
            "aaaaaaaaaaaa", "bbbbbbbbbbbb", 0, 0, 0, 0, 0, true);
        var result = new EvidencePullRequestGateVerificationResult(true, "ASEVG000", "Verified.", summary);

        Assert.Contains("claim=TargetedComplete", EvidenceGateResultRenderer.RenderTerminal(result), StringComparison.Ordinal);
        Assert.Contains("Verified claim: `TargetedComplete`", EvidenceGateResultRenderer.RenderMarkdown(result), StringComparison.Ordinal);
    }

    [Fact]
    public void EligibleProfileWithProducerButNoObligationReportsTargetedClaim()
    {
        var summary = new EvidencePullRequestGateSummary(
            "producer-only", "matched-policy-rules", [], [], [], [], [],
            "aaaaaaaaaaaa", "bbbbbbbbbbbb", 0, 0, 0, 0, 0, true);
        var result = new EvidencePullRequestGateVerificationResult(true, "ASEVG000", "Verified.", summary);

        Assert.Contains("claim=TargetedComplete", EvidenceGateResultRenderer.RenderTerminal(result), StringComparison.Ordinal);
        Assert.Contains("Verified claim: `TargetedComplete`", EvidenceGateResultRenderer.RenderMarkdown(result), StringComparison.Ordinal);
    }

    [Fact]
    public void EligibleEmptyProfileReportsNoEvidenceRequired()
    {
        var summary = new EvidencePullRequestGateSummary(
            "documentation-only", "matched-policy-rules", [], [], [], [], [],
            "aaaaaaaaaaaa", "bbbbbbbbbbbb", 0, 0, 0, 0, 0, false);
        var result = new EvidencePullRequestGateVerificationResult(true, "ASEVG000", "Verified.", summary);

        Assert.Contains("claim=NoEvidenceRequired", EvidenceGateResultRenderer.RenderTerminal(result), StringComparison.Ordinal);
        Assert.Contains("Verified claim: `NoEvidenceRequired`", EvidenceGateResultRenderer.RenderMarkdown(result), StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownReportsWhenBoundedObligationRationalesWereOmitted()
    {
        var summary = new EvidencePullRequestGateSummary(
            "large-profile", "matched-policy-rules", [], [], [], [], [],
            "aaaaaaaaaaaa", "bbbbbbbbbbbb", 0, 0, 0, 0, 2, true);
        var result = new EvidencePullRequestGateVerificationResult(true, "ASEVG000", "Verified.", summary);

        var markdown = EvidenceGateResultRenderer.RenderMarkdown(result);

        Assert.Contains("### Obligation rationale\n- 2 more rationales omitted.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalSummaryReplacesWhitespaceAndFieldDelimitersInRevisionFields()
    {
        var summary = new EvidencePullRequestGateSummary(
            "release gate;\n=alpha", "matched-policy-rules", [], [], [], [], [],
            "abc def", "x;y=z\tq", 0, 0, 0, 0, 0, false);
        var result = new EvidencePullRequestGateVerificationResult(true, "ASEVG000", "Verified.", summary);

        var terminal = EvidenceGateResultRenderer.RenderTerminal(result);

        Assert.Equal(
            "gate=eligible; code=ASEVG000; claim=NoEvidenceRequired; profile=release_gate___alpha; base=abc_def; head=x_y_z_q; selected=0; closed=0; missing=0",
            terminal);
    }
}
