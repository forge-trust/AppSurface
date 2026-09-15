using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class SecretContractTests
{
    [Fact]
    public void DefaultSecret_IsEnabledButEmptyAndValueAccessFails()
    {
        var secret = new Secret<string>();

        Assert.True(secret.Enabled);
        Assert.False(secret.HasValue);
        Assert.Null(secret.ResolvedProvider);
        Assert.False(secret.TryGetValue(out string? value));
        Assert.Null(value);
        Assert.Throws<InvalidOperationException>(() => secret.Value);
        Assert.Equal("Secret { Enabled = True, HasValue = False, ResolvedProvider = none }", secret.ToString());
    }

    [Fact]
    public void SecretStates_PreserveDisabledResolvedAndEmptyStringPresence()
    {
        var disabled = new Secret<string>(enabled: false, hasValue: false, value: null, provider: null);
        var resolved = new Secret<string>(enabled: true, hasValue: true, value: "payload", provider: "google-secret-manager");
        var empty = new Secret<string>(enabled: true, hasValue: true, value: string.Empty, provider: "environment");

        Assert.False(disabled.Enabled);
        Assert.False(disabled.HasValue);
        Assert.Null(disabled.ResolvedProvider);

        Assert.True(resolved.Enabled);
        Assert.True(resolved.HasValue);
        Assert.Equal("payload", resolved.Value);
        Assert.Equal("payload", GetValue(resolved));
        Assert.Equal("google-secret-manager", resolved.ResolvedProvider);

        Assert.True(empty.HasValue);
        Assert.True(empty.TryGetValue(out var emptyValue));
        Assert.Equal(string.Empty, emptyValue);
        Assert.Equal("environment", empty.ResolvedProvider);
    }

    [Fact]
    public void SecretInternalConstructor_RejectsNullWhenValueIsPresent()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Secret<string>(enabled: true, hasValue: true, value: null, provider: "provider"));
    }

    [Fact]
    public void SecretFormattingAndJson_OmitThePayloadButKeepOpaqueState()
    {
        var secret = new Secret<string>(enabled: true, hasValue: true, value: "super-secret-payload", provider: "custom-provider");

        var text = secret.ToString();
        var json = JsonSerializer.Serialize(secret);

        Assert.Contains("HasValue = True", text, StringComparison.Ordinal);
        Assert.Contains("ResolvedProvider = custom-provider", text, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-payload", text, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-payload", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty(nameof(Secret<string>.Value), out _));
        Assert.Contains(@"""Enabled"":true", json, StringComparison.Ordinal);
        Assert.Contains(@"""HasValue"":true", json, StringComparison.Ordinal);
        Assert.Contains(@"""ResolvedProvider"":""custom-provider""", json, StringComparison.Ordinal);
    }

    private static string? GetValue(Secret<string> secret)
    {
        return secret.TryGetValue(out var value) ? value : null;
    }
}
