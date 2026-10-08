using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached shape controls only; no native identity, owner, peer, admission or kernel acceptance exists here.</summary>
public sealed class LinuxNegativeKernelObservationTests
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private sealed record Data(Guid Generation, LinuxProcessSample Identity, LinuxUnitProperties Terminal,
        LinuxCgroupSample Group, SupervisionOutputReceipt Output);

    private static Data Valid()
    {
        var generation = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, generation).Value;
        var group = "/system.slice/" + unit;
        var identity = new LinuxProcessSample(new(123, 42, 'S'), new(999, 999, 999, 999),
            new(987, 987, 987, 987), group);
        var terminal = new LinuxUnitProperties(unit, "loaded", "failed", "failed", group, 0, 123, 1, 1,
            "999", "987", "exec", "control-group", true, 60_000_000, 10_000_000);
        var error = ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("private-output-canary"));
        var stdout = new SupervisionOutputStreamReceipt(0, ImmutableArray<byte>.Empty, true, SupervisionOutputFailure.None);
        var stderr = new SupervisionOutputStreamReceipt(error.Length, error, true, SupervisionOutputFailure.None);
        return new(generation, identity, terminal, new(true, false, false, 0, 14, 32),
            new(stdout, stderr, error.Length, 8192, SupervisionOutputFailure.None, false, false));
    }

    private static LinuxNegativeKernelObservation Encode(Data data, string digest = Digest,
        CancellationToken token = default) => LinuxNegativeKernelObservation.CreateDetached(data.Generation,
            data.Identity, data.Terminal, data.Group, data.Output, digest, token);

    private static void Reject(Data data)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Encode(data));
        Assert.Equal("ASEVD410: Negative kernel observation data rejected.", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Encodes closed copied facts and hashes actual data bytes without echoing their canary.</summary>
    [Fact]
    public void ClosedShapeContainsOnlyDetachedFactsAndFullByteHashes()
    {
        var data = Valid();
        var value = Encode(data);
        using var json = JsonDocument.Parse(value.Bytes);
        var root = json.RootElement;
        Assert.Equal(12, root.EnumerateObject().Count());
        Assert.Equal("issue779-negative-kernel-observation-v1", root.GetProperty("schema").GetString());
        Assert.False(root.GetProperty("native_authority").GetBoolean());
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());
        Assert.True(root.GetProperty("observation_only").GetBoolean());
        Assert.Equal(4, root.GetProperty("process").GetProperty("uid4").GetArrayLength());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data.Output.Stderr.Prefix.AsSpan())),
            root.GetProperty("pumps").GetProperty("stderr").GetProperty("sha256").GetString());
        Assert.DoesNotContain("private-output-canary", Encoding.UTF8.GetString(value.Bytes));
        Assert.InRange(value.Length, 1, LinuxNegativeKernelObservation.MaximumJsonBytes);
    }

    /// <summary>Returned arrays are snapshots; a consumer cannot change the immutable stored result.</summary>
    [Fact]
    public void ByteCopiesCannotMutateFrozenEncoding()
    {
        var value = Encode(Valid());
        var before = value.Bytes;
        var returned = value.Bytes;
        Array.Fill(returned, (byte)0);
        Assert.Equal(before, value.Bytes);
    }

    /// <summary>Normal terminal status remains observed data, including zero; no negative acceptance is inferred.</summary>
    /// <param name="status">Detached actual normal-exit byte.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public void NormalTerminalStatusIsNotReplacedByJoinOrRootCode(int status)
    {
        var data = Valid();
        using var json = JsonDocument.Parse(Encode(data with { Terminal = data.Terminal with { ExecMainStatus = status } }).Bytes);
        Assert.Equal(status, json.RootElement.GetProperty("terminal").GetProperty("exec_main_status").GetInt32());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
    }

    /// <summary>A pruned group has no invented inode, device or population values.</summary>
    [Fact]
    public void AbsentGroupKeepsNulls()
    {
        using var json = JsonDocument.Parse(Encode(Valid() with { Group = new(false, null, null, null, null, null) }).Bytes);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("cgroup").GetProperty("inode").ValueKind);
    }

    /// <summary>Original token cancellation precedes encoding and is propagated without another timer.</summary>
    [Fact]
    public void OriginalCancellationCannotPublishData()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => Encode(Valid(), token: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    /// <summary>Existing proc grammar rejects wrong detached PID, liveness, tuple and generated group.</summary>
    /// <param name="field">Fixed pure-data mutation, never a production runtime selector.</param>
    [Theory]
    [InlineData("pid")][InlineData("start")][InlineData("dead")][InlineData("root")]
    [InlineData("uid4")][InlineData("gid4")][InlineData("group")][InlineData("generation")]
    public void InvalidIdentityCannotBecomeObservation(string field)
    {
        var data = Valid();
        var identity = data.Identity;
        switch (field)
        {
            case "pid": identity = identity with { Stat = identity.Stat with { Pid = 0 } }; break;
            case "start": identity = identity with { Stat = identity.Stat with { StartTimeTicks = 0 } }; break;
            case "dead": identity = identity with { Stat = identity.Stat with { State = 'Z' } }; break;
            case "root": identity = identity with { Uids = new(0, 0, 0, 0) }; break;
            case "uid4": identity = identity with { Uids = identity.Uids with { Effective = 1000 } }; break;
            case "gid4": identity = identity with { Gids = identity.Gids with { FileSystem = 988 } }; break;
            case "group": identity = identity with { ControlGroup = "/private/group-canary" }; break;
            case "generation": data = data with { Generation = Guid.Empty }; break;
        }
        Reject(data with { Identity = identity });
    }

    /// <summary>Terminal code/status, selected PID and closed policy labels cannot be substituted.</summary>
    /// <param name="field">Fixed detached terminal mutation.</param>
    [Theory]
    [InlineData("pid")][InlineData("uninitialized")][InlineData("signal")][InlineData("status")]
    [InlineData("state")][InlineData("unit")][InlineData("user")][InlineData("kill")]
    public void InvalidTerminalRejectsWithoutRawCanary(string field)
    {
        var data = Valid();
        var terminal = field switch
        {
            "pid" => data.Terminal with { ExecMainPid = 124 },
            "uninitialized" => data.Terminal with { ExecMainCode = 0 },
            "signal" => data.Terminal with { ExecMainCode = 2 },
            "status" => data.Terminal with { ExecMainStatus = 256 },
            "state" => data.Terminal with { ActiveState = "state-canary" },
            "unit" => data.Terminal with { Id = "unit-canary" },
            "user" => data.Terminal with { User = "user-canary" },
            _ => data.Terminal with { KillMode = "kill-canary" },
        };
        Reject(data with { Terminal = terminal });
    }

    /// <summary>Hashes require joined successful full retention, rather than a retained prefix or failure-as-EOF.</summary>
    /// <param name="field">Fixed detached output mutation.</param>
    [Theory]
    [InlineData("eof")][InlineData("failure")][InlineData("discarded")][InlineData("default")]
    [InlineData("quota")][InlineData("counter")][InlineData("limit")][InlineData("null")]
    public void IncompleteOutputCannotPublishHashes(string field)
    {
        var data = Valid();
        var output = data.Output;
        output = field switch
        {
            "eof" => output with { Stderr = output.Stderr with { EndOfStream = false } },
            "failure" => output with { Failure = (SupervisionOutputFailure)int.MaxValue },
            "discarded" => output with { Stderr = output.Stderr with { ReceivedBytes = output.Stderr.ReceivedBytes + 1 }, ReceivedBytes = output.ReceivedBytes + 1 },
            "default" => output with { Stderr = output.Stderr with { Prefix = default } },
            "quota" => output with { QuotaExceeded = true },
            "counter" => output with { ReceivedBytes = output.ReceivedBytes + 1 },
            "limit" => output with { ReceivedByteLimit = 16L * 1024 * 1024 + 1 },
            _ => output with { Stdout = null! },
        };
        Reject(data with { Output = output });
    }

    /// <summary>Empty-group data is strict, including absent/null and retained inode requirements.</summary>
    /// <param name="field">Fixed group metadata mutation.</param>
    [Theory]
    [InlineData("populated")][InlineData("absent")][InlineData("inode")][InlineData("frozen")]
    public void InvalidGroupShapeRejects(string field)
    {
        var data = Valid();
        var group = field switch
        {
            "populated" => data.Group with { Populated = true },
            "absent" => data.Group with { Exists = false },
            "inode" => data.Group with { KernelInode = 0 },
            _ => data.Group with { Frozen = null },
        };
        Reject(data with { Group = group });
    }

    /// <summary>Descriptor digests are exact lowercase SHA256 data; malformed values never reach JSON.</summary>
    /// <param name="digest">Invalid detached digest.</param>
    [Theory]
    [InlineData(null)][InlineData("canary")][InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void InvalidDigestCannotEchoInput(string? digest)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Encode(Valid(), digest!));
        Assert.Equal("ASEVD410: Negative kernel observation data rejected.", error.Message);
        Assert.Null(error.InnerException);
    }
}
