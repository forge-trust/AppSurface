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
    [InlineData("-1")]
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
}
