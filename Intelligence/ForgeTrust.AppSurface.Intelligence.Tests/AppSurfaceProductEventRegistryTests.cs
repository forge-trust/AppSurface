using System.Text.Json;
using ForgeTrust.AppSurface.Intelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Intelligence.Tests;

public sealed class AppSurfaceProductEventRegistryTests
{
    public static TheoryData<string> RegisteredDogfoodEvents => new()
    {
        AppSurfaceProductEventRegistry.DocsSearchSubmitted,
        AppSurfaceProductEventRegistry.DocsSearchReturnedZeroResults,
        AppSurfaceProductEventRegistry.DocsSearchResultSelected,
        AppSurfaceProductEventRegistry.DocsRecoveryLinkSelected,
        AppSurfaceProductEventRegistry.DocsSearchFilterChanged,
        AppSurfaceProductEventRegistry.DocsSearchFrictionFeedbackSubmitted,
        AppSurfaceProductEventRegistry.RazorWireFormFailed,
        AppSurfaceProductEventRegistry.RazorWireFormFailureRecovered,
        AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected
    };

    [Theory]
    [MemberData(nameof(RegisteredDogfoodEvents))]
    public void DogfoodEvents_AreExperimentalContracts(string eventName)
    {
        var contract = AppSurfaceProductEventRegistry.Find(eventName);

        Assert.NotNull(contract);
        Assert.Equal(AppSurfaceProductEventLifecycle.Experimental, contract.Lifecycle);
        Assert.NotEmpty(contract.Purpose);
        Assert.NotEmpty(contract.Owner);
        Assert.NotEmpty(contract.RetentionExpectation);
        Assert.NotEmpty(contract.ForbiddenExamples);
        Assert.All(contract.Properties, property =>
        {
            Assert.NotEmpty(property.Name);
            Assert.NotEmpty(property.Description);
            Assert.True(Enum.IsDefined(property.Sensitivity));
            Assert.True(Enum.IsDefined(property.Cardinality));
            Assert.True(Enum.IsDefined(property.ValueShape));
            Assert.NotEqual(AppSurfaceProductEventCardinality.High, property.Cardinality);
        });
    }

    [Fact]
    public void ProductIntelligenceOptions_ShouldEnableSpecificExperimentalEventsWithoutGlobalToggle()
    {
        var options = new AppSurfaceProductIntelligenceOptions()
            .EnableExperimentalEvents(AppSurfaceProductEventRegistry.DocsSearchFilterChanged);

        Assert.False(options.ExperimentalEventsEnabled);
        Assert.True(options.IsExperimentalEventEnabled(AppSurfaceProductEventRegistry.DocsSearchFilterChanged));
        Assert.False(options.IsExperimentalEventEnabled(AppSurfaceProductEventRegistry.RazorWireFormFailed));
        Assert.Contains(AppSurfaceProductEventRegistry.DocsSearchFilterChanged, options.EnabledExperimentalEventNames);
    }

    [Fact]
    public void ProductIntelligenceOptions_RegisterEventContracts_CopiesContractsAndAcceptsEmptyCollections()
    {
        var contracts = new List<AppSurfaceProductEventContract>
        {
            CreateSkoolieContract()
        };
        var options = new AppSurfaceProductIntelligenceOptions()
            .RegisterEventContracts(contracts)
            .RegisterEventContracts(Array.Empty<AppSurfaceProductEventContract>());

        contracts.Clear();

        Assert.Single(options.RegisteredEventContracts);
        Assert.Equal("skoolie.card.generated", options.RegisteredEventContracts[0].Name);
    }

    [Fact]
    public void ProductIntelligenceOptions_RegisterEventContracts_RejectsNullInputs()
    {
        var options = new AppSurfaceProductIntelligenceOptions();

        Assert.Throws<ArgumentNullException>(() => options.RegisterEventContracts((IEnumerable<AppSurfaceProductEventContract>)null!));
        Assert.Throws<ArgumentNullException>(() => options.RegisterEventContracts((AppSurfaceProductEventContract[])null!));
        Assert.Throws<ArgumentNullException>(() => options.RegisterEventContracts([null!]));
    }

