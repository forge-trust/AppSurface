namespace ForgeTrust.AppSurface.Auth.Tests;

public sealed class AppSurfaceUserIdentityContractTests
{
    [Theory]
    [InlineData(AppSurfaceUserIdentityStatus.Resolved, 0)]
    [InlineData(AppSurfaceUserIdentityStatus.MissingSubject, 1)]
    [InlineData(AppSurfaceUserIdentityStatus.MalformedSubject, 2)]
    [InlineData(AppSurfaceUserIdentityStatus.DisabledAppUser, 3)]
    [InlineData(AppSurfaceUserIdentityStatus.StaleOrUnknownSession, 4)]
    [InlineData(AppSurfaceUserIdentityStatus.DuplicateMapping, 5)]
    [InlineData(AppSurfaceUserIdentityStatus.StoreUnavailable, 6)]
    [InlineData(AppSurfaceUserIdentityStatus.ProvisioningDenied, 7)]
    public void AppSurfaceUserIdentityStatus_NumericValues_AreStable(
        AppSurfaceUserIdentityStatus value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(null, "subject-1", "issuer")]
    [InlineData("", "subject-1", "issuer")]
    [InlineData("   ", "subject-1", "issuer")]
    [InlineData("issuer-1", null, "subject")]
    [InlineData("issuer-1", "", "subject")]
    [InlineData("issuer-1", "   ", "subject")]
    public void ExternalSubject_WhenRequiredValueIsMissing_Throws(
        string? issuer,
        string? subject,
        string expectedParameter)
    {
        var exception = issuer is null || subject is null
            ? Assert.Throws<ArgumentNullException>(() => new ExternalSubject(issuer!, subject!))
            : Assert.Throws<ArgumentException>(() => new ExternalSubject(issuer, subject));

        Assert.Equal(expectedParameter, exception.ParamName);
    }

    [Fact]
    public void ExternalSubject_UsesOrdinalTupleEqualityAndNormalizesBlankPartition()
    {
        var subject = new ExternalSubject("issuer", "subject", " ");
        var same = new ExternalSubject("issuer", "subject");
        var differentPartition = new ExternalSubject("issuer", "subject", "tenant-a");
        var differentCase = new ExternalSubject("ISSUER", "subject");

        Assert.Null(subject.PartitionKey);
        Assert.Equal(subject, same);
        Assert.True(subject == same);
        Assert.True(subject != differentPartition);
        Assert.NotEqual(subject, differentCase);
        Assert.NotEqual(subject, new ExternalSubject("issuer", "other-subject"));
    }

