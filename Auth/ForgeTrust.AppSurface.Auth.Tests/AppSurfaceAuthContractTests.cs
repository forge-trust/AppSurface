namespace ForgeTrust.AppSurface.Auth.Tests;

public sealed class AppSurfaceAuthContractTests
{
    [Theory]
    [InlineData(AppSurfaceAuthOutcome.Allowed, 0)]
    [InlineData(AppSurfaceAuthOutcome.Challenge, 1)]
    [InlineData(AppSurfaceAuthOutcome.Forbid, 2)]
    [InlineData(AppSurfaceAuthOutcome.SetupFailure, 3)]
    [InlineData(AppSurfaceAuthOutcome.UnsafeNavigation, 4)]
    [InlineData(AppSurfaceAuthOutcome.StaleOrUnknownSession, 5)]
    public void AppSurfaceAuthOutcome_NumericValues_AreStable(AppSurfaceAuthOutcome value, int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(AppSurfaceAuthReason.None, 0)]
    [InlineData(AppSurfaceAuthReason.Unauthenticated, 1)]
    [InlineData(AppSurfaceAuthReason.Forbidden, 2)]
    [InlineData(AppSurfaceAuthReason.MissingPolicy, 3)]
    [InlineData(AppSurfaceAuthReason.MissingServices, 4)]
    [InlineData(AppSurfaceAuthReason.UnsafeReturnUrl, 5)]
    [InlineData(AppSurfaceAuthReason.StaleOrUnknownSession, 6)]
    [InlineData(AppSurfaceAuthReason.MissingSubject, 7)]
    public void AppSurfaceAuthReason_NumericValues_AreStable(AppSurfaceAuthReason value, int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Fact]
    public void User_PreservesRequiredIdAndNormalizesOptionalText()
    {
        var user = new AppSurfaceUser(" user-1 ", displayName: "  Ada  ", email: " ");

        Assert.Equal(" user-1 ", user.Id);
        Assert.Equal("  Ada  ", user.DisplayName);
        Assert.Null(user.Email);
        Assert.Empty(user.Metadata);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void User_WhenIdIsMissing_Throws(string? id)
    {
        if (id is null)
        {
            Assert.Throws<ArgumentNullException>(() => new AppSurfaceUser(id!));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new AppSurfaceUser(id));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Session_WhenIdIsMissing_Throws(string? id)
    {
        if (id is null)
        {
            Assert.Throws<ArgumentNullException>(() => new AppSurfaceSession(id!));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new AppSurfaceSession(id));
        }
    }

    [Fact]
    public void Session_WhenExpirationPrecedesStart_Throws()
    {
        var startedAt = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.FromHours(-4));
        var expiresAt = startedAt.AddSeconds(-1);

        var error = Assert.Throws<ArgumentException>(
            () => new AppSurfaceSession("session-1", startedAt, expiresAt));

        Assert.Equal("expiresAt", error.ParamName);
    }

    [Fact]
    public void Session_PreservesTimestampOffsets()
    {
        var startedAt = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.FromHours(-4));
        var expiresAt = startedAt.AddMinutes(30);

        var session = new AppSurfaceSession(
            "session-1",
            startedAt,
            expiresAt,
            new Dictionary<string, string> { ["issuer"] = "host" });

        Assert.Equal("session-1", session.Id);
        Assert.Equal(startedAt, session.StartedAt);
        Assert.Equal(expiresAt, session.ExpiresAt);
        Assert.Equal("host", session.Metadata["issuer"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Session_AllowsPartialTimestampPairs(bool includeStartedAt)
    {
        var timestamp = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);

        var session = includeStartedAt
            ? new AppSurfaceSession("session-1", startedAt: timestamp)
            : new AppSurfaceSession("session-1", expiresAt: timestamp);

        Assert.Equal(includeStartedAt ? timestamp : null, session.StartedAt);
        Assert.Equal(includeStartedAt ? null : timestamp, session.ExpiresAt);
    }

    [Fact]
    public void Context_WhenUserIsNull_IsAnonymous()
    {
        var context = new AppSurfaceAuthContext();

        Assert.Null(context.User);
        Assert.Null(context.Session);
        Assert.False(context.IsAuthenticated);
    }

    [Fact]
    public void Context_WhenUserAndSessionArePresent_IsAuthenticated()
    {
        var user = new AppSurfaceUser("user-1");
        var session = new AppSurfaceSession("session-1");

        var context = new AppSurfaceAuthContext(
            user,
            session,
            new Dictionary<string, string> { ["tenant"] = "alpha" });

        Assert.Same(user, context.User);
        Assert.Same(session, context.Session);
        Assert.True(context.IsAuthenticated);
        Assert.Equal("alpha", context.Metadata["tenant"]);
        Assert.Null(AppSurfaceAuthContext.Anonymous.User);
        Assert.Empty(AppSurfaceAuthContext.Anonymous.Metadata);
    }

    [Fact]
    public void Metadata_IsCopiedWithOrdinalKeys()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tenant"] = "alpha",
            ["Role"] = "reader",
        };

