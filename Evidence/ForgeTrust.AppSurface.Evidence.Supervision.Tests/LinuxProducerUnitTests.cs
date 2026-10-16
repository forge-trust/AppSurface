using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure unit recipe and lifecycle samples; no test issues native ownership or admission.</summary>
public sealed class LinuxProducerUnitTests
{
    [Fact]
    public void ClosedRecipeKeepsExactSubjectLimitsAndDenialsWithOnlyThreeWritableRoots()
    {
        using var stdout = new SafeFileHandle((nint)111, false);
        using var stderr = new SafeFileHandle((nint)112, false);
        var recipe = Recipe(stdout, stderr);
        var p = recipe.Properties;
        Assert.Equal("65012", p["User"].GetString());
        Assert.Equal("65013", p["Group"].GetString());
        Assert.Empty(p["SupplementaryGroups"].GetArray<string>());
        Assert.True(p["NoNewPrivileges"].GetBool());
        Assert.True(p["RestrictSUIDSGID"].GetBool());
        Assert.Equal(0UL, p["CapabilityBoundingSet"].GetUInt64());
        Assert.Equal(0UL, p["AmbientCapabilities"].GetUInt64());
        Assert.Equal(64UL, p["TasksMax"].GetUInt64());
        Assert.Equal(1UL << 30, p["MemoryMax"].GetUInt64());
        Assert.Equal("/run/producer/subject", p["WorkingDirectory"].GetString());
        Assert.Equal(new[] { "/run/sdk" }, p["ReadOnlyPaths"].GetArray<string>());
        Assert.Equal(new[] { "/run/producer/subject", "/run/producer/results/token", "/run/producer/scratch" },
            p["ReadWritePaths"].GetArray<string>());
        Assert.Equal(new[] { "/run/tool", "/run/source", "/run/control/raw-results", "/run/control/worker/broker/control.sock", "/run/control/output" },
            p["InaccessiblePaths"].GetArray<string>());
        Assert.Equal(new[] { recipe.Owner.Value }, p["BindsTo"].GetArray<string>());
        Assert.True(p["AddRef"].GetBool());
        Assert.True(p["RemainAfterExit"].GetBool());
        Assert.Contains("DOTNET_PROCESSOR_COUNT=1", recipe.Arguments);
        Assert.Contains("DOTNET_EnableDiagnostics_IPC=0", recipe.Arguments);
        Assert.Contains("DOTNET_EnableDiagnostics_Debugger=0", recipe.Arguments);
        Assert.Contains("TMPDIR=/run/producer/scratch/tmp", recipe.Arguments);
        Assert.Contains("NUGET_PACKAGES=/run/producer/scratch/nuget", recipe.Arguments);
        Assert.Equal(new[] { "test", "/run/producer/subject/Subject.csproj", "--configuration", "Debug", "--collect:XPlat Code Coverage",
            "--results-directory", "/run/producer/results/token", "--verbosity", "quiet", "-m:1", "-p:UseSharedCompilation=false", "-nodeReuse:false" },
            recipe.Arguments.Skip(16));
        p["ReadWritePaths"].GetArray<string>()[0] = "/canary";
        Assert.Equal("/run/producer/subject", recipe.Properties["ReadWritePaths"].GetArray<string>()[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(255)]
    public void NormalExitStatusIsPreservedButNeverSuppliesPhysicalSettlement(int exit)
    {
        using var stdout = new SafeFileHandle((nint)111, false);
        using var stderr = new SafeFileHandle((nint)112, false);
        var recipe = Recipe(stdout, stderr);
        var running = Running(recipe);
        Assert.True(recipe.HasRunningMain(running));
        Assert.False(recipe.HasFinished(running));
        var finished = running with { MainPid = 0, ExecMainCode = 1, ExecMainStatus = exit, ActiveState = "active", SubState = "exited" };
        Assert.True(recipe.HasFinished(finished));
        Assert.False(recipe.HasStopped(finished));
        Assert.True(recipe.HasStopped(finished with { ActiveState = "inactive", SubState = "dead", ControlGroup = "" }));
        Assert.False(recipe.HasFinished(finished with { ExecMainPid = 0 }));
        Assert.False(recipe.HasFinished(finished with { ExecMainCode = 0 }));
        Assert.False(recipe.HasFinished(finished with { ExecMainCode = 2 }));
        Assert.False(recipe.HasRunningMain(running with { ExecMainPid = 1235 }));
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("group")]
    [InlineData("unit")]
    [InlineData("cgroup")]
    [InlineData("runtime")]
    [InlineData("stop")]
    [InlineData("remain")]
    [InlineData("type")]
    public void ForeignPolicyCannotBeUsedForAnyLifecyclePredicate(string field)
    {
        using var stdout = new SafeFileHandle((nint)111, false);
        using var stderr = new SafeFileHandle((nint)112, false);
        var recipe = Recipe(stdout, stderr);
        var invalid = field switch
        {
            "uid" => Running(recipe) with { User = "0" }, "group" => Running(recipe) with { Group = "0" },
            "unit" => Running(recipe) with { Id = recipe.Owner.Value }, "cgroup" => Running(recipe) with { ControlGroup = "/foreign" },
            "runtime" => Running(recipe) with { RuntimeMaxMicroseconds = 1 }, "stop" => Running(recipe) with { TimeoutStopMicroseconds = 1 },
            "remain" => Running(recipe) with { RemainAfterExit = false }, _ => Running(recipe) with { Type = "simple" },
        };
        Assert.Throws<EvidenceAdmissionException>(() => recipe.HasRunningMain(invalid));
        Assert.Throws<EvidenceAdmissionException>(() => recipe.HasFinished(invalid));
        Assert.Throws<EvidenceAdmissionException>(() => recipe.HasStopped(invalid));
    }

    [Theory]
    [InlineData("deactivating", "stop", 0, 1)]
    [InlineData("deactivating", "stop-post", 0, 1)]
    [InlineData("active", "running", 0, 1)]
    [InlineData("inactive", "dead", 0, 0)]
    public void ExitTransitionIsPendingOnlyForOriginalCapturedExecution(string active, string sub, int main, int code)
    {
        using var stdout = new SafeFileHandle((nint)111, false);
        using var stderr = new SafeFileHandle((nint)112, false);
        var recipe = Recipe(stdout, stderr);
        var sample = Running(recipe) with { ActiveState = active, SubState = sub, MainPid = (uint)main, ExecMainCode = code };
        Assert.True(recipe.IsNormalExitTransition(sample, 1234));
        Assert.False(recipe.HasFinished(sample));
        Assert.False(recipe.IsNormalExitTransition(sample with { ExecMainPid = 4321 }, 1234));
        Assert.False(recipe.IsNormalExitTransition(sample with { MainPid = 4321 }, 1234));
        Assert.False(recipe.IsNormalExitTransition(sample with { ExecMainCode = 2, ExecMainStatus = 9 }, 1234));
        Assert.False(recipe.IsNormalExitTransition(sample, 0));
        Assert.Throws<EvidenceAdmissionException>(() => recipe.IsNormalExitTransition(sample with { User = "0" }, 1234));
    }

    private static LinuxProducerUnit Recipe(SafeFileHandle stdout, SafeFileHandle stderr)
    {
        var id = Guid.Parse("ba080484-cf24-4794-9180-b02447f9841e");
        return LinuxProducerUnit.Create(LinuxUnitName.Create(LinuxUnitRole.Producer, id), LinuxUnitName.Create(LinuxUnitRole.Owner, id),
            "/run/sdk/dotnet", "/run/sdk", "/run/tool", "/run/source", "/run/control/raw-results",
            "/run/control/worker/broker/control.sock", "/run/control/output", "/run/producer/subject", "/run/producer/subject/Subject.csproj",
            "/run/producer/results", "/run/producer/results/token", "/run/producer/scratch", "/run/producer/scratch/nuget", 65012, 65013,
            TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(5), stdout, stderr);
    }

    private static LinuxUnitProperties Running(LinuxProducerUnit unit) => new(unit.Unit.Value, "loaded", "active", "running",
        "/system.slice/" + unit.Unit.Value, 1234, 1234, 0, 0, "65012", "65013", "exec", "control-group", true,
        unit.RuntimeMicroseconds, unit.StoppingMicroseconds);
}