    [Fact]
    public void EventContract_RejectsNullPropertyContracts()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AppSurfaceProductEventContract(
            "docs.search.null_property",
            AppSurfaceProductEventLifecycle.Experimental,
            "Prove null property validation.",
            "Tests",
            "Discard during tests.",
            [null!],
            ["raw query"]));

        Assert.Contains("Property contract entries must not be null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EventContract_RejectsDuplicatePropertyContracts()
    {
        var duplicateProperties = new[]
        {
            new AppSurfaceProductEventPropertyContract(
                "surface",
                "Search surface.",
                AppSurfaceProductEventSensitivity.Operational,
                AppSurfaceProductEventCardinality.Low),
            new AppSurfaceProductEventPropertyContract(
                "surface",
                "Duplicate search surface.",
                AppSurfaceProductEventSensitivity.Operational,
                AppSurfaceProductEventCardinality.Low)
        };

        var exception = Assert.Throws<ArgumentException>(() => new AppSurfaceProductEventContract(
            "docs.search.duplicate",
            AppSurfaceProductEventLifecycle.Experimental,
            "Prove duplicate property validation.",
            "Tests",
            "Discard during tests.",
            duplicateProperties,
            ["raw query"]));

        Assert.Contains("surface", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EventContract_RejectsEmptyPropertyContracts()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AppSurfaceProductEventContract(
            "docs.search.empty",
            AppSurfaceProductEventLifecycle.Experimental,
            "Prove empty property validation.",
            "Tests",
            "Discard during tests.",
            [],
            ["raw query"]));

        Assert.Contains("At least one property contract", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyContract_RejectsInvalidLimitsAndAllowedValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppSurfaceProductEventPropertyContract(
            "surface",
            "Search surface.",
            AppSurfaceProductEventSensitivity.Operational,
            AppSurfaceProductEventCardinality.Low,
            maxLength: 0));

        var duplicateAllowedValue = Assert.Throws<ArgumentException>(() => new AppSurfaceProductEventPropertyContract(
            "surface",
            "Search surface.",
            AppSurfaceProductEventSensitivity.Operational,
            AppSurfaceProductEventCardinality.Low,
            allowedValues: ["search_page", "search_page"]));

        Assert.Contains("search_page", duplicateAllowedValue.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => new AppSurfaceProductEventPropertyContract(
            "surface",
            "Search surface.",
            AppSurfaceProductEventSensitivity.Operational,
            AppSurfaceProductEventCardinality.Low,
            valueShape: AppSurfaceProductEventValueShape.AllowedValue));

        Assert.Throws<ArgumentException>(() => new AppSurfaceProductEventPropertyContract(
            "surface",
            "Search surface.",
            AppSurfaceProductEventSensitivity.Operational,
            AppSurfaceProductEventCardinality.Low,
            allowedValues: ["search_page"],
            valueShape: AppSurfaceProductEventValueShape.BoundedText));
    }

    [Fact]
    public void Contracts_RejectUndefinedEnumMetadata()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppSurfaceProductEventContract(
            "host.invalid_lifecycle",
            (AppSurfaceProductEventLifecycle)99,
            "Prove undefined lifecycle values fail registration.",
            "Host",
            "Discard during tests.",
            [
                new AppSurfaceProductEventPropertyContract(
                    "surface",
                    "Search surface.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low)
            ],
            ["raw query"]));

        Assert.Throws<ArgumentOutOfRangeException>(() => new AppSurfaceProductEventPropertyContract(
            "surface",
            "Search surface.",
            (AppSurfaceProductEventSensitivity)99,
            AppSurfaceProductEventCardinality.Low));

        Assert.Throws<ArgumentOutOfRangeException>(() => new AppSurfaceProductEventPropertyContract(
            "surface",
            "Search surface.",
            AppSurfaceProductEventSensitivity.Operational,
            (AppSurfaceProductEventCardinality)99));

        Assert.Throws<ArgumentOutOfRangeException>(() => new AppSurfaceProductEventPropertyContract(
            "surface",
            "Search surface.",
            AppSurfaceProductEventSensitivity.Operational,
            AppSurfaceProductEventCardinality.Low,
            valueShape: (AppSurfaceProductEventValueShape)99));
    }

    [Fact]
    public void PropertyContract_AllowedValuesInferAllowedValueShape()
    {
        var property = new AppSurfaceProductEventPropertyContract(
            "delivery_state",
            "Normalized delivery state.",
            AppSurfaceProductEventSensitivity.Operational,
            AppSurfaceProductEventCardinality.Low,
            allowedValues: ["queued", "sent"]);

        Assert.Equal(AppSurfaceProductEventValueShape.AllowedValue, property.ValueShape);
    }

    [Fact]
    public void Constructors_RejectEmptyRequiredText()
    {
        Assert.Throws<ArgumentException>(() => new AppSurfaceProductEvent(
            " ",
            DateTimeOffset.UnixEpoch));

        Assert.Throws<ArgumentException>(() => new AppSurfaceProductEventPropertyContract(
            "surface",
            " ",
            AppSurfaceProductEventSensitivity.Operational,
            AppSurfaceProductEventCardinality.Low));

        Assert.Throws<ArgumentException>(() => new AppSurfaceProductEventPropertyContract(
            "surface",
            "Search surface.",
            AppSurfaceProductEventSensitivity.Operational,
            AppSurfaceProductEventCardinality.Low,
            allowedValues: ["search_page", " "]));

        Assert.Throws<ArgumentException>(() => new AppSurfaceProductEventContract(
            "docs.search.blank",
            AppSurfaceProductEventLifecycle.Experimental,
            "Prove required text validation.",
            "Tests",
            "Discard during tests.",
            [
                new AppSurfaceProductEventPropertyContract(
                    "surface",
                    "Search surface.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low)
            ],
            ["raw query", " "]));
    }

    [Fact]
    public void All_DoesNotContainDuplicateEventNames()
    {
        var duplicate = AppSurfaceProductEventRegistry.All
            .GroupBy(contract => contract.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        Assert.Null(duplicate);
    }

    [Fact]
    public void ComposedRegistry_IncludesBuiltInsAndCustomContracts()
    {
        using var provider = CreateProvider(options => options.RegisterEventContracts(CreateSkoolieContract()));
        var registry = provider.GetRequiredService<IAppSurfaceProductEventRegistry>();

        Assert.NotNull(registry.Find(AppSurfaceProductEventRegistry.DocsSearchSubmitted));
        Assert.NotNull(registry.Find("skoolie.card.generated"));
        Assert.Contains("token", registry.ForbiddenProperties);
    }

    [Fact]
    public void ComposedRegistry_TreatsIdenticalDuplicatesAsIdempotent()
    {
        var contract = CreateSkoolieContract();
        using var provider = CreateProvider(options => options
            .RegisterEventContracts(contract)
            .RegisterEventContracts(contract));
        var registry = provider.GetRequiredService<IAppSurfaceProductEventRegistry>();

        Assert.Single(registry.All, candidate => candidate.Name == "skoolie.card.generated");
    }

    [Fact]
    public void ComposedRegistry_TreatsReorderedSemanticDuplicatesAsIdempotent()
    {
        using var provider = CreateProvider(options => options
            .RegisterEventContracts(CreateReorderableContract())
            .RegisterEventContracts(CreateReorderableContract(reordered: true)));
        var registry = provider.GetRequiredService<IAppSurfaceProductEventRegistry>();

        Assert.Single(registry.All, candidate => candidate.Name == "host.reordered_duplicate");
    }

    [Fact]
    public void ComposedRegistry_RejectsConflictingDuplicateContracts()
    {
        using var provider = CreateProvider(options => options
            .RegisterEventContracts(CreateSkoolieContract())
            .RegisterEventContracts(CreateSkoolieContract(owner: "Another.Package")));

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IAppSurfaceProductEventRegistry>());

        Assert.Contains("skoolie.card.generated", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Skoolie", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Another.Package", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductIntelligenceOptionsValidator_RejectsConflictingDuplicateContracts()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceProductIntelligence(options => options
            .RegisterEventContracts(CreateSkoolieContract())
            .RegisterEventContracts(CreateSkoolieContract(owner: "Another.Package")));
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        var monitor = provider.GetRequiredService<IOptions<AppSurfaceProductIntelligenceOptions>>();

        var exception = Assert.Throws<OptionsValidationException>(() => monitor.Value);

        Assert.Contains("skoolie.card.generated", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Another.Package", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("query")]
    public void ComposedRegistry_RejectsCustomForbiddenProperties(string propertyName)
    {
        var contract = new AppSurfaceProductEventContract(
            "host.forbidden",
            AppSurfaceProductEventLifecycle.Stable,
            "Prove forbidden custom properties fail registration.",
            "Host",
            "Discard during tests.",
            [
                new AppSurfaceProductEventPropertyContract(
                    propertyName,
                    "Forbidden property.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low)
            ],
            ["secret values"]);

        using var provider = CreateProvider(options => options.RegisterEventContracts(contract));

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IAppSurfaceProductEventRegistry>());

        Assert.Contains(propertyName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("child@example.test", "safe.property")]
    [InlineData("host.safe", "child@example.test")]
    public void ComposedRegistry_RejectsUnsafeCustomEventOrPropertyNames(
        string eventName,
        string propertyName)
    {
        var contract = new AppSurfaceProductEventContract(
            eventName,
            AppSurfaceProductEventLifecycle.Stable,
            "Prove unsafe custom contract names fail registration.",
            "Host",
            "Discard during tests.",
            [
                new AppSurfaceProductEventPropertyContract(
                    propertyName,
                    "Unsafe property name.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low)
            ],
            ["unsafe names"]);

        using var provider = CreateProvider(options => options.RegisterEventContracts(contract));

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IAppSurfaceProductEventRegistry>());

        Assert.Contains("[unsafe-property-name]", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("child@example.test", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposedRegistry_RegistrationDiagnosticsScrubUnsafeOwners()
    {
        var contract = new AppSurfaceProductEventContract(
            "host.owner",
            AppSurfaceProductEventLifecycle.Stable,
            "Prove unsafe custom owner labels are scrubbed in registration diagnostics.",
            "child@example.test",
            "Discard during tests.",
            [
                new AppSurfaceProductEventPropertyContract(
                    "token",
                    "Forbidden property.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low)
            ],
            ["unsafe owner labels"]);

        using var provider = CreateProvider(options => options.RegisterEventContracts(contract));

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IAppSurfaceProductEventRegistry>());

        Assert.Contains("[unsafe-owner]", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("child@example.test", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposedRegistry_RejectsCustomSensitiveAndHighCardinalityProperties()
    {
        using var sensitiveProvider = CreateProvider(options => options.RegisterEventContracts(new AppSurfaceProductEventContract(
            "host.sensitive",
            AppSurfaceProductEventLifecycle.Stable,
            "Prove sensitive custom properties fail registration.",
            "Host",
            "Discard during tests.",
            [
                new AppSurfaceProductEventPropertyContract(
                    "recipient_segment",
                    "Potentially sensitive segment.",
                    AppSurfaceProductEventSensitivity.Sensitive,
                    AppSurfaceProductEventCardinality.Low)
            ],
            ["recipient identity"])));
        using var highProvider = CreateProvider(options => options.RegisterEventContracts(new AppSurfaceProductEventContract(
            "host.high",
            AppSurfaceProductEventLifecycle.Stable,
            "Prove high-cardinality custom properties fail registration.",
            "Host",
            "Discard during tests.",
            [
                new AppSurfaceProductEventPropertyContract(
                    "object_key",
                    "High-cardinality object key.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.High)
            ],
            ["object identity"])));

        Assert.Throws<OptionsValidationException>(
            () => sensitiveProvider.GetRequiredService<IAppSurfaceProductEventRegistry>());
        Assert.Throws<OptionsValidationException>(
            () => highProvider.GetRequiredService<IAppSurfaceProductEventRegistry>());
    }

    [Fact]
    public void ComposedRegistry_SourceListMutationsAfterRegistration_DoNotChangeRegistry()
    {
        var properties = new List<AppSurfaceProductEventPropertyContract>
        {
            new(
                "launch_surface",
                "Host-owned surface that generated the card.",
                AppSurfaceProductEventSensitivity.Operational,
                AppSurfaceProductEventCardinality.Low,
                required: true)
        };
        var contract = new AppSurfaceProductEventContract(
            "skoolie.card.generated",
            AppSurfaceProductEventLifecycle.Stable,
            "Measure launch-card generation.",
            "Skoolie",
            "Discard during tests.",
            properties,
            ["child identity"]);
        using var provider = CreateProvider(options => options.RegisterEventContracts(contract));
        var registry = provider.GetRequiredService<IAppSurfaceProductEventRegistry>();

        properties.Add(new AppSurfaceProductEventPropertyContract(
            "late_mutation",
            "Late mutation should not appear.",
            AppSurfaceProductEventSensitivity.Operational,
            AppSurfaceProductEventCardinality.Low));

        var composed = registry.Find("skoolie.card.generated");
        Assert.NotNull(composed);
        Assert.DoesNotContain(composed!.Properties, property => property.Name == "late_mutation");
    }

    [Fact]
    public void Validate_UnregisteredEvent_IsInvalidAndDoesNotEchoSensitiveValues()
    {
        var productEvent = new AppSurfaceProductEvent(
            "docs.search.raw",
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["query"] = "token=abc123 secret stack body"
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);
        var rendered = JsonSerializer.Serialize(result);

        Assert.False(result.IsValid);
        Assert.Null(result.Contract);
        Assert.Empty(result.SanitizedProperties);
        Assert.DoesNotContain("abc123", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("secret stack body", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_UnsafePropertyNames_AreScrubbedFromDiagnostics()
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.DocsSearchSubmitted,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["surface"] = "search_page",
                ["result_count"] = "1",
                ["andrew@example.test"] = "safe-token"
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);
        var rendered = JsonSerializer.Serialize(result);

        Assert.True(result.IsValid);
        Assert.Contains("[unsafe-property-name]", result.RejectedProperties);
        Assert.DoesNotContain("andrew@example.test", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("token")]
    [InlineData("Token")]
    [InlineData("cookie")]
    [InlineData("secret")]
    [InlineData("password")]
    [InlineData("body")]
    [InlineData("stack")]
    public void Validate_DropsForbiddenOrUnregisteredProperties(string propertyName)
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.DocsSearchSubmitted,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["surface"] = "search_page",
                ["result_count"] = "0",
                [propertyName] = "raw-sensitive-value"
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);
        var rendered = JsonSerializer.Serialize(result);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(propertyName, result.SanitizedProperties.Keys);
        Assert.Contains(propertyName, result.RejectedProperties);
        Assert.DoesNotContain("raw-sensitive-value", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DropsMissingRequiredEvent()
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["current_count"] = "1"
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);

        Assert.False(result.IsValid);
        Assert.Contains("rejection_reason", string.Join(" ", result.Diagnostics), StringComparison.Ordinal);
        Assert.Contains("limit_name", string.Join(" ", result.Diagnostics), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DropsSensitiveValueEvenWhenPropertyNameIsAllowed()
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.DocsSearchSubmitted,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["surface"] = "search_page token=abc123",
                ["result_count"] = "0"
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);
        var rendered = JsonSerializer.Serialize(result);

        Assert.False(result.IsValid);
        Assert.Contains("surface", result.RejectedProperties);
        Assert.DoesNotContain("abc123", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("search_page token", string.Join(" ", result.SanitizedProperties.Values), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DropsUnregisteredLowCardinalityValue()
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.DocsRecoveryLinkSelected,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["surface"] = "search_page",
                ["link_kind"] = "https://example.test/customer/123",
                ["source_state"] = "no_results"
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);

        Assert.False(result.IsValid);
        Assert.Contains("link_kind", result.RejectedProperties);
        Assert.DoesNotContain("https://example.test/customer/123", result.SanitizedProperties.Values);
    }

    [Fact]
    public void Validate_DropsLowCardinalityValuesWithUnsafeCharacters()
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.DocsSearchSubmitted,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["surface"] = "search page",
                ["result_count"] = "1"
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);

        Assert.False(result.IsValid);
        Assert.Contains("surface", result.RejectedProperties);
        Assert.DoesNotContain("search page", result.SanitizedProperties.Values);
    }

    [Fact]
    public void RegistryCollections_DoNotExposeMutableBackingState()
    {
        var contracts = Assert.IsAssignableFrom<IList<AppSurfaceProductEventContract>>(AppSurfaceProductEventRegistry.All);
        var forbidden = Assert.IsAssignableFrom<ISet<string>>(AppSurfaceProductEventRegistry.ForbiddenProperties);

        Assert.Throws<NotSupportedException>(() => contracts.Clear());
        Assert.True(forbidden.Remove("token"));
        Assert.Contains("token", AppSurfaceProductEventRegistry.ForbiddenProperties);
    }

    [Fact]
    public void ProductEventAndValidationResults_DoNotExposeMutableBackingState()
    {
        var properties = new Dictionary<string, string>
        {
            ["surface"] = "search_page",
            ["result_count"] = "1"
        };
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.DocsSearchSubmitted,
            DateTimeOffset.UnixEpoch,
            properties);

        properties["surface"] = "sidebar";
        Assert.Equal("search_page", productEvent.Properties["surface"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)productEvent.Properties).Clear());

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);

        Assert.True(result.IsValid);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)result.SanitizedProperties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.RejectedProperties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Diagnostics).Clear());
    }

    [Fact]
    public void Validate_DropsPropertyValuesThatExceedRegisteredMaximumLength()
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.RazorWireFormFailed,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["failure_mode"] = "handled",
                ["response_kind"] = "html",
                ["http_status"] = "422",
                ["failure_ui"] = new string('a', 65)
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);

        Assert.True(result.IsValid);
        Assert.Contains("failure_ui", result.RejectedProperties);
        Assert.DoesNotContain(new string('a', 65), result.SanitizedProperties.Values);
    }

    [Fact]
    public void ComposedRegistry_ValidatesCustomValueShapesAndPrivacy()
    {
        using var provider = CreateProvider(options => options.RegisterEventContracts(CreateSkoolieContract()));
        var registry = provider.GetRequiredService<IAppSurfaceProductEventRegistry>();

        var result = registry.Validate(new AppSurfaceProductEvent(
            "skoolie.card.generated",
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["launch_surface"] = "dashboard",
                ["attachment_count"] = " 004 ",
                ["delivery_state"] = "queued",
                ["raw_query"] = "token=secret-value",
                ["debug_note"] = "child@example.test"
            }));
        var rendered = JsonSerializer.Serialize(result);

        Assert.True(result.IsValid);
        Assert.Equal("4", result.SanitizedProperties["attachment_count"]);
        Assert.Equal("queued", result.SanitizedProperties["delivery_state"]);
        Assert.Contains("raw_query", result.RejectedProperties);
        Assert.Contains("debug_note", result.RejectedProperties);
        Assert.DoesNotContain("child@example.test", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposedRegistry_DeprecatedContractsValidateWithSafeDiagnostic()
    {
        using var provider = CreateProvider(options => options.RegisterEventContracts(CreateSkoolieContract(
            lifecycle: AppSurfaceProductEventLifecycle.Deprecated)));
        var registry = provider.GetRequiredService<IAppSurfaceProductEventRegistry>();

        var result = registry.Validate(new AppSurfaceProductEvent(
            "skoolie.card.generated",
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["launch_surface"] = "dashboard",
                ["attachment_count"] = "1",
                ["delivery_state"] = "queued"
            }));

        Assert.True(result.IsValid);
        Assert.Contains("deprecated", string.Join(" ", result.Diagnostics), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_PreservesUnhandledRazorWireFailureMode()
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.RazorWireFormFailed,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["failure_mode"] = "unhandled",
                ["response_kind"] = "network",
                ["failure_ui"] = "generated"
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);

        Assert.True(result.IsValid);
        Assert.Equal("unhandled", result.SanitizedProperties["failure_mode"]);
        Assert.Equal("network", result.SanitizedProperties["response_kind"]);
    }

    [Fact]
    public void Validate_PreservesRegisteredDocsResultKind()
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.DocsSearchResultSelected,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["surface"] = "search_page",
                ["result_rank"] = "1",
                ["result_kind"] = "javascript-api"
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);

        Assert.True(result.IsValid);
        Assert.Equal("1", result.SanitizedProperties["result_rank"]);
        Assert.Equal("javascript-api", result.SanitizedProperties["result_kind"]);
        Assert.DoesNotContain("result_rank", result.RejectedProperties);
        Assert.DoesNotContain("result_kind", result.RejectedProperties);
    }

    [Theory]
    [InlineData(" 0032 ", "32")]
    [InlineData("0", "0")]
    public void Validate_NormalizesNonNegativeIntegerProperties(string rawValue, string expected)
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["rejection_reason"] = "TooManyLiveSubscriptions",
                ["limit_name"] = "max_live_subscriptions",
                ["current_count"] = rawValue
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.SanitizedProperties["current_count"]);
    }

    [Theory]
    [InlineData(AppSurfaceProductEventRegistry.DocsSearchSubmitted, "active_filter_count")]
    [InlineData(AppSurfaceProductEventRegistry.DocsSearchSubmitted, "query_length")]
    [InlineData(AppSurfaceProductEventRegistry.DocsSearchSubmitted, "result_count")]
    [InlineData(AppSurfaceProductEventRegistry.DocsSearchResultSelected, "result_rank")]
    [InlineData(AppSurfaceProductEventRegistry.RazorWireFormFailed, "http_status")]
    [InlineData(AppSurfaceProductEventRegistry.RazorWireFormFailureRecovered, "attempt_count")]
    public void Validate_NormalizesEveryIntegerPropertyName(string eventName, string propertyName)
    {
        var properties = CreateValidProperties(eventName);
        properties[propertyName] = " 0042 ";
        var productEvent = new AppSurfaceProductEvent(
            eventName,
            DateTimeOffset.UnixEpoch,
            properties);

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);

        Assert.True(result.IsValid);
        Assert.Equal("42", result.SanitizedProperties[propertyName]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1,000")]
    [InlineData("tenant-42")]
    public void Validate_DropsInvalidIntegerProperties(string rawValue)
    {
        var productEvent = new AppSurfaceProductEvent(
            AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>
            {
                ["rejection_reason"] = "TooManyLiveSubscriptions",
                ["limit_name"] = "max_live_subscriptions",
                ["current_count"] = rawValue
            });

        var result = AppSurfaceProductEventRegistry.Validate(productEvent);

        Assert.True(result.IsValid);
        Assert.Contains("current_count", result.RejectedProperties);
        Assert.DoesNotContain(rawValue, result.SanitizedProperties.Values);
    }

    private static Dictionary<string, string> CreateValidProperties(string eventName)
    {
        return eventName switch
        {
            AppSurfaceProductEventRegistry.DocsSearchSubmitted => new Dictionary<string, string>
            {
                ["surface"] = "search_page",
                ["result_count"] = "1"
            },
            AppSurfaceProductEventRegistry.DocsSearchResultSelected => new Dictionary<string, string>
            {
                ["surface"] = "search_page",
                ["result_kind"] = "guide"
            },
            AppSurfaceProductEventRegistry.RazorWireFormFailed => new Dictionary<string, string>
            {
                ["failure_mode"] = "handled",
                ["response_kind"] = "html",
                ["failure_ui"] = "generated"
            },
            AppSurfaceProductEventRegistry.RazorWireFormFailureRecovered => new Dictionary<string, string>
            {
                ["recovery_action"] = "retry_submit"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, "Unexpected event name.")
        };
    }

    private static ServiceProvider CreateProvider(Action<AppSurfaceProductIntelligenceOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceProductIntelligence(configure);
        return services.BuildServiceProvider();
    }

    private static AppSurfaceProductEventContract CreateSkoolieContract(
        string owner = "Skoolie",
        AppSurfaceProductEventLifecycle lifecycle = AppSurfaceProductEventLifecycle.Stable)
    {
        return new AppSurfaceProductEventContract(
            "skoolie.card.generated",
            lifecycle,
            "Measure whether launch-card generation moves safely into send review.",
            owner,
            "Short launch-quality retention; aggregate before long-term storage.",
            [
                new AppSurfaceProductEventPropertyContract(
                    "launch_surface",
                    "Host-owned surface that generated the card.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low,
                    required: true,
                    valueShape: AppSurfaceProductEventValueShape.Token),
                new AppSurfaceProductEventPropertyContract(
                    "attachment_count",
                    "Number of safe launch artifacts attached to the generated card.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Medium,
                    valueShape: AppSurfaceProductEventValueShape.NonNegativeInteger),
                new AppSurfaceProductEventPropertyContract(
                    "delivery_state",
                    "Normalized downstream state.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Low,
                    required: true,
                    allowedValues: ["queued", "sent"],
                    valueShape: AppSurfaceProductEventValueShape.AllowedValue),
                new AppSurfaceProductEventPropertyContract(
                    "debug_note",
                    "Bounded operator note for diagnostics.",
                    AppSurfaceProductEventSensitivity.Operational,
                    AppSurfaceProductEventCardinality.Medium,
                    maxLength: 24,
                    valueShape: AppSurfaceProductEventValueShape.BoundedText)
            ],
            ["child identity", "email body", "raw attachment"]);
    }

    private static AppSurfaceProductEventContract CreateReorderableContract(bool reordered = false)
    {
        AppSurfaceProductEventPropertyContract[] properties =
        [
            new(
                "delivery_state",
                "Normalized downstream state.",
                AppSurfaceProductEventSensitivity.Operational,
                AppSurfaceProductEventCardinality.Low,
                required: true,
                allowedValues: reordered ? ["sent", "queued"] : ["queued", "sent"],
                valueShape: AppSurfaceProductEventValueShape.AllowedValue),
            new(
                "launch_surface",
                "Host-owned surface that generated the card.",
                AppSurfaceProductEventSensitivity.Operational,
                AppSurfaceProductEventCardinality.Low,
                required: true,
                valueShape: AppSurfaceProductEventValueShape.Token)
        ];

        return new AppSurfaceProductEventContract(
            "host.reordered_duplicate",
            AppSurfaceProductEventLifecycle.Stable,
            "Measure host-owned launch-card generation.",
            "Host",
            "Discard during tests.",
            reordered ? properties.Reverse() : properties,
            reordered ? ["raw attachment", "child identity"] : ["child identity", "raw attachment"]);
    }
}
