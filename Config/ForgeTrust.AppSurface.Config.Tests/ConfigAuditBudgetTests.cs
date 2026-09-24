namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigAuditBudgetTests
{
    [Fact]
    public void NoticeLimit_DuplicateFromResolutionAndInventoryDoesNotSaturate()
    {
        using var scope = new ConfigResolutionScope(auditOptions: new ConfigResourceOptions { MaxNoticeIdentities = 1 });
        var key = AppSurfaceConfigKey.Parse("Feature:One");
        scope.AddNotice("provider", key, ConfigDiagnosticCatalog.LegacyAlias("FEATURE_ONE", key.Value), 1);
        scope.AddNotice("provider", AppSurfaceConfigKey.Parse("feature:one"), ConfigDiagnosticCatalog.LegacyAlias("FEATURE_ONE", "feature:one"), 1);

        Assert.Single(scope.Notices);
        Assert.Null(scope.IncompleteAuditDiagnostic);

        scope.AddNotice("provider", key, ConfigDiagnosticCatalog.LegacyAlias("feature_one", key.Value), 1);
        Assert.Single(scope.Notices);
        Assert.Equal("config-audit-notice-limit", scope.IncompleteAuditDiagnostic!.Code);
    }

    [Fact]
    public void NoticeLimit_RetainsBoundedEvidenceAndReportsOverflowWithoutValues()
    {
        using var scope = new ConfigResolutionScope(auditOptions: new ConfigResourceOptions { MaxNoticeIdentities = 1 });
        var first = AppSurfaceConfigKey.Parse("First:Key");
        var second = AppSurfaceConfigKey.Parse("Second:Key");
        scope.AddNotice("test", first, ConfigDiagnosticCatalog.LegacyAlias("FIRST_KEY", first.Value), 1);
        Assert.Null(scope.IncompleteAuditDiagnostic);

        scope.AddNotice("test", second, ConfigDiagnosticCatalog.LegacyAlias("SECOND_KEY", second.Value), 1);

        Assert.Equal(first, Assert.Single(scope.Notices).Key);
        var diagnostic = scope.IncompleteAuditDiagnostic;
        Assert.Equal("config-audit-notice-limit", diagnostic!.Code);
        Assert.DoesNotContain("SECOND_KEY", diagnostic.ToDisplayString(), StringComparison.Ordinal);
        scope.MarkAuditDeadline();
        Assert.Same(diagnostic, scope.IncompleteAuditDiagnostic);
    }

    [Fact]
    public void NonAudit_DoesNotRetainBudgetState()
    {
        using var scope = new ConfigResolutionScope();
        Assert.False(scope.IsAudit);
        for (var i = 0; i < 300; i++)
        {
            Assert.True(scope.TryAcquireRemoteLookup(out var lease, out var diagnostic));
            Assert.Null(lease);
            Assert.Null(diagnostic);
        }
        Assert.Null(scope.IncompleteAuditDiagnostic);
    }

    [Fact]
    public void LookupLimit_RejectsBeforeRemoteWorkAndRetainsFirstFailure()
    {
        using var scope = new ConfigResolutionScope(auditOptions: new ConfigResourceOptions { MaxAuditRemoteLookups = 1 });
        Assert.True(scope.IsAudit);
        Assert.True(scope.TryAcquireRemoteLookup(out var lease, out _));
        lease!.Dispose();
        lease.Dispose();
        Assert.False(scope.TryAcquireRemoteLookup(out var rejected, out var diagnostic));
        Assert.Null(rejected);
        Assert.Equal("config-audit-remote-lookup-limit", diagnostic!.Code);
        Assert.Same(diagnostic, scope.IncompleteAuditDiagnostic);
        Assert.Equal("config-audit-deadline", scope.MarkAuditDeadline().Code);
        Assert.Same(diagnostic, scope.IncompleteAuditDiagnostic);
    }

    [Fact]
    public async Task SemaphoreWait_EndsAtAggregateDeadline()
    {
        using var scope = new ConfigResolutionScope(auditOptions: new ConfigResourceOptions
        {
            AuditTimeout = TimeSpan.FromMilliseconds(100),
            MaxAuditConcurrency = 1
        });
        Assert.True(scope.TryAcquireRemoteLookup(out var first, out _));
        using (first)
        {
            var diagnostic = await Task.Run(() =>
            {
                Assert.False(scope.TryAcquireRemoteLookup(out var lease, out var failure));
                Assert.Null(lease);
                return failure;
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("config-audit-deadline", diagnostic!.Code);
            Assert.False(scope.TryAcquireRemoteLookup(out _, out var repeated));
            Assert.Equal(diagnostic.Code, repeated!.Code);
        }
    }

    [Fact]
    public void CallerCancellation_RemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var scope = new ConfigResolutionScope(cancellation.Token, new ConfigResourceOptions());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => scope.TryAcquireRemoteLookup(out _, out _));
        Assert.Throws<OperationCanceledException>(() => scope.MarkAuditDeadline());
        Assert.Null(scope.IncompleteAuditDiagnostic);
    }

    [Fact]
    public async Task ConcurrentReservations_AdmitOnlyConfiguredCount()
    {
        using var scope = new ConfigResolutionScope(auditOptions: new ConfigResourceOptions { MaxAuditRemoteLookups = 8 });
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            var admitted = scope.TryAcquireRemoteLookup(out var lease, out _);
            lease?.Dispose();
            return admitted;
        })));
        Assert.Equal(8, results.Count(admitted => admitted));
        Assert.Equal("config-audit-remote-lookup-limit", scope.IncompleteAuditDiagnostic!.Code);
    }
}
