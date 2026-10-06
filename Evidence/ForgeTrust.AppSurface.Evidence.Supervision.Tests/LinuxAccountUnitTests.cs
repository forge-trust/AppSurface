using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Fixed utility policy and terminal/sample data controls, not native root execution or account leases.</summary>
public sealed class LinuxAccountUnitTests
{
    [Fact]
    public void UtilityCannotSelectExecutableEnvironmentOrWorkerPermissions()
    {
        using var stdout = Output(71);
        using var stderr = Output(72);
        var unit = Recipe(stdout, stderr);
        Assert.Equal(new[] { "/usr/sbin/useradd", "--system", "--user-group", "--no-create-home",
            "--shell", "/usr/sbin/nologin", unit.Command.Arguments[^1] }, unit.Arguments);
        Assert.Equal(LinuxRunAccountOperation.CreateUser, unit.Command.Operation);
        var properties = unit.Properties;
        Assert.Equal("0", properties["User"].GetString());
        Assert.Equal("0", properties["Group"].GetString());
        Assert.Empty(properties["SupplementaryGroups"].GetArray<string>());
        Assert.True(properties["NoNewPrivileges"].GetBool());
        Assert.Equal(11UL, properties["CapabilityBoundingSet"].GetUInt64());
        Assert.Equal(0UL, properties["AmbientCapabilities"].GetUInt64());
        Assert.True(properties["ProtectControlGroups"].GetBool());
        Assert.True(properties["RestrictSUIDSGID"].GetBool());
        Assert.Equal("strict", properties["ProtectSystem"].GetString());
        Assert.Equal("yes", properties["ProtectHome"].GetString());
        Assert.True(properties["PrivateTmp"].GetBool());
        Assert.Equal(new[] { "/etc", "/var/log" }, properties["ReadWritePaths"].GetArray<string>());
        Assert.Equal(new[] { unit.Owner.Value }, properties["After"].GetArray<string>());
        Assert.Equal(new[] { unit.Owner.Value }, properties["BindsTo"].GetArray<string>());
        Assert.Equal("control-group", properties["KillMode"].GetString());
        Assert.Equal("no", properties["Restart"].GetString());
        Assert.True(properties["RemainAfterExit"].GetBool());
        Assert.True(properties["AddRef"].GetBool());
        Assert.True(properties["SendSIGKILL"].GetBool());
        Assert.Equal(9, properties["FinalKillSignal"].GetInt32());
        Assert.Equal(0UL, properties["LimitCORE"].GetUInt64());
        Assert.Empty(properties["PassEnvironment"].GetArray<string>());
        Assert.Equal(LinuxRunAccountCommand.Environment, properties["Environment"].GetArray<string>());
        Assert.Equal(LinuxOwnerFacts.UnsafeEnvironment, properties["UnsetEnvironment"].GetArray<string>());
        Assert.Equal("null", properties["StandardInput"].GetString());
        Assert.Equal(64UL, properties["TasksMax"].GetUInt64());
        Assert.Equal(1UL << 30, properties["MemoryMax"].GetUInt64());
        Assert.DoesNotContain("/run/output", properties["ReadWritePaths"].GetArray<string>());
    }

