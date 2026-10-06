using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Launch metadata grammar controls, without filesystem, runtime, clock or admission authority.</summary>
public sealed class EvidenceSupervisorRequestTests
{
    [Fact]
    public void ObservationRequestDecodesDeploymentWithoutInspectingOrAuthorizingIt()
    {
        var data = Valid();
        data["diff_file"] = "/opt/tool/changes.diff";
        data["diff_sha256"] = new string('c', 64);
        data["solution"] = "/work/subject/app.sln";
        var request = Parse(data);
        Assert.Equal("evidence-supervisor-linux-v1", request.Schema);
        Assert.Equal("observation", request.Mode);
        Assert.Equal("/opt/runtime/dotnet", request.RuntimeHost);
        Assert.Equal("/opt/tool/Worker.dll", request.EntryPath);
        Assert.Equal("/opt/tool/changes.diff", request.DiffFile);
        Assert.Equal(new string('c', 64), request.DiffSha256);
        Assert.Equal("/work/subject/app.sln", request.Solution);
    }

    [Fact]
    public void AllThreeListSnapshotsSurviveDocumentAndInputMutationAndRejectMutableCasts()
    {
        var paths = new[] { "src/値.cs" };
        var profiles = new[] { "profile-1" };
        var producers = new[] { "producer-1" };
        var data = Valid();
        data["paths"] = paths;
        data["observation_profile_ids"] = profiles;
        data["observation_producer_ids"] = producers;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
        var request = EvidenceSupervisorRequest.Parse(bytes);
        Array.Fill(bytes, (byte)'x');
        paths[0] = profiles[0] = producers[0] = "changed";
        Assert.Equal("src/値.cs", Assert.Single(request.Paths));
        Assert.Equal("profile-1", Assert.Single(request.ObservationProfileIds));
        Assert.Equal("producer-1", Assert.Single(request.ObservationProducerIds));
        foreach (var list in new[] { request.Paths, request.ObservationProfileIds, request.ObservationProducerIds })
        {
            var writable = Assert.IsAssignableFrom<IList<string>>(list);
            Assert.Throws<NotSupportedException>(() => writable[0] = "changed");
            Assert.Throws<NotSupportedException>(() => writable.Add("extra"));
        }
    }