    [Fact]
    public void ExternalSubject_ToString_RedactsRawValues()
    {
        var subject = new ExternalSubject("issuer-secret", "subject-secret", "tenant-secret");
        var text = subject.ToString();

        Assert.Contains("ExternalSubject", text, StringComparison.Ordinal);
        Assert.Contains("present", text, StringComparison.Ordinal);
        Assert.DoesNotContain("issuer-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("subject-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalSubject_ToString_WhenPartitionIsMissing_ReportsNoneWithoutRawValues()
    {
        var subject = new ExternalSubject("issuer-secret", "subject-secret");
        var text = subject.ToString();

        Assert.Contains("ExternalSubject", text, StringComparison.Ordinal);
        Assert.Contains("none", text, StringComparison.Ordinal);
        Assert.DoesNotContain("issuer-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("subject-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalSubject_EqualsObjectAndDefaultHashCode_AreStable()
    {
        var subject = new ExternalSubject("issuer", "subject");
        object boxedSame = new ExternalSubject("issuer", "subject");
        object differentType = "issuer";

        Assert.True(subject.Equals(boxedSame));
        Assert.False(subject.Equals(differentType));
        Assert.False(object.Equals(subject, null));
        Assert.Equal(default(ExternalSubject).GetHashCode(), default(ExternalSubject).GetHashCode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AppUserId_WhenValueIsMissing_Throws(string? value)
    {
        if (value is null)
        {
            Assert.Throws<ArgumentNullException>(() => new AppUserId(value!));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new AppUserId(value));
        }
    }

    [Fact]
    public void AppUserId_UsesOrdinalEqualityAndRedactsToString()
    {
        var id = new AppUserId("app-user-secret");
        var same = new AppUserId("app-user-secret");
        var differentCase = new AppUserId("APP-USER-SECRET");

        Assert.Equal(id, same);
        Assert.True(id == same);
        Assert.True(id != differentCase);
        Assert.DoesNotContain("app-user-secret", id.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AppUserId_EqualsObjectAndDefaultHashCode_AreStable()
    {
        var id = new AppUserId("app-user");
        object boxedSame = new AppUserId("app-user");
        object differentType = "app-user";

        Assert.True(id.Equals(boxedSame));
        Assert.False(id.Equals(differentType));
        Assert.False(object.Equals(id, null));
        Assert.Equal(0, default(AppUserId).GetHashCode());
    }

    [Fact]
    public void ResolutionContext_NormalizesCorrelationAndCopiesMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tenant"] = "alpha",
        };

        var context = new AppSurfaceUserIdentityResolutionContext(" ", metadata);
        metadata["Tenant"] = "beta";

        Assert.Null(context.CorrelationId);
        Assert.Equal("alpha", context.Metadata["Tenant"]);
        Assert.False(context.Metadata.ContainsKey("tenant"));
        Assert.Empty(AppSurfaceUserIdentityResolutionContext.Empty.Metadata);
    }

    [Fact]
    public void ResolutionContext_PreservesNonblankCorrelationAndUsesEmptyMetadata()
    {
        var context = new AppSurfaceUserIdentityResolutionContext("correlation-1");

        Assert.Equal("correlation-1", context.CorrelationId);
        Assert.Empty(context.Metadata);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolutionContext_WhenMetadataKeyIsBlank_Throws(string key)
    {
        var metadata = new Dictionary<string, string> { [key] = "value" };

        Assert.Throws<ArgumentException>(() => new AppSurfaceUserIdentityResolutionContext(metadata: metadata));
    }

    [Fact]
    public void ResolutionContext_WhenMetadataValueIsNull_Throws()
    {
        var metadata = new Dictionary<string, string> { ["key"] = null! };

        Assert.Throws<ArgumentException>(() => new AppSurfaceUserIdentityResolutionContext(metadata: metadata));
    }

    [Fact]
    public void Result_Resolved_CapturesAppUserSubjectMessageAndMetadata()
    {
        var appUserId = new AppUserId("app-user-1");
        var subject = new ExternalSubject("issuer", "subject", "tenant-a");
        var metadata = new Dictionary<string, string> { ["source"] = "loaded" };

        var result = AppSurfaceUserIdentityResult.Resolved(
            appUserId,
            subject,
            " App user resolved ",
            metadata);
        metadata["source"] = "provisioned";

        Assert.True(result.Succeeded);
        Assert.Equal(AppSurfaceUserIdentityStatus.Resolved, result.Status);
        Assert.Equal(appUserId, result.AppUserId);
        Assert.Equal(subject, result.Subject);
        Assert.Equal(" App user resolved ", result.Message);
        Assert.Equal("loaded", result.Metadata["source"]);
    }

    [Theory]
    [InlineData(AppSurfaceUserIdentityStatus.MissingSubject)]
    [InlineData(AppSurfaceUserIdentityStatus.MalformedSubject)]
    [InlineData(AppSurfaceUserIdentityStatus.DisabledAppUser)]
    [InlineData(AppSurfaceUserIdentityStatus.StaleOrUnknownSession)]
    [InlineData(AppSurfaceUserIdentityStatus.DuplicateMapping)]
    [InlineData(AppSurfaceUserIdentityStatus.StoreUnavailable)]
    [InlineData(AppSurfaceUserIdentityStatus.ProvisioningDenied)]
    public void Result_FailureFactories_SetEveryStatus(AppSurfaceUserIdentityStatus status)
    {
        var subject = new ExternalSubject("issuer", "subject");
        var result = status switch
        {
            AppSurfaceUserIdentityStatus.MissingSubject =>
                AppSurfaceUserIdentityResult.MissingSubject(),
            AppSurfaceUserIdentityStatus.MalformedSubject =>
                AppSurfaceUserIdentityResult.MalformedSubject(),
            AppSurfaceUserIdentityStatus.DisabledAppUser =>
                AppSurfaceUserIdentityResult.DisabledAppUser(subject),
            AppSurfaceUserIdentityStatus.StaleOrUnknownSession =>
                AppSurfaceUserIdentityResult.StaleOrUnknownSession(subject),
            AppSurfaceUserIdentityStatus.DuplicateMapping =>
                AppSurfaceUserIdentityResult.DuplicateMapping(subject),
            AppSurfaceUserIdentityStatus.StoreUnavailable =>
                AppSurfaceUserIdentityResult.StoreUnavailable(subject),
            AppSurfaceUserIdentityStatus.ProvisioningDenied =>
                AppSurfaceUserIdentityResult.ProvisioningDenied(subject),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

        Assert.False(result.Succeeded);
        Assert.Equal(status, result.Status);
        Assert.Null(result.AppUserId);

        if (status is AppSurfaceUserIdentityStatus.MissingSubject or AppSurfaceUserIdentityStatus.MalformedSubject)
        {
            Assert.Null(result.Subject);
        }
        else
        {
            Assert.Equal(subject, result.Subject);
        }
    }

    [Fact]
    public void Result_FailureFactory_NormalizesMessageAndCopiesMetadata()
    {
        var metadata = new Dictionary<string, string> { ["reason"] = "invite_required" };

        var result = AppSurfaceUserIdentityResult.ProvisioningDenied(
            message: " ",
            metadata: metadata);
        metadata["reason"] = "changed";

        Assert.False(result.Succeeded);
        Assert.Equal(AppSurfaceUserIdentityStatus.ProvisioningDenied, result.Status);
        Assert.Null(result.Message);
        Assert.Equal("invite_required", result.Metadata["reason"]);
    }

    [Theory]
    [InlineData(AppSurfaceUserIdentityStatus.DisabledAppUser)]
    [InlineData(AppSurfaceUserIdentityStatus.StaleOrUnknownSession)]
    [InlineData(AppSurfaceUserIdentityStatus.DuplicateMapping)]
    [InlineData(AppSurfaceUserIdentityStatus.StoreUnavailable)]
    [InlineData(AppSurfaceUserIdentityStatus.ProvisioningDenied)]
    public void Result_OptionalSubjectFailureFactories_AllowMissingSubject(AppSurfaceUserIdentityStatus status)
    {
        var result = status switch
        {
            AppSurfaceUserIdentityStatus.DisabledAppUser =>
                AppSurfaceUserIdentityResult.DisabledAppUser(),
            AppSurfaceUserIdentityStatus.StaleOrUnknownSession =>
                AppSurfaceUserIdentityResult.StaleOrUnknownSession(),
            AppSurfaceUserIdentityStatus.DuplicateMapping =>
                AppSurfaceUserIdentityResult.DuplicateMapping(),
            AppSurfaceUserIdentityStatus.StoreUnavailable =>
                AppSurfaceUserIdentityResult.StoreUnavailable(),
            AppSurfaceUserIdentityStatus.ProvisioningDenied =>
                AppSurfaceUserIdentityResult.ProvisioningDenied(),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

        Assert.False(result.Succeeded);
        Assert.Equal(status, result.Status);
        Assert.Null(result.AppUserId);
        Assert.Null(result.Subject);
        Assert.Empty(result.Metadata);
    }

    [Fact]
    public void Result_WhenDefaultValueObjectsAreUsed_Throws()
    {
        var subject = new ExternalSubject("issuer", "subject");
        var appUserId = new AppUserId("app-user-1");

        Assert.Throws<ArgumentNullException>(
            () => AppSurfaceUserIdentityResult.Resolved(default, subject));
        Assert.Throws<ArgumentNullException>(
            () => AppSurfaceUserIdentityResult.Resolved(appUserId, default));
        Assert.Throws<ArgumentNullException>(
            () => AppSurfaceUserIdentityResult.DisabledAppUser(default(ExternalSubject)));
    }

    [Fact]
    public async Task ResolverContract_AllowsAppOwnedAsyncResolutionWithCancellation()
    {
        var appUserId = new AppUserId("app-user-1");
        var subject = new ExternalSubject("issuer", "subject");
        var context = new AppSurfaceUserIdentityResolutionContext(
            correlationId: "correlation-1",
            metadata: new Dictionary<string, string> { ["partition_hint"] = "tenant-a" });
        using var cancellation = new CancellationTokenSource();
        IAppSurfaceUserIdentityResolver resolver = new CapturingResolver(appUserId);

        var result = await resolver.ResolveAsync(subject, context, cancellation.Token);

        Assert.True(result.Succeeded);
        Assert.Equal(appUserId, result.AppUserId);
        Assert.Equal(subject, result.Subject);
        Assert.Equal("tenant-a", result.Metadata["partition_hint"]);
    }

    private sealed class CapturingResolver(AppUserId appUserId) : IAppSurfaceUserIdentityResolver
    {
        public ValueTask<AppSurfaceUserIdentityResult> ResolveAsync(
            ExternalSubject subject,
            AppSurfaceUserIdentityResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = AppSurfaceUserIdentityResult.Resolved(
                appUserId,
                subject,
                metadata: context.Metadata);

            return ValueTask.FromResult(result);
        }
    }
}
