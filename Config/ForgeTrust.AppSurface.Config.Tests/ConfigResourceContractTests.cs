using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigResourceContractTests
{
    public static IEnumerable<object[]> InvalidLimits()
    {
        yield return [new ConfigResourceOptions { MaxFileBytes = 0 }];
        yield return [new ConfigResourceOptions { MaxFilesPerEnvironment = -1 }];
        yield return [new ConfigResourceOptions { MaxEnvironmentEntries = 0 }];
        yield return [new ConfigResourceOptions { MaxEnvironmentBytes = 0 }];
        yield return [new ConfigResourceOptions { MaxNoticeIdentities = 0 }];
        yield return [new ConfigResourceOptions { MaxRenderedIdentifierCharacters = 0 }];
        yield return [new ConfigResourceOptions { MaxBindingDepth = 0 }];
        yield return [new ConfigResourceOptions { AuditTimeout = TimeSpan.Zero }];
        yield return [new ConfigResourceOptions { AuditTimeout = TimeSpan.FromDays(60) }];
        yield return [new ConfigResourceOptions { MaxAuditRemoteLookups = 0 }];
        yield return [new ConfigResourceOptions { MaxAuditConcurrency = 0 }];
    }

    [Theory]
    [MemberData(nameof(InvalidLimits))]
    public void Limits_RejectEveryNonpositiveOption(ConfigResourceOptions options) =>
        Assert.Throws<OptionsValidationException>(() => options.Snapshot());

    [Fact]
    public void Limits_HaveDocumentedDefaultsAndSnapshotOwnership()
    {
        var options = new ConfigResourceOptions();
        var copy = options.Snapshot();
        Assert.NotSame(options, copy);
        Assert.Equal(16 * 1024 * 1024, copy.MaxFileBytes);
        Assert.Equal(256, copy.MaxFilesPerEnvironment);
        Assert.Equal(65_536, copy.MaxEnvironmentEntries);
        Assert.Equal(64 * 1024 * 1024, copy.MaxEnvironmentBytes);
        Assert.Equal(4096, copy.MaxNoticeIdentities);
        Assert.Equal(256, copy.MaxRenderedIdentifierCharacters);
        Assert.Equal(32, copy.MaxBindingDepth);
        Assert.Equal(TimeSpan.FromSeconds(30), copy.AuditTimeout);
        Assert.Equal(256, copy.MaxAuditRemoteLookups);
        Assert.Equal(4, copy.MaxAuditConcurrency);
        options.MaxFileBytes = 1;
        Assert.Equal(16 * 1024 * 1024, copy.MaxFileBytes);
    }

    [Fact]
    public async Task Limits_ValidateOnStartRejectsInvalidOptions()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddOptions<ConfigResourceOptions>()
                    .Configure(options => options.MaxFileBytes = 0)
                    .Validate(options =>
                    {
                        _ = options.Snapshot();
                        return true;
                    }, "Configuration resource limits must be positive.")
                    .ValidateOnStart();
            })
            .Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Theory]
    [InlineData("ordinary", "ordinary")]
    [InlineData("a\nb\r\t", "a\\u000ab\\u000d\\u0009")]
    [InlineData("a\u202eb\u2028c\u2029", "a\\u202eb\\u2028c\\u2029")]
    [InlineData(null, "(none)")]
    public void Identifiers_EscapeUntrustedControlAndFormatCharacters(string? input, string expected) =>
        Assert.Equal(expected, ConfigDiagnosticText.Identifier(input));

    [Fact]
    public void Identifiers_BoundRenderingAndAppendStableDistinctDigest()
    {
        Assert.Equal("abcd", ConfigDiagnosticText.Identifier("abcd", 4));
        var first = ConfigDiagnosticText.Identifier("abcde", 4);
        Assert.StartsWith("abcd…#", first);
        Assert.Equal(22, first.Length);
        Assert.Equal(first, ConfigDiagnosticText.Identifier("abcde", 4));
        Assert.NotEqual(first, ConfigDiagnosticText.Identifier("abcdf", 4));
        Assert.StartsWith("…#", ConfigDiagnosticText.Identifier("\n", 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfigDiagnosticText.Identifier("a", 0));
    }

    [Fact]
    public void Catalog_RejectsUntrustedCodeAndContainsCompleteGuidance()
    {
        var diagnostic = ConfigDiagnosticCatalog.Terminal("SENTINEL_SECRET");
        Assert.Equal("config-provider-failed", diagnostic.Code);
        Assert.DoesNotContain("SENTINEL_SECRET", diagnostic.ToDisplayString());
        Assert.Equal("config-key-collision", ConfigDiagnosticCatalog.SafeCode("config-key-collision", "fallback"));
        Assert.Equal("fallback", ConfigDiagnosticCatalog.SafeCode("SENTINEL_SECRET", "fallback"));
        var capture = ConfigDiagnosticCatalog.Terminal("config-environment-snapshot-failed", "ID\n");
        Assert.True(capture.Retryable);
        Assert.Contains("ID\\u000a", capture.Cause);
        Assert.NotNull(capture.Docs);
        Assert.Equal("config-key-legacy-provider-alias", ConfigDiagnosticCatalog.SafeCode("config-key-legacy-provider-alias", "fallback"));
        Assert.Equal("config-key-legacy-dot-path", ConfigDiagnosticCatalog.SafeCode("config-key-legacy-dot-path", "fallback"));
        var alias = ConfigDiagnosticCatalog.LegacyAlias("OLD", "NEW");
        Assert.Equal("OLD", alias.SafeSourceIdentifier);
        Assert.Contains("NEW", alias.Fix);
        var legacy = ConfigDiagnosticCatalog.LegacyDot(AppSurfaceConfigKey.Parse("A:B").WithInput(ConfigKeyInputOrigin.TranslatedDot, "A.B"));
        Assert.Contains("A.B", legacy.Cause);
        Assert.Contains("A:B", legacy.Fix);
    }

    [Fact]
    public async Task ScopeNoticeCapacityRemainsBoundedUnderConcurrency()
    {
        using var scope = new ConfigResolutionScope();
        var key = AppSurfaceConfigKey.Parse("A");
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
            scope.AddNotice("provider", key, ConfigDiagnosticCatalog.LegacyAlias($"old-{index}", "new"), 5))));
        Assert.Equal(5, scope.Notices.Count);
        Assert.Equal(5, scope.Notices.Select(item => item.Notice.SafeSourceIdentifier).Distinct().Count());
        Assert.All(scope.Notices, item => Assert.Same(key, item.Key));
        Assert.Equal("config-audit-notice-limit", scope.IncompleteAuditDiagnostic!.Code);
        Assert.False(scope.CancellationToken.IsCancellationRequested);
        using var cancellation = new CancellationTokenSource();
        using var cancelledScope = new ConfigResolutionScope(cancellation.Token);
        cancellation.Cancel();
        Assert.True(cancelledScope.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void ScopeNoticeSnapshotsStayStableWhenCollectionContinues()
    {
        using var scope = new ConfigResolutionScope();
        var key = AppSurfaceConfigKey.Parse("A");
        var notice = ConfigDiagnosticCatalog.LegacyAlias("old", "new");
        scope.AddNotice("provider", key, notice, 2);
        var first = scope.Notices;
        scope.AddNotice("provider", key, ConfigDiagnosticCatalog.LegacyAlias("second", "new"), 2);
        scope.AddNotice("provider", key, ConfigDiagnosticCatalog.LegacyAlias("third", "new"), 2);
        Assert.Single(first);
        Assert.Same(notice, first[0].Notice);
        Assert.Equal(2, scope.Notices.Count);
        Assert.Equal("config-audit-notice-limit", scope.IncompleteAuditDiagnostic!.Code);
    }
}
