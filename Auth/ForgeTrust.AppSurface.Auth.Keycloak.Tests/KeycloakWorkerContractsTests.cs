using System.Collections.Immutable;
using System.Text.Json;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

public sealed class KeycloakWorkerContractsTests
{
    [Fact]
    public void BindingsPreserveWorkerJsonMembersAndNullableFields()
    {
        var bindings = new KeycloakWorkerBindings(
            "https://localhost:8443/", "admin", "password", null, null, null, null);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(bindings));
        var json = document.RootElement;
        Assert.Equal("https://localhost:8443/", json.GetProperty("BaseAddress").GetString());
        Assert.Equal("admin", json.GetProperty("AdminUsername").GetString());
        Assert.Equal("password", json.GetProperty("AdminPassword").GetString());
        Assert.True(json.GetProperty("ClientSecret").ValueKind is JsonValueKind.Null);
        Assert.True(json.GetProperty("CaptureRoot").ValueKind is JsonValueKind.Null);
        Assert.True(json.GetProperty("CaptureRunId").ValueKind is JsonValueKind.Null);
        Assert.True(json.GetProperty("BrokerSecrets").ValueKind is JsonValueKind.Null);
        Assert.False(json.TryGetProperty("ClientCredentialReference", out _));
        Assert.True(json.GetProperty("ClientCredentialReferenceFile").ValueKind is JsonValueKind.Null);
        Assert.True(json.GetProperty("ExpectedCaptureRunId").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public void BindingsToStringReturnsFixedSafeValueForSentinelValues()
    {
        var bindings = new KeycloakWorkerBindings(
            "sentinel-base-address",
            "sentinel-admin-username",
            "sentinel-admin-password",
            "sentinel-client-secret",
            "sentinel-capture-root",
            "sentinel-capture-run-id",
            ImmutableDictionary<string, string>.Empty.Add("sentinel-broker-name", "sentinel-broker-secret"))
        {
            ClientCredentialReference = new KeycloakClientCredentialReference(
                TestPathUtils.PathUnder(Path.GetTempPath(), "sentinel-run-directory"),
                "11111111111111111111111111111111",
                new Uri("https://sentinel-authority.example/realms/sentinel"),
                "sentinel-reference-client-id"),
            ClientCredentialReferenceFile = "sentinel-client-credential-reference-file",
            ExpectedCaptureRunId = "sentinel-expected-capture-run-id"
        };

        Assert.Equal(nameof(KeycloakWorkerBindings), bindings.ToString());
    }

    [Fact]
    public void BindingsReadLegacyCamelCaseAndKeepOptionalReferenceFieldsAbsent()
    {
        const string json = """
            {"baseAddress":"https://localhost:8443/","adminUsername":"admin","adminPassword":"password","clientSecret":null,"captureRoot":null,"captureRunId":null,"brokerSecrets":{"google":"provider-secret"}}
            """;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var bindings = JsonSerializer.Deserialize<KeycloakWorkerBindings>(json, options)!;

        Assert.Equal("https://localhost:8443/", bindings.BaseAddress);
        Assert.Equal("provider-secret", bindings.BrokerSecrets!["google"]);
        Assert.Null(bindings.ClientCredentialReference);
        Assert.Null(bindings.ClientCredentialReferenceFile);
        Assert.Null(bindings.ExpectedCaptureRunId);
    }

    [Fact]
    public void VolumeOwnershipRoundTripsAbsentAndPresentDatabaseOwners()
    {
        var incarnation = new KeycloakVolumeIncarnation(8, 1, 42, 1_700_000_000, 5);
        var absent = new KeycloakWorkerVolumeOwnership(incarnation, 1000, 0, null);
        var absentJson = JsonSerializer.Serialize(absent);
        using (var document = JsonDocument.Parse(absentJson))
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("Database").ValueKind);
        Assert.Equal(absent, JsonSerializer.Deserialize<KeycloakWorkerVolumeOwnership>(absentJson));

        var present = new KeycloakWorkerVolumeOwnership(incarnation, 1000, 0,
            new KeycloakWorkerDatabaseOwnership(501, 20));
        var presentJson = JsonSerializer.Serialize(present);
        var roundTrip = JsonSerializer.Deserialize<KeycloakWorkerVolumeOwnership>(presentJson)!;
        Assert.Equal(incarnation, roundTrip.Incarnation);
        Assert.Equal((uint)1000, roundTrip.Uid);
        Assert.Equal((uint)0, roundTrip.Gid);
        Assert.Equal(new KeycloakWorkerDatabaseOwnership(501, 20), roundTrip.Database);
    }

    [Theory]
    [InlineData("{\"Uid\":1000,\"Gid\":0}")]
    [InlineData("{\"Database\":{\"Uid\":501}}")]
    [InlineData("{\"Database\":{\"Gid\":20}}")]
    public void VolumeOwnershipRequiresDatabaseShapeAndBothDatabaseIds(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<KeycloakWorkerVolumeOwnership>(json));
    }
}