        var user = new AppSurfaceUser("user-1", metadata: metadata);
        metadata["Tenant"] = "beta";

        Assert.Equal("alpha", user.Metadata["Tenant"]);
        Assert.True(user.Metadata.ContainsKey("Tenant"));
        Assert.False(user.Metadata.ContainsKey("tenant"));
    }

    [Fact]
    public void Metadata_WhenReadOnlyViewIsMutated_Throws()
    {
        var user = new AppSurfaceUser("user-1", metadata: new Dictionary<string, string> { ["one"] = "two" });
        var writableView = Assert.IsAssignableFrom<IDictionary<string, string>>(user.Metadata);

        Assert.Throws<NotSupportedException>(() => writableView.Add("three", "four"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Metadata_WhenKeyIsBlank_Throws(string key)
    {
        var metadata = new Dictionary<string, string> { [key] = "value" };

        Assert.Throws<ArgumentException>(() => new AppSurfaceUser("user-1", metadata: metadata));
    }

    [Fact]
    public void Metadata_WhenValueIsNull_Throws()
    {
        var metadata = new Dictionary<string, string> { ["key"] = null! };

        Assert.Throws<ArgumentException>(() => new AppSurfaceUser("user-1", metadata: metadata));
    }

    [Fact]
    public void ResultFactories_SetExpectedOutcomesAndReasons()
    {
        var allowed = AppSurfaceAuthResult.Allowed();
        var challenge = AppSurfaceAuthResult.Challenge();
        var unauthenticated = AppSurfaceAuthResult.Unauthenticated();
        var forbid = AppSurfaceAuthResult.Forbid();
        var forbidden = AppSurfaceAuthResult.Forbidden();
        var missingPolicy = AppSurfaceAuthResult.MissingPolicy();
        var missingServices = AppSurfaceAuthResult.MissingServices();
        var missingSubject = AppSurfaceAuthResult.MissingSubject();
        var unsafeReturnUrl = AppSurfaceAuthResult.UnsafeReturnUrl();
        var staleSession = AppSurfaceAuthResult.StaleOrUnknownSession();

        AssertResult(allowed, AppSurfaceAuthOutcome.Allowed, AppSurfaceAuthReason.None);
        AssertResult(challenge, AppSurfaceAuthOutcome.Challenge, AppSurfaceAuthReason.Unauthenticated);
        AssertResult(unauthenticated, AppSurfaceAuthOutcome.Challenge, AppSurfaceAuthReason.Unauthenticated);
        AssertResult(forbid, AppSurfaceAuthOutcome.Forbid, AppSurfaceAuthReason.Forbidden);
        AssertResult(forbidden, AppSurfaceAuthOutcome.Forbid, AppSurfaceAuthReason.Forbidden);
        AssertResult(missingPolicy, AppSurfaceAuthOutcome.SetupFailure, AppSurfaceAuthReason.MissingPolicy);
        AssertResult(missingServices, AppSurfaceAuthOutcome.SetupFailure, AppSurfaceAuthReason.MissingServices);
        AssertResult(missingSubject, AppSurfaceAuthOutcome.SetupFailure, AppSurfaceAuthReason.MissingSubject);
        AssertResult(unsafeReturnUrl, AppSurfaceAuthOutcome.UnsafeNavigation, AppSurfaceAuthReason.UnsafeReturnUrl);
        AssertResult(staleSession, AppSurfaceAuthOutcome.StaleOrUnknownSession, AppSurfaceAuthReason.StaleOrUnknownSession);
    }

    [Fact]
    public void Result_ExposesHelperProperties()
    {
        var context = AppSurfaceAuthContext.Anonymous;
        var allowed = AppSurfaceAuthResult.Allowed(
            context,
            " Access granted ",
            new Dictionary<string, string> { ["policy"] = "reader" });

        Assert.Same(context, allowed.Context);
        Assert.Equal(" Access granted ", allowed.Message);
        Assert.Equal("reader", allowed.Metadata["policy"]);
        Assert.True(allowed.IsAllowed);
        Assert.Null(AppSurfaceAuthResult.Forbid(message: " ").Message);
        Assert.True(AppSurfaceAuthResult.Challenge().RequiresAuthentication);
        Assert.True(AppSurfaceAuthResult.MissingPolicy().IsConfigurationFailure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AuditEvent_WhenNameIsMissing_Throws(string? name)
    {
        var timestamp = DateTimeOffset.UtcNow;

        if (name is null)
        {
            Assert.Throws<ArgumentNullException>(() => new AppSurfaceAuthAuditEvent(
                name!,
                timestamp,
                AppSurfaceAuthOutcome.Allowed,
                AppSurfaceAuthReason.None));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new AppSurfaceAuthAuditEvent(
                name,
                timestamp,
                AppSurfaceAuthOutcome.Allowed,
                AppSurfaceAuthReason.None));
        }
    }

    [Fact]
    public void AuditEvent_WhenOutcomeAndReasonConflict_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AppSurfaceAuthAuditEvent(
            "auth.allowed",
            DateTimeOffset.UtcNow,
            AppSurfaceAuthOutcome.Allowed,
            AppSurfaceAuthReason.Forbidden));
    }

    [Fact]
    public void AuditEvent_WhenForbidOutcomeHasWrongReason_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AppSurfaceAuthAuditEvent(
            "auth.denied",
            DateTimeOffset.UtcNow,
            AppSurfaceAuthOutcome.Forbid,
            AppSurfaceAuthReason.Unauthenticated));
    }

    [Fact]
    public void AuditEvent_WhenOutcomeIsUnknown_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AppSurfaceAuthAuditEvent(
            "auth.unknown",
            DateTimeOffset.UtcNow,
            (AppSurfaceAuthOutcome)999,
            AppSurfaceAuthReason.None));
    }

    [Fact]
    public void AuditEvent_IsPassiveValue()
    {
        var timestamp = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero);

        var auditEvent = new AppSurfaceAuthAuditEvent(
            "auth.denied",
            timestamp,
            AppSurfaceAuthOutcome.Forbid,
            AppSurfaceAuthReason.Forbidden,
            userId: " user-1 ",
            sessionId: " ",
            metadata: new Dictionary<string, string> { [AppSurfaceAuthMetadataKeys.CorrelationId] = "corr-1" });

        Assert.Equal("auth.denied", auditEvent.Name);
        Assert.Equal(timestamp, auditEvent.Timestamp);
        Assert.Equal(AppSurfaceAuthOutcome.Forbid, auditEvent.Outcome);
        Assert.Equal(AppSurfaceAuthReason.Forbidden, auditEvent.Reason);
        Assert.Equal(" user-1 ", auditEvent.UserId);
        Assert.Null(auditEvent.SessionId);
        Assert.Equal("corr-1", auditEvent.Metadata[AppSurfaceAuthMetadataKeys.CorrelationId]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("/", "/")]
    [InlineData("/account/login", "/account/login")]
    public void LoginPrompt_NormalizesSafeTargets(string? targetPath, string? expected)
    {
        var prompt = new AppSurfaceLoginPrompt(
            targetPath,
            displayText: " Sign in ",
            metadata: new Dictionary<string, string> { ["provider"] = "oidc" });

        Assert.Equal(expected, prompt.TargetPath);
        Assert.Equal(" Sign in ", prompt.DisplayText);
        Assert.Equal("oidc", prompt.Metadata["provider"]);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("//example.com")]
    [InlineData("/\\example")]
    [InlineData("/some\\path")]
    [InlineData("/line\rbreak")]
    [InlineData("/line\nbreak")]
    public void LoginPrompt_WhenTargetIsUnsafe_Throws(string targetPath)
    {
        Assert.Throws<ArgumentException>(() => new AppSurfaceLoginPrompt(targetPath));
    }

    [Fact]
    public void LogoutPrompt_IsPassiveAndUsesSameTargetPolicy()
    {
        var prompt = new AppSurfaceLogoutPrompt(
            "/signed-out",
            displayText: " Sign out ",
            metadata: new Dictionary<string, string> { ["provider"] = "oidc" });

        Assert.Equal("/signed-out", prompt.TargetPath);
        Assert.Equal(" Sign out ", prompt.DisplayText);
        Assert.Equal("oidc", prompt.Metadata["provider"]);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("//example.com")]
    [InlineData("/\\example")]
    [InlineData("/some\\path")]
    [InlineData("/line\rbreak")]
    [InlineData("/line\nbreak")]
    public void LogoutPrompt_WhenTargetIsUnsafe_Throws(string targetPath)
    {
        Assert.Throws<ArgumentException>(() => new AppSurfaceLogoutPrompt(targetPath));
    }

    [Fact]
    public void AuthAssembly_DoesNotReferenceAspNetCore()
    {
        var referencedAssemblies = typeof(AppSurfaceAuthResult).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(
            referencedAssemblies,
            assembly => assembly.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }

    private static void AssertResult(
        AppSurfaceAuthResult result,
        AppSurfaceAuthOutcome expectedOutcome,
        AppSurfaceAuthReason expectedReason)
    {
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedReason, result.Reason);
    }
}
