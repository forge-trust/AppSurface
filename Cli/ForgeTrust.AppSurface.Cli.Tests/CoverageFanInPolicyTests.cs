using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInPolicyTests
{
    [Fact]
    public void DirectPolicyConverterRejectsNullRatherThanManufacturingDefaultIdentity()
    {
        var bytes = Encoding.UTF8.GetBytes("null");
        var reader = new Utf8JsonReader(bytes);
        Assert.True(reader.Read());
        var refused = false;
        try
        {
            _ = new CoverageFanInPolicyJsonConverter().Read(ref reader, typeof(CoverageFanInPolicy), new(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            refused = true;
        }
        Assert.True(refused);
    }

    private const string CanonicalGolden = """{"gate":{"diffBase":null,"minBranch":85,"minLine":95,"minPatchBranch":85,"minPatchLine":95,"mode":"aggregateOnly","patchLineMode":"codecov","tolerance":0.5},"instrumentation":{"driver":"collector","exclude":"[*.Tests]*,[*.IntegrationTests]*","format":"cobertura","include":null,"normalization":"appsurface-coverage-merge"},"limits":{"attributesPerElement":32,"branchRecordsPerReport":2000000,"depth":64,"identitySeconds":30,"inputBundles":32,"jsonBytes":1048576,"lineRecordsPerReport":1000000,"operationSeconds":300,"tokenCharacters":65536,"totalBytes":4294967296,"totalRecords":2000000,"xmlBytes":536870912,"xmlElementsPerReport":8000000},"run":{"attempt":1,"producerSystem":"local","runId":"run-1","runScope":"dogfood"},"source":{"commit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","configuration":"Release","sdk":"10.0.102","tree":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},"supplements":[],"testProjects":["tests/First.csproj","tests/Second.csproj"]}""";

    internal static CoverageFanInPolicy Policy() => new(
        new(new string('a', 40), new string('b', 40), "10.0.102", "Release"),
        new("local", "dogfood", "run-1", 1),
        ["tests/Second.csproj", "tests/First.csproj"], [], new(CoverageGateMode.AggregateOnly));

    private static CoverageFanInPolicy V3Policy(int objectIdLength = 40)
    {
        var policy = Policy();
        return policy with
        {
            ContractVersion = 3,
            ReportProfileId = "coverlet-collector-cobertura-v1",
            Source = new(new string('a', objectIdLength), new string('b', objectIdLength), "10.0.102", "Release"),
            Gate = policy.Gate with
            {
                Mode = CoverageGateMode.AggregateAndPatch,
                DiffBase = new string('c', objectIdLength)
            }
        };
    }

    [Fact]
    public void CanonicalPolicyMatchesTheCompleteVersionedGoldenVector()
    {
        var bytes = CoveragePolicyEncoding.CanonicalBytes(Policy());
        Assert.Equal(CanonicalGolden, Encoding.UTF8.GetString(bytes));
        Assert.Equal("b0660c1b9a827c191d2363002f1c770d4d032e3205885a8e50ff9f88fe3bdc53", CoveragePolicyEncoding.Digest(Policy()));
        Assert.DoesNotContain("95.0", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(" ", Encoding.UTF8.GetString(bytes));
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void PolicyDefaultsToV2AndItsCanonicalProjectionOmitsNewVersionProperties()
    {
        var policy = Policy();
        var element = CoveragePolicyEncoding.ToElement(policy);

        Assert.Equal(2, policy.ContractVersion);
        Assert.Null(policy.ReportProfileId);
        Assert.False(element.TryGetProperty("contractVersion", out _));
        Assert.False(element.TryGetProperty("reportProfileId", out _));
        Assert.Equal(CanonicalGolden, Encoding.UTF8.GetString(CoveragePolicyEncoding.CanonicalBytes(policy)));
        Assert.Equal("b0660c1b9a827c191d2363002f1c770d4d032e3205885a8e50ff9f88fe3bdc53",
            CoveragePolicyEncoding.Digest(policy));
    }

    [Fact]
    public void V3PolicyProjectionIncludesItsClosedProfileAndRoundTripsCanonically()
    {
        var policy = V3Policy();
        var element = CoveragePolicyEncoding.ToElement(policy);
        var canonical = Encoding.UTF8.GetString(CoveragePolicyEncoding.CanonicalBytes(policy));
        var decoded = CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(
            JsonSerializer.SerializeToUtf8Bytes(policy, CoveragePolicyEncoding.Json));

        Assert.Equal(3, element.GetProperty("contractVersion").GetInt32());
        Assert.Equal("coverlet-collector-cobertura-v1", element.GetProperty("reportProfileId").GetString());
        Assert.StartsWith("{\"contractVersion\":3,", canonical, StringComparison.Ordinal);
        Assert.NotEqual(CoveragePolicyEncoding.Digest(Policy()), CoveragePolicyEncoding.Digest(policy));
        Assert.Equal(CoveragePolicyEncoding.CanonicalBytes(policy), CoveragePolicyEncoding.CanonicalBytes(decoded));
    }

    [Fact]
    public void V3DiffPolicySelectorIsOptionalDigestBoundAndDefaultsToStrictAtTheEngineBoundary()
    {
        var strictPolicy = V3Policy();
        var strictElement = CoveragePolicyEncoding.ToElement(strictPolicy);
        var strictExpected = new CoverageFanInExpected(strictElement, CoveragePolicyEncoding.Digest(strictPolicy));

        Assert.False(strictElement.TryGetProperty("diffPolicyId", out _));
        Assert.Equal("appsurface-git-patch-v1", strictExpected.DiffPolicyId);

        var visualPolicy = strictPolicy with { DiffPolicyId = CoverageFanInPolicy.VisualBaselinePngDiffPolicyId };
        var visualElement = CoveragePolicyEncoding.ToElement(visualPolicy);
        var visualExpected = new CoverageFanInExpected(visualElement, CoveragePolicyEncoding.Digest(visualPolicy));
        var decoded = CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(
            JsonSerializer.SerializeToUtf8Bytes(visualPolicy, CoveragePolicyEncoding.Json));

        Assert.Equal(CoverageFanInPolicy.VisualBaselinePngDiffPolicyId,
            visualElement.GetProperty("diffPolicyId").GetString());
        Assert.Equal(CoverageFanInPolicy.VisualBaselinePngDiffPolicyId, visualExpected.DiffPolicyId);
        Assert.NotEqual(strictExpected.Digest, visualExpected.Digest);
        Assert.Equal(CoveragePolicyEncoding.Digest(visualPolicy), CoveragePolicyEncoding.Digest(decoded));
        Assert.Equal(CoverageFanInPolicy.VisualBaselinePngDiffPolicyId, decoded.DiffPolicyId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("future-policy")]
    public void V3RejectsUnrecognizedDiffPolicySelectors(string selector)
    {
        var policy = V3Policy() with { DiffPolicyId = selector };
        Assert.Throws<ArgumentException>("diffPolicyId", () => CoveragePolicyEncoding.Freeze(policy));
    }

    [Fact]
    public void V2PolicyCannotSelectTheV3DiffPolicy()
        => Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        { DiffPolicyId = CoverageFanInPolicy.VisualBaselinePngDiffPolicyId }));

    [Fact]
    public void GenericJsonKeepsV2PolicyClosedAndRoundTripsV3Selectors()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        var capture = new CoverageCaptureRequest(Policy(), ".", "output", "primary");
        var captureJson = JsonSerializer.Serialize(capture, options);
        using var captureDocument = JsonDocument.Parse(captureJson);
        var v2PolicyJson = captureDocument.RootElement.GetProperty("policy");

        Assert.False(v2PolicyJson.TryGetProperty("contractVersion", out _));
        Assert.False(v2PolicyJson.TryGetProperty("reportProfileId", out _));
        Assert.False(v2PolicyJson.TryGetProperty("diffPolicyId", out _));
        var decodedCapture = CoveragePolicyEncoding.Decode<CoverageCaptureRequest>(Encoding.UTF8.GetBytes(captureJson));
        Assert.Equal(2, decodedCapture.Policy.ContractVersion);
        Assert.Null(decodedCapture.Policy.ReportProfileId);
        Assert.Equal(CanonicalGolden, Encoding.UTF8.GetString(
            CoveragePolicyEncoding.CanonicalBytes(decodedCapture.Policy)));

        var v3DefaultJson = JsonSerializer.Serialize(V3Policy(), options);
        using var v3DefaultDocument = JsonDocument.Parse(v3DefaultJson);
        Assert.False(v3DefaultDocument.RootElement.TryGetProperty("diffPolicyId", out _));

        var v3Policy = V3Policy() with { DiffPolicyId = CoverageFanInPolicy.VisualBaselinePngDiffPolicyId };
        var v3Json = JsonSerializer.Serialize(v3Policy, options);
        using var v3Document = JsonDocument.Parse(v3Json);
        Assert.Equal(3, v3Document.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.Equal("coverlet-collector-cobertura-v1",
            v3Document.RootElement.GetProperty("reportProfileId").GetString());
        Assert.Equal(CoverageFanInPolicy.VisualBaselinePngDiffPolicyId,
            v3Document.RootElement.GetProperty("diffPolicyId").GetString());

        var v3RoundTrip = JsonSerializer.Deserialize<CoverageFanInPolicy>(v3Json, options);
        Assert.NotNull(v3RoundTrip);
        Assert.Equal(3, v3RoundTrip.ContractVersion);
        Assert.Equal(v3Policy.ReportProfileId, v3RoundTrip.ReportProfileId);
        Assert.Equal(v3Policy.DiffPolicyId, v3RoundTrip.DiffPolicyId);
        Assert.Equal(CoveragePolicyEncoding.CanonicalBytes(v3Policy),
            CoveragePolicyEncoding.CanonicalBytes(v3RoundTrip));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(-1)]
    public void UnsupportedContractVersionsAreRejectedDuringPolicyFreeze(int version)
        => Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        { ContractVersion = version }));

    [Fact]
    public void PolicyFreezeRequiresTheExactVersionSpecificProfile()
    {
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        { ReportProfileId = "coverlet-collector-cobertura-v1" }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(V3Policy() with
        { ReportProfileId = null }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(V3Policy() with
        { ReportProfileId = "coverlet-cobertura-v1" }));
    }

    [Fact]
    public void V3PatchBaseMustBeAFullLowercaseIdMatchingTheSourceObjectFormat()
    {
        Assert.Equal(new string('c', 40), CoveragePolicyEncoding.Freeze(V3Policy(40)).Gate.DiffBase);
        Assert.Equal(new string('c', 64), CoveragePolicyEncoding.Freeze(V3Policy(64)).Gate.DiffBase);

        foreach (var invalid in new[]
        {
            V3Policy() with { Gate = V3Policy().Gate with { DiffBase = "main" } },
            V3Policy() with { Gate = V3Policy().Gate with { DiffBase = new string('c', 64) } },
            V3Policy() with { Gate = V3Policy().Gate with { DiffBase = new string('C', 40) } },
            V3Policy() with { Source = V3Policy().Source with { Tree = new string('b', 64) } }
        })
            Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(invalid));
    }

    [Fact]
    public void PatchMetricWireShapeRequiresEveryFieldAndUsesClosedOutcomeStrings()
    {
        var metric = new CoveragePatchMetricResult(CoveragePatchApplicabilityOutcome.NotApplicable,
            "no-reported-patch-points", 1, 0, 0, null);
        const string expected = """{"outcome":"notApplicable","reason":"no-reported-patch-points","changedLines":1,"measurable":0,"covered":0,"percent":null}""";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metric, CoveragePolicyEncoding.Json);
        var decoded = CoveragePolicyEncoding.Decode<CoveragePatchMetricResult>(bytes);

        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.Equal(metric, decoded);
        foreach (var (outcome, wire) in new[]
        {
            (CoveragePatchApplicabilityOutcome.Measured, "measured"),
            (CoveragePatchApplicabilityOutcome.NotApplicable, "notApplicable"),
            (CoveragePatchApplicabilityOutcome.Unverified, "unverified")
        })
        {
            var serialized = JsonSerializer.SerializeToElement(new CoveragePatchMetricResult(
                outcome, "reason", null, null, null, null), CoveragePolicyEncoding.Json);
            Assert.Equal(wire, serialized.GetProperty("outcome").GetString());
        }

        Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoveragePatchMetricResult>(
            Encoding.UTF8.GetBytes("""{"outcome":"unverified","reason":"invalid-evidence","changedLines":null,"measurable":null,"covered":null}""")));
        Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoveragePatchMetricSet>(
            Encoding.UTF8.GetBytes("""{"line":{"outcome":"unverified","reason":"invalid-evidence","changedLines":null,"measurable":null,"covered":null,"percent":null}}""")));
        Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoveragePatchApplicability>(
            Encoding.UTF8.GetBytes("""{"primary":{"line":{"outcome":"notApplicable","reason":"no-reported-patch-points","changedLines":1,"measurable":0,"covered":0,"percent":null},"branch":{"outcome":"notApplicable","reason":"no-reported-patch-points","changedLines":1,"measurable":0,"covered":0,"percent":null}}}""")));
    }

    [Fact]
    public void CanonicalizationSortsSetArraysFreezesDefaultsAndCopiesCallerCollections()
    {
        var inputProjects = new List<string> { "tests/Second.csproj", "tests/First.csproj" };
        var supplements = new List<CoverageSupplement>
        {
            new("zeta", "windows", "Trait=slow", ["checks/Zeta.csproj", "checks/Alpha.csproj"]),
            new("alpha", "linux", "Category=fast", ["checks/Contract.vbproj", "checks/Smoke.csproj"])
        };
        var policy = Policy() with { TestProjects = inputProjects, Supplements = supplements };
        var baseline = CoveragePolicyEncoding.CanonicalBytes(policy);
        var reordered = policy with
        {
            TestProjects = inputProjects.AsEnumerable().Reverse().ToArray(),
            Supplements = supplements.AsEnumerable().Reverse().Select(x => x with
            {
                TestProjects = x.TestProjects?.Reverse().ToArray()
            }).ToArray(),
            Instrumentation = new(),
            Limits = new(),
            Gate = policy.Gate with { MinLine = 95.000m }
        };

        Assert.Equal(baseline, CoveragePolicyEncoding.CanonicalBytes(reordered));
        Assert.Equal(CoveragePolicyEncoding.Digest(policy), CoveragePolicyEncoding.Digest(reordered));

        var frozen = CoveragePolicyEncoding.Freeze(policy);
        inputProjects.Clear();
        supplements.Clear();
        Assert.Equal(["tests/First.csproj", "tests/Second.csproj"], frozen.TestProjects);
        Assert.Equal(["alpha", "zeta"], frozen.Supplements.Select(x => x.Id));
        Assert.Equal(["checks/Alpha.csproj", "checks/Zeta.csproj"], frozen.Supplements[1].TestProjects);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)frozen.TestProjects)[0] = "changed.csproj");
    }

    [Fact]
    public void EquivalentJsonLexicalPropertySetAndDefaultFormsHaveOneCanonicalVector()
    {
        var omittedDefaults = PolicyJson(
            gateJson: "{\"mode\":\"aggregateOnly\"}",
            runScopeJson: "\"dogfood\"",
            projectArrayJson: "[\"tests/Second.csproj\",\"tests/First.csproj\"]");
        var explicitDefaults = """
            {
              "testProjects": ["tests/First.csproj", "tests/Second.csproj"],
              "gate": {"tolerance": 5e-1, "patchLineMode": "codecov", "minPatchLine": 95.0000, "minPatchBranch": 8.500e1, "minLine": 9.5e1, "mode": "aggregateOnly", "minBranch": 85.0, "diffBase": null},
              "run": {"attempt": 1.0, "runScope": "dogfood", "runId": "run-1", "producerSystem": "local"},
              "supplements": [],
              "source": {"configuration": "Release", "sdk": "10.0.102", "tree": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "commit": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},
              "instrumentation": null,
              "limits": null
            }
            """;

        var first = CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(omittedDefaults));
        var second = CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(explicitDefaults));
        Assert.Equal(CanonicalGolden, Encoding.UTF8.GetString(CoveragePolicyEncoding.CanonicalBytes(first)));
        Assert.Equal(CoveragePolicyEncoding.CanonicalBytes(first), CoveragePolicyEncoding.CanonicalBytes(second));
        Assert.Equal(CoveragePolicyEncoding.Digest(first), CoveragePolicyEncoding.Digest(second));
    }

    [Fact]
    public void CanonicalizationUsesOrdinalOrderAndDoesNotNfcFoldUnicode()
    {
        var escaped = CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(PolicyJson(
            gateJson: "{\"mode\":\"aggregateOnly\"}", runScopeJson: "\"caf\\u00e9\"")));
        var literal = CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(PolicyJson(
            gateJson: "{\"mode\":\"aggregateOnly\"}", runScopeJson: "\"café\"")));
        Assert.Equal(CoveragePolicyEncoding.CanonicalBytes(escaped), CoveragePolicyEncoding.CanonicalBytes(literal));

        var composed = Policy() with { Run = Policy().Run with { RunScope = "caf\u00e9" } };
        var decomposed = Policy() with { Run = Policy().Run with { RunScope = "cafe\u0301" } };
        Assert.NotEqual(CoveragePolicyEncoding.Digest(composed), CoveragePolicyEncoding.Digest(decomposed));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        {
            Run = Policy().Run with { RunScope = "bad\u202Escope" }
        }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        {
            Run = Policy().Run with { RunScope = "bad\uFDD0scope" }
        }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        {
            Run = Policy().Run with { RunScope = "bad\uD800scope" }
        }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<CoverageRunIdentity>(Utf8(
            """{"producerSystem":"local","runScope":"bad\uD800scope","runId":"run-1","attempt":1}""")));
    }

    [Fact]
    public void DecodeRequiresPolicyFieldsRejectsUnknownAndNullRequiredValuesAndDefaultsOnlyOptionalNulls()
    {
        Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoverageRunIdentity>(Utf8(
            """{"producerSystem":"local","runScope":"dogfood","runId":"run-1"}""")));
        Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(
            PolicyJson(includeTestProjects: false))));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(
            PolicyJson(sourceJson: "null"))));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(
            PolicyJson(runJson: "{\"producerSystem\":\"local\",\"runScope\":null,\"runId\":\"run-1\",\"attempt\":1}"))));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(
            PolicyJson(testProjectsJson: "null"))));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(
            PolicyJson(supplementsJson: "[null]"))));
        Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(
            PolicyJson(extraProperties: ",\"unexpected\":true"))));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(
            PolicyJson(extraProperties: ",\"gate\":{\"mode\":\"aggregateOnly\"}"))));

        var defaults = CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(PolicyJson(
            gateJson: "{\"mode\":\"aggregateOnly\"}")));
        var explicitNulls = CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(PolicyJson(
            gateJson: "{\"mode\":\"aggregateOnly\"}", optionalProperties: ",\"instrumentation\":null,\"limits\":null")));
        Assert.Equal(CanonicalGolden, Encoding.UTF8.GetString(CoveragePolicyEncoding.CanonicalBytes(defaults)));
        Assert.Equal(CoveragePolicyEncoding.CanonicalBytes(defaults), CoveragePolicyEncoding.CanonicalBytes(explicitNulls));
        Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoverageGatePolicy>(Utf8("""{"mode":0}""")));
    }

    [Fact]
    public void DuplicatePropertiesMustMatchOrdinallyAndAreRejectedBeforeDeserialization()
    {
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<CoverageRunIdentity>(Utf8(
            """{"producerSystem":"local","runScope":"x","runId":"1","attempt":1,"attempt":2}""")));
        Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoverageRunIdentity>(Utf8(
            """{"producerSystem":"local","runScope":"x","runId":"1","attempt":1,"token":"secret"}""")));
        Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoverageRunIdentity>(Utf8(
            """{"producerSystem":"local","runScope":"x","runId":"1","Attempt":1}""")));
    }

    [Fact]
    public void PolicyInventoryLimitsAndMissingComponentsAreRejectedWithoutChangingCallerCollections()
    {
        var policy = Policy();
        foreach (var invalid in new[]
        {
            policy with { Source = null! }, policy with { Run = null! }, policy with { Gate = null! },
            policy with { TestProjects = null! }, policy with { Supplements = null! },
            policy with { TestProjects = [] },
            policy with { TestProjects = Enumerable.Range(0, 10001).Select(i => $"checks/P{i}.csproj").ToArray() },
            policy with { Supplements = Enumerable.Range(0, 1024).Select(i => new CoverageSupplement($"p{i}", $"os{i}", "fast")).ToArray() },
            policy with { Supplements = [new("native", "Linux", "fast", Enumerable.Range(0, 10001).Select(i => $"checks/P{i}.csproj").ToArray())] },
            policy with { Source = policy.Source with { Commit = null! } },
            policy with { Source = policy.Source with { Tree = null! } },
            policy with { Source = policy.Source with { Sdk = null! } },
            policy with { Instrumentation = new(Format: "opencover") },
            policy with { Instrumentation = new(Include: "\nprivate-sentinel") }
        })
            Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(invalid));
        Assert.Equal(["tests/Second.csproj", "tests/First.csproj"], policy.TestProjects);
        var maximum = policy with { TestProjects = Enumerable.Range(0, 10000).Select(i => $"checks/P{i}.csproj").ToArray() };
        Assert.Equal(10000, CoveragePolicyEncoding.Freeze(maximum).TestProjects.Count);
        Assert.Equal("[Core]*", CoveragePolicyEncoding.Freeze(policy with { Instrumentation = new(Include: "[Core]*") }).Instrumentation!.Include);
    }

    [Theory]
    [InlineData("alpha-_.Z9", "OS-_.9")]
    [InlineData("a", "z")]
    public void PolicyProducerTokensAcceptEveryDocumentedAsciiPunctuation(string id, string platform)
    {
        var frozen = CoveragePolicyEncoding.Freeze(Policy() with { Supplements = [new(id, platform, "Category=fast")] });
        Assert.Equal(id, frozen.Supplements[0].Id);
        Assert.Equal(platform, frozen.Supplements[0].Platform);
    }

    [Theory]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("COM9")]
    [InlineData("LPT9")]
    [InlineData("COM¹")]
    [InlineData("LPT²")]
    [InlineData("COM³")]
    [InlineData("con.txt")]
    public void PolicyProjectPathsRejectPortableWindowsDeviceAliases(string component)
        => Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        { TestProjects = [$"checks/{component}/Unit.csproj"] }));

    [Theory]
    [InlineData("-12.5", "-12.5")]
    [InlineData("1e2", "100")]
    [InlineData("-1e2", "-100")]
    [InlineData("1.25e1", "12.5")]
    public void LosslessDecimalDecodingRetainsSignAndPositiveExponent(string json, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            CoveragePolicyEncoding.Decode<decimal>(Utf8(json)));

    [Theory]
    [InlineData("true")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("1.5")]
    public void LosslessLongDecodingRejectsWrongTypesRangesAndFractions(string json)
        => Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<long>(Utf8(json)));

    [Theory]
    [InlineData("-1e1001")]
    [InlineData("1e-1001")]
    [InlineData("1e2147483648")]
    [InlineData("1e-2147483649")]
    public void LosslessDecimalDecodingRejectsExponentsBeyondTheBoundedWireContract(string json)
        => Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<decimal>(Utf8(json)));

    [Theory]
    [InlineData("\\uDE80")]
    [InlineData("\\uD83D")]
    [InlineData("\\uD83Dx")]
    [InlineData("\\uD83D\\n")]
    [InlineData("\\uD83D\\u0041")]
    [InlineData("\\uD83D\\uD83D")]
    public void UnicodeWireStringsRejectUnpairedSurrogatesBeforePolicyDeserialization(string escaped)
        => Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<CoverageRunIdentity>(Utf8(
            PolicyJsonRun(escaped))));

    private static string PolicyJsonRun(string escapedScope) =>
        "{\"producerSystem\":\"local\",\"runScope\":\"" + escapedScope + "\",\"runId\":\"run-1\",\"attempt\":1}";

    [Theory]
    [InlineData("checks\\Unit.csproj")]
    [InlineData("/checks/Unit.csproj")]
    [InlineData("checks//Unit.csproj")]
    [InlineData("checks/../Unit.csproj")]
    [InlineData("checks/Unit.csproj ")]
    [InlineData("checks/Unit.csproj.")]
    [InlineData("checks/Unit.csproj:")]
    [InlineData("checks/Unit.csproj?")]
    [InlineData("checks/Unit.csproj*")]
    [InlineData("checks/Unit.csproj|")]
    [InlineData("checks/Unit.csproj<")]
    [InlineData("checks/Unit.csproj>")]
    [InlineData("checks/Unit.csproj\"")]
    public void PolicyProjectPathsRejectNonportableFileSpellings(string project)
        => Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        { TestProjects = [project] }));

    [Fact]
    public void PolicyProjectPathLengthAcceptsTheExactLimitAndRejectsTheNextCharacter()
    {
        var maximum = "checks/" + new string('a', 4082) + ".csproj";
        Assert.Equal(4096, maximum.Length);
        Assert.Equal(maximum, Assert.Single(CoveragePolicyEncoding.Freeze(Policy() with
        { TestProjects = [maximum] }).TestProjects));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        { TestProjects = [maximum + "x"] }));
    }

    [Fact]
    public void UnicodeWireStringsRetainPairedSurrogatesAndOrdinaryEscapes()
    {
        var decoded = CoveragePolicyEncoding.Decode<CoverageRunIdentity>(Utf8(
            """{"producerSystem":"local","runScope":"quoted\"path\\x\uD83D\uDE80","runId":"run-1","attempt":1}"""));
        Assert.Equal("quoted\"path\\x🚀", decoded.RunScope);
        Assert.NotEmpty(CoveragePolicyEncoding.CanonicalBytes(Policy() with { Run = decoded }));
    }

    [Fact]
    public void NumericDecodingIsLosslessCanonicalAndRejectsFractionsOrPrecisionLoss()
    {
        var integer = CoveragePolicyEncoding.Decode<CoverageRunIdentity>(Utf8(
            """{"producerSystem":"local","runScope":"x","runId":"1","attempt":1}"""));
        var lexicalInteger = CoveragePolicyEncoding.Decode<CoverageRunIdentity>(Utf8(
            """{"producerSystem":"local","runScope":"x","runId":"1","attempt":1e0}"""));
        Assert.Equal(CoveragePolicyEncoding.CanonicalBytes(Policy() with { Run = integer }),
            CoveragePolicyEncoding.CanonicalBytes(Policy() with { Run = lexicalInteger }));
        Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoverageRunIdentity>(Utf8(
            """{"producerSystem":"local","runScope":"x","runId":"1","attempt":1.5}""")));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<decimal>(Utf8("1e-29")));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<decimal>(Utf8(
            "1.00000000000000000000000000001")));

        var one = CoveragePolicyEncoding.Decode<CoverageGatePolicy>(Utf8("""{"mode":"aggregateOnly","minLine":1}"""));
        var oneDecimal = CoveragePolicyEncoding.Decode<CoverageGatePolicy>(Utf8("""{"mode":"aggregateOnly","minLine":1.000e0}"""));
        Assert.Equal(one.MinLine, oneDecimal.MinLine);
        Assert.Equal("1", CanonicalNumber(oneDecimal.MinLine));
    }

    [Fact]
    public void PolicyValidationAcceptsDocumentedBudgetBoundaries()
    {
        var upper = new CoverageOperationLimits(
            XmlBytes: int.MaxValue,
            JsonBytes: int.MaxValue,
            TotalBytes: 1099511627776,
            Depth: 256,
            LineRecordsPerReport: int.MaxValue,
            InputBundles: 1024,
            XmlElementsPerReport: int.MaxValue,
            AttributesPerElement: int.MaxValue,
            TokenCharacters: int.MaxValue,
            BranchRecordsPerReport: int.MaxValue,
            TotalRecords: int.MaxValue,
            OperationSeconds: 86400,
            IdentitySeconds: 300);
        var lower = new CoverageOperationLimits(
            XmlBytes: 1, JsonBytes: 1, TotalBytes: 1, Depth: 1,
            LineRecordsPerReport: 1, InputBundles: 1, XmlElementsPerReport: 1,
            AttributesPerElement: 1, TokenCharacters: 1, BranchRecordsPerReport: 1,
            TotalRecords: 1, OperationSeconds: 1, IdentitySeconds: 1);

        Assert.Equal(upper, CoveragePolicyEncoding.Freeze(Policy() with { Limits = upper }).Limits);
        Assert.Equal(lower, CoveragePolicyEncoding.Freeze(Policy() with { Limits = lower }).Limits);
        Assert.Equal(0m, CoveragePolicyEncoding.Freeze(Policy() with
        {
            Gate = new(CoverageGateMode.AggregateOnly, 0, 0, 0, 0, 0)
        }).Gate!.MinLine);
        Assert.Equal(100m, CoveragePolicyEncoding.Freeze(Policy() with
        {
            Gate = new(CoverageGateMode.AggregateOnly, 100, 100, 100, 100, 100)
        }).Gate!.MinLine);
    }

    [Fact]
    public void PolicyValidationRejectsOutOfRangeBudgetsAndUncoveredBundleInventory()
    {
        var invalid = new[]
        {
            new CoverageOperationLimits(XmlBytes: 0),
            new CoverageOperationLimits(XmlBytes: (long)int.MaxValue + 1),
            new CoverageOperationLimits(JsonBytes: 0),
            new CoverageOperationLimits(JsonBytes: (long)int.MaxValue + 1),
            new CoverageOperationLimits(TotalBytes: 0),
            new CoverageOperationLimits(TotalBytes: 1099511627777),
            new CoverageOperationLimits(Depth: 0),
            new CoverageOperationLimits(Depth: 257),
            new CoverageOperationLimits(LineRecordsPerReport: 0),
            new CoverageOperationLimits(InputBundles: 0),
            new CoverageOperationLimits(InputBundles: 1025),
            new CoverageOperationLimits(XmlElementsPerReport: 0),
            new CoverageOperationLimits(AttributesPerElement: 0),
            new CoverageOperationLimits(TokenCharacters: 0),
            new CoverageOperationLimits(BranchRecordsPerReport: 0),
            new CoverageOperationLimits(TotalRecords: 0),
            new CoverageOperationLimits(OperationSeconds: 0),
            new CoverageOperationLimits(OperationSeconds: 86401),
            new CoverageOperationLimits(IdentitySeconds: 0),
            new CoverageOperationLimits(IdentitySeconds: 301)
        };

        foreach (var limits in invalid)
            Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with { Limits = limits }));

        var supplement = new CoverageSupplement("linux", "linux", "Category=fast");
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        {
            Supplements = [supplement],
            Limits = new(InputBundles: 1)
        }));
    }

    [Fact]
    public void PolicyValidationRejectsInvalidIdentityGateAndInstrumentationValues()
    {
        var original = Policy();
        var invalidPolicies = new[]
        {
            original with { Source = original.Source with { Commit = new string('A', 40) } },
            original with { Source = original.Source with { Sdk = "10.0" } },
            original with { Source = original.Source with { Configuration = " \t" } },
            original with { Run = original.Run with { Attempt = 0 } },
            original with { Run = original.Run with { ProducerSystem = null! } },
            original with { Gate = original.Gate with { Mode = (CoverageGateMode)99 } },
            original with { Gate = original.Gate with { MinLine = -0.000001m } },
            original with { Gate = original.Gate with { MinLine = 100.000001m } },
            original with { Gate = original.Gate with { Tolerance = 0.0000001m } },
            original with { Gate = original.Gate with { PatchLineMode = "Codecov" } },
            original with { Gate = new(CoverageGateMode.AggregateAndPatch) },
            original with { Gate = new(CoverageGateMode.AggregateOnly, DiffBase: "main") },
            original with { Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: "--output=caller-path") },
            original with { Instrumentation = new(Driver: "reportgenerator") },
            original with { Instrumentation = new(Exclude: " \t") },
            original with { Supplements = [new("", "linux", "fast")] },
            original with { Supplements = [new("linux", "linux", "fast", [])] }
        };

        foreach (var policy in invalidPolicies)
            Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(policy));
    }

    [Theory]
    [InlineData("../tests/A.csproj")]
    [InlineData("tests//A.csproj")]
    [InlineData("tests/./A.csproj")]
    [InlineData("tests/../A.csproj")]
    [InlineData("/tests/A.csproj")]
    [InlineData("C:/tests/A.csproj")]
    [InlineData("C:tests/A.csproj")]
    [InlineData("tests\\A.csproj")]
    [InlineData("tests/A.csproj:stream")]
    [InlineData("tests/A.csproj.")]
    [InlineData("tests/A.csproj ")]
    [InlineData("tests/CON.csproj")]
    [InlineData("tests/aux.txt")]
    [InlineData("tests/a?.csproj")]
    [InlineData("tests/bad\u202E.csproj")]
    public void PolicyRejectsNonportableOrUnsafeProjectIdentities(string project)
        => Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with { TestProjects = [project] }));

    [Fact]
    public void PolicyRejectsCaseAndUnicodeNormalizationAliasesInEveryProjectSet()
    {
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        {
            TestProjects = ["test/A.csproj", "test/a.csproj"]
        }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        {
            TestProjects = ["test/caf\u00e9.csproj", "test/cafe\u0301.csproj"]
        }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        {
            Supplements = [new("linux", "linux", "fast", ["tests/A.csproj", "tests/a.csproj"])]
        }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        {
            Supplements = [new("linux", "linux", "fast"), new("LINUX", "windows", "fast")]
        }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with
        {
            Supplements = [new("Primary", "linux", "fast")]
        }));
    }

    [Fact]
    public void ExplicitProjectInventoryAcceptsProjectsWithoutConventionalTestSuffixes()
    {
        var policy = Policy() with
        {
            TestProjects = ["checks/Contract.csproj", "verification/Smoke.fsproj", "custom/Verifier.project"]
        };
        Assert.Equal(
            ["checks/Contract.csproj", "custom/Verifier.project", "verification/Smoke.fsproj"],
            CoveragePolicyEncoding.Freeze(policy).TestProjects);
    }

    [Fact]
    public void MeaningfulPolicyMutationsChangeTheDigest()
    {
        var original = Policy();
        var digest = CoveragePolicyEncoding.Digest(original);
        Assert.NotEqual(digest, CoveragePolicyEncoding.Digest(original with
        {
            Run = original.Run with { Attempt = 2 }
        }));
        Assert.NotEqual(digest, CoveragePolicyEncoding.Digest(original with
        {
            TestProjects = ["tests/First.csproj", "tests/Third.csproj"]
        }));
        Assert.NotEqual(digest, CoveragePolicyEncoding.Digest(original with
        {
            Gate = original.Gate with { MinLine = 94.5m }
        }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"wrong-kind\"")]
    public void TypedDecoderRejectsNullAndNonPolicyRootValues(string json)
    {
        if (json == "null") Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(json)));
        else Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(Utf8(json)));
    }

    [Theory]
    [InlineData("-2147483649")]
    [InlineData("2147483648")]
    [InlineData("null")]
    [InlineData("\"1\"")]
    [InlineData("1e28")]
    public void IntDecoderRejectsEveryNonrepresentableShape(string json)
        => Assert.Throws<JsonException>(() => CoveragePolicyEncoding.Decode<int>(Utf8(json)));

    [Theory]
    [InlineData("id")]
    [InlineData("platform")]
    [InlineData("filter")]
    [InlineData("configuration")]
    public void PolicyTextAndTokenBoundariesAreValidatedAtTheActualField(string field)
    {
        var policy = Policy() with { Supplements = [new("native", "Linux", "fast")] };
        foreach (var value in new[] { " ", new string('x', field == "filter" ? 4097 : 257), "bad\nvalue" })
        {
            var supplement = policy.Supplements[0];
            var invalid = field switch
            {
                "id" => policy with { Supplements = [supplement with { Id = value }] },
                "platform" => policy with { Supplements = [supplement with { Platform = value }] },
                "filter" => policy with { Supplements = [supplement with { Filter = value }] },
                _ => policy with { Source = policy.Source with { Configuration = value } }
            };
            Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(invalid));
        }
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(policy with
        { Supplements = [policy.Supplements[0] with { Id = "invalid/id" }] }));
    }

    [Fact]
    public void ProjectPathsAndNumericWireLexemesEnforceFiniteDocumentedBounds()
    {
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Freeze(Policy() with { TestProjects = [new string('a', 4097)] }));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<decimal>(Utf8("1e" + new string('0', 255))));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<decimal>(Utf8("1e2147483648")));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<decimal>(Utf8("1e1001")));
        Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<decimal>(Utf8("1e-1001")));
        foreach (var text in new[] { "\"\\uDC00\"", "\"\\uD800x\"", "\"\\uD800\\n\"", "\"\\uD800\\u0041\"" })
            Assert.Throws<ArgumentException>(() => CoveragePolicyEncoding.Decode<string>(Utf8(text)));
    }

    private static string PolicyJson(
        string gateJson = "{\"mode\":\"aggregateOnly\"}",
        string runScopeJson = "\"dogfood\"",
        string projectArrayJson = "[\"tests/Second.csproj\",\"tests/First.csproj\"]",
        string sourceJson = "{\"commit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"tree\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"sdk\":\"10.0.102\",\"configuration\":\"Release\"}",
        string runJson = "",
        string testProjectsJson = "",
        string supplementsJson = "[]",
        string optionalProperties = "",
        string extraProperties = "",
        bool includeTestProjects = true)
    {
        runJson = runJson.Length == 0
            ? $"{{\"producerSystem\":\"local\",\"runScope\":{runScopeJson},\"runId\":\"run-1\",\"attempt\":1}}"
            : runJson;
        var projects = testProjectsJson.Length > 0 ? testProjectsJson : projectArrayJson;
        var projectField = includeTestProjects ? $",\"testProjects\":{projects}" : string.Empty;
        return $"{{\"source\":{sourceJson},\"run\":{runJson}{projectField},\"supplements\":{supplementsJson},\"gate\":{gateJson}{optionalProperties}{extraProperties}}}";
    }

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    private static string CanonicalNumber(decimal value)
        => value.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);
}
