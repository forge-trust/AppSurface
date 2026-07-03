using System.Text;
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

    [Fact]
    public async Task WriteTextArtifactBytesAsync_ShouldFailBeforeWriting_EncodedUnsafePayload()
    {
        var output = Directory.CreateTempSubdirectory("rw-auth-byte-audit-").FullName;
        var artifactPath = Path.Join(output, "search.json");
        var contents = Encoding.UTF8.GetBytes("""{"html":"\u003Cdiv data-rw-auth-state=\u0022allowed\u0022\u003E\u003C/div\u003E"}""");

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => ExportAuthArtifactAuditor.WriteTextArtifactBytesAsync(
                output,
                artifactPath,
                "search payload",
                "/search",
                contents,
                declaredEncoding: null,
                CancellationToken.None));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(ExportAuthArtifactAuditor.DiagnosticCode, diagnostic.Code);
        Assert.Equal("/search", diagnostic.Route);
        Assert.Contains("[auth-private-content]", diagnostic.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(artifactPath));
    }

    [Fact]
    public async Task WriteTextArtifactBytesAsync_ShouldWriteOriginalBytes_WhenSafe()
    {
        var output = Directory.CreateTempSubdirectory("rw-auth-byte-safe-").FullName;
        var artifactPath = Path.Join(output, "legacy.js");
        byte[] contents =
        [
            0x63, 0x6F, 0x6E, 0x73, 0x74, 0x20, 0x6C, 0x61, 0x62, 0x65, 0x6C, 0x20,
            0x3D, 0x20, 0x22, 0x63, 0x61, 0x66, 0xE9, 0x22, 0x3B, 0x0A
        ];

        await ExportAuthArtifactAuditor.WriteTextArtifactBytesAsync(
            output,
            artifactPath,
            "JavaScript route artifact",
            "/legacy.js",
            contents,
            Encoding.Latin1,
            CancellationToken.None);

        Assert.Equal(contents, await File.ReadAllBytesAsync(artifactPath));
    }

    [Fact]
    public void ValidateTextArtifactBytes_ShouldReject_EncodedUnsafePayload()
    {
        var contents = Encoding.UTF8.GetBytes("""{"html":"&lt;div data-rw-auth-state=&quot;allowed&quot;&gt;&lt;/div&gt;"}""");

        var exception = Assert.Throws<ExportValidationException>(
            (Action)(() => ExportAuthArtifactAuditor.ValidateTextArtifactBytes(contents, "search payload", "/search")));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(ExportAuthArtifactAuditor.DiagnosticCode, diagnostic.Code);
        Assert.Contains("[auth-private-content]", diagnostic.Message, StringComparison.Ordinal);
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
    [InlineData("""{"title":"data-appsurface-dev-auth"}""", "auth-artifact-leak")]
    [InlineData("""{"body":"appsurface-dev-auth-marker"}""", "auth-artifact-leak")]
    [InlineData("""{"body":"data-appsurface-persona"}""", "auth-artifact-leak")]
    [InlineData("""{"body":"data-appsurface-subject"}""", "auth-artifact-leak")]
    [InlineData("""{"body":"data-appsurface-dev-\u0061uth"}""", "auth-artifact-leak")]
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
