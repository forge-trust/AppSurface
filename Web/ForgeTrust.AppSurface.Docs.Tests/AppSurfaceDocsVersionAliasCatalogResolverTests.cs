using System.Text.Json;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsVersionAliasCatalogResolverTests
{
    [Theory]
    [InlineData("PREVIEW", true, "preview")]
    [InlineData(" x ", true, "x")]
    [InlineData(".bad", false, "")]
    [InlineData("bad-", false, "")]
    [InlineData("two words", false, "")]
    [InlineData("a/b", false, "")]
    [InlineData("%61", false, "")]
    [InlineData("..", false, "")]
    [InlineData("ümlaut", false, "")]
    public void AliasName_ShouldApplySharedAsciiGrammar(string? input, bool expectedValid, string expectedName)
    {
        var valid = AppSurfaceDocsVersionAliasName.TryNormalize(input, out var name);

        Assert.Equal(expectedValid, valid);
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void AliasName_ShouldAcceptMaximumLengthAndRejectLongerNames()
    {
        var maximum = "a" + new string('b', 62) + "9";
        var tooLong = "a" + new string('b', 63) + "9";

        Assert.True(AppSurfaceDocsVersionAliasName.TryNormalize(maximum, out var normalized));
        Assert.Equal(maximum, normalized);
        Assert.False(AppSurfaceDocsVersionAliasName.TryNormalize(tooLong, out var rejected));
        Assert.Equal(string.Empty, rejected);
    }

    [Fact]
    public void Parse_ShouldDistinguishInactiveAndMalformedCollections()
    {
        var logger = new RecordingLogger();
        var resolver = CreateResolver(logger);

        Assert.False(resolver.Parse(Parse("{}")).IsActive);
        Assert.False(resolver.Parse(Parse("{\"aliases\":null}")).IsActive);
        Assert.False(resolver.Parse(Parse("{\"aliases\":[]}")).IsActive);

        var malformed = resolver.Parse(Parse("{\"aliases\":{\"name\":\"stable\"}}"));
        Assert.True(malformed.IsActive);
        Assert.Empty(malformed.Items);
        Assert.Contains("ASDOCSALIAS001", Assert.Single(logger.Messages), StringComparison.Ordinal);
        Assert.Contains("#asdocsalias001", Assert.Single(logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ShouldPreserveOrderAndVisibilityProvenance_AndRequireVerifiedPublicTargets()
    {
        var resolver = CreateResolver();
        var declaration = resolver.Parse(Parse("""
            {
              "aliases": [
                { "name": " PREVIEW ", "version": "2.0.0-RC.1", "label": "Preview", "summary": "Release candidate" },
                { "name": "null-default", "version": "2.0.0-RC.1", "visibility": null },
                { "name": "hidden", "version": "1.0.0", "visibility": "Hidden" },
                { "name": "legacy", "version": "0.9.0" },
                { "name": "draft", "version": "3.0.0" },
                { "name": "unverified", "version": "0.8.0" }
              ]
            }
            """));
        var versions = new[]
        {
            Version("2.0.0-RC.1", AppSurfaceDocsVersionVisibility.Public, available: true, verified: true),
            Version("1.0.0", AppSurfaceDocsVersionVisibility.Public, available: true, verified: true),
            Version("0.9.0", AppSurfaceDocsVersionVisibility.Public, available: false, verified: false),
            Version("3.0.0", AppSurfaceDocsVersionVisibility.Hidden, available: true, verified: true),
            Version("0.8.0", AppSurfaceDocsVersionVisibility.Public, available: true, verified: false)
        };

        var aliases = resolver.Resolve(declaration, versions);

        Assert.Equal(new[] { "preview", "null-default", "hidden", "legacy", "draft", "unverified" }, aliases.Select(alias => alias.Name));
        Assert.True(aliases[0].IsAvailable);
        Assert.Equal("2.0.0-RC.1", aliases[0].TargetVersion?.Version);
        Assert.Equal("/docs/a/preview", aliases[0].RootUrl);
        Assert.False(aliases[0].HasExplicitPublicVisibility);
        Assert.Equal(AppSurfaceDocsVersionVisibility.Public, aliases[0].Visibility);
        Assert.True(aliases[1].IsAvailable);
        Assert.False(aliases[1].HasExplicitPublicVisibility);
        Assert.Equal(AppSurfaceDocsVersionVisibility.Public, aliases[1].Visibility);
        Assert.False(aliases[2].IsAvailable);
        Assert.Null(aliases[2].TargetVersion);
        Assert.Null(aliases[2].DiagnosticCode);
        Assert.True(aliases[3].IsDefinitionValid);
        Assert.Equal("ASDOCSALIAS008", aliases[3].DiagnosticCode);
        Assert.Equal("0.9.0", aliases[3].TargetVersion?.Version);
        Assert.Equal("ASDOCSALIAS007", aliases[4].DiagnosticCode);
        Assert.Null(aliases[4].TargetVersion);
        Assert.DoesNotContain("3.0.0", aliases[4].AvailabilityIssue, StringComparison.Ordinal);
        Assert.Equal("ASDOCSALIAS008", aliases[5].DiagnosticCode);
        Assert.Equal("0.8.0", aliases[5].TargetVersion?.Version);
    }

    [Fact]
    public void Resolve_ShouldRetainPerEntryProvenance_AndReportDuplicateNameOncePerGroup()
    {
        var logger = new RecordingLogger();
        var resolver = CreateResolver(logger);
        var declaration = resolver.Parse(Parse("""
            { "aliases": [
                { "name": "Stable", "version": "1", "visibility": "Public" },
                { "name": " stable ", "version": "hidden-target", "visibility": "Hidden" },
                { "name": "Broken", "version": 4, "visibility": "Public", "label": 12 },
                { "name": "bad/name", "version": "1", "visibility": "Public" },
                9,
                { "name": "unknown-visibility", "version": "1", "visibility": "future" },
                { "name": "numeric-visibility", "version": "1", "visibility": 99 }
            ] }
            """));

        var aliases = resolver.Resolve(
            declaration,
            [Version("1", AppSurfaceDocsVersionVisibility.Public, available: true, verified: true)]);

        Assert.Equal(7, aliases.Count);
        Assert.All(aliases.Take(2), alias =>
        {
            Assert.True(alias.IsNameConflict);
            Assert.Equal("ASDOCSALIAS005", alias.DiagnosticCode);
        });
        Assert.Equal("stable", aliases[0].Name);
        Assert.Equal("ASDOCSALIAS004", aliases[2].DiagnosticCode);
        Assert.True(aliases[2].HasExplicitPublicVisibility);
        Assert.False(aliases[2].IsDefinitionValid);
        Assert.Null(aliases[3].Name);
        Assert.Equal("ASDOCSALIAS003", aliases[3].DiagnosticCode);
        Assert.Null(aliases[3].TargetVersion);
        Assert.Equal("ASDOCSALIAS002", aliases[4].DiagnosticCode);
        Assert.Null(aliases[5].Visibility);
        Assert.False(aliases[5].HasExplicitPublicVisibility);
        Assert.Null(aliases[6].Visibility);
        Assert.False(aliases[6].HasExplicitPublicVisibility);
        Assert.Equal("ASDOCSALIAS004", aliases[6].DiagnosticCode);
        Assert.Equal(1, logger.Messages.Count(message => message.Contains("ASDOCSALIAS005", StringComparison.Ordinal)));
        Assert.Contains("EntryIndices=0,1", logger.Messages.Single(message => message.Contains("ASDOCSALIAS005", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("#asdocsalias005", logger.Messages.Single(message => message.Contains("ASDOCSALIAS005", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-target", string.Join('\n', logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain("bad/name", string.Join('\n', logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ShouldNotExposeConfiguredHiddenTargetOrRouteWhenTrustedRootResolutionFailed()
    {
        var resolver = CreateResolver();
        var declaration = resolver.Parse(Parse("{\"aliases\":[{\"name\":\"stable\",\"version\":\"1.0\"}]}"));

        var alias = Assert.Single(resolver.Resolve(declaration, [], targetResolutionAvailable: false));

        Assert.False(alias.IsAvailable);
        Assert.Equal("1.0", alias.ConfiguredVersion);
        Assert.Null(alias.TargetVersion);
        Assert.Equal("ASDOCSALIAS008", alias.DiagnosticCode);
        Assert.Equal("/docs/a/stable", alias.RootUrl);
    }

    [Fact]
    public void Resolve_ShouldUseOnlyExactVersionDictionaryAndAllowAliasNameToMatchExactVersion()
    {
        var resolver = CreateResolver();
        var declaration = resolver.Parse(Parse("""
            { "aliases": [
                { "name": "same-name", "version": " SAME-NAME " },
                { "name": "chain", "version": "same-name-alias" },
                { "name": "unknown", "version": "missing" }
            ] }
            """));
        var versions = new[]
        {
            Version("same-name", AppSurfaceDocsVersionVisibility.Public, available: true, verified: true)
        };

        var aliases = resolver.Resolve(declaration, versions);

        Assert.True(aliases[0].IsAvailable);
        Assert.Same(versions[0], aliases[0].TargetVersion);
        Assert.Null(aliases[1].TargetVersion);
        Assert.Equal("ASDOCSALIAS006", aliases[1].DiagnosticCode);
        Assert.Equal("ASDOCSALIAS006", aliases[2].DiagnosticCode);
        Assert.Null(aliases[2].TargetVersion);
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(20000)]
    public void Resolve_ShouldRetainEveryAliasInAuthoredOrder_AtLargeCatalogSizes(int count)
    {
        var json = "{\"aliases\":[" + string.Join(',', Enumerable.Range(0, count)
            .Select(index => $"{{\"name\":\"alias-{index}\",\"version\":\"1.0.0\"}}")) + "]}";
        var resolver = CreateResolver();
        var declaration = resolver.Parse(Parse(json));
        var target = Version("1.0.0", AppSurfaceDocsVersionVisibility.Public, available: true, verified: true);

        var aliases = resolver.Resolve(declaration, [target]);

        Assert.Equal(count, aliases.Count);
        Assert.Equal("alias-0", aliases[0].Name);
        Assert.Equal($"alias-{count / 2}", aliases[count / 2].Name);
        Assert.Equal($"alias-{count - 1}", aliases[^1].Name);
        Assert.All(aliases, alias =>
        {
            Assert.Same(target, alias.TargetVersion);
            Assert.Equal("1.0.0", alias.TargetVersion?.Version);
            Assert.True(alias.IsAvailable);
        });
    }

    private static AppSurfaceDocsVersionAliasCatalogResolver CreateResolver(ILogger? logger = null)
    {
        return new AppSurfaceDocsVersionAliasCatalogResolver(
            new DocsUrlBuilder(new AppSurfaceDocsOptions()),
            logger ?? NullLogger.Instance);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AppSurfaceDocsResolvedVersion Version(
        string version,
        AppSurfaceDocsVersionVisibility visibility,
        bool available,
        bool verified)
    {
        return new AppSurfaceDocsResolvedVersion(
            version,
            version,
            null,
            "/trusted/shared",
            $"/docs/v/{version}",
            AppSurfaceDocsVersionSupportState.Current,
            visibility,
            AppSurfaceDocsVersionAdvisoryState.None,
            available,
            available ? null : "Unavailable",
            ArchiveVerificationState: !available
                ? AppSurfaceDocsReleaseArchiveVerificationState.Unavailable
                : verified
                    ? AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified
                    : AppSurfaceDocsReleaseArchiveVerificationState.AvailableUnverifiedLegacy,
            VerifiedReleaseArchive: verified
                ? new AppSurfaceDocsVerifiedReleaseArchive(
                    new Dictionary<string, AppSurfaceDocsReleaseArchiveFile>(StringComparer.Ordinal),
                    AppSurfaceDocsFrozenRouteManifest.Empty)
                : null);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