    [Theory]
    [InlineData("admission_seconds", 30)]
    [InlineData("start_seconds", 120)]
    [InlineData("collection_seconds", 60)]
    [InlineData("cleanup_seconds", 600)]
    [InlineData("stopping_seconds", 30)]
    public void DurationEndpointsAreDataNotNewAllowances(string name, int maximum)
    {
        foreach (var value in new[] { 1, maximum })
        {
            var data = Valid();
            data[name] = value;
            var request = Parse(data);
            Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), request.JobDeadlineUtc);
        }
        foreach (var value in new[] { 0, maximum + 1 })
        {
            var data = Valid();
            data[name] = value;
            Reject(data);
        }
    }

    [Fact]
    public void StoppingMustFitCleanupWithoutChangingTheRequestedTimestamp()
    {
        var data = Valid();
        data["cleanup_seconds"] = 2;
        data["stopping_seconds"] = 2;
        Assert.Equal(2, Parse(data).StoppingSeconds);
        data["stopping_seconds"] = 3;
        Reject(data);
    }

    [Theory]
    [InlineData("schema", "evidence-supervisor-linux-v2")]
    [InlineData("mode", "trusted")]
    [InlineData("mode", "Observation")]
    [InlineData("base_revision", "private-canary")]
    [InlineData("workflow_identity", "")]
    [InlineData("workflow_identity", "\tprivate-canary")]
    public void ClosedModeSchemaRevisionAndIdentityRejectInvalidNeighbors(string name, string value)
    {
        var data = Valid();
        data[name] = value;
        Reject(data);
    }

    [Theory]
    [InlineData("worker_uid")]
    [InlineData("worker_pid")]
    [InlineData("unit")]
    [InlineData("argv")]
    [InlineData("environment")]
    [InlineData("capability")]
    [InlineData("proof_digest")]
    public void RequestsCannotSupplyNativeIdentitiesOrCapabilities(string name)
    {
        var data = Valid();
        data[name] = "private-canary";
        Reject(data);
    }

    [Fact]
    public void ExactRequiredMembersRejectMissingNullUnknownDuplicateAndCaseAliases()
    {
        foreach (var name in Valid().Keys)
        {
            var missing = Valid();
            missing.Remove(name);
            Reject(missing);
            var nullValue = Valid();
            nullValue[name] = null;
            Reject(nullValue);
        }
        var json = JsonSerializer.Serialize(Valid());
        Reject(Encoding.UTF8.GetBytes(json[..^1] + ",\"mode\":\"observation\"}"));
        Reject(Encoding.UTF8.GetBytes(json[..^1] + ",\"Mode\":\"observation\"}"));
        Reject(Encoding.UTF8.GetBytes(json[..^1] + ",\"m\\u006fde\":\"observation\"}"));
        Reject(Encoding.UTF8.GetBytes(json.Replace("\"mode\"", "\"Mode\"", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("/")]
    [InlineData("/opt/runtime//dotnet")]
    [InlineData("/opt/runtime/./dotnet")]
    [InlineData("/opt/runtime/a/../dotnet")]
    [InlineData("/opt/runtime/dotnet/")]
    [InlineData("/opt/runtime/a\\dotnet")]
    [InlineData("/opt/runtime/dotnet\0")]
    public void AbsolutePathsRequireCanonicalLinuxSpelling(string path)
    {
        var data = Valid();
        data["runtime_host"] = path;
        Reject(data);
    }

    [Fact]
    public void SubjectRootIsDisjointButRuntimeAndToolRootsCanOverlap()
    {
        var data = Valid();
        data["runtime_root"] = "/opt/tool";
        data["runtime_host"] = "/opt/tool/dotnet";
        Assert.Equal("/opt/tool", Parse(data).RuntimeRoot);
        data["runtime_root"] = "/opt/tool/runtime";
        data["runtime_host"] = "/opt/tool/runtime/dotnet";
        Assert.Equal("/opt/tool/runtime", Parse(data).RuntimeRoot);
        data["runtime_root"] = "/opt";
        data["runtime_host"] = "/opt/dotnet";
        Assert.Equal("/opt", Parse(data).RuntimeRoot);
        foreach (var subject in new[] { "/opt/tool", "/opt/tool/subject", "/opt", "/opt/runtime" })
        {
            var invalid = Valid();
            invalid["subject_root"] = subject;
            Reject(invalid);
        }
        var prefixNeighbor = Valid();
        prefixNeighbor["subject_root"] = "/opt/toolbox";
        Assert.Equal("/opt/toolbox", Parse(prefixNeighbor).SubjectRoot);
    }

    [Theory]
    [InlineData("runtime_host", "/opt/runtimebox/dotnet")]
    [InlineData("runtime_host", "/opt/runtime")]
    [InlineData("entry_path", "/opt/toolbox/Worker.dll")]
    [InlineData("entry_path", "/opt/tool")]
    [InlineData("policy_file", "/work/subject/policy.json")]
    [InlineData("diff_file", "/work/subject/changes.diff")]
    [InlineData("solution", "/work/subjectbox/app.sln")]
    public void FileContainmentUsesWholeSegmentsAndRequiresADescendant(string name, string path)
    {
        var data = Valid();
        if (name == "diff_file") data["diff_sha256"] = new string('c', 64);
        data[name] = path;
        Reject(data);
    }

    [Fact]
    public void OptionalDiffAndDigestArePairedWhileNullAndAbsentRemainEquivalent()
    {
        var absent = Parse(Valid());
        Assert.Null(absent.DiffFile);
        Assert.Null(absent.DiffSha256);
        Assert.Null(absent.Solution);
        var data = Valid();
        data["diff_file"] = data["diff_sha256"] = data["solution"] = null;
        Assert.Null(Parse(data).DiffFile);
        data["diff_file"] = "/opt/tool/changes.diff";
        Reject(data);
        data["diff_sha256"] = new string('c', 64);
        Assert.Equal("/opt/tool/changes.diff", Parse(data).DiffFile);
        data["diff_file"] = null;
        Reject(data);
        data.Remove("diff_file");
        Reject(data);
    }

    [Theory]
    [InlineData("base_revision", 40)]
    [InlineData("subject_revision", 40)]
    [InlineData("diff_sha256", 64)]
    public void RevisionsAndDigestsRequireExactLowercaseHex(string name, int length)
    {
        foreach (var invalid in new[] { new string('a', length - 1), new string('a', length + 1),
                     new string('A', length), new string('g', length) })
        {
            var data = Valid();
            if (name == "diff_sha256") data["diff_file"] = "/opt/tool/changes.diff";
            data[name] = invalid;
            Reject(data);
        }
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/x")]
    [InlineData("a/./x")]
    [InlineData("a//x")]
    [InlineData("a/")]
    [InlineData("a\\x")]
    [InlineData("")]
    public void DiffPathItemsAreNonemptyAndNormalized(string path)
    {
        var data = Valid();
        data["paths"] = new[] { path };
        Reject(data);
    }

    [Fact]
    public void ListBoundsUniquenessAndIdentifierGrammarHaveValidNeighbors()
    {
        var data = Valid();
        data["paths"] = Enumerable.Range(0, 4096).Select(i => "p" + i).ToArray();
        data["observation_profile_ids"] = Enumerable.Range(0, 32).Select(i => "profile-" + i).ToArray();
        data["observation_producer_ids"] = Enumerable.Range(0, 32).Select(i => "producer-" + i).ToArray();
        var request = Parse(data);
        Assert.Equal(4096, request.Paths.Count);
        Assert.Equal(32, request.ObservationProfileIds.Count);
        Assert.Equal(32, request.ObservationProducerIds.Count);
        foreach (var name in new[] { "paths", "observation_profile_ids", "observation_producer_ids" })
        {
            var invalid = Valid();
            invalid[name] = new[] { "duplicate", "duplicate" };
            Reject(invalid);
            invalid[name] = Enumerable.Range(0, name == "paths" ? 4097 : 33).Select(i => "p" + i).ToArray();
            Reject(invalid);
            invalid[name] = Array.Empty<string>();
            Parse(invalid); // Empty metadata is not permission to bypass subsequent planner or admission checks.
        }
        foreach (var id in new[] { "_id", "path/id", "値", new string('a', 97) })
        {
            data = Valid();
            data["observation_profile_ids"] = new[] { id };
            Reject(data);
        }
    }

    [Theory]
    [InlineData("2001-01-01T00:00:00")]
    [InlineData("2001-01-01T01:00:00+01:00")]
    [InlineData("2001-13-01T00:00:00Z")]
    [InlineData("private-canaryZ")]
    public void DeadlineMustBeExplicitUtcButIsNotAnArmingOperation(string deadline)
    {
        var data = Valid();
        data["job_deadline_utc"] = deadline;
        Reject(data);
    }

    [Fact]
    public void PastUtcAndExplicitZeroOffsetAreAcceptedAsTimestampDataOnly()
    {
        var data = Valid();
        var first = Parse(data);
        data["job_deadline_utc"] = "2001-01-01T00:00:00+00:00";
        Assert.Equal(first.JobDeadlineUtc, Parse(data).JobDeadlineUtc);
    }

    [Fact]
    public void Utf8TokenAndFullFrameBoundsCountActualBytes()
    {
        var data = Valid();
        var maximum = string.Concat(Enumerable.Repeat("😀", 64));
        data["workflow_identity"] = maximum;
        Assert.Equal(256, Encoding.UTF8.GetByteCount(maximum));
        Assert.Equal(maximum, Parse(data).WorkflowIdentity);
        data["workflow_identity"] = maximum + "a";
        Reject(data);
        data = Valid();
        data["paths"] = new[] { new string('a', 4095) };
        Assert.Equal(4095, Assert.Single(Parse(data).Paths).Length);
        data["paths"] = new[] { new string('a', 4096) };
        Reject(data);
        data = Valid();
        var prefix = "/opt/runtime/";
        data["runtime_host"] = prefix + new string('a', 4095 - prefix.Length);
        Assert.Equal(4095, Parse(data).RuntimeHost.Length);
        data["runtime_host"] = (string)data["runtime_host"]! + "a";
        Reject(data);
        var json = JsonSerializer.SerializeToUtf8Bytes(Valid());
        var exact = Enumerable.Repeat((byte)' ', 64 * 1024).ToArray();
        json.CopyTo(exact, 0);
        EvidenceSupervisorRequest.Parse(exact);
        Reject(exact.Concat(new byte[] { (byte)' ' }).ToArray());
    }

    [Fact]
    public void WrongTypesNestedDataInvalidUtf8AndMalformedJsonRejectBeforeOsInteraction()
    {
        foreach (var name in new[] { "runtime_host", "admission_seconds", "paths", "observation_producer_ids" })
        {
            foreach (var invalid in new object?[] { true, new { secret = "private-canary" }, new[] { new[] { "x" } } })
            {
                var data = Valid();
                data[name] = invalid;
                Reject(data);
            }
        }
        var fractional = Valid();
        fractional["start_seconds"] = 1.5;
        Reject(fractional);
        foreach (var json in new[] { "", "null", "[]", "{}", "/*private-canary*/{}", "{\"schema\":null,}",
                     "{\"private-canary\":[[[[[]]]]]}" })
        {
            Reject(Encoding.UTF8.GetBytes(json));
        }
        Reject(new byte[] { (byte)'{', (byte)'"', 0xff, (byte)'"', (byte)':', (byte)'0', (byte)'}' });
        var escapedSurrogate = JsonSerializer.Serialize(Valid()).Replace(
            "\"workflow_identity\":\"workflow-1\"", "\"workflow_identity\":\"\\uD800\"", StringComparison.Ordinal);
        Reject(Encoding.UTF8.GetBytes(escapedSurrogate));
    }

    private static Dictionary<string, object?> Valid() => new(StringComparer.Ordinal)
    {
        ["schema"] = "evidence-supervisor-linux-v1", ["mode"] = "observation",
        ["tool_root"] = "/opt/tool", ["runtime_root"] = "/opt/runtime", ["runtime_host"] = "/opt/runtime/dotnet",
        ["entry_path"] = "/opt/tool/Worker.dll", ["policy_file"] = "/opt/tool/policy.json",
        ["subject_root"] = "/work/subject", ["base_revision"] = new string('a', 40),
        ["subject_revision"] = new string('b', 40), ["workflow_identity"] = "workflow-1",
        ["paths"] = new[] { "src/Calculation.cs" }, ["observation_profile_ids"] = new[] { "profile-1" },
        ["observation_producer_ids"] = new[] { "producer-1" }, ["job_deadline_utc"] = "2001-01-01T00:00:00Z",
        ["admission_seconds"] = 10, ["start_seconds"] = 30, ["collection_seconds"] = 15,
        ["cleanup_seconds"] = 60, ["stopping_seconds"] = 1,
    };

    private static EvidenceSupervisorRequest Parse(Dictionary<string, object?> data) =>
        EvidenceSupervisorRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(data));

    private static void Reject(Dictionary<string, object?> data) => Reject(JsonSerializer.SerializeToUtf8Bytes(data));

    private static void Reject(byte[] data)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceSupervisorRequest.Parse(data));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.StartsWith("ASEVD402: The supervisor launch request is malformed or exceeds its fixed limits.",
            error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-canary", error.Message, StringComparison.Ordinal);
    }
}
