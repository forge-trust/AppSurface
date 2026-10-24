using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached failed-settlement data controls; no live process, lease, native owner or authority is fabricated.</summary>
public sealed class LinuxFailedSettlementObservationTests
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed record Data(Guid Generation, LinuxProcessSample Identity, LinuxFailedSettlementState State,
        SupervisionPendingStartSnapshot Pending, LinuxUnitProperties? Terminal, LinuxCgroupSample? Group,
        SupervisionOutputReceipt Output);

    private static Data Valid()
    {
        var generation = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, generation).Value;
        var cgroup = "/system.slice/" + unit;
        var identity = new LinuxProcessSample(new(123, 42, 'S'), new(999, 999, 999, 999),
            new(987, 987, 987, 987), cgroup);
        var terminal = new LinuxUnitProperties(unit, "loaded", "failed", "failed", cgroup,
            0, 123, 2, 9, "999", "987", "exec", "control-group", true, 60_000_000, 10_000_000);
        var prefix = ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("private-stream-canary"));
        var stdout = new SupervisionOutputStreamReceipt(0, ImmutableArray<byte>.Empty, true, SupervisionOutputFailure.None);
        var stderr = new SupervisionOutputStreamReceipt(prefix.Length, prefix, true, SupervisionOutputFailure.None);
        return new(generation, identity, new(true, true, true, false, LinuxFailedMonitorState.Faulted, true),
            new(true, true, true, true, true, SupervisionPendingStartFailure.StopFailed), terminal,
            new(false, null, null, null, null, null),
            new(stdout, stderr, prefix.Length, 8192, SupervisionOutputFailure.None, false, false));
    }

    private static LinuxFailedSettlementObservation Encode(Data value, CancellationToken token = default) =>
        LinuxFailedSettlementObservation.CreateDetached(value.Generation, value.Identity, Digest, value.State,
            value.Pending, value.Terminal, value.Group, value.Output, token);

    private static LinuxFailedSettlementObservation EncodeParentReplacement(Data value,
        CancellationToken token = default) =>
        LinuxFailedSettlementObservation.CreateDetached(value.Generation, value.Identity, Digest, value.State,
            value.Pending, value.Terminal, value.Group, value.Output, token,
            LinuxFailedSettlementScenario.ParentReplacement);

    /// <summary>The rejected-frame family changes only a closed label; neither existing family changes bytes.</summary>
    [Fact]
    public void ParentReplacementChangesOnlySchemaAndPreservesExistingFamilies()
    {
        var data = Valid();
        var original = Encoding.UTF8.GetString(Encode(data).Bytes);
        var replacement = Encoding.UTF8.GetString(EncodeParentReplacement(data).Bytes);
        Assert.Equal(original.Replace("issue779-n11-original-failed-settlement-v1",
            "issue779-n07-original-failed-settlement-v1", StringComparison.Ordinal), replacement);
        using var json = JsonDocument.Parse(replacement);
        Assert.False(json.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("stop_timing", out _));
    }

    /// <summary>Failed or settled lifetime metadata stays literal, without creating a positive settlement.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ParentReplacementDoesNotInventLifetimeFlags(bool failed, bool settled)
    {
        var data = Valid();
        using var json = JsonDocument.Parse(EncodeParentReplacement(data with
        {
            State = data.State with { LifetimeFailed = failed, PhysicallySettled = settled },
        }).Bytes);
        Assert.Equal(failed, json.RootElement.GetProperty("lifetime").GetProperty("failed").GetBoolean());
        Assert.Equal(settled, json.RootElement.GetProperty("lifetime").GetProperty("physically_settled").GetBoolean());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
    }

    /// <summary>Exact rejected bytes are private diagnostic data, without accepting any N07 prefix.</summary>
    [Fact]
    public void RejectedFirstFrameExportsExactPrivateRawAndHashes()
    {
        var data = Valid();
        var bytes = Encoding.UTF8.GetBytes("not-an-n07-precleanup-frame\nprivate-rejected-frame-canary");
        var stderr = data.Output.Stderr with { Prefix = ImmutableArray.CreateRange(bytes), ReceivedBytes = bytes.Length };
        var observation = EncodeParentReplacement(data with
        {
            Output = data.Output with { Stderr = stderr, ReceivedBytes = bytes.Length },
        });
        var encoded = observation.Bytes;
        using var json = JsonDocument.Parse(encoded);
        var pumps = json.RootElement.GetProperty("pumps");
        Assert.True(pumps.GetProperty("export_complete").GetBoolean());
        Assert.Equal(bytes, Convert.FromBase64String(pumps.GetProperty("stderr").GetProperty("raw_base64").GetString()!));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            pumps.GetProperty("stderr").GetProperty("sha256").GetString());
        Assert.DoesNotContain("private-rejected-frame-canary", Encoding.UTF8.GetString(encoded));
        Array.Fill(observation.Bytes, (byte)0);
        Assert.Equal(encoded, observation.Bytes);
        Assert.InRange(encoded.Length, 1, LinuxFailedSettlementObservation.MaximumJsonBytes);
    }

    /// <summary>Incomplete or oversized rejected streams omit both raw streams and hashes, retaining honest counters.</summary>
    [Theory]
    [InlineData("stdout-eof")]
    [InlineData("stderr-eof")]
    [InlineData("read-failed")]
    [InlineData("cancelled")]
    [InlineData("signal-failed")]
    [InlineData("quota")]
    [InlineData("stdout-truncated")]
    [InlineData("stderr-truncated")]
    [InlineData("oversized")]
    public void ParentReplacementIncompleteOrOversizedDataHasNoRawExport(string condition)
    {
        var data = Valid();
        var output = data.Output;
        output = condition switch
        {
            "stdout-eof" => output with { Stdout = output.Stdout with { EndOfStream = false } },
            "stderr-eof" => output with { Stderr = output.Stderr with { EndOfStream = false } },
            "read-failed" => output with
            {
                Failure = SupervisionOutputFailure.ReadFailed,
                Stderr = output.Stderr with { Failure = SupervisionOutputFailure.ReadFailed }
            },
            "cancelled" => output with { Failure = SupervisionOutputFailure.Cancelled },
            "signal-failed" => output with { StopSignalFailed = true },
            "quota" => output with { QuotaExceeded = true, Failure = SupervisionOutputFailure.QuotaExceeded },
            "stdout-truncated" => output with
            {
                Stdout = output.Stdout with { ReceivedBytes = 1 },
                ReceivedBytes = output.ReceivedBytes + 1
            },
            "stderr-truncated" => output with
            {
                Stderr = output.Stderr with { ReceivedBytes = output.Stderr.ReceivedBytes + 1 },
                ReceivedBytes = output.ReceivedBytes + 1
            },
            "oversized" => output with
            {
                Stderr = output.Stderr with
                {
                    Prefix = ImmutableArray.CreateRange(new byte[LinuxFailedSettlementObservation.MaximumRawBytes + 1]),
                    ReceivedBytes = LinuxFailedSettlementObservation.MaximumRawBytes + 1,
                },
                ReceivedBytes = LinuxFailedSettlementObservation.MaximumRawBytes + 1,
                ReceivedByteLimit = LinuxFailedSettlementObservation.MaximumRawBytes + 1,
            },
            _ => throw new InvalidOperationException(),
        };
        using var json = JsonDocument.Parse(EncodeParentReplacement(data with { Output = output }).Bytes);
        var pumps = json.RootElement.GetProperty("pumps");
        Assert.False(pumps.GetProperty("export_complete").GetBoolean());
        Assert.Equal(output.ReceivedBytes, pumps.GetProperty("received_bytes").GetInt64());
        Assert.Equal(output.DiscardedBytes, pumps.GetProperty("discarded_bytes").GetInt64());
        Assert.Equal(output.Failure.ToString(), pumps.GetProperty("failure").GetString());
        foreach (var name in new[] { "stdout", "stderr" })
        {
            Assert.Equal(JsonValueKind.Null, pumps.GetProperty(name).GetProperty("raw_base64").ValueKind);
            Assert.Equal(JsonValueKind.Null, pumps.GetProperty(name).GetProperty("sha256").ValueKind);
        }
    }

    /// <summary>The failure family still rejects every unavailable original join.</summary>
    [Theory]
    [InlineData("startup")]
    [InlineData("stop")]
    [InlineData("pending-start")]
    [InlineData("pending-stop")]
    [InlineData("pending-open")]
    [InlineData("pending-unreserved")]
    public void ParentReplacementMissingOriginalJoinsReject(string field)
    {
        var data = Valid();
        data = field switch
        {
            "startup" => data with { State = data.State with { StartupJoined = false } },
            "stop" => data with { State = data.State with { StopJoined = false } },
            "pending-start" => data with { Pending = data.Pending with { StartJoined = false } },
            "pending-stop" => data with { Pending = data.Pending with { StopJoined = false } },
            "pending-open" => data with { Pending = data.Pending with { Closed = false } },
            "pending-unreserved" => data with { Pending = data.Pending with { StartReserved = false } },
            _ => throw new InvalidOperationException(),
        };
        var error = Assert.Throws<InvalidOperationException>(() => EncodeParentReplacement(data));
        Assert.Equal("ASEVD410: Original failed-settlement observation data rejected.", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Missing final samples stay null, and cancellation or an N11-only timing input still rejects.</summary>
    [Fact]
    public void ParentReplacementMissingSamplesCancellationAndForeignTimingStayClosed()
    {
        var data = Valid();
        using var json = JsonDocument.Parse(EncodeParentReplacement(data with
        {
            Terminal = null,
            Group = null,
            State = data.State with { CgroupAfterPumps = false },
        }).Bytes);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("terminal").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("cgroup").ValueKind);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => EncodeParentReplacement(data, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        var timing = new LinuxWorkerStopTimingSnapshot(20_000, 5000, 15_000_000,
            0, 1, 2, null, null, 3, 4, 5, 6, null, 7);
        Assert.Throws<InvalidOperationException>(() => LinuxFailedSettlementObservation.CreateDetached(
            data.Generation, data.Identity, Digest, data.State, data.Pending, data.Terminal, data.Group,
            data.Output, scenario: LinuxFailedSettlementScenario.ParentReplacement, stopTiming: timing));
    }

    /// <summary>Adding the parent-replacement family does not open arbitrary private diagnostic selectors.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(999)]
    public void ExpandedFamilyStillRejectsEveryUnrecognizedScenario(int scenario)
    {
        var data = Valid();
        var error = Assert.Throws<InvalidOperationException>(() =>
            LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest, data.State,
                data.Pending, data.Terminal, data.Group, data.Output,
                scenario: (LinuxFailedSettlementScenario)scenario));
        Assert.Equal("ASEVD410: Original failed-settlement observation data rejected.", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Original N11 account numbers add exactly five fields without changing any earlier observation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void N11AccountIdsAddOnlyTheExactOriginalFiveNumbers(bool includeTiming)
    {
        var data = Valid();
        var accounts = new LinuxRunAccountSnapshot(999, 987, 1000, 988, 989);
        var timing = includeTiming ? new LinuxWorkerStopTimingSnapshot(20_000, 5000, 15_000_000,
            0, 1, 2, null, null, 3, 4, 5, 6, null, 7) : null;
        var original = LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
            data.State, data.Pending, data.Terminal, data.Group, data.Output, stopTiming: timing);
        var observation = LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
            data.State, data.Pending, data.Terminal, data.Group, data.Output,
            stopTiming: timing, accountIds: accounts);
        using var before = JsonDocument.Parse(original.Bytes);
        using var after = JsonDocument.Parse(observation.Bytes);
        foreach (var property in before.RootElement.EnumerateObject())
            Assert.Equal(property.Value.GetRawText(), after.RootElement.GetProperty(property.Name).GetRawText());
        Assert.Equal(before.RootElement.EnumerateObject().Count() + 1, after.RootElement.EnumerateObject().Count());
        var ids = after.RootElement.GetProperty("account_ids");
        Assert.Equal(new[] { "worker_uid", "worker_gid", "subject_uid", "subject_gid", "results_gid" },
            ids.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal(new uint[] { 999, 987, 1000, 988, 989 },
            ids.EnumerateObject().Select(static property => property.Value.GetUInt32()).ToArray());
        Assert.False(after.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(after.RootElement.GetProperty("native_acceptance").GetBoolean());
        Assert.DoesNotContain("private-stream-canary", Encoding.UTF8.GetString(observation.Bytes));
        var saved = observation.Bytes;
        Array.Fill(observation.Bytes, (byte)0);
        Assert.Equal(saved, observation.Bytes);
    }

    /// <summary>Null account data preserves each old family byte-for-byte, including the existing timing append.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AbsentAccountIdsPreserveExistingFamilyBytes(int scenarioValue)
    {
        var scenario = (LinuxFailedSettlementScenario)scenarioValue;
        var data = Valid();
        var original = LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
            data.State, data.Pending, data.Terminal, data.Group, data.Output, scenario: scenario).Bytes;
        var explicitNull = LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
            data.State, data.Pending, data.Terminal, data.Group, data.Output,
            scenario: scenario, accountIds: null).Bytes;
        Assert.Equal(original, explicitNull);
        using var json = JsonDocument.Parse(explicitNull);
        Assert.False(json.RootElement.TryGetProperty("account_ids", out _));
        if (scenario == LinuxFailedSettlementScenario.WorkerStall)
        {
            var timing = new LinuxWorkerStopTimingSnapshot(20_000, 5000, 15_000_000,
                0, 1, 2, null, null, 3, 4, 5, 6, null, 7);
            var timed = LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
                data.State, data.Pending, data.Terminal, data.Group, data.Output, stopTiming: timing).Bytes;
            var timedNull = LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
                data.State, data.Pending, data.Terminal, data.Group, data.Output,
                stopTiming: timing, accountIds: null).Bytes;
            Assert.Equal(timed, timedNull);
        }
    }

    /// <summary>The N11-only account projection is rejected for both other diagnostic families.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ForeignFamilyAccountIdsRejectWithoutSuppliedData(int scenarioValue)
    {
        var scenario = (LinuxFailedSettlementScenario)scenarioValue;
        var data = Valid();
        var accounts = new LinuxRunAccountSnapshot(999, 987, 1000, 988, 989);
        var error = Assert.Throws<InvalidOperationException>(() =>
            LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
                data.State, data.Pending, data.Terminal, data.Group, data.Output,
                scenario: scenario, accountIds: accounts));
        Assert.Equal("ASEVD410: Original failed-settlement observation data rejected.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-stream-canary", error.Message);
    }

    /// <summary>One changed account field rejects invalid numbers, worker mismatches, and within-namespace collisions.</summary>
    [Theory]
    [InlineData("worker-uid-zero")]
    [InlineData("worker-uid-max")]
    [InlineData("worker-gid-zero")]
    [InlineData("worker-gid-max")]
    [InlineData("subject-uid-zero")]
    [InlineData("subject-uid-max")]
    [InlineData("subject-gid-zero")]
    [InlineData("subject-gid-max")]
    [InlineData("results-gid-zero")]
    [InlineData("results-gid-max")]
    [InlineData("worker-uid-mismatch")]
    [InlineData("worker-gid-mismatch")]
    [InlineData("same-uids")]
    [InlineData("worker-subject-gids")]
    [InlineData("worker-results-gids")]
    [InlineData("subject-results-gids")]
    public void MalformedN11AccountIdsRejectFromValidAdjacentData(string condition)
    {
        var data = Valid();
        var accounts = new LinuxRunAccountSnapshot(999, 987, 1000, 988, 989);
        _ = LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
            data.State, data.Pending, data.Terminal, data.Group, data.Output, accountIds: accounts);
        accounts = condition switch
        {
            "worker-uid-zero" => accounts with { WorkerUid = 0 },
            "worker-uid-max" => accounts with { WorkerUid = uint.MaxValue },
            "worker-gid-zero" => accounts with { WorkerGid = 0 },
            "worker-gid-max" => accounts with { WorkerGid = uint.MaxValue },
            "subject-uid-zero" => accounts with { SubjectUid = 0 },
            "subject-uid-max" => accounts with { SubjectUid = uint.MaxValue },
            "subject-gid-zero" => accounts with { SubjectGid = 0 },
            "subject-gid-max" => accounts with { SubjectGid = uint.MaxValue },
            "results-gid-zero" => accounts with { ResultsGid = 0 },
            "results-gid-max" => accounts with { ResultsGid = uint.MaxValue },
            "worker-uid-mismatch" => accounts with { WorkerUid = 998 },
            "worker-gid-mismatch" => accounts with { WorkerGid = 986 },
            "same-uids" => accounts with { SubjectUid = accounts.WorkerUid },
            "worker-subject-gids" => accounts with { SubjectGid = accounts.WorkerGid },
            "worker-results-gids" => accounts with { ResultsGid = accounts.WorkerGid },
            "subject-results-gids" => accounts with { ResultsGid = accounts.SubjectGid },
            _ => throw new InvalidOperationException(),
        };
        var error = Assert.Throws<InvalidOperationException>(() =>
            LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
                data.State, data.Pending, data.Terminal, data.Group, data.Output, accountIds: accounts));
        Assert.Equal("ASEVD410: Original failed-settlement observation data rejected.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-stream-canary", error.Message);
    }

    /// <summary>UID and GID namespaces may share numbers while each namespace retains its required distinctions.</summary>
    [Fact]
    public void CrossNamespaceNumericEqualityIsNotAnAccountCollision()
    {
        var data = Valid();
        var accounts = new LinuxRunAccountSnapshot(999, 987, 987, 999, 1001);
        var observation = LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
            data.State, data.Pending, data.Terminal, data.Group, data.Output, accountIds: accounts);
        using var json = JsonDocument.Parse(observation.Bytes);
        var ids = json.RootElement.GetProperty("account_ids");
        Assert.Equal(ids.GetProperty("worker_uid").GetUInt32(), ids.GetProperty("subject_gid").GetUInt32());
        Assert.Equal(ids.GetProperty("worker_gid").GetUInt32(), ids.GetProperty("subject_uid").GetUInt32());
    }

    private static void Reject(Data data)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Encode(data));
        Assert.Equal("ASEVD410: Original failed-settlement observation data rejected.", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Preserves sticky failure, false settlement and actual signal terminal data without acceptance.</summary>
    [Fact]
    public void FailedSettlementRetainsFailureAndSignalWithoutAcceptance()
    {
        using var json = JsonDocument.Parse(Encode(Valid()).Bytes);
        var root = json.RootElement;
        Assert.True(root.GetProperty("lifetime").GetProperty("failed").GetBoolean());
        Assert.False(root.GetProperty("lifetime").GetProperty("physically_settled").GetBoolean());
        Assert.Equal("Faulted", root.GetProperty("monitor").GetString());
        Assert.Equal("StopFailed", root.GetProperty("pending_start").GetProperty("first_failure").GetString());
        Assert.Equal(2, root.GetProperty("terminal").GetProperty("exec_main_code").GetInt32());
        Assert.Equal(9, root.GetProperty("terminal").GetProperty("exec_main_status").GetInt32());
        Assert.True(root.GetProperty("observation_only").GetBoolean());
        Assert.False(root.GetProperty("native_authority").GetBoolean());
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());
    }

    /// <summary>The cancellation fallback preserves joined fault data and leaves settlement false.</summary>
    [Fact]
    public void CancellationFamilyChangesOnlyTheClosedSchemaLabel()
    {
        var data = Valid();
        var ordinary = Encode(data).Bytes;
        var cancellation = LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
            data.State, data.Pending, data.Terminal, data.Group, data.Output,
            scenario: LinuxFailedSettlementScenario.OriginalCancellation).Bytes;
        using var json = JsonDocument.Parse(cancellation);
        Assert.Equal("issue779-cancellation-original-failed-settlement-v1",
            json.RootElement.GetProperty("schema").GetString());
        Assert.Equal("Faulted", json.RootElement.GetProperty("monitor").GetString());
        Assert.False(json.RootElement.GetProperty("lifetime").GetProperty("physically_settled").GetBoolean());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
        Assert.Equal(Encoding.UTF8.GetString(ordinary).Replace("issue779-n11-original-failed-settlement-v1",
            "issue779-cancellation-original-failed-settlement-v1", StringComparison.Ordinal),
            Encoding.UTF8.GetString(cancellation));
    }

    /// <summary>Unrecognized family metadata rejects before supplied text or bytes can enter a diagnostic.</summary>
    [Fact]
    public void UnknownDiagnosticFamilyRejects()
    {
        var data = Valid();
        var error = Assert.Throws<InvalidOperationException>(() =>
            LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
                data.State, data.Pending, data.Terminal, data.Group, data.Output,
                scenario: (LinuxFailedSettlementScenario)999));
        Assert.Equal("ASEVD410: Original failed-settlement observation data rejected.", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Optional original stop timing stays detached numeric data and cannot change any failure fact.</summary>
    [Fact]
    public void StopTimingAddsOnlyExplicitN11DiagnosticField()
    {
        var data = Valid();
        var timing = new LinuxWorkerStopTimingSnapshot(20_000, 5000, 15_000_000,
            0, 1, 2, null, null, 3, 4, 5, 6, null, 7);
        var observation = LinuxFailedSettlementObservation.CreateDetached(data.Generation, data.Identity, Digest,
            data.State, data.Pending, data.Terminal, data.Group, data.Output, stopTiming: timing);
        using var json = JsonDocument.Parse(observation.Bytes);
        using var original = JsonDocument.Parse(Encode(data).Bytes);
        foreach (var property in original.RootElement.EnumerateObject())
            Assert.Equal(property.Value.GetRawText(), json.RootElement.GetProperty(property.Name).GetRawText());
        Assert.Equal(7, json.RootElement.GetProperty("stop_timing").GetProperty("stop_joined_ms").GetInt64());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
        Assert.Throws<InvalidOperationException>(() => LinuxFailedSettlementObservation.CreateDetached(
            data.Generation, data.Identity, Digest, data.State, data.Pending, data.Terminal, data.Group,
            data.Output, scenario: LinuxFailedSettlementScenario.OriginalCancellation, stopTiming: timing));
    }

    /// <summary>Exports exact full raw bytes only when both streams are clean, complete and within the bound.</summary>
    [Fact]
    public void CompleteStreamsExportExactBytesAndReturnDefensiveCopies()
    {
        var data = Valid();
        var observation = Encode(data);
        var before = observation.Bytes;
        using var json = JsonDocument.Parse(before);
        var pumps = json.RootElement.GetProperty("pumps");
        Assert.True(pumps.GetProperty("export_complete").GetBoolean());
        Assert.Equal(data.Output.Stderr.Prefix.ToArray(),
            Convert.FromBase64String(pumps.GetProperty("stderr").GetProperty("raw_base64").GetString()!));
        Assert.DoesNotContain("private-stream-canary", Encoding.UTF8.GetString(before));
        Array.Fill(observation.Bytes, (byte)0);
        Assert.Equal(before, observation.Bytes);
        Assert.InRange(before.Length, 1, LinuxFailedSettlementObservation.MaximumJsonBytes);
    }

    /// <summary>Incomplete or failed pump conditions retain metadata but omit every raw and digest field.</summary>
    [Theory]
    [InlineData("no-eof")]
    [InlineData("read-failed")]
    [InlineData("discarded")]
    [InlineData("cancelled")]
    [InlineData("signal-failed")]
    [InlineData("quota")]
    public void IncompletePumpDataCannotBecomeFullExport(string condition)
    {
        var data = Valid();
        var output = condition switch
        {
            "no-eof" => data.Output with { Stderr = data.Output.Stderr with { EndOfStream = false } },
            "read-failed" => data.Output with
            {
                Stderr = data.Output.Stderr with { Failure = SupervisionOutputFailure.ReadFailed },
                Failure = SupervisionOutputFailure.ReadFailed
            },
            "discarded" => data.Output with
            {
                Stderr = data.Output.Stderr with { ReceivedBytes = data.Output.Stderr.ReceivedBytes + 1 },
                ReceivedBytes = data.Output.ReceivedBytes + 1
            },
            "cancelled" => data.Output with { Failure = SupervisionOutputFailure.Cancelled },
            "signal-failed" => data.Output with { StopSignalFailed = true },
            "quota" => data.Output with { QuotaExceeded = true, Failure = SupervisionOutputFailure.QuotaExceeded },
            _ => throw new InvalidOperationException(),
        };
        using var json = JsonDocument.Parse(Encode(data with { Output = output }).Bytes);
        var pumps = json.RootElement.GetProperty("pumps");
        Assert.False(pumps.GetProperty("export_complete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pumps.GetProperty("stderr").GetProperty("raw_base64").ValueKind);
        Assert.Equal(JsonValueKind.Null, pumps.GetProperty("stderr").GetProperty("sha256").ValueKind);
        Assert.Equal(output.Stderr.EndOfStream, pumps.GetProperty("stderr").GetProperty("eof").GetBoolean());
        Assert.Equal(output.Stderr.ReceivedBytes, pumps.GetProperty("stderr").GetProperty("received_bytes").GetInt64());
    }

    /// <summary>Each omitted original join is rejected from the detached metadata seam.</summary>
    [Theory]
    [InlineData("startup")]
    [InlineData("stop")]
    [InlineData("pending-start")]
    [InlineData("pending-stop")]
    [InlineData("pending-open")]
    [InlineData("pending-unreserved")]
    public void MissingOriginalJoinsReject(string field)
    {
        var data = Valid();
        data = field switch
        {
            "startup" => data with { State = data.State with { StartupJoined = false } },
            "stop" => data with { State = data.State with { StopJoined = false } },
            "pending-start" => data with { Pending = data.Pending with { StartJoined = false } },
            "pending-stop" => data with { Pending = data.Pending with { StopJoined = false } },
            "pending-open" => data with { Pending = data.Pending with { Closed = false } },
            "pending-unreserved" => data with { Pending = data.Pending with { StartReserved = false } },
            _ => throw new InvalidOperationException(),
        };
        Reject(data);
    }

    /// <summary>Absent samples stay null and an actually observed nonterminal unit remains literal data.</summary>
    [Fact]
    public void MissingSamplesAndNonterminalUnitAreNotInvented()
    {
        var data = Valid();
        using var json = JsonDocument.Parse(Encode(data with
        {
            Terminal = null,
            Group = null,
            State = data.State with { CgroupAfterPumps = false },
        }).Bytes);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("terminal").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("cgroup").ValueKind);
        using var observed = JsonDocument.Parse(Encode(data with
        {
            Terminal = data.Terminal! with { ActiveState = "running", SubState = "running", ExecMainCode = 0 },
        }).Bytes);
        Assert.Equal(0, observed.RootElement.GetProperty("terminal").GetProperty("exec_main_code").GetInt32());
    }

    /// <summary>The raw export bound suppresses bytes and hashes without relaxing the collector's own quota.</summary>
    [Fact]
    public void CombinedRawBoundSuppressesExport()
    {
        var data = Valid();
        var prefix = ImmutableArray.CreateRange(new byte[LinuxFailedSettlementObservation.MaximumRawBytes + 1]);
        var stderr = data.Output.Stderr with { Prefix = prefix, ReceivedBytes = prefix.Length };
        var output = data.Output with
        {
            Stderr = stderr,
            ReceivedBytes = prefix.Length,
            ReceivedByteLimit = prefix.Length
        };
        using var json = JsonDocument.Parse(Encode(data with { Output = output }).Bytes);
        var pumps = json.RootElement.GetProperty("pumps");
        Assert.False(pumps.GetProperty("export_complete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pumps.GetProperty("stderr").GetProperty("raw_base64").ValueKind);
        Assert.Equal(JsonValueKind.Null, pumps.GetProperty("stderr").GetProperty("sha256").ValueKind);
    }

    /// <summary>Only actually completed original task states can be represented.</summary>
    [Fact]
    public void ClosedTaskStatesRejectPendingAndDoNotRetainExceptionText()
    {
        Assert.Equal(LinuxFailedMonitorState.Completed,
            LinuxFailedSettlementObservation.ClosedTaskState(Task.CompletedTask));
        var faulted = Task.FromException(new InvalidOperationException("exception-canary"));
        Assert.Equal(LinuxFailedMonitorState.Faulted, LinuxFailedSettlementObservation.ClosedTaskState(faulted));
        _ = faulted.Exception;
        Assert.Equal(LinuxFailedMonitorState.Cancelled,
            LinuxFailedSettlementObservation.ClosedTaskState(Task.FromCanceled(new CancellationToken(true))));
        var pending = new TaskCompletionSource();
        Assert.Throws<InvalidOperationException>(() => LinuxFailedSettlementObservation.ClosedTaskState(pending.Task));
        pending.SetResult();
    }

    /// <summary>Cancellation and malformed closed enums fail with fixed data-only errors.</summary>
    [Fact]
    public void CancellationPreventsEncoding()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = Assert.Throws<OperationCanceledException>(() => Encode(Valid(), cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
    }

    /// <summary>Unknown closed enums never enter the private schema.</summary>
    [Theory]
    [InlineData("monitor")]
    [InlineData("pending")]
    [InlineData("pump")]
    public void UnknownEnumsReject(string field)
    {
        var data = Valid();
        data = field switch
        {
            "monitor" => data with { State = data.State with { Monitor = (LinuxFailedMonitorState)999 } },
            "pending" => data with { Pending = data.Pending with { FirstFailure = (SupervisionPendingStartFailure)999 } },
            "pump" => data with { Output = data.Output with { Failure = (SupervisionOutputFailure)999 } },
            _ => throw new InvalidOperationException(),
        };
        Reject(data);
    }

    /// <summary>Digest metadata accepts only the fixed lowercase hexadecimal descriptor shape.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag")]
    public void MalformedDescriptorDigestRejectsFromValidNeighbor(string? digest)
    {
        var data = Valid();
        AssertDetached(data);
        var error = Assert.Throws<InvalidOperationException>(() => LinuxFailedSettlementObservation.CreateDetached(
            data.Generation, data.Identity, digest!, data.State, data.Pending, data.Terminal, data.Group, data.Output));
        Assert.Equal("ASEVD410: Original failed-settlement observation data rejected.", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Terminal code and status boundaries remain literal data while invalid neighbors stay closed.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 255)]
    public void TerminalBoundaryValuesRemainLiteralAndUnaccepted(int code, int status)
    {
        var data = Valid();
        AssertDetached(data);
        data = data with { Terminal = data.Terminal! with { ExecMainCode = code, ExecMainStatus = status } };
        using var json = JsonDocument.Parse(Encode(data).Bytes);
        Assert.Equal(code, json.RootElement.GetProperty("terminal").GetProperty("exec_main_code").GetInt32());
        Assert.Equal(status, json.RootElement.GetProperty("terminal").GetProperty("exec_main_status").GetInt32());
        Assert.False(json.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("pid")]
    [InlineData("code-negative")]
    [InlineData("code-seven")]
    [InlineData("status-negative")]
    [InlineData("status-256")]
    public void MalformedTerminalIdentityOrBoundsRejectFromValidNeighbor(string mutation)
    {
        var data = Valid();
        AssertDetached(data);
        data = mutation switch
        {
            "unit" => data with { Terminal = data.Terminal! with { Id = "wrong-unit" } },
            "pid" => data with { Terminal = data.Terminal! with { ExecMainPid = 124 } },
            "code-negative" => data with { Terminal = data.Terminal! with { ExecMainCode = -1 } },
            "code-seven" => data with { Terminal = data.Terminal! with { ExecMainCode = 7 } },
            "status-negative" => data with { Terminal = data.Terminal! with { ExecMainStatus = -1 } },
            "status-256" => data with { Terminal = data.Terminal! with { ExecMainStatus = 256 } },
            _ => throw new InvalidOperationException(),
        };
        Reject(data);
    }

    /// <summary>An existing cgroup sample carries every nullable field, including a nonzero inode.</summary>
    [Fact]
    public void ExistingGroupPreservesCompleteSampleWithoutAuthority()
    {
        var data = Valid() with { Group = new(true, false, false, 0, 14, 32) };
        using var json = JsonDocument.Parse(Encode(data).Bytes);
        var group = json.RootElement.GetProperty("cgroup");
        Assert.Equal(JsonValueKind.Object, group.ValueKind);
        Assert.True(group.GetProperty("exists").GetBoolean());
        Assert.Equal(32UL, group.GetProperty("inode").GetUInt64());
        Assert.False(json.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
    }

    [Theory]
    [InlineData("populated")]
    [InlineData("frozen")]
    [InlineData("major")]
    [InlineData("minor")]
    [InlineData("inode-null")]
    [InlineData("inode-zero")]
    public void ExistingGroupMissingOrInvalidFieldRejects(string field)
    {
        var data = Valid() with { Group = new(true, false, false, 0, 14, 32) };
        AssertDetached(data);
        data = field switch
        {
            "populated" => data with { Group = data.Group! with { Populated = null } },
            "frozen" => data with { Group = data.Group! with { Frozen = null } },
            "major" => data with { Group = data.Group! with { DeviceMajor = null } },
            "minor" => data with { Group = data.Group! with { DeviceMinor = null } },
            "inode-null" => data with { Group = data.Group! with { KernelInode = null } },
            "inode-zero" => data with { Group = data.Group! with { KernelInode = 0 } },
            _ => throw new InvalidOperationException(),
        };
        Reject(data);
    }

    [Theory]
    [InlineData("populated")]
    [InlineData("frozen")]
    [InlineData("major")]
    [InlineData("minor")]
    [InlineData("inode")]
    public void PrunedGroupRequiresEveryFieldNull(string field)
    {
        var valid = Valid();
        var data = valid with
        {
            Group = new(false, null, null, null, null, null),
            State = valid.State with { CgroupAfterPumps = false }
        };
        AssertDetached(data);
        data = field switch
        {
            "populated" => data with { Group = data.Group! with { Populated = false } },
            "frozen" => data with { Group = data.Group! with { Frozen = false } },
            "major" => data with { Group = data.Group! with { DeviceMajor = 0 } },
            "minor" => data with { Group = data.Group! with { DeviceMinor = 0 } },
            "inode" => data with { Group = data.Group! with { KernelInode = 1 } },
            _ => throw new InvalidOperationException(),
        };
        Reject(data);
    }

    [Fact]
    public void NullGroupRequiresCgroupAfterPumpsFalse()
    {
        var data = Valid();
        AssertDetached(data);
        Reject(data with { Group = null });
    }

    [Theory]
    [InlineData("received-negative")]
    [InlineData("received-mismatch")]
    [InlineData("limit-zero")]
    [InlineData("limit-over")]
    public void InvalidOutputCountersAndLimitsRejectFromValidNeighbor(string mutation)
    {
        var data = Valid();
        AssertDetached(data);
        var output = mutation switch
        {
            "received-negative" => data.Output with { ReceivedBytes = -1 },
            "received-mismatch" => data.Output with { ReceivedBytes = data.Output.ReceivedBytes + 1 },
            "limit-zero" => data.Output with { ReceivedByteLimit = 0 },
            "limit-over" => data.Output with { ReceivedByteLimit = SupervisionOutputCollector.MaximumReceivedByteLimit + 1 },
            _ => throw new InvalidOperationException(),
        };
        Reject(data with { Output = output });
    }

    private static void AssertDetached(Data data)
    {
        using var json = JsonDocument.Parse(Encode(data).Bytes);
        Assert.False(json.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
    }

    [Theory]
    [InlineData('0')]
    [InlineData('9')]
    [InlineData('f')]
    public void DigestEndpointsRemainLiteralWithoutAuthority(char digit)
    {
        var data = Valid();
        var digest = new string(digit, 64);
        using var json = JsonDocument.Parse(LinuxFailedSettlementObservation.CreateDetached(
            data.Generation, data.Identity, digest, data.State, data.Pending, data.Terminal, data.Group, data.Output).Bytes);
        Assert.Equal(digest, json.RootElement.GetProperty("ready").GetProperty("descriptor_sha256").GetString());
        Assert.False(json.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("identity")]
    [InlineData("state")]
    [InlineData("pending")]
    [InlineData("output")]
    public void MissingRequiredDetachedValueRejectsBeforeProjection(string field)
    {
        var data = Valid();
        AssertDetached(data);
        data = field switch
        {
            "generation" => data with { Generation = Guid.Empty },
            "identity" => data with { Identity = null! },
            "state" => data with { State = null! },
            "pending" => data with { Pending = null! },
            "output" => data with { Output = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Reject(data);
    }

    [Theory]
    [InlineData("stdout-null")]
    [InlineData("stderr-null")]
    [InlineData("stdout-default")]
    [InlineData("stderr-default")]
    [InlineData("stdout-negative")]
    [InlineData("stderr-missing-byte")]
    [InlineData("stdout-failure")]
    [InlineData("stderr-failure")]
    public void MalformedDetachedStreamRejectsBeforeRawProjection(string field)
    {
        var data = Valid();
        AssertDetached(data);
        var output = data.Output;
        output = field switch
        {
            "stdout-null" => output with { Stdout = null! },
            "stderr-null" => output with { Stderr = null! },
            "stdout-default" => output with { Stdout = output.Stdout with { Prefix = default } },
            "stderr-default" => output with { Stderr = output.Stderr with { Prefix = default } },
            "stdout-negative" => output with { Stdout = output.Stdout with { ReceivedBytes = -1 } },
            "stderr-missing-byte" => output with { Stderr = output.Stderr with { ReceivedBytes = output.Stderr.ReceivedBytes - 1 } },
            "stdout-failure" => output with { Stdout = output.Stdout with { Failure = (SupervisionOutputFailure)999 } },
            "stderr-failure" => output with { Stderr = output.Stderr with { Failure = (SupervisionOutputFailure)999 } },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Reject(data with { Output = output });
    }
}
