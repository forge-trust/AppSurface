using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigSecretContractTests
{
    [Fact]
    public void RawResolution_RejectsOversizedAndControlCharacterProviderNames()
    {
        Assert.Throws<ArgumentException>(() => ConfigCompositionValueResolution.Missing(new string('p', 129), 1));
        Assert.Throws<ArgumentException>(() => ConfigCompositionValueResolution.Unclaimed("bad\nname", 1));
        Assert.Equal(ConfigCompositionValueResolutionStatus.Missing,
            ConfigCompositionValueResolution.Missing(new string('p', 128), 1).Status);
    }

    [Fact]
    public void LogicalPath_DottedSpellingJoinsParsedSegments()
    {
        var path = ConfigLogicalPath.Parse("Service:Nested.Token");

        Assert.Equal("Service.Nested.Token", path.Dotted);
        Assert.Equal("Service:Nested:Token", path.Canonical);
    }

    [Theory]
    [InlineData(ConfigSecretReferenceValidationStatus.Supported, "secret-reference-supported")]
    [InlineData(ConfigSecretReferenceValidationStatus.Unclaimed, "secret-reference-unsupported")]
    [InlineData(ConfigSecretReferenceValidationStatus.Invalid, "secret-reference-invalid")]
    public void ReferenceValidationFactories_ExposeStableStatusAndCode(
        ConfigSecretReferenceValidationStatus status, string expectedCode)
    {
        var validation = status switch
        {
            ConfigSecretReferenceValidationStatus.Supported => ConfigSecretReferenceValidation.Supported(),
            ConfigSecretReferenceValidationStatus.Unclaimed => ConfigSecretReferenceValidation.Unclaimed(),
            ConfigSecretReferenceValidationStatus.Invalid => ConfigSecretReferenceValidation.Invalid(),
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

        Assert.Equal(status, validation.Status);
        Assert.Equal(expectedCode, validation.Code);
        Assert.Equal(expectedCode, validation.ToString());
    }

    [Fact]
    public void ProviderResolutionFactories_ExposeStatusesRetryabilityAndSafeMetadata()
    {
        var source = ConfigSecretSourceMetadata.WithSafeCorrelationToken(
            "google-secret-manager", "remote", "opaque_token-1");

        var resolved = ConfigSecretProviderResolution.Resolved("secret-value", source);
        Assert.Equal(ConfigSecretProviderResolutionStatus.Resolved, resolved.Status);
        Assert.Equal("google-secret-manager", resolved.ProviderId);
        Assert.False(resolved.Retryable);
        Assert.Same(source, resolved.Source);

        Assert.Equal(ConfigSecretProviderResolutionStatus.Unclaimed,
            ConfigSecretProviderResolution.Unclaimed("provider").Status);
        Assert.Equal(ConfigSecretProviderResolutionStatus.Missing,
            ConfigSecretProviderResolution.Missing("provider").Status);
        Assert.Equal(ConfigSecretProviderResolutionStatus.AccessDenied,
            ConfigSecretProviderResolution.AccessDenied("provider").Status);
        Assert.Equal(ConfigSecretProviderResolutionStatus.InvalidReference,
            ConfigSecretProviderResolution.InvalidReference("provider").Status);
        Assert.Equal(ConfigSecretProviderResolutionStatus.ProviderFailed,
            ConfigSecretProviderResolution.ProviderFailed("provider").Status);

        var unavailable = ConfigSecretProviderResolution.Unavailable("provider");
        Assert.Equal(ConfigSecretProviderResolutionStatus.Unavailable, unavailable.Status);
        Assert.True(unavailable.Retryable);
    }

    [Fact]
    public void ProviderResolution_ResolvedAcceptsEmptyPayloadButRejectsNulls()
    {
        var source = ConfigSecretSourceMetadata.Create("provider", "custom");
        var empty = ConfigSecretProviderResolution.Resolved(string.Empty, source);

        Assert.Equal(ConfigSecretProviderResolutionStatus.Resolved, empty.Status);
        Assert.DoesNotContain("secret", empty.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentNullException>(() =>
            ConfigSecretProviderResolution.Resolved(null!, source));
        Assert.Throws<ArgumentNullException>(() =>
            ConfigSecretProviderResolution.Resolved("value", null!));
    }

    [Fact]
    public void SecretMetadata_UsesAllowlistedSourceKindsAndSafeCorrelationTokens()
    {
        var withoutToken = ConfigSecretSourceMetadata.Create("provider", "local");
        var withToken = ConfigSecretSourceMetadata.WithSafeCorrelationToken(
            "provider", "custom", "A1_opaque-token");

        Assert.Equal("provider", withoutToken.ProviderId);
        Assert.Equal("local", withoutToken.SourceKind);
        Assert.Null(withoutToken.CorrelationToken);
        Assert.Equal("A1_opaque-token", withToken.CorrelationToken);
        Assert.Equal("provider (custom)", withToken.ToString());

        foreach (var sourceKind in new[] { "remote", "local", "custom" })
            Assert.Equal(sourceKind, ConfigSecretSourceMetadata.Create("provider", sourceKind).SourceKind);

        Assert.Throws<ArgumentException>(() => ConfigSecretSourceMetadata.Create("provider", "database"));
        Assert.Throws<ArgumentException>(() => ConfigSecretSourceMetadata.WithSafeCorrelationToken("provider", "custom", "short"));
        Assert.Throws<ArgumentException>(() => ConfigSecretSourceMetadata.WithSafeCorrelationToken("provider", "custom", new string('a', 65)));
        Assert.Throws<ArgumentException>(() => ConfigSecretSourceMetadata.WithSafeCorrelationToken("provider", "custom", "unsafe.token"));
        Assert.Throws<ArgumentException>(() => ConfigSecretSourceMetadata.WithSafeCorrelationToken("provider", "custom", "unsafe token"));
        Assert.Throws<ArgumentException>(() => ConfigSecretSourceMetadata.WithSafeCorrelationToken("provider", "custom", null!));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("google-secret-manager")]
    [InlineData("a1-b2")]
    public void ProviderIds_AcceptLowercaseKebabCase(string providerId)
    {
        Assert.Equal(providerId, ConfigSecretSourceMetadata.Create(providerId).ProviderId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Provider")]
    [InlineData("-provider")]
    [InlineData("provider-")]
    [InlineData("provider--id")]
    [InlineData("provider_id")]
    [InlineData("provider.id")]
    [InlineData("provider id")]
    public void ProviderIds_RejectNonCanonicalForms(string providerId)
    {
        Assert.Throws<ArgumentException>(() => ConfigSecretSourceMetadata.Create(providerId));
    }

    [Fact]
    public void PublicContracts_KeepOpaqueKeyVersionAndPayloadOutOfTextAndJson()
    {
        var claim = new ConfigSecretConfiguredClaim(
            ConfigSecretConfiguredClaimKind.ExactMapping,
            "Service:ApiKey",
            "provider",
            "projects/secret-resource",
            "version-7");
        var reference = new ConfigSecretReference(
            "Production",
            "Service:ApiKey",
            "projects/secret-resource",
            "version-7");
        var resolution = ConfigSecretProviderResolution.Resolved(
            "secret-payload",
            ConfigSecretSourceMetadata.Create("provider"));

        foreach (var text in new[] { claim.ToString(), reference.ToString(), resolution.ToString(), JsonSerializer.Serialize(claim), JsonSerializer.Serialize(reference), JsonSerializer.Serialize(resolution) })
        {
            Assert.DoesNotContain("projects/secret-resource", text, StringComparison.Ordinal);
            Assert.DoesNotContain("version-7", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-payload", text, StringComparison.Ordinal);
        }

        var claimJson = JsonSerializer.Serialize(claim);
        var referenceJson = JsonSerializer.Serialize(reference);
        Assert.DoesNotContain("\"Key\"", claimJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Version\"", claimJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Key\"", referenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Version\"", referenceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolutionContext_RemainingIsMonotonicAndNeverNegative()
    {
        var time = new ManualTimeProvider();
        var context = new ConfigSecretResolutionContext(time, TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(5), context.Remaining);
        time.Advance(TimeSpan.FromSeconds(2));
        var afterTwoSeconds = context.Remaining;
        time.Advance(TimeSpan.FromSeconds(4));
        var afterDeadline = context.Remaining;
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(3), afterTwoSeconds);
        Assert.Equal(TimeSpan.Zero, afterDeadline);
        Assert.Equal(TimeSpan.Zero, context.Remaining);
        Assert.True(afterTwoSeconds >= afterDeadline);
    }

    [Fact]
    public void OptionsValidator_RequiresEveryBoundToBePositiveAndKeepsDefaultsValid()
    {
        var validator = new AppSurfaceConfigOptionsValidator();
        var defaults = new AppSurfaceConfigOptions();

        Assert.True(validator.Validate(Options.DefaultName, defaults).Succeeded);

        Assert.False(validator.Validate(Options.DefaultName, new AppSurfaceConfigOptions { ProviderlessResolutionBudget = TimeSpan.Zero }).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new AppSurfaceConfigOptions { ProviderlessResolutionBudget = TimeSpan.FromSeconds(-1) }).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new AppSurfaceConfigOptions { MaxCompositionGraphDepth = 0 }).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new AppSurfaceConfigOptions { MaxCompositionGraphNodes = -1 }).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new AppSurfaceConfigOptions { MaxSecretDestinationsPerRoot = 0 }).Succeeded);
    }

    [Fact]
    public void CompositionException_SnapshotsAndOrdersCatalogFailuresWithoutSensitiveText()
    {
        var source = new ConfigAuditSourceRecord
        {
            Kind = ConfigAuditSourceKind.File,
            Role = ConfigAuditSourceRole.Base,
            FilePath = "/config/appsettings.json",
            ConfigPath = "Service:ApiKey"
        };
        var later = new ConfigCompositionFailure(
            "Service:Zed", "secret-provider-unavailable", "provider-b", retryable: true, source);
        var earlier = new ConfigCompositionFailure(
            "Service:ApiKey", "secret-provider-failed", "provider-a");

        var exception = new ConfigurationCompositionException(
            "Production", "Service", [later, earlier]);

        Assert.Equal("Production", exception.EnvironmentName);
        Assert.Equal("Service", exception.RootKey);
        Assert.Equal(2, exception.Failures.Count);
        Assert.Same(earlier, exception.Failures[0]);
        Assert.Same(later, exception.Failures[1]);
        Assert.Same(source, exception.Failures[1].Source);
        Assert.True(exception.Failures[1].Retryable);
        Assert.NotNull(exception.Failures[0].Problem);
        Assert.NotNull(exception.Failures[0].Cause);
        Assert.NotNull(exception.Failures[0].Fix);
        Assert.Contains("secret-provider-failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("https://appsurface.dev/config/secret-references#provider-failures", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("projects/secret-resource", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void CompositionException_RejectsNullEmptyAndNonemptyInputViolations()
    {
        var failure = new ConfigCompositionFailure("Service:ApiKey", "secret-not-found");

        Assert.Throws<ArgumentNullException>(() =>
            new ConfigurationCompositionException("Production", "Service", null!));
        Assert.Throws<ArgumentException>(() =>
            new ConfigurationCompositionException("Production", "Service", []));
        Assert.Throws<ArgumentException>(() =>
            new ConfigurationCompositionException("Production", "Service", [failure, null!]));
        Assert.Throws<ArgumentException>(() =>
            new ConfigurationCompositionException(" ", "Service", [failure]));
        Assert.Throws<ArgumentException>(() =>
            new ConfigurationCompositionException("Production", " ", [failure]));
    }

    [Theory]
    [InlineData("secret-provider-failed", "provider-failures")]
    [InlineData("unknown-catalog-code", "diagnostics")]
    public void CompositionFailure_UsesCatalogTextAndDocumentationAnchor(string code, string anchor)
    {
        var failure = new ConfigCompositionFailure("Service:ApiKey", code, "provider");

        Assert.Equal("Service:ApiKey", failure.Path);
        Assert.Equal(code, failure.Code);
        Assert.Equal("provider", failure.ProviderId);
        Assert.Contains($"https://appsurface.dev/config/secret-references#{anchor}", failure.Docs, StringComparison.Ordinal);
        Assert.Contains(code, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("resource-key", failure.ToString(), StringComparison.Ordinal);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
