using ForgeTrust.RazorWire.TagHelpers;

namespace ForgeTrust.RazorWire.Cli.Tests;

public sealed class ExportAuthArtifactAuditorTests
{
    [Fact]
    public async Task WriteTextArtifactAsync_ShouldFailBeforeWriting_StaticViolationMarker()
    {
        var output = Directory.CreateTempSubdirectory("rw-auth-audit-").FullName;
        var artifactPath = Path.Join(output, "admin.html");

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => ExportAuthArtifactAuditor.WriteTextArtifactAsync(
                output,
                artifactPath,
                "HTML route artifact",
                "/admin",
                $"""
                <div data-rw-auth-helper="auth-view"
                     {AuthViewTagHelper.StaticViolationAttributeName}="{AuthViewTagHelper.StaticMissingFallbackReason}"></div>
                """,
                encoding: null,
                CancellationToken.None));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(ExportAuthArtifactAuditor.DiagnosticCode, diagnostic.Code);
        Assert.Equal("/admin", diagnostic.Route);
        Assert.Contains("[auth-missing-fallback]", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Helper: auth-view", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(ExportAuthArtifactAuditor.DocsPath, diagnostic.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(artifactPath));
    }

    [Theory]
    [InlineData("""<div data-rw-auth-outcome="Allowed"></div>""", "auth-unsafe-metadata")]
    [InlineData("""<div data-rw-auth-policy="docs.publish"></div>""", "auth-diagnostics")]
    [InlineData("""<div data-rw-auth-state="allowed"></div>""", "auth-private-content")]
    [InlineData("""{"html":"<div data-rw-auth-state=\"allowed\"></div>"}""", "auth-private-content")]
    [InlineData("""{"html":"&lt;div data-rw-auth-state=&quot;allowed&quot;&gt;&lt;/div&gt;"}""", "auth-private-content")]
    [InlineData("""{"html":"\u003Cdiv data-rw-auth-state=\u0022allowed\u0022\u003E\u003C/div\u003E"}""", "auth-private-content")]
    [InlineData("""<aside class="appsurface-dev-auth-marker"></aside>""", "auth-artifact-leak")]
    [InlineData("""<aside data-appsurface-dev-auth="marker"></aside>""", "auth-artifact-leak")]
    [InlineData("""{"html":"&lt;main data-appsurface-dev-auth=&quot;control-page&quot;&gt;&lt;/main&gt;"}""", "auth-artifact-leak")]
    [InlineData("""{"html":"\u003Cmain data-appsurface-dev-auth=\u0022control-page\u0022\u003E\u003C/main\u003E"}""", "auth-artifact-leak")]
    public void ValidateTextArtifact_ShouldReject_StaticUnsafeAuthOutput(string contents, string expectedReason)
    {
        var exception = Assert.Throws<ExportValidationException>(
            () => ExportAuthArtifactAuditor.ValidateTextArtifact(contents, "final export artifact", "/docs"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(ExportAuthArtifactAuditor.DiagnosticCode, diagnostic.Code);
        Assert.Contains($"[{expectedReason}]", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("application/json", "route", true)]
    [InlineData("application/problem+json", "route", true)]
    [InlineData("application/atom+xml", "route", true)]
    [InlineData("application/vnd.api+json; charset=utf-8", "route", true)]
    [InlineData("application/manifest+json", "route", true)]
    [InlineData("text/javascript", "route", true)]
    [InlineData(null, "app.webmanifest", true)]
    [InlineData(null, "CNAME", true)]
    [InlineData("image/png", "image.png", false)]
    public void IsTextArtifact_ShouldClassifyTextPayloads(string? contentType, string artifactPath, bool expected)
    {
        Assert.Equal(expected, ExportAuthArtifactAuditor.IsTextArtifact(contentType, artifactPath));
    }

    [Theory]
    [InlineData("search", true)]
    [InlineData("CNAME", true)]
    [InlineData("image.png", false)]
    public void ShouldAuditLocalTextArtifact_ShouldIncludeExtensionlessTextCandidates(string artifactPath, bool expected)
    {
        Assert.Equal(expected, ExportAuthArtifactAuditor.ShouldAuditLocalTextArtifact(artifactPath));
    }
}
