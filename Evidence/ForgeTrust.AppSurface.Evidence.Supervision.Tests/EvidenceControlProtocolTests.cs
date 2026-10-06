using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure grammar and snapshot controls, without dispatch, admission or native authority.</summary>
public sealed class EvidenceControlProtocolTests
{
    [Theory]
    [InlineData("ready")]
    [InlineData("stop")]
    [InlineData("wait")]
    [InlineData("exit")]
    public void OperationOnlyRequestsDecodeToTheirClosedTypes(string operation)
    {
        var request = Parse(JsonSerializer.Serialize(new { op = operation }));
        switch (operation)
        {
            case "ready": Assert.IsType<EvidenceReadyControlRequest>(request); break;
            case "stop": Assert.IsType<EvidenceStopControlRequest>(request); break;
            case "wait": Assert.IsType<EvidenceWaitControlRequest>(request); break;
            case "exit": Assert.IsType<EvidenceExitControlRequest>(request); break;
        }
    }

    [Fact]
    public void RunDataIsDetachedAndReadOnlyWithoutAuthorizingItsProcedure()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            op = "run", executable = "/unselected/tool", arguments = new[] { "test", "", "値" },
            working_directory = "/unselected/source",
        });
        var request = Assert.IsType<EvidenceRunControlRequest>(EvidenceControlProtocol.Parse(bytes));
        Array.Fill(bytes, (byte)'x');
        Assert.Equal("/unselected/tool", request.Executable);
        Assert.Equal("/unselected/source", request.WorkingDirectory);
        Assert.Equal(new[] { "test", "", "値" }, request.Arguments);
        var list = Assert.IsAssignableFrom<IList<string>>(request.Arguments);
        Assert.Throws<NotSupportedException>(() => list[0] = "changed");
        var source = new List<string> { "original" };
        var copy = new EvidenceRunControlRequest("tool", source, "source");
        source[0] = "changed";
        Assert.Equal("original", Assert.Single(copy.Arguments));
    }

    [Fact]
    public void ArtifactsAndApplicationIdentitiesDecodeWithoutSelectingRegistrations()
    {
        var artifacts = Assert.IsType<EvidenceArtifactsControlRequest>(
            Parse("""{"op":"artifacts","relative_root":"results-1"}"""));
        Assert.Equal("results-1", artifacts.RelativeRoot);
        var app = Assert.IsType<EvidenceApplicationStartControlRequest>(Parse(JsonSerializer.Serialize(new
        {
            op = "application-start", application_id = "app-1", entry_digest = new string('a', 64),
        })));
        Assert.Equal("app-1", app.ApplicationId);
        Assert.Equal(new string('a', 64), app.EntryDigest);
        var resource = Assert.IsType<EvidenceResourceWaitControlRequest>(Parse(JsonSerializer.Serialize(new
        {
            op = "resource-wait", lease_id = new string('b', 32), resource_id = "http-1",
        })));
        Assert.Equal(new string('b', 32), resource.LeaseId);
        Assert.Equal("http-1", resource.ResourceId);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void ArtifactOffsetsAcceptExactIntegerBoundaries(long offset)
    {
        var request = Assert.IsType<EvidenceArtifactControlRequest>(Parse(JsonSerializer.Serialize(new
        {
            op = "artifact", relative_root = "results", relative_path = "nested/値.xml", offset,
        })));
        Assert.Equal("nested/値.xml", request.Path);
        Assert.Equal(offset, request.Offset);
    }

    [Fact]
    public void ActualClientShapeRetainsTheRunResultsTokenAndHasNoCallerChunkSize()
    {
        var resultsToken = "coverage-" + new string('a', 32);
        var inventory = Assert.IsType<EvidenceArtifactsControlRequest>(Parse(JsonSerializer.Serialize(new
        {
            op = "artifacts", relative_root = resultsToken,
        })));
        var read = Assert.IsType<EvidenceArtifactControlRequest>(Parse(JsonSerializer.Serialize(new
        {
            op = "artifact", relative_root = resultsToken, relative_path = "run/coverage.cobertura.xml", offset = 0,
        })));
        Assert.Equal(inventory.RelativeRoot, read.RelativeRoot);
        Assert.Equal("run/coverage.cobertura.xml", read.Path);
    }

    [Theory]
    [InlineData("{\"op\":\"ready\",\"op\":\"stop\"}")]
    [InlineData("{\"op\":\"ready\",\"OP\":\"ready\"}")]
    [InlineData("{\"op\":\"ready\",\"o\\u0070\":\"ready\"}")]
    [InlineData("{\"OP\":\"ready\"}")]
    [InlineData("{\"op\":\"Ready\"}")]
    [InlineData("{\"op\":\"ready\",\"private-canary\":true}")]
    [InlineData("{\"op\":\"run\",\"executable\":\"tool\",\"arguments\":[\"x\"],\"Working_Directory\":\"source\"}")]
    [InlineData("{\"op\":\"artifact\",\"relative_root\":\"r\",\"relative_path\":\"f\",\"offset\":0,\"count\":1}")]
    [InlineData("{\"op\":\"artifact\",\"relative_root\":\"r\",\"path\":\"f\",\"offset\":0}")]
    [InlineData("{\"op\":\"artifact\",\"relative_root\":\"r\",\"relative_path\":\"f\",\"path\":\"f\",\"offset\":0}")]
    [InlineData("{\"op\":\"artifact\",\"relative_root\":\"r\",\"Relative_Path\":\"f\",\"offset\":0}")]
    [InlineData("{\"op\":\"artifact\",\"relative_root\":\"r\",\"relative_path\":\"f\",\"Offset\":0}")]
    [InlineData("{\"op\":\"artifact\",\"relative_root\":\"r\",\"relative_path\":\"f\","
        + "\"relative_path\":\"g\",\"offset\":0}")]
    public void DuplicateAliasUnknownAndLegacyFieldNamesRejectWithFixedDiagnostics(string json) => Reject(json);

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"op\":null}")]
    [InlineData("{\"op\":1}")]
    [InlineData("{\"op\":{\"private-canary\":\"ready\"}}")]
    [InlineData("{\"op\":\"private-canary\"}")]
    [InlineData("{\"op\":\"ready\",}")]
    [InlineData("/*private-canary*/{\"op\":\"ready\"}")]
    [InlineData("{\"op\":\"ready\"}{\"op\":\"stop\"}")]
    [InlineData("{\"op\":\"run\",\"executable\":\"tool\",\"arguments\":[\"x\"]}")]
    [InlineData("{\"op\":\"run\",\"executable\":null,\"arguments\":[\"x\"],\"working_directory\":\"s\"}")]
    [InlineData("{\"op\":\"run\",\"executable\":\"tool\",\"arguments\":null,\"working_directory\":\"s\"}")]
    [InlineData("{\"op\":\"run\",\"executable\":\"tool\",\"arguments\":[null],\"working_directory\":\"s\"}")]
    [InlineData("{\"op\":\"run\",\"executable\":\"tool\",\"arguments\":[[\"x\"]],\"working_directory\":\"s\"}")]
    [InlineData("{\"op\":\"run\",\"executable\":\"tool\",\"arguments\":[true],\"working_directory\":\"s\"}")]
    [InlineData("{\"op\":\"run\",\"executable\":\"tool\",\"arguments\":[\"x\"],\"working_directory\":null}")]
    [InlineData("{\"op\":\"run\",\"executable\":\"tool\",\"arguments\":[\"\\u0000\"],\"working_directory\":\"s\"}")]
    public void MalformedMissingNullAndNestedValuesReject(string json) => Reject(json);

    [Theory]
    [InlineData("{\"op\":\"artifacts\",\"relative_root\":null}")]
    [InlineData("{\"op\":\"artifacts\",\"relative_root\":[\"r\"]}")]
    [InlineData("{\"op\":\"artifact\",\"relative_root\":\"r\",\"relative_path\":null,\"offset\":0}")]
    [InlineData("{\"op\":\"artifact\",\"relative_root\":\"r\",\"relative_path\":\"f\"}")]
    [InlineData("{\"op\":\"application-start\",\"application_id\":\"app\"}")]
    [InlineData("{\"op\":\"application-start\",\"application_id\":\"app\",\"entry_digest\":null}")]
    [InlineData("{\"op\":\"resource-wait\",\"lease_id\":32,\"resource_id\":\"http\"}")]
    [InlineData("{\"op\":\"resource-wait\",\"lease_id\":null,\"resource_id\":{\"private-canary\":true}}")]
    public void FixedIdentityAndRangeFieldsRejectStructuralAlternatives(string json) => Reject(json);

    [Theory]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("1.5")]
    [InlineData("\"0\"")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("1e0")]
    public void ArtifactOffsetsRejectNegativeNonintegerOrExcessiveValues(string offset) =>
        Reject($$"""{"op":"artifact","relative_root":"r","relative_path":"f","offset":{{offset}}}""");

    [Theory]
    [InlineData("../f")]
    [InlineData("a/./f")]
    [InlineData("a//f")]
    [InlineData("/f")]
    [InlineData("a\\f")]
    [InlineData("a/")]
    [InlineData("")]
    public void ArtifactPathsRequireNormalizedRelativeSegments(string path) => Reject(JsonSerializer.Serialize(new
    {
        op = "artifact", relative_root = "r", relative_path = path, offset = 0,
    }));

    [Fact]
    public void IdentifierAndDigestRejectionsHaveValidNeighboringRequests()
    {
        foreach (var root in new[] { "", "../r", "r/f", "_r", "値", new string('a', 97) })
        {
            Reject(JsonSerializer.Serialize(new { op = "artifacts", relative_root = root }));
        }
        Assert.IsType<EvidenceArtifactsControlRequest>(Parse(JsonSerializer.Serialize(new
        {
            op = "artifacts", relative_root = new string('a', 96),
        })));
        foreach (var digest in new[] { new string('a', 63), new string('A', 64), new string('g', 64) })
        {
            Reject(JsonSerializer.Serialize(new
            {
                op = "application-start", application_id = "app", entry_digest = digest,
            }));
        }
        foreach (var lease in new[] { new string('b', 31), new string('B', 32), new string('z', 32) })
        {
            Reject(JsonSerializer.Serialize(new { op = "resource-wait", lease_id = lease, resource_id = "http" }));
        }
    }

    [Fact]
    public void DecodedUtf8TokenLimitCountsBytesInsteadOfCharactersOrJsonEscapes()
    {
        var maximum = string.Concat(Enumerable.Repeat("😀", 1024));
        var accepted = RunJson([maximum]);
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(maximum));
        Assert.Equal(maximum, Assert.Single(Assert.IsType<EvidenceRunControlRequest>(Parse(accepted)).Arguments));
        Reject(RunJson([maximum + "a"]));
        Reject(RunJson([new string('a', 4097)]));
    }

    [Fact]
    public void ExecutableAndWorkingDirectoryHaveIndependentNonemptyUtf8Bounds()
    {
        var maximum = new string('a', 4096);
        var accepted = Assert.IsType<EvidenceRunControlRequest>(Parse(JsonSerializer.Serialize(new
        {
            op = "run", executable = maximum, arguments = Array.Empty<string>(), working_directory = maximum,
        })));
        Assert.Equal(maximum, accepted.Executable);
        Assert.Equal(maximum, accepted.WorkingDirectory);
        foreach (var invalid in new[] { "", maximum + "a", "private-canary\0" })
        {
            Reject(JsonSerializer.Serialize(new
            {
                op = "run", executable = invalid, arguments = Array.Empty<string>(), working_directory = "source",
            }));
            Reject(JsonSerializer.Serialize(new
            {
                op = "run", executable = "tool", arguments = Array.Empty<string>(), working_directory = invalid,
            }));
        }
    }

    [Fact]
    public void ArgumentCountAndCompleteFrameLimitsAreIndependent()
    {
        Assert.Empty(Assert.IsType<EvidenceRunControlRequest>(Parse(RunJson([]))).Arguments);
        Assert.Equal(128, Assert.IsType<EvidenceRunControlRequest>(Parse(
            RunJson(Enumerable.Repeat("x", 128).ToArray()))).Arguments.Count);
        Reject(RunJson(Enumerable.Repeat("x", 129).ToArray()));
        var token = new string('a', 4096);
        var tooLarge = RunJson(Enumerable.Repeat(token, 16).ToArray());
        Assert.True(Encoding.UTF8.GetByteCount(tooLarge) > EvidenceControlProtocol.MaximumRequestBytes);
        Reject(tooLarge);
        var ready = Encoding.UTF8.GetBytes("{\"op\":\"ready\"}");
        var exact = Enumerable.Repeat((byte)' ', EvidenceControlProtocol.MaximumRequestBytes).ToArray();
        ready.CopyTo(exact, 0);
        Assert.IsType<EvidenceReadyControlRequest>(EvidenceControlProtocol.Parse(exact));
        Reject(exact.Concat(new byte[] { (byte)' ' }).ToArray());
    }

    [Fact]
    public void InvalidUtf8AndSurrogatesCannotBecomeReplacementText()
    {
        Reject(new byte[] { (byte)'{', (byte)'"', 0xff, (byte)'"', (byte)':', (byte)'0', (byte)'}' });
        Reject("{\"op\":\"run\",\"executable\":\"t\",\"arguments\":[\"\\uD800\"],\"working_directory\":\"s\"}");
    }

    private static string RunJson(string[] arguments) => JsonSerializer.Serialize(new
    {
        op = "run", executable = "tool", arguments, working_directory = "source",
    });

    private static EvidenceControlRequest Parse(string json) =>
        EvidenceControlProtocol.Parse(Encoding.UTF8.GetBytes(json));

    private static void Reject(string json) => Reject(Encoding.UTF8.GetBytes(json));

    private static void Reject(byte[] json)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceControlProtocol.Parse(json));
        Assert.Equal("ASEVD420", error.Code);
        Assert.Null(error.InnerException);
        Assert.StartsWith("ASEVD420: The control request is malformed or exceeds its fixed limits.",
            error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-canary", error.Message, StringComparison.Ordinal);
    }
}
