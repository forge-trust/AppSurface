using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfaceCanaryRegistrationTests
{
    [Fact]
    public void EvaluationContext_PreservesValidatedInputs()
    {
        var freshSince = new DateTimeOffset(2026, 7, 12, 12, 30, 45, TimeSpan.Zero);

        var context = new AppSurfaceCanaryEvaluationContext("forwarding.alpha-evidence", " deploy-42 ", freshSince);

        Assert.Equal("forwarding.alpha-evidence", context.Name);
        Assert.Equal(" deploy-42 ", context.Marker);
        Assert.Equal(freshSince, context.FreshSince);
    }

    [Fact]
    public void EvaluationContext_RejectsNullName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceCanaryEvaluationContext(null!, null, null));

        Assert.Equal("name", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Forwarding")]
    [InlineData("forwarding..proof")]
    [InlineData("-forwarding")]
    [InlineData("forwarding-")]
    [InlineData("forwarding_alpha")]
    [InlineData("proof\n")]
    [InlineData("proof\r")]
    public void EvaluationContext_RejectsInvalidName(string name)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new AppSurfaceCanaryEvaluationContext(name, null, null));

        Assert.Equal("name", exception.ParamName);
        Assert.StartsWith("ASCAN101", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppSurfaceCanaryStatus.Pass)]
    [InlineData(AppSurfaceCanaryStatus.Pending)]
    [InlineData(AppSurfaceCanaryStatus.Fail)]
    [InlineData(AppSurfaceCanaryStatus.Stale)]
    [InlineData(AppSurfaceCanaryStatus.NotConfigured)]
    public void Result_PreservesDefinedStatus(AppSurfaceCanaryStatus status)
    {
        var result = new AppSurfaceCanaryResult(status);

        Assert.Equal(status, result.Status);
        Assert.Null(result.ObservedAt);
        Assert.Null(result.MatchedCount);
        Assert.Null(result.ReasonCode);
        Assert.Null(result.Summary);
        Assert.Null(result.CorrelationId);
        Assert.Empty(result.Details);
    }

    [Fact]
    public void Result_SnapshotsConfiguredEvidenceAndNormalizesObservedAt()
    {
        AppSurfaceCanaryResultOptions? captured = null;
        var observedAt = new DateTimeOffset(2026, 7, 16, 1, 2, 3, TimeSpan.FromHours(-4));

        var result = new AppSurfaceCanaryResult(
            AppSurfaceCanaryStatus.Pending,
            options =>
            {
                captured = options;
                options.ObservedAt = observedAt;
                options.MatchedCount = 2;
                options.ReasonCode = "multiple-matches";
                options.Summary = "Two operator-safe proofs matched.";
                options.CorrelationId = "deploy:20260716.1";
                options.AddDetail("proof.kind", "forwarding");
                options.AddDetail("provider.region", "us-east");
            });

        captured!.ObservedAt = DateTimeOffset.MinValue;
        captured.MatchedCount = 99;
        captured.ReasonCode = "changed";
        captured.Summary = "Changed later.";
        captured.CorrelationId = "changed";
        captured.AddDetail("changed", "later");

        Assert.Equal(observedAt.ToUniversalTime(), result.ObservedAt);
        Assert.Equal(2, result.MatchedCount);
        Assert.Equal("multiple-matches", result.ReasonCode);
        Assert.Equal("Two operator-safe proofs matched.", result.Summary);
        Assert.Equal("deploy:20260716.1", result.CorrelationId);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["proof.kind"] = "forwarding",
                ["provider.region"] = "us-east"
            },
            result.Details);
        Assert.False(result.Details.ContainsKey("changed"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)result.Details).Add("mutated", "later"));
    }

    [Fact]
    public void Result_RejectsNullConfiguration()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceCanaryResult(AppSurfaceCanaryStatus.Pass, null!));

        Assert.Equal("configure", exception.ParamName);
    }

    [Fact]
    public void Result_PropagatesConfigurationException()
    {
        var expected = new TestRegistrationException();

        var actual = Assert.Throws<TestRegistrationException>(() =>
            new AppSurfaceCanaryResult(AppSurfaceCanaryStatus.Pass, _ => throw expected));

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData("matched-count")]
    [InlineData("reason-code")]
    [InlineData("summary")]
    [InlineData("correlation-id")]
    public void Result_RejectsInvalidScalarOptions(string field)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Fail,
                options =>
                {
                    switch (field)
                    {
                        case "matched-count":
                            options.MatchedCount = -1;
                            break;
                        case "reason-code":
                            options.ReasonCode = "Invalid_Code";
                            break;
                        case "summary":
                            options.Summary = " ";
                            break;
                        default:
                            options.CorrelationId = "-invalid";
                            break;
                    }
                }));

        Assert.Equal("configure", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two..parts")]
    [InlineData("under_score")]
    public void Result_AddDetailRejectsInvalidKey(string key)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Pass,
                options => options.AddDetail(key, "value")));

        Assert.Equal("key", exception.ParamName);
    }

    [Fact]
    public void Result_AddDetailRejectsNullKeyAndValue()
    {
        Assert.Equal(
            "key",
            Assert.Throws<ArgumentNullException>(() =>
                new AppSurfaceCanaryResult(
                    AppSurfaceCanaryStatus.Pass,
                    options => options.AddDetail(null!, "value"))).ParamName);
        Assert.Equal(
            "value",
            Assert.Throws<ArgumentNullException>(() =>
                new AppSurfaceCanaryResult(
                    AppSurfaceCanaryStatus.Pass,
                    options => options.AddDetail("proof.kind", null!))).ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("line\nbreak")]
    public void Result_AddDetailRejectsInvalidValue(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Pass,
                options => options.AddDetail("proof.kind", value)));

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void Result_RejectsMalformedUnicodeInSummaryAndDetail()
    {
        var malformed = new string((char)0xD800, 1);

        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentException>(() =>
                new AppSurfaceCanaryResult(
                    AppSurfaceCanaryStatus.Pass,
                    options => options.Summary = malformed)).ParamName);
        Assert.Equal(
            "value",
            Assert.Throws<ArgumentException>(() =>
                new AppSurfaceCanaryResult(
                    AppSurfaceCanaryStatus.Pass,
                    options => options.AddDetail("proof.kind", malformed))).ParamName);
    }

    [Fact]
    public void Result_EnforcesUtf8ByteBoundaries()
    {
        var accepted = new string('\u00E9', 64);
        var rejected = accepted + "a";
        var maximumLengthKey = new string('a', 64);

        var result = new AppSurfaceCanaryResult(
            AppSurfaceCanaryStatus.Pass,
            options => options.AddDetail(maximumLengthKey, accepted));
        Assert.Equal(accepted, result.Details[maximumLengthKey]);

        Assert.Equal(
            "value",
            Assert.Throws<ArgumentException>(() =>
                new AppSurfaceCanaryResult(
                    AppSurfaceCanaryStatus.Pass,
                    options => options.AddDetail("proof.kind", rejected))).ParamName);
    }

    [Fact]
    public void Result_AcceptsExactScalarAndCollectionLimits()
    {
        var reasonCode = $"a{new string('b', 62)}z";
        var correlationId = $"a{new string('_', 126)}z";
        var summary = new string('\u00E9', 128);

        var result = new AppSurfaceCanaryResult(
            AppSurfaceCanaryStatus.Stale,
            options =>
            {
                options.MatchedCount = 0;
                options.ReasonCode = reasonCode;
                options.CorrelationId = correlationId;
                options.Summary = summary;
                for (var index = 0; index < 16; index++)
                {
                    options.AddDetail($"detail-{index}", "value");
                }
            });

        Assert.Equal(reasonCode, result.ReasonCode);
        Assert.Equal(correlationId, result.CorrelationId);
        Assert.Equal(summary, result.Summary);
        Assert.Equal(16, result.Details.Count);
    }

    [Fact]
    public void Result_RejectsValuesImmediatelyBeyondScalarLimits()
    {
        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentException>(() =>
                new AppSurfaceCanaryResult(
                    AppSurfaceCanaryStatus.Fail,
                    options => options.ReasonCode = new string('a', 65))).ParamName);
        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentException>(() =>
                new AppSurfaceCanaryResult(
                    AppSurfaceCanaryStatus.Fail,
                    options => options.CorrelationId = new string('a', 129))).ParamName);
        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentException>(() =>
                new AppSurfaceCanaryResult(
                    AppSurfaceCanaryStatus.Fail,
                    options => options.Summary = new string('\u00E9', 129))).ParamName);
        Assert.Equal(
            "key",
            Assert.Throws<ArgumentException>(() =>
                new AppSurfaceCanaryResult(
                    AppSurfaceCanaryStatus.Fail,
                    options => options.AddDetail(new string('a', 65), "value"))).ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("under_score")]
    public void Result_RejectsInvalidReasonCodeGrammar(string reasonCode)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Fail,
                options => options.ReasonCode = reasonCode));

        Assert.Equal("configure", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("contains space")]
    [InlineData("contains/slash")]
    public void Result_RejectsInvalidCorrelationIdGrammar(string correlationId)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Fail,
                options => options.CorrelationId = correlationId));

        Assert.Equal("configure", exception.ParamName);
    }

    [Fact]
    public void Result_RejectsDuplicateAndSeventeenthDetailsImmediately()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Pass,
                options =>
                {
                    options.AddDetail("duplicate", "one");
                    options.AddDetail("duplicate", "two");
                }));

        Assert.Throws<InvalidOperationException>(() =>
            new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Pass,
                options =>
                {
                    for (var index = 0; index < 17; index++)
                    {
                        options.AddDetail(
                            $"detail-{index}",
                            index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }));
    }

    [Fact]
    public void Result_RejectsUndefinedStatus()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AppSurfaceCanaryResult((AppSurfaceCanaryStatus)99));

        Assert.Equal("status", exception.ParamName);
    }

    [Fact]
    public void AddAppSurfaceCanary_ValidatesNullArguments()
    {
        IServiceCollection services = null!;

        Assert.Equal(
            "services",
            Assert.Throws<ArgumentNullException>(
                () => services.AddAppSurfaceCanary<PassingEvaluator>("proof")).ParamName);
        Assert.Equal(
            "name",
            Assert.Throws<ArgumentNullException>(
                () => new ServiceCollection().AddAppSurfaceCanary<PassingEvaluator>(null!)).ParamName);
    }

    [Fact]
    public void AddAppSurfaceCanary_ReturnsOriginalCollectionAndCapturesDefaults()
    {
        var services = new ServiceCollection();

        var returned = services.AddAppSurfaceCanary<PassingEvaluator>("proof");

        Assert.Same(services, returned);
        var descriptor = Assert.Single(
            services,
            service => service.ServiceType == typeof(AppSurfaceCanaryDescriptor)).ImplementationInstance;
        var canary = Assert.IsType<AppSurfaceCanaryDescriptor>(descriptor);
        Assert.Equal("proof", canary.Name);
        Assert.Equal("proof", canary.DisplayName);
        Assert.Null(canary.Description);
        Assert.Empty(canary.Tags);
        Assert.False(canary.MarkerRequired);
        Assert.False(canary.FreshSinceRequired);
        Assert.Empty(canary.AllowedDetailKeys);
        Assert.Equal(typeof(PassingEvaluator), canary.EvaluatorType);
    }

    [Fact]
    public void AddAppSurfaceCanary_SnapshotsConfiguredMetadataAndRequirements()
    {
        AppSurfaceCanaryRegistrationOptions? captured = null;
        var services = new ServiceCollection();

        services.AddAppSurfaceCanary<PassingEvaluator>(
            "proof.deploy",
            options =>
            {
                captured = options;
                options.DisplayName = "Deploy proof";
                options.Description = "Proves the current deployment.";
                options.Tags.Add("z-last");
                options.Tags.Add("deploy");
                options.Tags.Add("deploy");
                options.RequireMarker();
                options.RequireMarker();
                options.RequireFreshSince();
                options.RequireFreshSince();
                options.AllowedDetailKeys.Add("provider.region");
                options.AllowedDetailKeys.Add("proof.kind");
                options.AllowedDetailKeys.Add("proof.kind");
            });

        captured!.DisplayName = "Changed later";
        captured.Description = "Changed later";
        captured.Tags.Clear();
        captured.Tags.Add("changed");
        captured.AllowedDetailKeys.Clear();
        captured.AllowedDetailKeys.Add("changed");

        var descriptor = GetDescriptor(services);
        Assert.Equal("Deploy proof", descriptor.DisplayName);
        Assert.Equal("Proves the current deployment.", descriptor.Description);
        Assert.Equal(new[] { "deploy", "z-last" }, descriptor.Tags);
        Assert.True(descriptor.MarkerRequired);
        Assert.True(descriptor.FreshSinceRequired);
        Assert.True(
            new HashSet<string>(descriptor.AllowedDetailKeys, StringComparer.Ordinal)
                .SetEquals(new[] { "proof.kind", "provider.region" }));
        Assert.False(descriptor.AllowedDetailKeys.Contains("changed"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two..parts")]
    [InlineData("under_score")]
    public void AddAppSurfaceCanary_InvalidAllowedDetailKeyIsAtomic(string key)
    {
        var services = new ServiceCollection();
        services.AddSingleton("existing");
        var count = services.Count;

        var exception = Assert.Throws<ArgumentException>(() =>
            services.AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                options => options.AllowedDetailKeys.Add(key)));

        Assert.Equal("configure", exception.ParamName);
        Assert.StartsWith("ASCAN101", exception.Message, StringComparison.Ordinal);
        Assert.Equal(count, services.Count);
    }

    [Fact]
    public void AddAppSurfaceCanary_NullOrSeventeenthAllowedDetailKeyIsAtomic()
    {
        var services = new ServiceCollection();
        services.AddSingleton("existing");
        var count = services.Count;

        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentException>(() =>
                services.AddAppSurfaceCanary<PassingEvaluator>(
                    "proof",
                    options => options.AllowedDetailKeys.Add(null!))).ParamName);
        Assert.Equal(count, services.Count);

        Assert.Equal(
            "configure",
            Assert.Throws<ArgumentException>(() =>
                services.AddAppSurfaceCanary<PassingEvaluator>(
                    "proof",
                    options =>
                    {
                        for (var index = 0; index < 17; index++)
                        {
                            options.AllowedDetailKeys.Add($"detail-{index}");
                        }
                    })).ParamName);
        Assert.Equal(count, services.Count);
    }

    [Fact]
    public void AddAppSurfaceCanary_AcceptsSixteenUniqueAllowedKeysAndDuplicateDeclarations()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceCanary<PassingEvaluator>(
            "proof",
            options =>
            {
                for (var index = 0; index < 16; index++)
                {
                    options.AllowedDetailKeys.Add($"detail-{index}");
                }

                options.AllowedDetailKeys.Add("detail-0");
            });

        Assert.Equal(16, GetDescriptor(services).AllowedDetailKeys.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Proof")]
    [InlineData("proof..deploy")]
    [InlineData("proof/deploy")]
    [InlineData("proof-")]
    public void AddAppSurfaceCanary_RejectsInvalidNamesWithoutMutation(string name)
    {
        var services = new ServiceCollection();
        services.AddSingleton("existing");
        var count = services.Count;

        var exception = Assert.Throws<ArgumentException>(
            () => services.AddAppSurfaceCanary<PassingEvaluator>(name));

        Assert.Equal("name", exception.ParamName);
        Assert.StartsWith("ASCAN101", exception.Message, StringComparison.Ordinal);
        Assert.Equal(count, services.Count);
    }

    [Fact]
    public void AddAppSurfaceCanary_RejectsOverlongNameWithoutMutation()
    {
        var services = new ServiceCollection();
        var name = new string('a', AppSurfaceCanaryValidation.MaximumNameLength + 1);

        var exception = Assert.Throws<ArgumentException>(
            () => services.AddAppSurfaceCanary<PassingEvaluator>(name));

        Assert.Equal("name", exception.ParamName);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("display")]
    [InlineData("description")]
    [InlineData("tag")]
    public void AddAppSurfaceCanary_InvalidConfiguredMetadataIsAtomic(string invalidField)
    {
        var services = new ServiceCollection();
        services.AddSingleton("existing");
        var count = services.Count;

        var exception = Assert.Throws<ArgumentException>(() =>
            services.AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                options =>
                {
                    if (invalidField == "display")
                    {
                        options.DisplayName = " ";
                    }
                    else if (invalidField == "description")
                    {
                        options.Description = new string('x', AppSurfaceCanaryValidation.MaximumDescriptionLength + 1);
                    }
                    else
                    {
                        options.Tags.Add("Invalid_Tag");
                    }
                }));

        Assert.Equal("configure", exception.ParamName);
        Assert.StartsWith("ASCAN101", exception.Message, StringComparison.Ordinal);
        Assert.Equal(count, services.Count);
    }

    [Fact]
    public void AddAppSurfaceCanary_ThrowingCallbackIsAtomic()
    {
        var services = new ServiceCollection();
        services.AddSingleton("existing");
        var count = services.Count;

        var exception = Assert.Throws<TestRegistrationException>(() =>
            services.AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                _ => throw new TestRegistrationException()));

        Assert.NotNull(exception);
        Assert.Equal(count, services.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two.parts")]
    [InlineData("deploy\n")]
    [InlineData("deploy\r")]
    public void AddAppSurfaceCanary_RejectsInvalidTags(string tag)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                options => options.Tags.Add(tag)));

        Assert.Equal("configure", exception.ParamName);
        Assert.StartsWith("ASCAN101", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAppSurfaceCanary_RejectsOverlongTag()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                options => options.Tags.Add(new string('a', 65))));

        Assert.Equal("configure", exception.ParamName);
    }

    [Fact]
    public void AddAppSurfaceCanary_RejectsNullTag()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddAppSurfaceCanary<PassingEvaluator>(
                "proof",
                options => options.Tags.Add(null!)));

        Assert.Equal("configure", exception.ParamName);
    }

    [Fact]
    public void AddAppSurfaceCanary_RegistersDefaultEvaluatorAsTransient()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceCanary<PassingEvaluator>("proof");

        var evaluator = Assert.Single(services, service => service.ServiceType == typeof(PassingEvaluator));
        Assert.Equal(ServiceLifetime.Transient, evaluator.Lifetime);
        Assert.Equal(typeof(PassingEvaluator), evaluator.ImplementationType);
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public void AddAppSurfaceCanary_PreservesExplicitEvaluatorLifetime(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        ((ICollection<ServiceDescriptor>)services).Add(
            new ServiceDescriptor(typeof(PassingEvaluator), typeof(PassingEvaluator), lifetime));

        services.AddAppSurfaceCanary<PassingEvaluator>("proof");

        var evaluator = Assert.Single(services, service => service.ServiceType == typeof(PassingEvaluator));
        Assert.Equal(lifetime, evaluator.Lifetime);
    }

    [Fact]
    public void AddAppSurfaceCanary_RegistersSharedInfrastructureOnce()
    {
        var services = new ServiceCollection();

        services.AddAppSurfaceCanary<PassingEvaluator>("proof.one");
        services.AddAppSurfaceCanary<SecondPassingEvaluator>("proof.two");

        Assert.Single(services, service => service.ServiceType == typeof(AppSurfaceCanaryRegistry));
        Assert.Single(services, service => service.ServiceType == typeof(AppSurfaceCanaryEvaluationRunner));
        Assert.Single(services, service => service.ServiceType == typeof(AppSurfaceCanaryMappingState));
        Assert.Single(
            services,
            service => service.ServiceType == typeof(IStartupFilter)
                && service.ImplementationType == typeof(AppSurfaceCanaryStartupValidator));
        Assert.Equal(2, services.Count(service => service.ServiceType == typeof(AppSurfaceCanaryDescriptor)));
    }

    [Fact]
    public void Registry_RejectsDuplicateNamesWhenMaterialized()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppSurfaceCanary<PassingEvaluator>("proof");
        services.AddAppSurfaceCanary<SecondPassingEvaluator>("proof");
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<AppSurfaceCanaryRegistry>());

        Assert.StartsWith("ASCAN102", exception.Message, StringComparison.Ordinal);
        Assert.Contains("proof", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_UsesExactOrdinalNameLookup()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceCanary<PassingEvaluator>("proof.deploy");
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<AppSurfaceCanaryRegistry>();

        Assert.True(registry.TryGet("proof.deploy", out var descriptor));
        Assert.Equal("proof.deploy", descriptor.Name);
        Assert.False(registry.TryGet("Proof.Deploy", out _));
    }

    private static AppSurfaceCanaryDescriptor GetDescriptor(IServiceCollection services) =>
        Assert.IsType<AppSurfaceCanaryDescriptor>(
            Assert.Single(
                services,
                service => service.ServiceType == typeof(AppSurfaceCanaryDescriptor)).ImplementationInstance);

    private sealed class PassingEvaluator : IAppSurfaceCanaryEvaluator
    {
        public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AppSurfaceCanaryResult(AppSurfaceCanaryStatus.Pass));
    }

    private sealed class SecondPassingEvaluator : IAppSurfaceCanaryEvaluator
    {
        public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
            AppSurfaceCanaryEvaluationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AppSurfaceCanaryResult(AppSurfaceCanaryStatus.Pass));
    }

    private sealed class TestRegistrationException : Exception;
}
