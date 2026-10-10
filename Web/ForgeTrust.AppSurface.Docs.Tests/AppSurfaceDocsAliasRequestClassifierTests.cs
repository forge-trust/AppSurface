using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsAliasRequestClassifierTests
{
    [Theory]
    [InlineData("/docs/a/stable/guide", "/docs/a/stable/guide", "", "/docs/a", "stable", true)]
    [InlineData("/docs/a/stable", "", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable", "/docs/a/stable", "/", "/docs/a", "stable", true)]
    [InlineData("/docs/a/STABLE/guide?x=1", "/docs/a/STABLE/guide", "", "/docs/a", "stable", true)]
    [InlineData("/docs/%61/stable/guide", "/docs/a/stable/guide", "", "/docs/a", "stable", true)]
    [InlineData("/docs/a/stable/hello%20world", "/docs/a/stable/hello world", "", "/docs/a", "stable", true)]
    [InlineData("/docs/a/stable/caf%C3%A9", "/docs/a/stable/café", "", "/docs/a", "stable", true)]
    [InlineData("/docs/a", "/docs/a", "", "/docs/a", null, true)]
    [InlineData("/docs/a/", "/docs/a/", "", "/docs/a", null, true)]
    [InlineData("/docs/a/%73table", "/docs/a/stable", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable/../guide", "/docs/guide", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable/%2e%2e/guide", "/docs/guide", "", "/docs/a", null, false)]
    [InlineData("/docs%2fa/stable/guide", "/docs%2fa/stable/guide", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable%2fguide", "/docs/a/stable%2fguide", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable/%5cguide", "/docs/a/stable/\\guide", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable/%252e%252e", "/docs/a/stable/%2e%2e", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable/%", "/docs/a/stable/%", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable/%GG", "/docs/a/stable/%GG", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable/%C0%AF", "/docs/a/stable/x", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable/%00", "/docs/a/stable/\0", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable/.", "/docs/a/stable", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable/guide", "/docs/a/preview/guide", "", "/docs/a", null, false)]
    [InlineData(null, "/docs/a/stable", "", "/docs/a", null, false)]
    [InlineData("bad", "/docs/a/stable", "", "/docs/a", null, false)]
    [InlineData("/base/docs/a/stable", "/docs/a/stable", "/base", "/docs/a", "stable", true)]
    [InlineData("/base%20one/docs/a/stable", "/docs/a/stable", "/base one", "/docs/a", "stable", true)]
    [InlineData("/wrong/docs/a/stable", "/docs/a/stable", "/base", "/docs/a", null, false)]
    [InlineData("/base/base/a/stable", "/base/a/stable", "/base", "/base/a", "stable", true)]
    [InlineData("/foo/bar/a/v1/guide", "/foo/bar/a/v1/guide", "", "/foo/bar/a", "v1", true)]
    [InlineData("/a/x", "/a/x", "", "/a", "x", true)]
    [InlineData("https://example.test/docs/a/stable?q=/foo", "/docs/a/stable", "", "/docs/a", "stable", true)]
    [InlineData("/docs/a/-wrong", "/docs/a/-wrong", "", "/docs/a", null, false)]
    [InlineData("/docs/a/stable\\guide", "/docs/a/stable\\guide", "", "/docs/a", null, false)]
    public void Classify_RetainsOwnershipAndValidatesReceivedEvidence(string? raw, string path, string pathBase,
        string root, string? name, bool safe)
    {
        var context = Context(raw, path, pathBase);
        var receivedPath = context.Request.Path.Value;
        var result = AppSurfaceDocsAliasRequestClassifier.Classify(context, [root]);
        Assert.NotNull(result);
        Assert.Equal(root, result.NamespaceRoot);
        Assert.Equal(name, result.Name);
        Assert.Equal(safe, result.IsSafe);
        Assert.Equal(receivedPath, context.Request.Path.Value);
    }

    [Theory]
    [InlineData("/docs/apple", "/docs/apple")]
    [InlineData("/docs/ab", "/docs/ab")]
    [InlineData("/docs/next/guide", "/docs/next/guide")]
    [InlineData("/docs/v/1/a", "/docs/v/1/a")]
    [InlineData(null, "/other")]
    public void Classify_UnownedRoutesRemainUntouched(string? raw, string path)
    {
        Assert.Null(AppSurfaceDocsAliasRequestClassifier.Classify(Context(raw, path, ""), ["/docs/a"]));
        Assert.Null(AppSurfaceDocsAliasRequestClassifier.Classify(Context(raw, path, ""), []));
    }

    [Fact]
    public void Classify_RawOwnerWinsOverDifferentNamedFrameworkOwner()
    {
        var result = AppSurfaceDocsAliasRequestClassifier.Classify(
            Context("/public/a/stable/../guide", "/private/a/stable", ""), ["/public/a", "/private/a"]);
        Assert.NotNull(result);
        Assert.Equal("/public/a", result.NamespaceRoot);
        Assert.False(result.IsSafe);
    }

    [Theory]
    [InlineData("http://example.test?next=/docs/a/stable", "/", true)]
    [InlineData("https://example.test", "/", true)]
    [InlineData("http://example.test/a/../b?q=x", "/a/../b", true)]
    [InlineData("ftp://example.test/docs/a/stable", "", false)]
    [InlineData("http:///docs/a/stable", "", false)]
    [InlineData("http://user@example.test/docs/a/stable", "", false)]
    [InlineData("/docs/a#bad", "/docs/a#bad", false)]
    [InlineData("*", "", false)]
    [InlineData("", "", false)]
    public void ExtractPath_NeverNormalizesReceivedSegments(string target, string path, bool success)
    {
        Assert.Equal(success, AppSurfaceDocsAliasRequestClassifier.TryExtractPath(target, out var extracted));
        Assert.Equal(path, extracted);
    }

    private static DefaultHttpContext Context(string? raw, string path, string pathBase)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.PathBase = pathBase;
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = raw!;
        return context;
    }
}
