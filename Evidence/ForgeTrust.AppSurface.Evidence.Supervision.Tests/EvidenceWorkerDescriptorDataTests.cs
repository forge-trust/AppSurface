using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure descriptor/ready metadata controls; no test authenticates a process, FD, socket or admission.</summary>
public sealed class EvidenceWorkerDescriptorDataTests
{
    private static readonly Guid Generation = Guid.Parse("4523b7bf-72a3-4fc7-a367-86857556e574");
    private static readonly LinuxRunAccountSnapshot Accounts = new(65010, 65011, 65012, 65013, 65014);
    private static readonly LinuxWorkspaceLayout Layout = LinuxWorkspaceLayout.Create(Generation, Accounts);
    private static readonly string EntryHash = new('a', 64);
    private static readonly string PolicyHash = new('b', 64);

    [Fact]
    public void CompleteV1ShapeAndReadyWrapperPreserveTheSameDescriptorWithoutAuthorityFields()
    {
        var data = Make();
        using var descriptor = JsonDocument.Parse(data.DescriptorBytes);
        using var ready = JsonDocument.Parse(data.ReadyBytes);
        var value = descriptor.RootElement;
        Assert.Equal(41, value.EnumerateObject().Count());
        Assert.Equal(new[] { "ok", "descriptor", "job_remaining_seconds" },
            ready.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.True(ready.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(value.GetRawText(), ready.RootElement.GetProperty("descriptor").GetRawText());
        Assert.Equal("evidence-worker-linux-v1", value.GetProperty("schema").GetString());
        Assert.Equal("csharp/4523b7bf72a34fc7a36786857556e574", value.GetProperty("run_id").GetString());
        Assert.Equal("appsurface-evidence-worker-4523b7bf72a34fc7a36786857556e574.service", value.GetProperty("unit").GetString());
        Assert.Equal("/system.slice/" + value.GetProperty("unit").GetString(), value.GetProperty("cgroup").GetString());
        Assert.Equal("observation", value.GetProperty("mode").GetString());
        Assert.Equal("github-actions", value.GetProperty("provider").GetString());
        Assert.Equal("linux-x64", value.GetProperty("platform").GetString());
        Assert.Equal("", value.GetProperty("proof_digest").GetString());
        Assert.Equal(Layout.DescriptorPath, value.GetProperty("descriptor_path").GetString());
        Assert.Equal(Layout.ControlSocket, value.GetProperty("socket_path").GetString());
        Assert.Equal(Layout.OutputParent, value.GetProperty("output_parent").GetString());
        Assert.Equal("evidence", value.GetProperty("output_slot").GetString());
        Assert.Equal(Layout.RawResultsRoot, value.GetProperty("test_output_root").GetString());
        Assert.Equal("/opt/runtime/dotnet", value.GetProperty("dotnet_path").GetString());
        Assert.Equal(83ul, value.GetProperty("output_parent_identity").GetProperty("inode").GetUInt64());
        Assert.Equal(65010u, value.GetProperty("output_parent_identity").GetProperty("uid").GetUInt32());
        foreach (var name in new[] { "diff_file", "diff_sha256", "solution" })
            Assert.Equal(JsonValueKind.Null, value.GetProperty(name).ValueKind);
        Assert.False(value.TryGetProperty("application", out _));
        Assert.False(value.TryGetProperty("argv", out _));
        Assert.False(value.TryGetProperty("environment", out _));
    }

    [Fact]
    public void OptionalBindingsAndImmutableListsAreCopiedIntoWireData()
    {
        var input = Valid();
        var paths = new[] { "src/値.cs" };
        input["paths"] = paths;
        input["diff_file"] = "/opt/tool/changes.diff";
        input["diff_sha256"] = new string('c', 64);
        input["solution"] = "/work/subject/App.sln";
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(input);
        var request = EvidenceSupervisorRequest.Parse(requestBytes);
        paths[0] = "changed";
        Array.Fill(requestBytes, (byte)0);
        var data = Make(request);
        using var document = JsonDocument.Parse(data.DescriptorBytes);
        var value = document.RootElement;
        Assert.Equal("src/値.cs", Assert.Single(value.GetProperty("paths").EnumerateArray()).GetString());
        Assert.Equal("profile-1", Assert.Single(value.GetProperty("observation_profile_ids").EnumerateArray()).GetString());
        Assert.Equal("producer-1", Assert.Single(value.GetProperty("observation_producer_ids").EnumerateArray()).GetString());
        Assert.Equal("/opt/tool/changes.diff", value.GetProperty("diff_file").GetString());
        Assert.Equal(new string('c', 64), value.GetProperty("diff_sha256").GetString());
        Assert.Equal("/work/subject/App.sln", value.GetProperty("solution").GetString());
    }

    [Fact]
    public void ReturnedByteArraysNeverExposeTheRetainedDescriptorOrWrapper()
    {
        var data = Make();
        var descriptor = data.DescriptorBytes;
        var ready = data.ReadyBytes;
        var originalDescriptor = descriptor.ToArray();
        var originalReady = ready.ToArray();
        Array.Fill(descriptor, (byte)0); Array.Fill(ready, (byte)0);
        Assert.Equal(originalDescriptor, data.DescriptorBytes);
        Assert.Equal(originalReady, data.ReadyBytes);
        Assert.NotSame(data.DescriptorBytes, data.DescriptorBytes);
        Assert.NotSame(data.ReadyBytes, data.ReadyBytes);
        Assert.Equal(originalDescriptor.Length, data.DescriptorLength);
        Assert.Equal(originalReady.Length, data.ReadyLength);
    }

    [Theory]
    [InlineData(0, 456)]
    [InlineData(-1, 456)]
    [InlineData(123, 0)]
    [InlineData(123, -1)]
    [InlineData(123, 123)]
    public void InvalidOrAliasedProcessNumbersRejectAsData(int worker, int broker) =>
        Reject(() => Make(workerPid: worker, brokerPid: broker));

    [Fact]
    public void PositivePidBoundariesAreOnlyDataAndNeverCheckTheOperatingSystem()
    {
        var data = Make(workerPid: 1, brokerPid: int.MaxValue);
        using var document = JsonDocument.Parse(data.DescriptorBytes);
        Assert.Equal(1, document.RootElement.GetProperty("worker_pid").GetInt32());
        Assert.Equal(int.MaxValue, document.RootElement.GetProperty("broker_pid").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("private-canary")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag")]
    public void BothDigestSpellingsRejectInvalidNeighborsWithoutEcho(string hash)
    {
        Reject(() => Make(entry: hash));
        Reject(() => Make(policy: hash));
    }

    [Fact]
    public void NullInputsAndEmptyGenerationRejectBeforeSerialization()
    {
        var request = Parse(Valid());
        Reject(() => EvidenceWorkerDescriptorData.Create(null!, Generation, 123, 456, Accounts, Layout,
            0, 27, 83, 65010, 65011, EntryHash, PolicyHash, TimeSpan.FromSeconds(60)));
        Reject(() => EvidenceWorkerDescriptorData.Create(request, Generation, 123, 456, null!, Layout,
            0, 27, 83, 65010, 65011, EntryHash, PolicyHash, TimeSpan.FromSeconds(60)));
        Reject(() => EvidenceWorkerDescriptorData.Create(request, Generation, 123, 456, Accounts, null!,
            0, 27, 83, 65010, 65011, EntryHash, PolicyHash, TimeSpan.FromSeconds(60)));
        Reject(() => Make(generation: Guid.Empty));
        Reject(() => EvidenceWorkerDescriptorData.Create(request, Generation, 123, 456, Accounts, Layout,
            0, 27, 83, 65010, 65011, null!, PolicyHash, TimeSpan.FromSeconds(60)));
        Reject(() => EvidenceWorkerDescriptorData.Create(request, Generation, 123, 456, Accounts, Layout,
            0, 27, 83, 65010, 65011, EntryHash, null!, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void GenerationAndAllAccountRolesMustMatchTheSelectedLayout()
    {
        Reject(() => Make(generation: Guid.Parse("9a01aab5-ea89-4c88-a8c4-c34a64947b54")));
        foreach (var changed in new[]
        {
            Accounts with { WorkerUid = 65110 }, Accounts with { WorkerGid = 65111 },
            Accounts with { SubjectUid = 65112 }, Accounts with { SubjectGid = 65113 },
            Accounts with { ResultsGid = 65114 }, Accounts with { WorkerUid = 0 },
            Accounts with { ResultsGid = uint.MaxValue }, Accounts with { SubjectUid = Accounts.WorkerUid },
        }) Reject(() => Make(accounts: changed));
        var alternate = Accounts with { SubjectUid = 65112 };
        var valid = Make(accounts: alternate, layout: LinuxWorkspaceLayout.Create(Generation, alternate));
        using var document = JsonDocument.Parse(valid.DescriptorBytes);
        Assert.Equal(65112u, document.RootElement.GetProperty("subject_uid").GetUInt32());
    }

    [Fact]
    public void OutputIdentityRequiresNonzeroInodeAndTheExactWorkerOwnerAndGroup()
    {
        Reject(() => Make(inode: 0));
        Reject(() => Make(uid: 0));
        Reject(() => Make(uid: 65012));
        Reject(() => Make(gid: 0));
        Reject(() => Make(gid: 65013));
        Assert.True(Make(inode: ulong.MaxValue).ReadyLength > 0); // Numeric range is not kernel authentication.
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(1234567L)]
    [InlineData(36000000000L)]
    public void FrozenPositiveRemainderIsSerializedWithoutConsultingOrResettingAClock(long ticks)
    {
        var remaining = TimeSpan.FromTicks(ticks);
        var data = Make(remaining: remaining);
        using var document = JsonDocument.Parse(data.ReadyBytes);
        Assert.Equal(remaining.TotalSeconds, document.RootElement.GetProperty("job_remaining_seconds").GetDouble());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(36000000001L)]
    [InlineData(long.MaxValue)]
    public void ExpiredNegativeAndOversizedRemaindersReject(long ticks) =>
        Reject(() => Make(remaining: TimeSpan.FromTicks(ticks)));

    [Fact]
    public void CompleteReadyBoundaryReservesLfAndOneMoreJsonByteRejectsEvenWhenDescriptorFits()
    {
        var paths = Enumerable.Range(0, 16).Select(index => $"p{index:D2}/" + new string('a', 3500)).ToArray();
        var initial = Make(Parse(Valid(paths)));
        AddPadding(paths, EvidenceWorkerDescriptorData.MaximumReadyJsonBytes - initial.ReadyLength);
        var exact = Make(Parse(Valid(paths)));
        Assert.Equal(EvidenceWorkerDescriptorData.MaximumReadyJsonBytes, exact.ReadyLength);
        Assert.Equal(EvidenceWorkerDescriptorData.MaximumJsonBytes, exact.ReadyLength + 1);
        Assert.InRange(exact.DescriptorLength + 1, 1, EvidenceWorkerDescriptorData.MaximumJsonBytes);
        AddPadding(paths, 1);
        var stillValidRequest = Parse(Valid(paths));
        Reject(() => Make(stillValidRequest));
    }

    [Fact]
    public void StandaloneDescriptorOverflowRejectsAnOtherwiseValidBoundedRequest()
    {
        var paths = Enumerable.Range(0, 16).Select(index => $"p{index:D2}/" + new string('a', 3500)).ToArray();
        var initial = Make(Parse(Valid(paths)));
        AddPadding(paths, EvidenceWorkerDescriptorData.MaximumJsonBytes - initial.DescriptorLength + 1);
        var stillValidRequest = Parse(Valid(paths));
        Reject(() => Make(stillValidRequest));
    }

    [Fact]
    public void EscapedUnicodeWireBytesAreCountedRatherThanDecodedCharacterLength()
    {
        var paths = Enumerable.Range(0, 16).Select(index => $"p{index:D2}/" + new string('値', 1000)).ToArray();
        Assert.True(Encoding.UTF8.GetByteCount(paths[0]) > paths[0].Length);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Valid(paths), new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        Assert.InRange(bytes.Length, 1, EvidenceSupervisorRequest.MaximumRequestBytes);
        var request = EvidenceSupervisorRequest.Parse(bytes);
        Assert.Equal(16, request.Paths.Count);
        Reject(() => Make(request)); // The bounded writer's escaped JSON is over 64 KiB.
        var neighbor = Make(Parse(Valid(new[] { paths[0] })));
        using var document = JsonDocument.Parse(neighbor.DescriptorBytes);
        Assert.Equal(paths[0], Assert.Single(document.RootElement.GetProperty("paths").EnumerateArray()).GetString());
    }

    [Fact]
    public void DeploymentRootsCannotOverlapFixedControlOrOutputRoots()
    {
        var input = Valid();
        input["tool_root"] = Layout.Root;
        input["entry_path"] = Layout.Root + "/Worker.dll";
        input["policy_file"] = Layout.Root + "/policy.json";
        Reject(() => Make(Parse(input)));
        input = Valid();
        input["subject_root"] = Layout.Root;
        Reject(() => Make(Parse(input)));
    }

    private static EvidenceWorkerDescriptorData Make(EvidenceSupervisorRequest? request = null,
        Guid? generation = null, int workerPid = 123, int brokerPid = 456,
        LinuxRunAccountSnapshot? accounts = null, LinuxWorkspaceLayout? layout = null,
        ulong inode = 83, uint uid = 65010, uint gid = 65011, string? entry = null, string? policy = null,
        TimeSpan? remaining = null) =>
        EvidenceWorkerDescriptorData.Create(request ?? Parse(Valid()), generation ?? Generation, workerPid, brokerPid,
            accounts ?? Accounts, layout ?? Layout, 0, 27, inode, uid, gid, entry ?? EntryHash, policy ?? PolicyHash,
            remaining ?? TimeSpan.FromSeconds(60));

    private static EvidenceSupervisorRequest Parse(Dictionary<string, object?> fields) =>
        EvidenceSupervisorRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(fields));

    private static Dictionary<string, object?> Valid(string[]? paths = null) => new()
    {
        ["schema"] = "evidence-supervisor-linux-v1", ["mode"] = "observation",
        ["tool_root"] = "/opt/tool", ["runtime_root"] = "/opt/runtime", ["runtime_host"] = "/opt/runtime/dotnet",
        ["entry_path"] = "/opt/tool/Worker.dll", ["policy_file"] = "/opt/tool/policy.json",
        ["subject_root"] = "/work/subject", ["base_revision"] = new string('a', 40),
        ["subject_revision"] = new string('b', 40), ["workflow_identity"] = "workflow/ref",
        ["paths"] = paths ?? new[] { "src/Item.cs" }, ["observation_profile_ids"] = new[] { "profile-1" },
        ["observation_producer_ids"] = new[] { "producer-1" }, ["job_deadline_utc"] = "2001-01-01T00:00:00Z",
        ["admission_seconds"] = 30, ["start_seconds"] = 120, ["collection_seconds"] = 60,
        ["cleanup_seconds"] = 600, ["stopping_seconds"] = 30,
    };

    private static void AddPadding(string[] paths, int count)
    {
        Assert.True(count >= 0);
        for (var index = 0; index < paths.Length && count > 0; index++)
        {
            var extra = Math.Min(4095 - paths[index].Length, count);
            paths[index] += new string('a', extra);
            count -= extra;
        }
        Assert.Equal(0, count);
    }

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-canary", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/run/", error.Message, StringComparison.Ordinal);
    }
}
