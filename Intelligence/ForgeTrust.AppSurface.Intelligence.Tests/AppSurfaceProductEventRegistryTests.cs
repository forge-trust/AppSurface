using System.Text.Json;
using ForgeTrust.AppSurface.Intelligence;

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
}
