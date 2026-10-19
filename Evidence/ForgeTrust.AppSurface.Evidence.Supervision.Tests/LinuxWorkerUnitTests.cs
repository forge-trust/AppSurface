using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Recipe data controls only; these do not dispatch D-Bus calls or establish protected ownership.</summary>
public sealed class LinuxWorkerUnitTests
{
    [Fact]
    public void ProducerTreeIsHiddenWithoutAddingAnyWorkerWriteGrant()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var id = Guid.NewGuid();
        LinuxWorkerUnit Recipe(string root) => LinuxWorkerUnit.Create(LinuxUnitName.Create(LinuxUnitRole.Worker, id),
            LinuxUnitName.Create(LinuxUnitRole.Owner, id), "/usr/share/dotnet/dotnet", "/run/tool/appsurface.dll",
            65010, 65011, "/run/broker/control.sock", "/run/tool", "/run/subject", "/run/output", "/run/raw-results",
            TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5), output, error, root);
        var recipe = Recipe("/run/producer");
        Assert.Equal(new[] { "/run/raw-results", "/run/producer" }, recipe.Properties["InaccessiblePaths"].GetArray<string>());
        Assert.Equal(new[] { "/run/output" }, recipe.Properties["ReadWritePaths"].GetArray<string>());
        foreach (var invalid in new[] { "/run/tool", "/run/output", "/run", "/run/broker", "/usr/share/dotnet" })
            AssertClosed(() => Recipe(invalid));
    }

    [Fact]
    public void FixedArgumentsAndPolicyCannotAddCallerWorkOrWritablePaths()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var recipe = Create(output, error);
        Assert.Equal(new[]
        {
            "/usr/bin/env", "-i", "PATH=/usr/bin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8",
            "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_CLI_HOME=/tmp", "XDG_RUNTIME_DIR=/tmp", "/usr/share/dotnet/dotnet",
            "/run/tool/appsurface.dll", "evidence", "worker", "--control", "/run/broker/control.sock"
        }, recipe.Arguments);
        var properties = recipe.Properties;
        Assert.Equal(30, properties.Count);
        Assert.Equal("exec", properties["Type"].GetString());
        Assert.Equal("65010", properties["User"].GetString());
        Assert.Equal("65011", properties["Group"].GetString());
        Assert.Empty(properties["SupplementaryGroups"].GetArray<string>());
        Assert.True(properties["NoNewPrivileges"].GetBool());
        Assert.Equal(0UL, properties["CapabilityBoundingSet"].GetUInt64());
        Assert.Equal(0UL, properties["AmbientCapabilities"].GetUInt64());
        Assert.Equal("strict", properties["ProtectSystem"].GetString());
        Assert.Equal("yes", properties["ProtectHome"].GetString());
        Assert.True(properties["PrivateTmp"].GetBool());
        Assert.True(properties["ProtectControlGroups"].GetBool());
        Assert.False(properties["RestrictSUIDSGID"].GetBool());
        Assert.Equal(64UL, properties["TasksMax"].GetUInt64());
        Assert.Equal(1UL << 30, properties["MemoryMax"].GetUInt64());
        Assert.Equal("control-group", properties["KillMode"].GetString());
        Assert.Equal("no", properties["Restart"].GetString());
        Assert.True(properties["RemainAfterExit"].GetBool());
        Assert.True(properties["AddRef"].GetBool());
        Assert.Equal(0UL, properties["LimitCORE"].GetUInt64());
        Assert.Equal("null", properties["StandardInput"].GetString());
        Assert.Equal(new[] { recipe.Owner.Value }, properties["After"].GetArray<string>());
        Assert.Equal(new[] { recipe.Owner.Value }, properties["BindsTo"].GetArray<string>());
        Assert.Equal(new[] { "/run/tool", "/run/subject", "/usr/share/dotnet/dotnet", "/usr/share/dotnet" }, properties["ReadOnlyPaths"].GetArray<string>());
        Assert.Equal(new[] { "/run/output" }, properties["ReadWritePaths"].GetArray<string>());
        Assert.Equal(new[] { "/run/raw-results" }, properties["InaccessiblePaths"].GetArray<string>());
        Assert.Empty(properties["PassEnvironment"].GetArray<string>());
        Assert.Equal(new[]
        {
            "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ROOT",
            "DOTNET_ROOT_X64", "DOTNET_HOST_PATH", "DOTNET_ROLL_FORWARD", "DOTNET_ROLL_FORWARD_TO_PRERELEASE",
            "DOTNET_MULTILEVEL_LOOKUP", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH",
            "CORECLR_PROFILER_PATH_64", "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH",
            "COMPlus_ReadyToRun", "COMPlus_ZapDisable"
        }, properties["UnsetEnvironment"].GetArray<string>());
        Assert.Equal(new[] { "PATH=/usr/bin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_CLI_HOME=/tmp", "XDG_RUNTIME_DIR=/tmp" }, properties["Environment"].GetArray<string>());
    }

    [Fact]
    public void PropertyAndArgumentViewsCannotMutateTheRecipe()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var recipe = Create(output, error);
        recipe.Properties["ReadWritePaths"].GetArray<string>()[0] = "/canary";
        Assert.Equal("/run/output", recipe.Properties["ReadWritePaths"].GetArray<string>()[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)recipe.Arguments)[0] = "/canary");
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, VariantValue>)recipe.Properties).Add("ExecStartPre", "canary"));
    }

    [Theory]
    [InlineData("/run/canary=runtime/dotnet")]
    [InlineData("/run/canary$runtime/dotnet")]
    [InlineData("/run/canary%runtime/dotnet")]
    public void RuntimeCannotBecomeAnEnvAssignmentOrSystemdExpansion(string runtimeHost)
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        AssertClosed(() => Create(output, error, runtimeHost: runtimeHost));
        _ = Create(output, error);
    }

    [Fact]
    public void PositionalEntryAndSocketAssignmentsStayAfterTheRuntimeCommand()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var recipe = Create(output, error, entry: "/run/tool/entry=one.dll", socket: "/run/broker/control=one.sock");
        Assert.Equal(LinuxOwnerFacts.BootstrapArgumentCount, recipe.Arguments.Count);
        Assert.Equal("/usr/share/dotnet/dotnet", recipe.Arguments[8]);
        Assert.Equal("/run/tool/entry=one.dll", recipe.Arguments[9]);
        Assert.Equal("worker", recipe.Arguments[11]);
        Assert.Equal("--control", recipe.Arguments[12]);
        Assert.Equal("/run/broker/control=one.sock", recipe.Arguments[13]);
    }

    [Fact]
    public void SharedBootstrapCannotSelectAnUnknownRole()
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxOwnerFacts.ManagedArguments(
            (EvidenceProcessRole)42, "/usr/share/dotnet/dotnet", "/run/tool/appsurface.dll", "/run/broker/control.sock"));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("/")]
    [InlineData("/run/../canary")]
    [InlineData("/run/./canary")]
    [InlineData("/run//canary")]
    [InlineData("/run/canary/")]
    [InlineData("/run/canary\n")]
    [InlineData("/run/%n")]
    [InlineData("/run/$HOME")]
    public void UnsafeSocketGrammarHasAClosedFailure(string path)
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        AssertClosed(() => Create(output, error, socket: path));
        _ = Create(output, error);
    }

    [Fact]
    public void SocketAndDeploymentLengthsChargeUtf8Bytes()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        _ = Create(output, error, socket: "/" + new string('é', 49) + "a");
        AssertClosed(() => Create(output, error, socket: "/" + new string('é', 50)));
        _ = Create(output, error, entry: "/run/tool/" + new string('a', 4086));
        AssertClosed(() => Create(output, error, entry: "/run/tool/" + new string('a', 4087)));
    }

    [Theory]
    [InlineData(0u, 65011u)]
    [InlineData(65010u, 0u)]
    [InlineData(uint.MaxValue, 65011u)]
    [InlineData(65010u, uint.MaxValue)]
    public void RootOrInvalidAccountNumbersCannotSelectTheWorker(uint uid, uint gid)
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        AssertClosed(() => Create(output, error, uid: uid, gid: gid));
        _ = Create(output, error);
    }

    [Fact]
    public void RolesAndRunIdentityMustMatchWithoutCreatingOwnership()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var id = Guid.NewGuid();
        AssertClosed(() => Create(output, error, worker: LinuxUnitName.Create(LinuxUnitRole.Producer, id)));
        AssertClosed(() => Create(output, error, owner: LinuxUnitName.Create(LinuxUnitRole.Worker, id)));
        AssertClosed(() => Create(output, error, owner: LinuxUnitName.Create(LinuxUnitRole.Owner, Guid.NewGuid())));
        var recipe = Create(output, error);
        Assert.StartsWith("appsurface-evidence-worker-", recipe.Unit.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/run/tool")]
    [InlineData("/run/tool/output")]
    [InlineData("/run")]
    [InlineData("/run/subject")]
    [InlineData("/run/raw-results")]
    [InlineData("/usr/share/dotnet/output")]
    [InlineData("/run/broker")]
    public void WritableGrantCannotOverlapProtectedOrDeniedTrees(string outputParent)
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        AssertClosed(() => Create(output, error, outputParent: outputParent));
        _ = Create(output, error);
    }

    [Fact]
    public void EntryMustRemainInsideTheSelectedTool()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        AssertClosed(() => Create(output, error, entry: "/run/tool-alias/appsurface.dll"));
        _ = Create(output, error);
    }

    [Fact]
    public void TimeBoundsRoundDownAndNeverRenewTheSuppliedRemainder()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var recipe = Create(output, error, allowance: TimeSpan.FromTicks(109), stopping: TimeSpan.FromTicks(19));
        Assert.Equal(9UL, recipe.RuntimeMicroseconds);
        Assert.Equal(1UL, recipe.StoppingMicroseconds);
        Assert.Equal(9UL, recipe.Properties["RuntimeMaxUSec"].GetUInt64());
        Assert.Equal(1UL, recipe.Properties["TimeoutStopUSec"].GetUInt64());
        Assert.True(recipe.RuntimeMicroseconds + recipe.StoppingMicroseconds <= 109UL / 10);
        var seconds = Create(output, error, allowance: TimeSpan.FromSeconds(10), stopping: TimeSpan.FromSeconds(2));
        Assert.Equal(8_000_000UL, seconds.RuntimeMicroseconds);
        Assert.Equal(2_000_000UL, seconds.StoppingMicroseconds);
        foreach (var invalid in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromTicks(9) })
        {
            AssertClosed(() => Create(output, error, allowance: invalid));
            AssertClosed(() => Create(output, error, stopping: invalid));
        }
        AssertClosed(() => Create(output, error, allowance: TimeSpan.FromTicks(10), stopping: TimeSpan.FromTicks(11)));
        AssertClosed(() => Create(output, error, allowance: TimeSpan.FromSeconds(2), stopping: TimeSpan.FromSeconds(2)));
        _ = Create(output, error, allowance: TimeSpan.FromHours(1), stopping: TimeSpan.FromSeconds(30));
        AssertClosed(() => Create(output, error, allowance: TimeSpan.FromHours(1) + TimeSpan.FromTicks(1)));
        AssertClosed(() => Create(output, error, allowance: TimeSpan.FromHours(1), stopping: TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1)));
        AssertClosed(() => Create(output, error, socket: "/run/output/control.sock"));
        AssertClosed(() => Create(output, error, socket: "/run/output/nested/control.sock"));
        AssertClosed(() => Create(output, error, allowance: TimeSpan.FromTicks(19), stopping: TimeSpan.FromTicks(10)));
    }

    [Fact]
    public void InvalidOrClosedHandlesRejectAndValidHandlesStayBorrowed()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        using var invalid = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        AssertClosed(() => Create(invalid, error));
        AssertClosed(() => Create(output, invalid));
        var recipe = Create(output, error);
        Assert.Same(output, recipe.StandardOutput);
        Assert.Same(error, recipe.StandardError);
        Assert.False(output.IsClosed);
        Assert.False(error.IsClosed);
        error.Dispose();
        AssertClosed(() => Create(output, error));
    }

    [Fact]
    public void MissingHandlesPathsAndMalformedUnicodeHaveOnlyClosedFailures()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        AssertClosed(() => Create(null!, error));
        AssertClosed(() => Create(output, null!));
        AssertClosed(() => Create(output, error, socket: null!));
        AssertClosed(() => Create(output, error, entry: null!));
        AssertClosed(() => Create(output, error, socket: "/run/\ud800"));
        _ = Create(output, error);
    }

    [Fact]
    public void RunningMainRequiresExactLiveStateAndMatchingPositivePids()
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error); var running = Running(recipe);
        Assert.True(recipe.HasRunningMain(running));
        Assert.False(recipe.HasFinished(running));
        Assert.False(recipe.HasRunningMain(running with { MainPid = 0 }));
        Assert.False(recipe.HasRunningMain(running with { ExecMainPid = 0 }));
        Assert.False(recipe.HasRunningMain(running with { ExecMainPid = running.MainPid + 1 }));
        Assert.False(recipe.HasRunningMain(running with { ActiveState = "activating", SubState = "start" }));
        Assert.False(recipe.HasRunningMain(running with { ActiveState = "inactive", SubState = "dead" }));
        Assert.False(recipe.HasRunningMain(running with { ControlGroup = string.Empty }));
        Assert.False(recipe.HasRunningMain(running with { ExecMainCode = 1 }));
        Assert.False(recipe.HasRunningMain(running with { ExecMainStatus = 1 }));
        Assert.False(output.IsClosed); Assert.False(error.IsClosed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(255)]
    public void TerminalMainRetainsActualExitStatusWithoutClaimingPhysicalSettlement(int status)
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error);
        var terminal = Running(recipe) with
        {
            MainPid = 0, ExecMainCode = 1, ExecMainStatus = status,
            ActiveState = status == 0 ? "active" : "failed", SubState = status == 0 ? "exited" : "failed"
        };
        Assert.True(recipe.HasFinished(terminal));
        Assert.Equal(status, terminal.ExecMainStatus);
        Assert.False(recipe.HasRunningMain(terminal));
        Assert.True(recipe.HasFinished(terminal with { ControlGroup = string.Empty }));
        Assert.True(recipe.HasFinished(terminal with { ActiveState = "inactive", SubState = "dead" }));
        Assert.False(output.IsClosed); Assert.False(error.IsClosed);
    }

    [Fact]
    public void UninitializedLiveAndSignalExitSamplesCannotMatchNormalTerminalMain()
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error);
        var pending = Running(recipe) with
        {
            ActiveState = "inactive", SubState = "dead", ControlGroup = string.Empty,
            MainPid = 0, ExecMainPid = 0, ExecMainCode = 0, ExecMainStatus = 0
        };
        Assert.False(recipe.HasFinished(pending));
        Assert.False(recipe.HasRunningMain(pending));
        Assert.False(recipe.HasFinished(pending with { ExecMainPid = 4001 }));
        Assert.False(recipe.HasFinished(pending with { ExecMainCode = 1 }));
        var terminal = pending with { ExecMainPid = 4001, ExecMainCode = 1 };
        Assert.True(recipe.HasFinished(terminal));
        Assert.False(recipe.HasFinished(terminal with { MainPid = 4001 }));
        Assert.False(recipe.HasFinished(terminal with { ExecMainCode = 2, ExecMainStatus = 9 }));
        Assert.False(recipe.HasFinished(terminal with { ActiveState = "deactivating", SubState = "stop" }));
        Assert.False(recipe.HasFinished(terminal with { ActiveState = "active", SubState = "running" }));
        Assert.False(recipe.HasFinished(terminal with { ActiveState = "failed", SubState = "dead" }));
        Assert.False(recipe.HasFinished(terminal with { ActiveState = "inactive", SubState = "failed" }));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("load")]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("type")]
    [InlineData("kill")]
    [InlineData("remain")]
    [InlineData("runtime")]
    [InlineData("stop")]
    [InlineData("cgroup")]
    [InlineData("cgroup-prefix")]
    [InlineData("null-cgroup")]
    public void SampledUnitAndPolicyMismatchRejectBothPredicates(string field)
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error); var valid = Running(recipe);
        var bad = field switch
        {
            "id" => valid with { Id = recipe.Owner.Value },
            "load" => valid with { LoadState = "not-found" },
            "uid" => valid with { User = "0" },
            "gid" => valid with { Group = "065011" },
            "type" => valid with { Type = "simple" },
            "kill" => valid with { KillMode = "process" },
            "remain" => valid with { RemainAfterExit = false },
            "runtime" => valid with { RuntimeMaxMicroseconds = recipe.RuntimeMicroseconds + 1 },
            "stop" => valid with { TimeoutStopMicroseconds = recipe.StoppingMicroseconds + 1 },
            "cgroup" => valid with { ControlGroup = "/system.slice/" + recipe.Owner.Value },
            "cgroup-prefix" => valid with { ControlGroup = valid.ControlGroup + "/child" },
            "null-cgroup" => valid with { ControlGroup = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        AssertClosed(() => recipe.HasRunningMain(bad));
        AssertClosed(() => recipe.HasFinished(bad));
        Assert.True(recipe.HasRunningMain(valid));
    }

    [Theory]
    [InlineData("main-pid")]
    [InlineData("exec-pid")]
    [InlineData("negative-status")]
    [InlineData("oversize-exit-byte")]
    public void ImpossiblePidOrNormalExitStatusDataHasOnlyClosedFailure(string field)
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error);
        var valid = Running(recipe) with { MainPid = 0, ExecMainCode = 1, ActiveState = "active", SubState = "exited" };
        var bad = field switch
        {
            "main-pid" => valid with { MainPid = uint.MaxValue },
            "exec-pid" => valid with { ExecMainPid = uint.MaxValue },
            "negative-status" => valid with { ExecMainStatus = -1 },
            "oversize-exit-byte" => valid with { ExecMainStatus = 256 },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        AssertClosed(() => recipe.HasRunningMain(bad));
        AssertClosed(() => recipe.HasFinished(bad));
        AssertClosed(() => recipe.HasRunningMain(null!));
        AssertClosed(() => recipe.HasFinished(null!));
        Assert.True(recipe.HasFinished(valid));
    }

    [Theory]
    [InlineData("inactive", "dead", 1, 0)]
    [InlineData("failed", "failed", 1, 255)]
    [InlineData("inactive", "dead", 2, 1)]
    [InlineData("failed", "failed", 2, 64)]
    [InlineData("inactive", "dead", 3, 1)]
    [InlineData("failed", "failed", 3, 64)]
    public void StoppedMetadataRequiresClosedStateAndRealTerminationRecord(string state, string substate,
        int code, int status)
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error);
        var stopped = Running(recipe) with
        {
            ActiveState = state, SubState = substate, MainPid = 0, ExecMainCode = code, ExecMainStatus = status
        };
        Assert.True(recipe.HasStopped(stopped));
        Assert.True(recipe.HasStopped(stopped with { ControlGroup = string.Empty }));
        Assert.Equal(code == 1, recipe.HasFinished(stopped));
        Assert.False(recipe.HasRunningMain(stopped));
        Assert.False(recipe.HasStopped(stopped with { ActiveState = "active", SubState = "exited" }));
        Assert.False(recipe.HasStopped(stopped with { ActiveState = "inactive", SubState = "failed" }));
        Assert.False(recipe.HasStopped(stopped with { ActiveState = "failed", SubState = "dead" }));
        Assert.False(recipe.HasStopped(stopped with { MainPid = stopped.ExecMainPid }));
        Assert.False(recipe.HasStopped(stopped with { ExecMainPid = 0 }));
        Assert.False(recipe.HasStopped(stopped with { ExecMainPid = 0, ExecMainCode = 0, ExecMainStatus = 0 }));
        Assert.False(recipe.HasStopped(stopped with { ExecMainCode = 0, ExecMainStatus = 0 }));
        Assert.False(recipe.HasStopped(stopped with { ExecMainCode = 4 }));
        foreach (var bad in new[]
        {
            stopped with { Id = recipe.Owner.Value }, stopped with { LoadState = "not-found" },
            stopped with { User = "0" }, stopped with { Group = "0" }, stopped with { Type = "simple" },
            stopped with { KillMode = "process" }, stopped with { RemainAfterExit = false },
            stopped with { RuntimeMaxMicroseconds = recipe.RuntimeMicroseconds + 1 },
            stopped with { TimeoutStopMicroseconds = recipe.StoppingMicroseconds + 1 },
            stopped with { ControlGroup = "/system.slice/" + recipe.Owner.Value },
            stopped with { ExecMainCode = 1, ExecMainStatus = 256 },
            stopped with { ExecMainCode = 2, ExecMainStatus = 0 },
            stopped with { ExecMainCode = 3, ExecMainStatus = 65 }
        }) AssertClosed(() => recipe.HasStopped(bad));
        AssertClosed(() => recipe.HasStopped(null!));
        Assert.False(output.IsClosed); Assert.False(error.IsClosed);
    }

    [Theory]
    [InlineData("active", "exited", 0)]
    [InlineData("inactive", "dead", 17)]
    [InlineData("failed", "failed", 17)]
    public void HasExitedExecutionAcceptsNormalTerminalExitIncludingNonzeroStatus(string state, string substate, int status)
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error);
        var terminal = Running(recipe) with
        {
            ActiveState = state, SubState = substate, MainPid = 0,
            ExecMainCode = 1, ExecMainStatus = status
        };

        Assert.True(recipe.HasExitedExecution(terminal, terminal.ExecMainPid));
        Assert.True(recipe.HasFinished(terminal));
        Assert.False(recipe.HasRunningMain(terminal));
        Assert.False(output.IsClosed); Assert.False(error.IsClosed);
    }

    [Theory]
    [InlineData("inactive", "dead", 2, 15)]
    [InlineData("failed", "failed", 2, 9)]
    [InlineData("inactive", "dead", 3, 6)]
    public void HasExitedExecutionAcceptsValidSignalTerminationAsExitedOnly(string state, string substate,
        int code, int signal)
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error);
        var terminal = Running(recipe) with
        {
            ActiveState = state, SubState = substate, MainPid = 0,
            ExecMainCode = code, ExecMainStatus = signal
        };

        Assert.True(recipe.HasExitedExecution(terminal, terminal.ExecMainPid));
        Assert.True(recipe.HasStopped(terminal));
        Assert.False(recipe.HasFinished(terminal));
        Assert.False(recipe.HasRunningMain(terminal));
    }

    [Fact]
    public void HasExitedExecutionRejectsRunningWrongCapturedPidAndForeignPolicy()
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error);
        var running = Running(recipe);
        Assert.False(recipe.HasExitedExecution(running, running.ExecMainPid));

        var terminal = running with { MainPid = 0, ActiveState = "inactive", SubState = "dead", ExecMainCode = 1 };
        AssertRejected(() => recipe.HasExitedExecution(terminal, 0));
        AssertRejected(() => recipe.HasExitedExecution(terminal, terminal.ExecMainPid + 1));
        AssertClosed(() => recipe.HasExitedExecution(terminal with { User = "0" }, terminal.ExecMainPid));
        Assert.False(output.IsClosed); Assert.False(error.IsClosed);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 65)]
    [InlineData(3, 0)]
    [InlineData(3, 65)]
    public void HasExitedExecutionRejectsSignalsOutsideTheKernelSignalRange(int code, int signal)
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error);
        var terminal = Running(recipe) with
        {
            ActiveState = "inactive", SubState = "dead", MainPid = 0,
            ExecMainCode = code, ExecMainStatus = signal
        };
        AssertClosed(() => recipe.HasExitedExecution(terminal, terminal.ExecMainPid));
    }

    [Fact]
    public void ValidSignalTransitionStaysPendingUntilTheSameExecutionIsTerminal()
    {
        using var output = BorrowedOutput(); using var error = BorrowedError();
        var recipe = Create(output, error);
        var transition = Running(recipe) with
        {
            MainPid = 0, ExecMainCode = 3, ExecMainStatus = 6
        };

        Assert.True(recipe.IsExitTransition(transition, transition.ExecMainPid));
        Assert.False(recipe.HasExitedExecution(transition, transition.ExecMainPid));
        Assert.False(recipe.IsExitTransition(transition, transition.ExecMainPid + 1));

        var terminal = transition with { ActiveState = "failed", SubState = "failed" };
        Assert.False(recipe.IsExitTransition(terminal, terminal.ExecMainPid));
        Assert.True(recipe.HasExitedExecution(terminal, terminal.ExecMainPid));
        AssertClosed(() => recipe.IsExitTransition(transition with { User = "0" }, transition.ExecMainPid));
    }

    /// <summary>A supported stop transition stays pending, while a different substate does not.</summary>
    [Fact]
    public void ExitTransitionRejectsReloadBesideSupportedStopSigterm()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var recipe = Create(output, error);
        var valid = Running(recipe) with { ActiveState = "deactivating", SubState = "stop-sigterm" };
        Assert.True(recipe.IsExitTransition(valid, valid.ExecMainPid));
        Assert.False(recipe.IsExitTransition(valid with { SubState = "reload" }, valid.ExecMainPid));
    }

    /// <summary>Only a zero main PID makes the mixed active/running sample an exit transition.</summary>
    [Fact]
    public void ExitTransitionDistinguishesZeroMainPidFromStillRunningExecution()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var recipe = Create(output, error);
        var valid = Running(recipe) with { MainPid = 0 };
        Assert.True(recipe.IsExitTransition(valid, valid.ExecMainPid));
        Assert.False(recipe.IsExitTransition(valid with { MainPid = valid.ExecMainPid }, valid.ExecMainPid));
    }

    /// <summary>An inactive/dead sample stops being transitional when its exit code is available.</summary>
    [Fact]
    public void ExitTransitionDistinguishesInactiveDeadPendingAndCompletedExitCodes()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var recipe = Create(output, error);
        var valid = Running(recipe) with { ActiveState = "inactive", SubState = "dead", MainPid = 0 };
        Assert.True(recipe.IsExitTransition(valid, valid.ExecMainPid));
        Assert.False(recipe.IsExitTransition(valid with { ExecMainCode = 1 }, valid.ExecMainPid));
    }

    /// <summary>A failed/failed sample stops being transitional when its exit code is available.</summary>
    [Fact]
    public void ExitTransitionDistinguishesFailedPendingAndCompletedExitCodes()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var recipe = Create(output, error);
        var valid = Running(recipe) with { ActiveState = "failed", SubState = "failed", MainPid = 0 };
        Assert.True(recipe.IsExitTransition(valid, valid.ExecMainPid));
        Assert.False(recipe.IsExitTransition(valid with { ExecMainCode = 1 }, valid.ExecMainPid));
    }

    /// <summary>A supported stop sample cannot substitute a different positive main PID.</summary>
    [Fact]
    public void ExitTransitionRejectsForeignMainPidBesideMatchingExecution()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var recipe = Create(output, error);
        var valid = Running(recipe) with { ActiveState = "deactivating", SubState = "stop" };
        Assert.True(recipe.IsExitTransition(valid, valid.ExecMainPid));
        Assert.False(recipe.IsExitTransition(valid with { MainPid = valid.ExecMainPid + 1 }, valid.ExecMainPid));
    }

    /// <summary>The signal-status upper bound accepts 64 and rejects the adjacent value with fixed diagnostics.</summary>
    [Fact]
    public void ExitTransitionRejectsSignalStatusAboveTheSupportedMaximum()
    {
        using var output = BorrowedOutput();
        using var error = BorrowedError();
        var recipe = Create(output, error);
        var valid = Running(recipe) with
        {
            ActiveState = "deactivating", SubState = "stop", ExecMainCode = 3, ExecMainStatus = 64,
        };
        Assert.True(recipe.IsExitTransition(valid, valid.ExecMainPid));
        var rejected = Assert.Throws<EvidenceAdmissionException>(() =>
            recipe.IsExitTransition(valid with { ExecMainStatus = 65 }, valid.ExecMainPid));
        Assert.Equal("ASEVD410", rejected.Code);
        Assert.Equal("ASEVD410: The protected systemd operation or its response is invalid."
            + " Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.", rejected.Message);
        Assert.Null(rejected.InnerException);
        Assert.DoesNotContain("canary", rejected.ToString(), StringComparison.Ordinal);
    }

    private static void AssertRejected(Func<bool> predicate)
    {
        try { Assert.False(predicate()); }
        catch (EvidenceAdmissionException error)
        {
            Assert.Equal("ASEVD410", error.Code);
            Assert.Null(error.InnerException);
        }
    }

    private static LinuxUnitProperties Running(LinuxWorkerUnit recipe) => new(recipe.Unit.Value,
        "loaded", "active", "running", "/system.slice/" + recipe.Unit.Value, 4001, 4001, 0, 0,
        "65010", "65011", "exec", "control-group", true, recipe.RuntimeMicroseconds, recipe.StoppingMicroseconds);

    private static LinuxWorkerUnit Create(SafeFileHandle output, SafeFileHandle error, string socket = "/run/broker/control.sock",
        uint uid = 65010, uint gid = 65011, string entry = "/run/tool/appsurface.dll", string outputParent = "/run/output",
        TimeSpan? allowance = null, TimeSpan? stopping = null, LinuxUnitName? worker = null, LinuxUnitName? owner = null,
        string runtimeHost = "/usr/share/dotnet/dotnet")
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        return LinuxWorkerUnit.Create(worker ?? LinuxUnitName.Create(LinuxUnitRole.Worker, id),
            owner ?? LinuxUnitName.Create(LinuxUnitRole.Owner, id), runtimeHost, entry,
            uid, gid, socket, "/run/tool", "/run/subject", outputParent, "/run/raw-results",
            allowance ?? TimeSpan.FromSeconds(30), stopping ?? TimeSpan.FromSeconds(5), output, error);
    }

    // Borrow only process-standard descriptor numbers: no writes, OS claims, or fake protected capabilities.
    private static SafeFileHandle BorrowedOutput() => new(new IntPtr(1), ownsHandle: false);
    private static SafeFileHandle BorrowedError() => new(new IntPtr(2), ownsHandle: false);

    private static void AssertClosed(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
    }
}
