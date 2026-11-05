using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInPolicyTests
{
    private const string CanonicalGolden = """{"gate":{"diffBase":null,"minBranch":85,"minLine":95,"minPatchBranch":85,"minPatchLine":95,"mode":"aggregateOnly","patchLineMode":"codecov","tolerance":0.5},"instrumentation":{"driver":"collector","exclude":"[*.Tests]*,[*.IntegrationTests]*","format":"cobertura","include":null,"normalization":"appsurface-coverage-merge"},"limits":{"attributesPerElement":32,"branchRecordsPerReport":2000000,"depth":64,"identitySeconds":30,"inputBundles":32,"jsonBytes":1048576,"lineRecordsPerReport":1000000,"operationSeconds":300,"tokenCharacters":65536,"totalBytes":4294967296,"totalRecords":2000000,"xmlBytes":536870912,"xmlElementsPerReport":8000000},"run":{"attempt":1,"producerSystem":"local","runId":"run-1","runScope":"dogfood"},"source":{"commit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","configuration":"Release","sdk":"10.0.102","tree":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},"supplements":[],"testProjects":["tests/First.csproj","tests/Second.csproj"]}""";

    internal static CoverageFanInPolicy Policy() => new(
        new(new string('a', 40), new string('b', 40), "10.0.102", "Release"),
        new("local", "dogfood", "run-1", 1),
        ["tests/Second.csproj", "tests/First.csproj"], [], new(CoverageGateMode.AggregateOnly));

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
