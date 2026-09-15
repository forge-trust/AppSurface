using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigCompositionEngineTests
{
    private const string EnvironmentName = "Production";
    private const string EnvironmentSource = nameof(EnvironmentConfigProvider);
    private const string OneDeclaration = """{"Service":{"ApiKey":{"key":"api"}}}""";
    private const string TwoDeclarations =
        """{"Service":{"ApiKey":{"key":"api"},"SigningKey":{"key":"signing"}}}""";

    [Fact]
    public void Execute_FileDeclarationsComposeMultipleSlotsAndOrdinaryMembers()
    {
        using var files = new FileFixture("""
            {"Service":{"Endpoint":"https://service.test",
              "ApiKey":{"key":"opaque/api","version":"4","provider":"alpha"},
              "SigningKey":{"key":"opaque/signing","provider":"bravo"}}}
            """);
        var events = new List<string>();
        var alpha = new SecretProvider("alpha")
        {
            Validate = reference =>
            {
                events.Add("validate:" + reference.LogicalPath);
                return ConfigSecretReferenceValidation.Supported();
            },
            Resolve = (reference, _) =>
            {
                events.Add("resolve:" + reference.LogicalPath);
                return Success("alpha", "api-value");
            }
        };
        var bravo = new SecretProvider("bravo")
        {
            Validate = reference =>
            {
                events.Add("validate:" + reference.LogicalPath);
                return ConfigSecretReferenceValidation.Supported();
            },
            Resolve = (reference, _) =>
            {
                events.Add("resolve:" + reference.LogicalPath);
                return Success("bravo", "signing-value");
            }
        };
        var engine = CreateEngine(bases: [files.Provider], secrets: [bravo, alpha]);

        var result = engine.Execute(EnvironmentName, "Service", typeof(TwoSecrets));

        var value = Resolved<TwoSecrets>(result);
        Assert.Equal("https://service.test", value.Endpoint);
        AssertSecret(value.ApiKey, true, "api-value", "alpha");
        AssertSecret(value.SigningKey, true, "signing-value", "bravo");
        Assert.Equal(new[]
        {
            "validate:Service:ApiKey", "validate:Service:SigningKey",
            "resolve:Service:ApiKey", "resolve:Service:SigningKey"
        }, events);
        Assert.Equal(new ConfigSecretReference(EnvironmentName, "Service:ApiKey", "opaque/api", "4"),
            Assert.Single(alpha.Resolutions).Reference);
        Assert.Equal("opaque/signing", Assert.Single(bravo.Resolutions).Reference.Key);
        Assert.Equal(new[] { "Service:ApiKey", "Service:SigningKey" }, result.Slots.Select(s => s.Path));
        Assert.All(result.Slots, slot =>
        {
            Assert.Equal("secret-resolved", slot.Code);
            Assert.Equal(ConfigAuditSourceKind.File, slot.DeclarationSource!.Kind);
            Assert.Single(slot.Providers);
        });
    }

    [Fact]
    public void Execute_HigherFileDescriptorReplacesVersionInsteadOfInheritingMergedFields()
    {
        using var files = new FileFixture(
            """{"Service":{"Endpoint":"lower","ApiKey":{"key":"old","version":"7","provider":"alpha"}}}""",
            """{"Service":{"ApiKey":{"key":"new","provider":"alpha"}}}""");
        var alpha = new SecretProvider("alpha");
        var engine = CreateEngine(bases: [files.Provider], secrets: [alpha]);

        var result = engine.Execute(EnvironmentName, "Service", typeof(TwoSecrets));

        var value = Resolved<TwoSecrets>(result);
        Assert.Equal("lower", value.Endpoint);
        Assert.Equal("new", value.ApiKey.Value);
        var reference = Assert.Single(alpha.Resolutions).Reference;
        Assert.Equal("new", reference.Key);
        Assert.Null(reference.Version);
        Assert.EndsWith("appsettings.Production.json", result.Slots[0].DeclarationSource!.FilePath);
    }

    [Theory]
    [InlineData(ConfigSecretProviderResolutionStatus.Missing, "secret-not-found", false)]
    [InlineData(ConfigSecretProviderResolutionStatus.Unclaimed, "secret-reference-unsupported", false)]
    [InlineData(ConfigSecretProviderResolutionStatus.AccessDenied, "secret-provider-access-denied", false)]
    [InlineData(ConfigSecretProviderResolutionStatus.Unavailable, "secret-provider-unavailable", true)]
    [InlineData(ConfigSecretProviderResolutionStatus.InvalidReference, "secret-reference-invalid", false)]
    [InlineData(ConfigSecretProviderResolutionStatus.ProviderFailed, "secret-provider-failed", true)]
    public void Execute_ExplicitProviderFailureCannotFallBackToSensitiveLowerValue(
        ConfigSecretProviderResolutionStatus status, string code, bool retryable)
    {
        using var files = new FileFixture(
            """{"Service":{"ApiKey":{"key":"api","provider":"alpha"}}}""");
        var raw = new RawProvider("""{"ApiKey":"lower-secret"}""");
        var alpha = new SecretProvider("alpha") { Resolve = (_, _) => Outcome("alpha", status) };
        var unrelated = new SecretProvider("bravo");
        var engine = CreateEngine(bases: [files.Provider, raw], secrets: [unrelated, alpha]);

        var result = engine.Execute(EnvironmentName, "Service", typeof(OneSecret));

        var failure = Failed(result, code);
        Assert.Equal("Service:ApiKey", failure.Path);
        Assert.Equal(retryable, failure.Retryable);
        if (status is not (ConfigSecretProviderResolutionStatus.Missing or ConfigSecretProviderResolutionStatus.Unclaimed))
        {
            Assert.Equal("alpha", failure.ProviderId);
        }
        var slot = Assert.Single(result.Slots);
        Assert.False(slot.HasValue);
        Assert.Null(slot.ResolvedProvider);
        Assert.Equal(status, Assert.Single(slot.Providers).Status);
        Assert.Single(raw.Reads);
        Assert.Single(alpha.Resolutions);
        Assert.Empty(unrelated.Validations);
        Assert.Empty(unrelated.Resolutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_ThrowingOrMisidentifiedProviderIsAValueSafeFailure(bool wrongProviderId)
    {
        using var files = new FileFixture(OneDeclaration);
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (_, _) => wrongProviderId
                ? Success("different-provider", "sensitive-sentinel")
                : throw new InvalidOperationException("sensitive-sentinel")
        };

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        var failure = Failed(result, "secret-provider-failed");
        Assert.Equal("alpha", failure.ProviderId);
        Assert.DoesNotContain("sensitive-sentinel", failure.ToString());
        Assert.Equal(ConfigSecretProviderResolutionStatus.ProviderFailed,
            Assert.Single(Assert.Single(result.Slots).Providers).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Execute_UniqueStringSuccessPreservesEmptyAndWhitespacePayloads(string payload)
    {
        using var files = new FileFixture(OneDeclaration);
        var alpha = new SecretProvider("alpha") { Resolve = (_, _) => Success("alpha", payload) };

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        AssertSecret(Resolved<OneSecret>(result).ApiKey, true, payload, "alpha");
    }

    [Fact]
    public void Execute_UniquePayloadConversionFailureIsTerminal()
    {
        using var files = new FileFixture(OneDeclaration);
        var alpha = new SecretProvider("alpha") { Resolve = (_, _) => Success("alpha", "not-an-int") };

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha])
            .Execute(EnvironmentName, "Service", typeof(IntSecret));

        Assert.Equal("alpha", Failed(result, "secret-value-conversion-failed").ProviderId);
        Assert.Single(alpha.Resolutions);
    }

    [Fact]
    public void Execute_ProviderlessTwoSlotsUseCanonicalOrderAndOneSharedDeadline()
    {
        using var files = new FileFixture(TwoDeclarations);
        var clock = new ManualTimeProvider();
        var calls = new List<(string Path, string Provider, TimeSpan Remaining)>();
        var contexts = new List<ConfigSecretResolutionContext>();
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (reference, context) =>
            {
                calls.Add((reference.LogicalPath, "alpha", context.Remaining));
                contexts.Add(context);
                clock.Advance(TimeSpan.FromMilliseconds(4));
                return ConfigSecretProviderResolution.Missing("alpha");
            }
        };
        var bravo = new SecretProvider("bravo")
        {
            Resolve = (reference, context) =>
            {
                calls.Add((reference.LogicalPath, "bravo", context.Remaining));
                contexts.Add(context);
                return Success("bravo", reference.Key + "-value");
            }
        };
        var engine = CreateEngine(bases: [files.Provider], secrets: [bravo, alpha], time: clock,
            options: new() { ProviderlessResolutionBudget = TimeSpan.FromMilliseconds(10) });

        var result = engine.Execute(EnvironmentName, "Service", typeof(TwoSecrets));

        var value = Resolved<TwoSecrets>(result);
        AssertSecret(value.ApiKey, true, "api-value", "bravo");
        AssertSecret(value.SigningKey, true, "signing-value", "bravo");
        Assert.Equal(new[]
        {
            ("Service:ApiKey", "alpha", TimeSpan.FromMilliseconds(10)),
            ("Service:ApiKey", "bravo", TimeSpan.FromMilliseconds(6)),
            ("Service:SigningKey", "alpha", TimeSpan.FromMilliseconds(6)),
            ("Service:SigningKey", "bravo", TimeSpan.FromMilliseconds(2))
        }, calls);
        Assert.All(contexts, context => Assert.Same(contexts[0], context));
        Assert.All(result.Slots, slot =>
            Assert.Equal(new[] { "alpha", "bravo" }, slot.Providers.Select(p => p.ProviderId)));
        Assert.Equal(2, alpha.Resolutions.Count);
        Assert.Equal(2, bravo.Resolutions.Count);
    }

    [Theory]
    [InlineData(ConfigSecretProviderResolutionStatus.Unclaimed, ConfigSecretProviderResolutionStatus.Unclaimed,
        "secret-reference-unsupported")]
    [InlineData(ConfigSecretProviderResolutionStatus.Missing, ConfigSecretProviderResolutionStatus.Unclaimed,
        "secret-not-found")]
    [InlineData(ConfigSecretProviderResolutionStatus.Unclaimed, ConfigSecretProviderResolutionStatus.Missing,
        "secret-not-found")]
    [InlineData(ConfigSecretProviderResolutionStatus.Missing, ConfigSecretProviderResolutionStatus.Missing,
        "secret-not-found")]
    [InlineData(ConfigSecretProviderResolutionStatus.Resolved, ConfigSecretProviderResolutionStatus.Missing, null)]
    [InlineData(ConfigSecretProviderResolutionStatus.Missing, ConfigSecretProviderResolutionStatus.Resolved, null)]
    [InlineData(ConfigSecretProviderResolutionStatus.Resolved, ConfigSecretProviderResolutionStatus.Unclaimed, null)]
    [InlineData(ConfigSecretProviderResolutionStatus.Unclaimed, ConfigSecretProviderResolutionStatus.Resolved, null)]
    [InlineData(ConfigSecretProviderResolutionStatus.Resolved, ConfigSecretProviderResolutionStatus.Resolved,
        "secret-provider-ambiguous")]
    public void Execute_ProviderlessZeroOneAndMultipleSuccessesHaveDeterministicOutcomes(
        ConfigSecretProviderResolutionStatus first, ConfigSecretProviderResolutionStatus second, string? failureCode)
    {
        using var files = new FileFixture(OneDeclaration);
        var missing = new SecretProvider("missing") { Resolve = (_, _) => Outcome("missing", first) };
        var unclaimed = new SecretProvider("unclaimed") { Resolve = (_, _) => Outcome("unclaimed", second) };
        var engine = CreateEngine(bases: [files.Provider], secrets: [unclaimed, missing]);

        var result = engine.Execute(EnvironmentName, "Service", typeof(OneSecret));

        if (failureCode is not null)
        {
            Failed(result, failureCode);
            Assert.False(Assert.Single(result.Slots).HasValue);
        }
        else
        {
            var winner = first == ConfigSecretProviderResolutionStatus.Resolved ? "missing" : "unclaimed";
            AssertSecret(Resolved<OneSecret>(result).ApiKey, true, winner + "-value", winner);
        }
        Assert.Single(missing.Resolutions);
        Assert.Single(unclaimed.Resolutions);
        Assert.Equal(new[] { "missing", "unclaimed" },
            Assert.Single(result.Slots).Providers.Select(p => p.ProviderId));
    }

    [Theory]
    [InlineData(ConfigSecretProviderResolutionStatus.AccessDenied, "secret-provider-access-denied", false)]
    [InlineData(ConfigSecretProviderResolutionStatus.AccessDenied, "secret-provider-access-denied", true)]
    [InlineData(ConfigSecretProviderResolutionStatus.Unavailable, "secret-provider-unavailable", false)]
    [InlineData(ConfigSecretProviderResolutionStatus.Unavailable, "secret-provider-unavailable", true)]
    [InlineData(ConfigSecretProviderResolutionStatus.InvalidReference, "secret-reference-invalid", false)]
    [InlineData(ConfigSecretProviderResolutionStatus.InvalidReference, "secret-reference-invalid", true)]
    [InlineData(ConfigSecretProviderResolutionStatus.ProviderFailed, "secret-provider-failed", false)]
    [InlineData(ConfigSecretProviderResolutionStatus.ProviderFailed, "secret-provider-failed", true)]
    public void Execute_ProviderlessUncertaintyOverridesSuccessAndStopsLaterProviders(
        ConfigSecretProviderResolutionStatus terminal, string code, bool terminalFirst)
    {
        using var files = new FileFixture(OneDeclaration);
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (_, _) => terminalFirst ? Outcome("alpha", terminal) : Success("alpha", "value")
        };
        var bravo = new SecretProvider("bravo")
        {
            Resolve = (_, _) => terminalFirst ? Success("bravo", "value") : Outcome("bravo", terminal)
        };
        var charlie = new SecretProvider("charlie");

        var result = CreateEngine(bases: [files.Provider], secrets: [charlie, bravo, alpha])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        Assert.Equal(terminalFirst ? "alpha" : "bravo", Failed(result, code).ProviderId);
        Assert.Single(alpha.Resolutions);
        Assert.Equal(terminalFirst ? 0 : 1, bravo.Resolutions.Count);
        Assert.Empty(charlie.Resolutions);
        Assert.False(Assert.Single(result.Slots).HasValue);
        Assert.Equal(terminalFirst ? new[] { "alpha" } : ["alpha", "bravo"],
            result.Slots[0].Providers.Select(p => p.ProviderId));
    }

    [Fact]
    public void Execute_AmbiguousUnconvertiblePayloadsAreClassifiedBeforeConversion()
    {
        using var files = new FileFixture(OneDeclaration);
        var alpha = new SecretProvider("alpha") { Resolve = (_, _) => Success("alpha", "bad-int-a") };
        var bravo = new SecretProvider("bravo") { Resolve = (_, _) => Success("bravo", "bad-int-b") };

        var result = CreateEngine(bases: [files.Provider], secrets: [bravo, alpha])
            .Execute(EnvironmentName, "Service", typeof(IntSecret));

        Failed(result, "secret-provider-ambiguous");
        Assert.Single(alpha.Resolutions);
        Assert.Single(bravo.Resolutions);
    }

    [Fact]
    public void Execute_ZeroRegisteredProvidersFailsBeforeReadingRawBase()
    {
        using var files = new FileFixture(OneDeclaration);
        var raw = new RawProvider("""{"ApiKey":"lower"}""");

        var result = CreateEngine(bases: [files.Provider, raw], secrets: [])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        Failed(result, "secret-provider-not-registered");
        Assert.Empty(raw.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_LocalValidationFailureCannotBeRescuedByExactEnvironment(bool invalid)
    {
        using var files = new FileFixture(OneDeclaration);
        var raw = new RawProvider("""{"ApiKey":"lower"}""");
        var alpha = new SecretProvider("alpha")
        {
            Validate = _ => invalid ? ConfigSecretReferenceValidation.Invalid() : ConfigSecretReferenceValidation.Unclaimed()
        };
        var bravo = new SecretProvider("bravo")
        {
            Validate = _ => invalid ? ConfigSecretReferenceValidation.Supported() : ConfigSecretReferenceValidation.Unclaimed()
        };
        var environment = new TestEnvironmentProvider(new() { ["SERVICE__APIKEY"] = "exact" });

        var result = CreateEngine(bases: [files.Provider, raw], secrets: [bravo, alpha], environment: environment)
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        Failed(result, invalid ? "secret-reference-invalid" : "secret-reference-unsupported");
        Assert.Single(alpha.Validations);
        Assert.Single(bravo.Validations);
        Assert.Empty(alpha.Resolutions);
        Assert.Empty(bravo.Resolutions);
        Assert.Empty(raw.Reads);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Execute_ProviderlessLateSuccessAtOrAfterDeadlineFailsAndSkipsLaterSlot(int elapsedMilliseconds)
    {
        using var files = new FileFixture(TwoDeclarations);
        var clock = new ManualTimeProvider();
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (_, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(elapsedMilliseconds));
                return Success("alpha", "late-value");
            }
        };

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha], time: clock,
                options: new() { ProviderlessResolutionBudget = TimeSpan.FromMilliseconds(5) })
            .Execute(EnvironmentName, "Service", typeof(TwoSecrets));

        Assert.Equal(ConfigCompositionRootState.Failed, result.State);
        Assert.Null(result.Value);
        Assert.Equal(new[] { "Service:ApiKey", "Service:SigningKey" }, result.Failures.Select(f => f.Path));
        Assert.All(result.Failures, failure =>
        {
            Assert.Equal("secret-providerless-resolution-budget-exceeded", failure.Code);
            Assert.True(failure.Retryable);
        });
        Assert.Single(alpha.Resolutions);
        Assert.Single(result.Slots[0].Providers);
        Assert.Empty(result.Slots[1].Providers);
    }

    [Fact]
    public void Execute_BaseWorkConsumesProviderlessDeadlineBeforeFirstProviderCall()
    {
        using var files = new FileFixture(OneDeclaration);
        var clock = new ManualTimeProvider();
        var raw = new RawProvider("{}")
        {
            Resolve = (_, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(5));
                return ConfigCompositionValueResolution.Resolved("{}", "raw-base", 5, true);
            }
        };
        var alpha = new SecretProvider("alpha");

        var result = CreateEngine(bases: [files.Provider, raw], secrets: [alpha], time: clock,
                options: new() { ProviderlessResolutionBudget = TimeSpan.FromMilliseconds(5) })
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        Assert.True(Failed(result, "secret-providerless-resolution-budget-exceeded").Retryable);
        Assert.Single(raw.Reads);
        Assert.Empty(alpha.Resolutions);
    }

    [Fact]
    public void Execute_ExactEnvironmentRescuesExpiredSlotButDoesNotResetRootDeadline()
    {
        using var files = new FileFixture(TwoDeclarations);
        var clock = new ManualTimeProvider();
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (_, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(5));
                return Success("alpha", "late");
            }
        };
        var environment = new TestEnvironmentProvider(new() { ["SERVICE__APIKEY"] = "rescue" });

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha], environment: environment, time: clock,
                options: new() { ProviderlessResolutionBudget = TimeSpan.FromMilliseconds(5) })
            .Execute(EnvironmentName, "Service", typeof(TwoSecrets));

        Assert.Equal("Service:SigningKey", Failed(result, "secret-providerless-resolution-budget-exceeded").Path);
        Assert.Equal("secret-environment-rescued", result.Slots[0].Code);
        Assert.Equal(EnvironmentSource, result.Slots[0].ResolvedProvider);
        Assert.Single(alpha.Resolutions);
        Assert.Empty(result.Slots[1].Providers);
    }

    [Fact]
    public void Execute_ExplicitProviderIsNotBoundByProviderlessBudget()
    {
        using var files = new FileFixture(
            """{"Service":{"ApiKey":{"key":"api","provider":"alpha"},"SigningKey":{"key":"signing","provider":"alpha"}}}""");
        var clock = new ManualTimeProvider();
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (reference, context) =>
            {
                Assert.True(context.Remaining > TimeSpan.FromMilliseconds(5));
                clock.Advance(TimeSpan.FromSeconds(1));
                return Success("alpha", reference.Key);
            }
        };

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha], time: clock,
                options: new() { ProviderlessResolutionBudget = TimeSpan.FromMilliseconds(5) })
            .Execute(EnvironmentName, "Service", typeof(TwoSecrets));

        Assert.Equal("api", Resolved<TwoSecrets>(result).ApiKey.Value);
        Assert.Equal(2, alpha.Resolutions.Count);
    }

    [Fact]
    public void ValidatePlan_DisabledReferenceValidatesLocallyWithoutRawOrSecretResolution()
    {
        using var files = new FileFixture(
            """{"Service":{"ApiKey":{"key":"api","provider":"alpha","enabled":false}}}""");
        var raw = new RawProvider("""{"ApiKey":"lower"}""");
        var alpha = new SecretProvider("alpha");
        var environment = new TestEnvironmentProvider();
        var engine = CreateEngine(bases: [files.Provider, raw], secrets: [alpha], environment: environment);

        engine.ValidatePlan(EnvironmentName, "Service", typeof(OneSecret));

        Assert.Single(alpha.Validations);
        Assert.Empty(alpha.Resolutions);
        Assert.Empty(raw.Reads);
        Assert.Equal(new[] { "PRODUCTION_SERVICE", "SERVICE", "PRODUCTION__SERVICE" }, environment.Lookups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_DisabledReferenceStillRequiresRegisteredCompatibleProvider(bool registered)
    {
        using var files = new FileFixture(
            """{"Service":{"ApiKey":{"key":"api","provider":"alpha","enabled":false}}}""");
        var alpha = new SecretProvider("alpha") { Validate = _ => ConfigSecretReferenceValidation.Unclaimed() };

        var result = CreateEngine(bases: [files.Provider], secrets: registered ? [alpha] : [])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        Failed(result, registered ? "secret-reference-unsupported" : "secret-provider-not-registered");
        Assert.Equal(registered ? 1 : 0, alpha.Validations.Count);
        Assert.Empty(alpha.Resolutions);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Execute_DisabledReferenceCanReceiveLowerOrExactEnvironmentValue(bool lower, bool exact)
    {
        using var files = new FileFixture(
            """{"Service":{"ApiKey":{"key":"api","enabled":false}}}""");
        var raw = new RawProvider("""{"ApiKey":"lower"}""");
        var alpha = new SecretProvider("alpha");
        var environment = new TestEnvironmentProvider(exact ? new() { ["SERVICE__APIKEY"] = "exact" } : null);

        var result = CreateEngine(bases: lower ? [files.Provider, raw] : [files.Provider],
                secrets: [alpha], environment: environment)
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        var secret = Resolved<OneSecret>(result).ApiKey;
        Assert.False(secret.Enabled);
        Assert.Equal(lower || exact, secret.HasValue);
        if (lower || exact)
        {
            AssertSecret(secret, false, exact ? "exact" : "lower", exact ? EnvironmentSource : raw.Name);
        }
        else
        {
            Assert.Null(secret.ResolvedProvider);
            Assert.False(secret.TryGetValue(out _));
        }
        Assert.Single(alpha.Validations);
        Assert.Empty(alpha.Resolutions);
        Assert.Equal(lower ? 1 : 0, raw.Reads.Count);
    }

    [Fact]
    public void Execute_AbsentDescriptorAndAbsentRootStayMissingWithoutReferenceLookup()
    {
        using var files = new FileFixture("""{"Unrelated":{}}""");
        var raw = new RawProvider("{}")
        {
            Resolve = (_, _) => ConfigCompositionValueResolution.Missing("raw-base", 5)
        };
        var alpha = new SecretProvider("alpha");

        var result = CreateEngine(bases: [files.Provider, raw], secrets: [alpha])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        Assert.Equal(ConfigCompositionRootState.Missing, result.State);
        Assert.Null(result.Value);
        Assert.Empty(result.Failures);
        var slot = Assert.Single(result.Slots);
        Assert.Equal("secret-descriptor-absent", slot.Code);
        Assert.True(slot.Enabled);
        Assert.False(slot.HasValue);
        Assert.Empty(slot.Providers);
        Assert.Empty(alpha.Validations);
        Assert.Empty(alpha.Resolutions);
        Assert.Equal((EnvironmentName, "Service"), Assert.Single(raw.Reads));
    }

    [Fact]
    public void Execute_AbsentDescriptorAndPresentEmptyFileRootProduceAnEmptyEnabledWrapper()
    {
        using var files = new FileFixture("""{"Service":{}}""");
        var alpha = new SecretProvider("alpha");

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        var secret = Resolved<OneSecret>(result).ApiKey;
        Assert.True(secret.Enabled);
        Assert.False(secret.HasValue);
        Assert.Empty(alpha.Validations);
        Assert.Empty(alpha.Resolutions);
        Assert.Equal(ConfigAuditSourceKind.File, Assert.Single(result.Sources).Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Execute_AbsentDescriptorAcceptsOnlySensitiveLowerScalar(bool sensitive)
    {
        var raw = new RawProvider("""{"ApiKey":"lower"}""", sensitive: sensitive);
        var alpha = new SecretProvider("alpha");

        var result = CreateEngine(bases: [raw], secrets: [alpha])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        var secret = Resolved<OneSecret>(result).ApiKey;
        Assert.True(secret.Enabled);
        Assert.Equal(sensitive, secret.HasValue);
        if (sensitive)
        {
            AssertSecret(secret, true, "lower", raw.Name);
        }
        Assert.Empty(alpha.Validations);
        Assert.Empty(alpha.Resolutions);
    }

    [Fact]
    public void Execute_AbsentDescriptorExactEnvironmentAloneEstablishesRootPresence()
    {
        var alpha = new SecretProvider("alpha");
        var environment = new TestEnvironmentProvider(new()
        {
            ["SERVICE__APIKEY"] = """{"key":"literal-text-is-not-a-descriptor"}"""
        });

        var result = CreateEngine(bases: [], secrets: [alpha], environment: environment)
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        AssertSecret(Resolved<OneSecret>(result).ApiKey, true,
            """{"key":"literal-text-is-not-a-descriptor"}""", EnvironmentSource);
        Assert.Empty(alpha.Validations);
        Assert.Empty(alpha.Resolutions);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("{}", true)]
    [InlineData("[]", true)]
    public void Execute_SensitiveLowerNullIsEmptyButNonScalarIsConversionFailure(string lowerNode, bool fails)
    {
        var raw = new RawProvider($$"""{"ApiKey":{{lowerNode}}}""");

        var result = CreateEngine(bases: [raw])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        if (fails)
        {
            Failed(result, "secret-value-conversion-failed");
        }
        else
        {
            Assert.False(Resolved<OneSecret>(result).ApiKey.HasValue);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_ExactEnvironmentOverridesSuccessOrRescuesFailureAfterInvalidEarlierCandidate(bool denied)
    {
        using var files = new FileFixture(OneDeclaration);
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (_, _) => denied ? ConfigSecretProviderResolution.AccessDenied("alpha") : Success("alpha", "7")
        };
        var environment = new TestEnvironmentProvider(new()
        {
            ["PRODUCTION_SERVICE_APIKEY"] = "invalid-integer",
            ["SERVICE_APIKEY"] = "42",
            ["SERVICE__APIKEY"] = "99"
        });

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha], environment: environment)
            .Execute(EnvironmentName, "Service", typeof(IntSecret));

        AssertSecret(Resolved<IntSecret>(result).ApiKey, true, 42, EnvironmentSource);
        Assert.Equal(denied ? "secret-environment-rescued" : "secret-environment-supplied",
            Assert.Single(result.Slots).Code);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("config-environment-conversion-failed", diagnostic.Code);
        Assert.Equal("PRODUCTION_SERVICE_APIKEY", diagnostic.Source!.EnvironmentVariableName);
        Assert.DoesNotContain("SERVICE__APIKEY", environment.Lookups);
    }

    [Fact]
    public void Execute_InvalidExactEnvironmentKeepsValidProviderValue()
    {
        using var files = new FileFixture(OneDeclaration);
        var alpha = new SecretProvider("alpha") { Resolve = (_, _) => Success("alpha", "7") };
        var environment = new TestEnvironmentProvider(new() { ["SERVICE__APIKEY"] = "invalid" });

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha], environment: environment)
            .Execute(EnvironmentName, "Service", typeof(IntSecret));

        AssertSecret(Resolved<IntSecret>(result).ApiKey, true, 7, "alpha");
        Assert.Equal("config-environment-conversion-failed", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Execute_RescuingOneSecretAndOrdinarySiblingCannotRescueOtherSecret()
    {
        using var files = new FileFixture(TwoDeclarations);
        var alpha = new SecretProvider("alpha") { Resolve = (_, _) => ConfigSecretProviderResolution.AccessDenied("alpha") };
        var environment = new TestEnvironmentProvider(new()
        {
            ["SERVICE__APIKEY"] = "42",
            ["SERVICE__ENDPOINT"] = "https://override.test"
        });

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha], environment: environment)
            .Execute(EnvironmentName, "Service", typeof(TwoSecrets));

        Assert.Equal("Service:SigningKey", Failed(result, "secret-provider-access-denied").Path);
        Assert.Equal("secret-environment-rescued", result.Slots[0].Code);
        Assert.Equal(EnvironmentSource, result.Slots[0].ResolvedProvider);
        Assert.False(result.Slots[1].HasValue);
        Assert.Equal(2, alpha.Resolutions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_ParentEnvironmentObjectCannotRescueNestedSecretWithoutExactChild(bool exactChild)
    {
        using var files = new FileFixture(
            """{"Service":{"Nested":{"ApiKey":{"key":"api"},"Label":"file"}}}""");
        var alpha = new SecretProvider("alpha") { Resolve = (_, _) => ConfigSecretProviderResolution.AccessDenied("alpha") };
        var values = new Dictionary<string, string?>
        {
            ["SERVICE__NESTED"] = """{"ApiKey":"parent-value","Label":"parent-label"}"""
        };
        if (exactChild)
        {
            values["SERVICE__NESTED__APIKEY"] = "exact-child";
        }

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha],
                environment: new TestEnvironmentProvider(values))
            .Execute(EnvironmentName, "Service", typeof(NestedOptions));

        if (exactChild)
        {
            var value = Resolved<NestedOptions>(result);
            AssertSecret(value.Nested.ApiKey, true, "exact-child", EnvironmentSource);
            Assert.Equal("parent-label", value.Nested.Label);
        }
        else
        {
            Assert.Equal("Service:Nested:ApiKey", Failed(result, "secret-provider-access-denied").Path);
            Assert.False(Assert.Single(result.Slots).HasValue);
        }
        Assert.Single(alpha.Resolutions);
    }

    [Fact]
    public void Execute_OrdinaryIndexedEnvironmentArrayComposesAlongsideSecret()
    {
        using var files = new FileFixture(
            """{"Service":{"ApiKey":{"key":"api"},"Endpoints":["file-endpoint"]}}""");
        var alpha = new SecretProvider("alpha");
        var environment = new TestEnvironmentProvider(new()
        {
            ["PRODUCTION__SERVICE__ENDPOINTS__0"] = "https://one.test",
            ["PRODUCTION__SERVICE__ENDPOINTS__1"] = "https://two.test"
        });

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha], environment: environment)
            .Execute(EnvironmentName, "Service", typeof(ArrayAndSecretOptions));

        var value = Resolved<ArrayAndSecretOptions>(result);
        Assert.Equal(new[] { "https://one.test", "https://two.test" }, value.Endpoints);
        AssertSecret(value.ApiKey, true, "api", "alpha");
        Assert.Single(alpha.Resolutions);
        Assert.Contains(result.Sources, source =>
            source.EnvironmentVariableName == "PRODUCTION__SERVICE__ENDPOINTS__0");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Execute_DirectRootUsesAllFourCandidatesAndSkipsEveryDownstreamRead(int winnerIndex)
    {
        // A malformed file proves direct success does not compile file declarations.
        using var files = new FileFixture("{invalid-json");
        var raw = new RawProvider("{}");
        var alpha = new SecretProvider("alpha");
        var claims = new DeclarationSource();
        string[] candidates =
        [
            "PRODUCTION_SERVICE_OPTIONS", "SERVICE_OPTIONS",
            "PRODUCTION__SERVICE__OPTIONS", "SERVICE__OPTIONS"
        ];
        var values = new Dictionary<string, string?>
        {
            ["SERVICE__OPTIONS__APIKEY"] = "descendant-must-not-win"
        };
        for (var i = 0; i < candidates.Length; i++)
        {
            values[candidates[i]] = i < winnerIndex
                ? "invalid-json"
                : $$"""{"ApiKey":"candidate-{{i}}"}""";
        }
        var environment = new TestEnvironmentProvider(values);

        var result = CreateEngine(bases: [files.Provider, raw], secrets: [alpha],
                declarations: [claims], environment: environment)
            .Execute(EnvironmentName, "Service.Options", typeof(OneSecret));

        AssertSecret(Resolved<OneSecret>(result).ApiKey, true, "candidate-" + winnerIndex, EnvironmentSource);
        Assert.True(result.DirectRoot);
        Assert.Equal(candidates.Take(winnerIndex + 1), environment.Lookups);
        Assert.Equal(winnerIndex, result.Diagnostics.Count);
        Assert.Equal(candidates[winnerIndex], Assert.Single(result.Sources).EnvironmentVariableName);
        Assert.Empty(raw.Reads);
        Assert.Empty(raw.TypedReads);
        Assert.Empty(alpha.Validations);
        Assert.Empty(alpha.Resolutions);
        Assert.Empty(claims.Reads);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{invalid-json")]
    [InlineData("""{"ApiKey":{"key":"not-a-scalar"}}""")]
    public void Execute_InvalidDirectRootWarningsSurviveNormalFileComposition(string invalidRoot)
    {
        using var files = new FileFixture(OneDeclaration);
        var alpha = new SecretProvider("alpha");
        var environment = new TestEnvironmentProvider(new() { ["PRODUCTION_SERVICE"] = invalidRoot });

        var result = CreateEngine(bases: [files.Provider], secrets: [alpha], environment: environment)
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        AssertSecret(Resolved<OneSecret>(result).ApiKey, true, "api", "alpha");
        Assert.False(result.DirectRoot);
        Assert.Equal("PRODUCTION_SERVICE", Assert.Single(result.Diagnostics).Source!.EnvironmentVariableName);
        Assert.Single(alpha.Resolutions);
    }

    [Fact]
    public void Execute_ExactCodeClaimCanEstablishRootWithoutAnyBase()
    {
        var claims = new DeclarationSource(
            new ConfigSecretConfiguredClaim(
                ConfigSecretConfiguredClaimKind.ExactMapping, "Service:ApiKey", "alpha", "code-key", "2"));
        var alpha = new SecretProvider("alpha");

        var result = CreateEngine(bases: [], secrets: [alpha], declarations: [claims])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        AssertSecret(Resolved<OneSecret>(result).ApiKey, true, "code-key", "alpha");
        Assert.Single(claims.Reads);
        Assert.Equal("2", Assert.Single(alpha.Resolutions).Reference.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_BaseSelectionFallsThroughMissingOrUnclaimedInPriorityOrder(bool unclaimed)
    {
        using var files = new FileFixture("""{"Service":{"Endpoint":"file-only"}}""");
        var calls = new List<string>();
        var high = new RawProvider("{}", "high", 10)
        {
            Resolve = (_, _) =>
            {
                calls.Add("high");
                return unclaimed
                    ? ConfigCompositionValueResolution.Unclaimed("high", 10)
                    : ConfigCompositionValueResolution.Missing("high", 10);
            }
        };
        var selected = new RawProvider("{}", "selected", 5)
        {
            Resolve = (_, _) =>
            {
                calls.Add("selected");
                return ConfigCompositionValueResolution.Resolved("""{"ApiKey":"selected-value"}""", "selected", 5, true);
            }
        };
        var lower = new RawProvider("""{"ApiKey":"lower-value"}""", "lower", 2);

        var result = CreateEngine(bases: [files.Provider, lower, selected, high])
            .Execute(EnvironmentName, "Service", typeof(TwoSecrets));

        var value = Resolved<TwoSecrets>(result);
        AssertSecret(value.ApiKey, true, "selected-value", "selected");
        Assert.Equal("", value.Endpoint); // Whole-root alternatives do not merge file-only ordinary members.
        Assert.Equal(new[] { "high", "selected" }, calls);
        Assert.Empty(lower.Reads);
        Assert.Empty(high.TypedReads);
        Assert.Empty(selected.TypedReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_BaseTerminalOrExceptionStopsBeforeSecretResolution(bool throws)
    {
        using var files = new FileFixture(OneDeclaration);
        var raw = new RawProvider("{}")
        {
            Resolve = (_, _) => throws
                ? throw new InvalidOperationException("raw-sensitive-sentinel")
                : ConfigCompositionValueResolution.TerminalFailure("raw-base", 5, retryable: true)
        };
        var alpha = new SecretProvider("alpha");

        var result = CreateEngine(bases: [files.Provider, raw], secrets: [alpha])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        var failure = Failed(result, "config-composition-base-failed");
        Assert.Equal("Service", failure.Path);
        Assert.Equal(!throws, failure.Retryable);
        Assert.DoesNotContain("raw-sensitive-sentinel", failure.ToString());
        Assert.Empty(alpha.Resolutions);
    }

    [Theory]
    [InlineData("{invalid-json")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    public void Execute_MalformedOrNonObjectFoundRootIsTerminal(string body)
    {
        using var files = new FileFixture(OneDeclaration);
        var raw = new RawProvider(body);
        var alpha = new SecretProvider("alpha");

        var result = CreateEngine(bases: [files.Provider, raw], secrets: [alpha])
            .Execute(EnvironmentName, "Service", typeof(OneSecret));

        Assert.Equal("Service", Failed(result, "config-composition-bind-failed").Path);
        Assert.Empty(alpha.Resolutions);
    }

    [Fact]
    public async Task Execute_ConcurrentRealFileRootsBindRecordInitAndSerializedNamesWithSeparatePayloads()
    {
        using var files = new FileFixture("""
            {
              "First":{"Label":"first","api_key":{"key":"first-api"},
                       "Record":{"record_token":{"key":"first-record"}}},
              "Second":{"Label":"second","api_key":{"key":"second-api"},
                        "Record":{"record_token":{"key":"second-record"}}}
            }
            """);
        using var rendezvous = new Barrier(2);
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (reference, _) =>
            {
                if (!rendezvous.SignalAndWait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Concurrent calls did not overlap.");
                }
                return Success("alpha", reference.Key);
            }
        };
        var engine = CreateEngine(bases: [files.Provider], secrets: [alpha]);
        engine.ValidatePlan(EnvironmentName, "First", typeof(AnnotatedOptions));
        engine.ValidatePlan(EnvironmentName, "Second", typeof(AnnotatedOptions));

        var results = await Task.WhenAll(
            Task.Run(() => engine.Execute(EnvironmentName, "First", typeof(AnnotatedOptions))),
            Task.Run(() => engine.Execute(EnvironmentName, "Second", typeof(AnnotatedOptions))));

        var first = Resolved<AnnotatedOptions>(results[0]);
        var second = Resolved<AnnotatedOptions>(results[1]);
        Assert.Equal("first", first.Label);
        Assert.Equal("second", second.Label);
        AssertSecret(first.ApiKey, true, "first-api", "alpha");
        AssertSecret(second.ApiKey, true, "second-api", "alpha");
        AssertSecret(first.Record.Token, true, "first-record", "alpha");
        AssertSecret(second.Record.Token, true, "second-record", "alpha");
        Assert.NotSame(first.ApiKey, second.ApiKey);
        Assert.NotSame(first.Record.Token, second.Record.Token);
        Assert.Equal(4, alpha.Resolutions.Count);
        Assert.Equal(new[] { "First:Record:record_token", "First:api_key" }.Order(StringComparer.OrdinalIgnoreCase),
            results[0].Slots.Select(s => s.Path));
    }

    [Fact]
    public void Execute_ReentrantResolutionKeepsNestedInvocationSlotsSeparate()
    {
        using var files = new FileFixture("""
            {"Outer":{"ApiKey":{"key":"outer"}},"Inner":{"ApiKey":{"key":"inner"}}}
            """);
        ConfigCompositionEngine? engine = null;
        OneSecret? inner = null;
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (reference, _) =>
            {
                if (reference.Key == "outer")
                {
                    inner = Resolved<OneSecret>(engine!.Execute(EnvironmentName, "Inner", typeof(OneSecret)));
                }
                return Success("alpha", reference.Key);
            }
        };
        engine = CreateEngine(bases: [files.Provider], secrets: [alpha]);

        var outer = Resolved<OneSecret>(engine.Execute(EnvironmentName, "Outer", typeof(OneSecret)));

        AssertSecret(outer.ApiKey, true, "outer", "alpha");
        Assert.NotNull(inner);
        AssertSecret(inner.ApiKey, true, "inner", "alpha");
        Assert.NotSame(outer.ApiKey, inner.ApiKey);
    }

    [Fact]
    public void Execute_BindingFailureDoesNotRetainPayloadOrPoisonCachedPlan()
    {
        using var files = new FileFixture(OneDeclaration);
        var iteration = 0;
        var raw = new RawProvider("{}")
        {
            Resolve = (_, _) => ConfigCompositionValueResolution.Resolved(
                iteration == 0 ? """{"Count":"not-an-integer"}""" : """{"Count":7}""",
                "raw-base", 5, true)
        };
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (_, _) => Success("alpha", "payload-" + ++iteration)
        };
        var engine = CreateEngine(bases: [files.Provider, raw], secrets: [alpha]);

        var failure = engine.Execute(EnvironmentName, "Service", typeof(CountAndSecretOptions));
        var success = engine.Execute(EnvironmentName, "Service", typeof(CountAndSecretOptions));

        Failed(failure, "config-composition-bind-failed");
        var value = Resolved<CountAndSecretOptions>(success);
        AssertSecret(value.ApiKey, true, "payload-2", "alpha");
        Assert.Equal(7, value.Count);
        Assert.Single(alpha.Validations);
        Assert.Equal(2, alpha.Resolutions.Count);
        Assert.DoesNotContain("payload-1", JsonSerializer.Serialize(failure.Slots));
        Assert.DoesNotContain("payload-1", failure.ToString());
    }

    [Fact]
    public void Execute_ProviderFailureIsNotCachedAcrossExecutionsOfSameRoot()
    {
        using var files = new FileFixture(OneDeclaration);
        var attempt = 0;
        var alpha = new SecretProvider("alpha")
        {
            Resolve = (_, _) => ++attempt == 1
                ? ConfigSecretProviderResolution.AccessDenied("alpha")
                : Success("alpha", "recovered")
        };
        var engine = CreateEngine(bases: [files.Provider], secrets: [alpha]);

        var failed = engine.Execute(EnvironmentName, "Service", typeof(OneSecret));
        var recovered = engine.Execute(EnvironmentName, "Service", typeof(OneSecret));

        Failed(failed, "secret-provider-access-denied");
        AssertSecret(Resolved<OneSecret>(recovered).ApiKey, true, "recovered", "alpha");
        Assert.Single(alpha.Validations);
        Assert.Equal(2, alpha.Resolutions.Count);
    }

    [Fact]
    public void Execute_ReadOnlySecretDestinationFailsBeforeBaseOrSecretResolution()
    {
        using var files = new FileFixture(OneDeclaration);
        var raw = new RawProvider("{}");
        var alpha = new SecretProvider("alpha");

        var result = CreateEngine(bases: [files.Provider, raw], secrets: [alpha])
            .Execute(EnvironmentName, "Service", typeof(ReadOnlySecretOptions));

        Assert.Equal("Service:ApiKey", Failed(result, "secret-destination-type-unsupported").Path);
        Assert.Empty(raw.Reads);
        Assert.Empty(alpha.Validations);
        Assert.Empty(alpha.Resolutions);
    }

    [Fact]
    public void DataAnnotations_ValidateObjectMembersTreatsResolvedEmptyWrapperAsOpaque()
    {
        using var files = new FileFixture("""{"Service":{}}""");
        var result = CreateEngine(bases: [files.Provider])
            .Execute(EnvironmentName, "Service", typeof(ValidatedSecretOptions));
        var options = Resolved<ValidatedSecretOptions>(result);
        Assert.False(options.ApiKey.HasValue);
        Assert.Throws<InvalidOperationException>(() => options.ApiKey.Value);

        ConfigDataAnnotationsValidator.Validate(
            "Service", typeof(Config<ValidatedSecretOptions>), typeof(ValidatedSecretOptions), options);

        // Required validates wrapper presence; it does not imply the secret has a value.
        Assert.True(options.ApiKey.Enabled);
    }

    [Theory]
    [InlineData("service:options")]
    [InlineData("service.options")]
    public void Execute_CanonicalRootAndNestedMemberCasePreserveFileAndSecretValues(string key)
    {
        using var files = new FileFixture("""
            {"Service":{"Options":{"Label":"ordinary", "Record":{"RECORD_TOKEN":{"key":"nested"}},
              "API_KEY":{"key":"root"}}}}
            """);
        var alpha = new SecretProvider("alpha") { Resolve = (reference, _) => Success("alpha", reference.Key) };
        var result = CreateEngine(bases: [files.Provider], secrets: [alpha]).Execute(EnvironmentName, key, typeof(AnnotatedOptions));
        var value = Resolved<AnnotatedOptions>(result);
        Assert.Equal("ordinary", value.Label);
        AssertSecret(value.ApiKey, true, "root", "alpha");
        AssertSecret(value.Record.Token, true, "nested", "alpha");
    }

    [Fact]
    public void Execute_ColonRootUsesCanonicalDirectEnvironmentCandidateAndNestedNames()
    {
        var environment = new TestEnvironmentProvider(new Dictionary<string, string?>
        {
            ["SERVICE__OPTIONS"] = """{"Label":"direct", "record":{"record_token":"nested"}, "api_key":"root"}"""
        });
        var result = CreateEngine(environment: environment).Execute(EnvironmentName, "Service:Options", typeof(AnnotatedOptions));
        var value = Resolved<AnnotatedOptions>(result);
        Assert.True(result.DirectRoot);
        Assert.Equal("direct", value.Label);
        AssertSecret(value.ApiKey, true, "root", nameof(EnvironmentConfigProvider));
        AssertSecret(value.Record.Token, true, "nested", nameof(EnvironmentConfigProvider));
    }

    private static ConfigCompositionEngine CreateEngine(
        IConfigProvider[]? bases = null,
        IConfigSecretProvider[]? secrets = null,
        TestEnvironmentProvider? environment = null,
        IConfigSecretDeclarationSource[]? declarations = null,
        AppSurfaceConfigOptions? options = null,
        TimeProvider? time = null) =>
        new(new EnvironmentConfigProvider(environment ?? new TestEnvironmentProvider()), bases ?? [],
            secrets ?? [], declarations ?? [], options ?? new AppSurfaceConfigOptions(), time ?? TimeProvider.System);

    private static T Resolved<T>(ConfigCompositionExecutionResult result)
    {
        Assert.Empty(result.Failures);
        Assert.Equal(ConfigCompositionRootState.Resolved, result.State);
        return Assert.IsType<T>(result.Value);
    }

    private static ConfigCompositionFailure Failed(ConfigCompositionExecutionResult result, string code)
    {
        Assert.Equal(ConfigCompositionRootState.Failed, result.State);
        Assert.Null(result.Value);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(code, failure.Code);
        return failure;
    }

    private static void AssertSecret<T>(Secret<T> secret, bool enabled, T expected, string provider) where T : notnull
    {
        Assert.Equal(enabled, secret.Enabled);
        Assert.True(secret.HasValue);
        Assert.Equal(expected, secret.Value);
        Assert.True(secret.TryGetValue(out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(provider, secret.ResolvedProvider);
    }

    private static ConfigSecretProviderResolution Success(string provider, string payload) =>
        ConfigSecretProviderResolution.Resolved(payload, ConfigSecretSourceMetadata.Create(provider));

    private static ConfigSecretProviderResolution Outcome(string provider, ConfigSecretProviderResolutionStatus status) =>
        status switch
        {
            ConfigSecretProviderResolutionStatus.Resolved => Success(provider, provider + "-value"),
            ConfigSecretProviderResolutionStatus.Unclaimed => ConfigSecretProviderResolution.Unclaimed(provider),
            ConfigSecretProviderResolutionStatus.Missing => ConfigSecretProviderResolution.Missing(provider),
            ConfigSecretProviderResolutionStatus.AccessDenied => ConfigSecretProviderResolution.AccessDenied(provider),
            ConfigSecretProviderResolutionStatus.Unavailable => ConfigSecretProviderResolution.Unavailable(provider),
            ConfigSecretProviderResolutionStatus.InvalidReference => ConfigSecretProviderResolution.InvalidReference(provider),
            _ => ConfigSecretProviderResolution.ProviderFailed(provider, retryable: true)
        };

    // File declarations go through the production loader, including Layers and LoadEvents.
    // RawProvider is deliberately a separate whole-root value source, never a descriptor source.
    private sealed class FileFixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("appsurface-engine-");
        public FileBasedConfigProvider Provider { get; }

        public FileFixture(string json, string? productionJson = null)
        {
            File.WriteAllText(Path.Combine(_directory.FullName, "appsettings.json"), json);
            if (productionJson is not null)
            {
                File.WriteAllText(Path.Combine(_directory.FullName, "appsettings.Production.json"), productionJson);
            }
            Provider = new FileBasedConfigProvider(
                new FileLocation(_directory.FullName), NullLogger<FileBasedConfigProvider>.Instance);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed record FileLocation(string Directory) : IConfigFileLocationProvider;

    private sealed class SecretProvider(string id) : IConfigSecretProvider
    {
        public string Id => id;
        public ConcurrentQueue<ConfigSecretReference> Validations { get; } = new();
        public ConcurrentQueue<(ConfigSecretReference Reference, TimeSpan Remaining)> Resolutions { get; } = new();
        public Func<ConfigSecretReference, ConfigSecretReferenceValidation>? Validate { get; init; }
        public Func<ConfigSecretReference, ConfigSecretResolutionContext, ConfigSecretProviderResolution>? Resolve { get; init; }

        public ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference)
        {
            Validations.Enqueue(reference);
            return Validate?.Invoke(reference) ?? ConfigSecretReferenceValidation.Supported();
        }

        ConfigSecretProviderResolution IConfigSecretProvider.Resolve(
            ConfigSecretReference reference, ConfigSecretResolutionContext context)
        {
            Resolutions.Enqueue((reference, context.Remaining));
            return Resolve?.Invoke(reference, context) ?? Success(Id, reference.Key);
        }
    }

    private sealed class RawProvider(string body, string name = "raw-base", int priority = 5, bool sensitive = true)
        : IConfigProvider, IConfigCompositionValueProvider
    {
        public string Name => name;
        public int Priority => priority;
        public ConcurrentQueue<(string Environment, string Key)> Reads { get; } = new();
        public ConcurrentQueue<(string Environment, string Key)> TypedReads { get; } = new();
        public Func<string, string, ConfigCompositionValueResolution>? Resolve { get; init; }

        public T? GetValue<T>(string environment, string key)
        {
            TypedReads.Enqueue((environment, key));
            throw new InvalidOperationException("Composition must use the raw root capability.");
        }

        public ConfigCompositionValueResolution ResolveRaw(string environment, string logicalKey)
        {
            Reads.Enqueue((environment, logicalKey));
            return Resolve?.Invoke(environment, logicalKey)
                ?? ConfigCompositionValueResolution.Resolved(body, Name, Priority, sensitive);
        }
    }

    private sealed class DeclarationSource(params ConfigSecretConfiguredClaim[] claims) : IConfigSecretDeclarationSource
    {
        public ConcurrentQueue<string> Reads { get; } = new();
        public IReadOnlyList<ConfigSecretConfiguredClaim> InspectClaims(
            string rootLogicalPath, IReadOnlyList<string> secretDestinationPaths)
        {
            Reads.Enqueue(rootLogicalPath);
            return claims;
        }
    }

    private sealed class TestEnvironmentProvider(Dictionary<string, string?>? values = null) : IEnvironmentProvider
    {
        public string Environment => EnvironmentName;
        public bool IsDevelopment => false;
        public ConcurrentQueue<string> Lookups { get; } = new();

        public string? GetEnvironmentVariable(string name, string? defaultValue = null)
        {
            Lookups.Enqueue(name);
            return values is not null && values.TryGetValue(name, out var value) ? value : defaultValue;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class OneSecret
    {
        public Secret<string> ApiKey { get; init; } = new();
    }

    private sealed class TwoSecrets
    {
        public Secret<string> ApiKey { get; init; } = new();
        public Secret<string> SigningKey { get; init; } = new();
        public string Endpoint { get; init; } = "";
    }

    private sealed class IntSecret
    {
        public Secret<int> ApiKey { get; init; } = new();
    }

    private sealed class NestedOptions
    {
        public NestedSecretOptions Nested { get; init; } = new();
    }

    private sealed class NestedSecretOptions
    {
        public Secret<string> ApiKey { get; init; } = new();
        public string Label { get; init; } = "";
    }

    private sealed class ArrayAndSecretOptions
    {
        public string[] Endpoints { get; init; } = [];
        public Secret<string> ApiKey { get; init; } = new();
    }

    private sealed record AnnotatedOptions
    {
        public string Label { get; init; } = "";
        [JsonPropertyName("api_key")]
        public Secret<string> ApiKey { get; init; } = new();
        public ConstructorBoundSecret Record { get; init; } = new(new Secret<string>());
    }

    private sealed class ConstructorBoundSecret(Secret<string> token)
    {
        [JsonPropertyName("record_token")]
        public Secret<string> Token { get; } = token;
    }

    private sealed class CountAndSecretOptions
    {
        public Secret<string> ApiKey { get; init; } = new();
        public int Count { get; init; }
    }

    private sealed class ReadOnlySecretOptions
    {
        public Secret<string> ApiKey { get; } = new();
    }

    private sealed class ValidatedSecretOptions
    {
        [Required]
        [ValidateObjectMembers]
        public Secret<string> ApiKey { get; init; } = new();
    }
}
