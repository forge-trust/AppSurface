using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.Tests;

/// <summary>Verifies malformed boundaries and custom environment providers without diagnostic capabilities.</summary>
public sealed class ConfigCompositionBoundaryTests
{
    [Theory]
    [InlineData("PRODUCTION_SERVICE_COUNT", "SERVICE_COUNT")]
    [InlineData("PRODUCTION__SERVICE__COUNT", "SERVICE__COUNT")]
    public void BasicEnvironmentProvider_UsesNextParseableOrdinaryCandidate(string invalid, string valid)
    {
        var engine = CreateEngine(new()
        {
            [invalid] = "invalid-number-sentinel",
            [valid] = "42",
            ["SERVICE__TOKEN"] = "secret-sentinel"
        });

        var result = engine.Execute("Production", "Service", typeof(ScalarOptions<int>));

        var value = Assert.IsType<ScalarOptions<int>>(result.Value);
        Assert.Equal(42, value.Count);
        Assert.Equal("secret-sentinel", value.Token.Value);
        Assert.Contains(result.Sources, source => source.EnvironmentVariableName == valid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("config-environment-conversion-failed", diagnostic.Code);
        Assert.Equal(invalid, diagnostic.Source!.EnvironmentVariableName);
        Assert.DoesNotContain("invalid-number-sentinel", JsonSerializer.Serialize(result.Diagnostics));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("{\"Count\":")]
    public void BasicEnvironmentProvider_InvalidSecretParentFallsBackWithoutSupplyingSecret(string invalid)
    {
        var engine = CreateEngine(new()
        {
            ["PRODUCTION_SERVICE_CHILD"] = invalid,
            ["SERVICE_CHILD"] = "{\"Count\":7,\"Token\":\"parent-secret-sentinel\"}",
            ["SERVICE__CHILD__TOKEN"] = "exact-secret-sentinel"
        });

        var result = engine.Execute("Production", "Service", typeof(NestedOptions));

        var value = Assert.IsType<NestedOptions>(result.Value);
        Assert.Equal(7, value.Child.Count);
        Assert.Equal("exact-secret-sentinel", value.Child.Token.Value);
        Assert.Equal("config-environment-conversion-failed", Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("parent-secret-sentinel", JsonSerializer.Serialize(result.Diagnostics));
    }

    [Theory]
    [InlineData("Service..Token")]
    [InlineData("Service: :Token")]
    public void InvalidRootPath_HasNoSecretInventoryAndFailsBeforeProviderReads(string key)
    {
        var environment = new BasicEnvironmentProvider([]);
        var engine = new ConfigCompositionEngine(environment, [], [], [], new(), TimeProvider.System);

        Assert.Empty(engine.GetSecretPaths(key, typeof(ScalarOptions<int>)));
        var result = engine.Execute("Production", key, typeof(ScalarOptions<int>));

        Assert.Equal(ConfigCompositionRootState.Failed, result.State);
        Assert.Null(result.Value);
        Assert.NotEmpty(result.Failures);
        Assert.Equal(0, environment.Reads);
    }

    [Fact]
    public void Engine_RejectsInvalidLimitsBeforeReadingEnvironment()
    {
        var environment = new BasicEnvironmentProvider([]);

        Assert.Throws<OptionsValidationException>(() => new ConfigCompositionEngine(
            environment, [], [], [], new() { MaxCompositionGraphDepth = 0 }, TimeProvider.System));
        Assert.Equal(0, environment.Reads);
    }

    [Fact]
    public void DirectRoot_DateSecretUsesJsonStringConversion()
    {
        var engine = CreateEngine(new()
        {
            ["SERVICE"] = "{\"Token\":\"2030-01-02T03:04:05+00:00\"}"
        });

        var result = engine.Execute("Production", "Service", typeof(DateOptions));

        Assert.True(result.DirectRoot);
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Assert.IsType<DateOptions>(result.Value).Token.Value);
    }

    [Fact]
    public void DirectRoot_CustomSetterFailureUsesNextCandidateWithoutExposingException()
    {
        var engine = CreateEngine(new()
        {
            ["PRODUCTION_SERVICE"] = "{\"Token\":\"secret-sentinel\",\"Count\":-1}",
            ["SERVICE"] = "{\"Token\":\"accepted-secret\",\"Count\":42}"
        });

        var result = engine.Execute("Production", "Service", typeof(ThrowingSetterOptions));

        Assert.True(result.DirectRoot);
        Assert.Equal(42, Assert.IsType<ThrowingSetterOptions>(result.Value).Count);
        Assert.Equal("config-environment-conversion-failed", Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("secret-sentinel", JsonSerializer.Serialize(result.Diagnostics));
    }

    [Fact]
    public void FinalBinding_CustomSetterFailureReturnsValueSafeFailure()
    {
        var engine = CreateEngine(new()
        {
            ["SERVICE_COUNT"] = "-1",
            ["SERVICE_TOKEN"] = "secret-sentinel"
        });

        var result = engine.Execute("Production", "Service", typeof(ThrowingSetterOptions));

        Assert.Equal(ConfigCompositionRootState.Failed, result.State);
        Assert.Null(result.Value);
        Assert.Equal("config-composition-bind-failed", Assert.Single(result.Failures).Code);
        Assert.DoesNotContain("secret-sentinel", JsonSerializer.Serialize(result.Failures));
    }

    [Fact]
    public void RawProvider_CustomFailureStopsFallbackWithoutExposingException()
    {
        var fallback = new RawProvider(false);
        var engine = new ConfigCompositionEngine(new BasicEnvironmentProvider([]),
            [new RawProvider(true), fallback], [], [], new(), TimeProvider.System);

        var result = engine.Execute("Production", "Service", typeof(ScalarOptions<int>));

        Assert.Equal(ConfigCompositionRootState.Failed, result.State);
        Assert.Null(result.Value);
        Assert.Equal("config-composition-base-failed", Assert.Single(result.Failures).Code);
        Assert.DoesNotContain("secret-sentinel", JsonSerializer.Serialize(result.Failures));
        Assert.Equal(0, fallback.Reads);
    }

    [Theory]
    [InlineData("\"secret-sentinel\"")]
    [InlineData("2147483648")]
    [InlineData("-1")]
    [InlineData("1")]
    public void Binding_RejectsForgedOpaqueSlots(string slot)
    {
        var contract = new ConfigCompositionJsonContract(new());
        var payload = JsonNode.Parse("{\"Token\":" + slot + "}")!.AsObject();
        IConfigSecretValue[] slots = [new Secret<string>(true, true, "secret-sentinel", "provider")];

        var error = Assert.Throws<JsonException>(() => contract.Bind(typeof(ScalarOptions<int>), payload, slots));

        Assert.DoesNotContain("secret-sentinel", error.ToString());
        var valid = new JsonObject { ["Token"] = 0 };
        Assert.Equal("secret-sentinel", Assert.IsType<ScalarOptions<int>>(contract.Bind(
            typeof(ScalarOptions<int>), valid, slots)).Token.Value);
    }

    [Fact]
    public void Binding_RejectsSlotWithWrongWrapperType()
    {
        var contract = new ConfigCompositionJsonContract(new());

        Assert.Throws<JsonException>(() => contract.Bind(typeof(ScalarOptions<int>),
            new JsonObject { ["Token"] = 0 }, [new Secret<int>(true, true, 42, "provider")]));
    }

    [Fact]
    public void Shape_InvalidJsonMetadataBecomesStructuralFailure()
    {
        var contract = new ConfigCompositionJsonContract(new());

        var shape = contract.GetShape(typeof(DuplicateNames));

        Assert.Contains(shape.Failures, failure => failure.Code == "secret-destination-type-unsupported");
        Assert.Same(shape, contract.GetShape(typeof(DuplicateNames)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Token:Child")]
    public void LogicalPath_RejectsInvalidMemberSegments(string member)
    {
        var root = ConfigLogicalPath.Parse("Service");

        Assert.Throws<ArgumentException>(() => root.Append(member));
        Assert.Equal("Service", root.ToString());
    }

    [Fact]
    public void LogicalPath_AppendsDottedMemberAsOneLiteralSegment()
    {
        var path = ConfigLogicalPath.Parse("Service").Append("Token.Child");

        Assert.Equal(new[] { "Service", "Token.Child" }, path.Segments);
        Assert.False(path.Equals(ConfigLogicalPath.Parse("Service:Token:Child")));
    }

    [Fact]
    public void LogicalPath_ObjectEqualityUsesCaseInsensitiveCompleteSegments()
    {
        var path = ConfigLogicalPath.Parse("Service.Token");

        Assert.True(path.Equals((object)ConfigLogicalPath.Parse("service:TOKEN")));
        Assert.False(path.Equals((object)ConfigLogicalPath.Parse("Service.TokenExtra")));
        Assert.False(path.Equals((object?)null));
        Assert.False(path.Equals("Service.Token"));
        Assert.Equal(ConfigLogicalPath.Parse("service:TOKEN").GetHashCode(), path.GetHashCode());
        Assert.Equal("Service:Token", path.ToString());
    }

    private static ConfigCompositionEngine CreateEngine(Dictionary<string, string> values) =>
        new(new BasicEnvironmentProvider(values), [], [], [], new(), TimeProvider.System);

    private sealed class BasicEnvironmentProvider(Dictionary<string, string> values) : IEnvironmentConfigProvider
    {
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public int Priority => int.MaxValue;
        public string Name => "environment";
        public int Reads { get; private set; }
        public string? GetEnvironmentVariable(string name, string? defaultValue = null)
        {
            Reads++;
            return values.TryGetValue(name, out var value) ? value : defaultValue;
        }
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() =>
            new Dictionary<string, string>(values, StringComparer.Ordinal);
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) => throw new InvalidOperationException(
            "Composition must read raw candidates without invoking typed binding.");
        public T? GetValue<T>(string environment, string key) => throw new InvalidOperationException(
            "Composition must read raw candidates without invoking legacy typed binding.");
    }

    private sealed class ScalarOptions<T>
    {
        public Secret<string> Token { get; set; } = new();
        public T? Count { get; set; }
    }

    private sealed class NestedOptions
    {
        public ScalarOptions<int> Child { get; set; } = new();
    }

    private sealed class DateOptions
    {
        public Secret<DateTimeOffset> Token { get; set; } = new();
    }

    private sealed class ThrowingSetterOptions
    {
        private int _count;
        public Secret<string> Token { get; set; } = new();
        public int Count
        {
            get => _count;
            set => _count = value < 0 ? throw new SensitiveCallbackException() : value;
        }
    }

    private sealed class SensitiveCallbackException() : Exception("secret-sentinel");

    private sealed class RawProvider(bool throws) : IConfigProvider, IConfigCompositionValueProvider
    {
        public int Priority => throws ? 10 : 1;
        public string Name => "raw-provider";
        public int Reads { get; private set; }
        public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request) => throw new InvalidOperationException();
        public T? GetValue<T>(string environment, string key) => throw new InvalidOperationException();
        public ConfigCompositionValueResolution ResolveRaw(string environment, string logicalKey)
        {
            Reads++;
            return throws ? throw new SensitiveCallbackException()
                : ConfigCompositionValueResolution.Resolved("{}", Name, Priority, true);
        }
    }

    private sealed class DuplicateNames
    {
        public Secret<string> Token { get; set; } = new();
        [JsonPropertyName("Token")]
        public string Other { get; set; } = "";
    }
}