    [Fact]
    public void BorrowedHandlesAndImmutableViewsCannotRewriteTheFixedRecipe()
    {
        using var stdout = Output(71);
        using var stderr = Output(72);
        var unit = Recipe(stdout, stderr);
        Assert.Same(stdout, unit.StandardOutput);
        Assert.Same(stderr, unit.StandardError);
        unit.Properties["ReadWritePaths"].GetArray<string>()[0] = "/canary";
        Assert.Equal("/etc", unit.Properties["ReadWritePaths"].GetArray<string>()[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)unit.Arguments)[0] = "/canary");
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, VariantValue>)unit.Properties).Add("ExecStartPre", "canary"));
    }

    [Fact]
    public void RuntimeStopSumFitsOriginalRemainderAndCannotRenewIt()
    {
        using var stdout = Output(71);
        using var stderr = Output(72);
        var small = Recipe(stdout, stderr, TimeSpan.FromTicks(109), TimeSpan.FromTicks(19));
        Assert.Equal(9UL, small.RuntimeMicroseconds);
        Assert.Equal(1UL, small.StoppingMicroseconds);
        var normal = Recipe(stdout, stderr);
        Assert.Equal(10_000_000UL, normal.RuntimeMicroseconds);
        Assert.Equal(5_000_000UL, normal.StoppingMicroseconds);
        Assert.Equal(normal.RuntimeMicroseconds, normal.Properties["RuntimeMaxUSec"].GetUInt64());
        Assert.Equal(normal.StoppingMicroseconds, normal.Properties["TimeoutStopUSec"].GetUInt64());
        var shortRun = Recipe(stdout, stderr, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(2));
        Assert.Equal(5_000_000UL, shortRun.RuntimeMicroseconds);
        Assert.Equal(2_000_000UL, shortRun.StoppingMicroseconds);
        AssertClosed(() => Recipe(stdout, stderr, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        AssertClosed(() => Recipe(stdout, stderr, TimeSpan.FromHours(1) + TimeSpan.FromTicks(1)));
        AssertClosed(() => Recipe(stdout, stderr, stopping: TimeSpan.FromSeconds(6)));
        AssertClosed(() => Recipe(stdout, stderr, stopping: TimeSpan.FromTicks(9)));
        AssertClosed(() => Recipe(stdout, stderr, stopping: TimeSpan.Zero));
    }

    [Fact]
    public void WrongRolesAndMissingClosedCommandOrHandlesRejectBeforeDispatch()
    {
        using var stdout = Output(71);
        using var stderr = Output(72);
        var unit = Recipe(stdout, stderr);
        foreach (var role in new[] { LinuxUnitRole.Owner, LinuxUnitRole.Worker, LinuxUnitRole.Producer, LinuxUnitRole.Application })
            AssertClosed(() => LinuxAccountUnit.Create(LinuxUnitName.Create(role, Guid.NewGuid()), unit.Owner,
                unit.Command, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), stdout, stderr));
        AssertClosed(() => LinuxAccountUnit.Create(unit.Unit, unit.Unit, unit.Command,
            TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), stdout, stderr));
        AssertClosed(() => LinuxAccountUnit.Create(unit.Unit, unit.Owner, null!,
            TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), stdout, stderr));
        AssertClosed(() => Recipe(null!, stderr));
        using var invalid = Output(-1);
        AssertClosed(() => Recipe(invalid, stderr));
        stdout.Dispose();
        AssertClosed(() => Recipe(stdout, stderr));
    }

    [Fact]
    public void TerminalMainResultAllowsPrunedCgroupButNeverReplacesPhysicalJoin()
    {
        using var stdout = Output(71);
        using var stderr = Output(72);
        var unit = Recipe(stdout, stderr);
        var done = Terminal(unit);
        Assert.True(unit.HasFinished(done));
        Assert.True(unit.HasFinished(done with { ControlGroup = string.Empty }));
        Assert.False(unit.HasFinished(done with { MainPid = 100, SubState = "running" }));
        Assert.False(unit.HasFinished(done with { ExecMainPid = 0 }));
        Assert.False(unit.HasFinished(done with { ExecMainCode = 0 }));
        Assert.False(unit.HasFinished(done with { ActiveState = "activating", SubState = "start" }));
        AssertClosed(() => unit.HasFinished(done with { ExecMainCode = 2 }));
        AssertClosed(() => unit.HasFinished(done with { ExecMainStatus = 17 }));
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("load")]
    [InlineData("user")]
    [InlineData("group")]
    [InlineData("type")]
    [InlineData("kill")]
    [InlineData("remain")]
    [InlineData("runtime")]
    [InlineData("stop")]
    [InlineData("cgroup")]
    public void ForeignOrMalformedTerminalMetadataRejects(string mutation)
    {
        using var stdout = Output(71);
        using var stderr = Output(72);
        var unit = Recipe(stdout, stderr);
        var valid = Terminal(unit);
        var bad = mutation switch
        {
            "unit" => valid with { Id = "foreign-canary.service" },
            "load" => valid with { LoadState = "not-found" },
            "user" => valid with { User = "1" },
            "group" => valid with { Group = "1" },
            "type" => valid with { Type = "simple" },
            "kill" => valid with { KillMode = "process" },
            "remain" => valid with { RemainAfterExit = false },
            "runtime" => valid with { RuntimeMaxMicroseconds = valid.RuntimeMaxMicroseconds + 1 },
            "stop" => valid with { TimeoutStopMicroseconds = valid.TimeoutStopMicroseconds + 1 },
            _ => valid with { ControlGroup = "/system.slice/foreign-canary.service" },
        };
        var error = AssertClosed(() => unit.HasFinished(bad));
        Assert.DoesNotContain("canary", error.ToString());
        Assert.True(unit.HasFinished(valid));
    }

    [Fact]
    public void EmptyGroupDataRequiresCompleteShapeAndCannotCreateExecutorAuthority()
    {
        Assert.True(LinuxAccountUtility.GroupEmpty(new(false, null, null, null, null, null)));
        Assert.True(LinuxAccountUtility.GroupEmpty(new(true, false, false, 0, 28, 100)));
        Assert.False(LinuxAccountUtility.GroupEmpty(new(true, true, false, 0, 28, 100)));
        Assert.False(LinuxAccountUtility.GroupEmpty(new(true, false, true, 0, 28, 100)));
        AssertClosed(() => LinuxAccountUtility.GroupEmpty(new(false, false, null, null, null, null)));
        AssertClosed(() => LinuxAccountUtility.GroupEmpty(new(true, null, false, 0, 28, 100)));
        AssertClosed(() => LinuxAccountUtility.GroupEmpty(new(true, false, null, 0, 28, 100)));
        AssertClosed(() => LinuxAccountUtility.GroupEmpty(new(true, false, false, null, 28, 100)));
        AssertClosed(() => LinuxAccountUtility.GroupEmpty(new(true, false, false, 0, null, 100)));
        AssertClosed(() => LinuxAccountUtility.GroupEmpty(new(true, false, false, 0, 28, null)));
        AssertClosed(() => LinuxAccountUtility.GroupEmpty(new(true, false, false, 0, 28, 0)));
        AssertClosed(() => LinuxAccountUtility.GroupEmpty(null!));
        var command = LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateUser,
            LinuxRunAccountNames.Create(Guid.NewGuid()).Worker);
        Assert.Throws<ArgumentNullException>(() => LinuxAccountUtility.Create(null!, command, cleanup: false));
    }

    private static SafeFileHandle Output(int fd) => new((nint)fd, ownsHandle: false);

    private static LinuxAccountUnit Recipe(SafeFileHandle stdout, SafeFileHandle stderr,
        TimeSpan? remaining = null, TimeSpan? stopping = null)
    {
        var unit = LinuxUnitName.Create(LinuxUnitRole.AccountUtility, Guid.NewGuid());
        var owner = LinuxUnitName.Create(LinuxUnitRole.Owner, Guid.NewGuid());
        var command = LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateUser,
            LinuxRunAccountNames.Create(Guid.NewGuid()).Worker);
        return LinuxAccountUnit.Create(unit, owner, command, remaining ?? TimeSpan.FromMinutes(1),
            stopping ?? TimeSpan.FromSeconds(5), stdout, stderr);
    }

    private static LinuxUnitProperties Terminal(LinuxAccountUnit unit) => new(unit.Unit.Value,
        "loaded", "active", "exited", "/system.slice/" + unit.Unit.Value, 0, 100, 1, 0,
        "0", "0", "exec", "control-group", true, unit.RuntimeMicroseconds, unit.StoppingMicroseconds);

    private static EvidenceAdmissionException AssertClosed(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        return error;
    }
}
