using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Recipe and byte-grammar controls only; they manufacture no native owner or application lease.</summary>
public sealed class LinuxApplicationUnitTests
{
    [Fact]
    public void RecipeProjectsOnlyClosedCommandAndPrivateApplicationGrants()
    {
        using var stdout = new SafeFileHandle((nint)111, false);
        using var stderr = new SafeFileHandle((nint)112, false);
        var recipe = Recipe(stdout, stderr);
        var properties = recipe.Properties;
        Assert.True(properties["PrivateNetwork"].GetBool());
        Assert.True(properties["NoNewPrivileges"].GetBool());
        Assert.True(properties["RestrictSUIDSGID"].GetBool());
        Assert.Equal(0UL, properties["CapabilityBoundingSet"].GetUInt64());
        Assert.Equal(0UL, properties["AmbientCapabilities"].GetUInt64());
        Assert.Empty(properties["SupplementaryGroups"].GetArray<string>());
        Assert.Equal(128UL, properties["TasksMax"].GetUInt64());
        Assert.Equal(1UL << 30, properties["MemoryMax"].GetUInt64());
        Assert.Equal(new[] { "/run/app/scratch" }, properties["ReadWritePaths"].GetArray<string>());
        Assert.Equal(new[] { "/run/sdk", "/run/app/bundle" }, properties["ReadOnlyPaths"].GetArray<string>());
        Assert.Equal(new[] { "/run/tool", "/run/subject", "/run/worker/output", "/run/worker/control", "/run/producer", "/run/app/denied-output" },
            properties["InaccessiblePaths"].GetArray<string>());
        Assert.Equal(new[] { "/run/sdk/dotnet", "/run/app/bundle/AppHost.dll", "--scratch", "/run/app/scratch", "--case", "normal" }, recipe.Arguments.TakeLast(6));
        Assert.Equal(new[] { "/usr/bin/env", "-i" }, recipe.Arguments.Take(2));
        Assert.Contains("ASPIRE__STORE__PATH=/run/app/scratch/.aspire-store", recipe.Arguments);
        Assert.Contains("PROOF_PROTECTED_OUTPUT=/run/app/denied-output", recipe.Arguments);
        Assert.DoesNotContain(recipe.Arguments, arg => arg.Contains("coverage", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyActualRunningMainTupleCanSupplyStartupData()
    {
        using var stdout = new SafeFileHandle((nint)111, false);
        using var stderr = new SafeFileHandle((nint)112, false);
        var recipe = Recipe(stdout, stderr);
        var facts = new LinuxUnitProperties(recipe.Unit.Value, "loaded", "active", "running",
            "/system.slice/" + recipe.Unit.Value, 42, 42, 0, 0, "65014", "65015", "exec", "control-group", true,
            recipe.Properties["RuntimeMaxUSec"].GetUInt64(), recipe.Properties["TimeoutStopUSec"].GetUInt64());
        Assert.True(recipe.IsRunning(facts));
        Assert.False(recipe.IsStopped(facts));
        var pending = facts with { ActiveState = "inactive", SubState = "dead", ControlGroup = "", MainPid = 0, ExecMainPid = 0 };
        Assert.False(recipe.IsRunning(pending));
        // A stopped tuple supplies no startup authority, even after explicit stop joined.
        Assert.True(recipe.IsStopped(pending));
        Assert.False(recipe.IsRunning(facts with { MainPid = 0 }));
        Assert.False(recipe.IsRunning(facts with { ExecMainPid = 41 }));
        foreach (var bad in new[] { facts with { User = "0" }, facts with { Group = "0" },
            facts with { Type = "simple" }, facts with { KillMode = "process" },
            facts with { RemainAfterExit = false }, facts with { ControlGroup = "/system.slice/foreign.service" },
            facts with { RuntimeMaxMicroseconds = 1 }, facts with { TimeoutStopMicroseconds = 1 } })
            Assert.Throws<EvidenceAdmissionException>(() => recipe.IsRunning(bad));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void UnsupportedTaskGrantsRejectBeforeAnyNativeDispatch(int tasks)
    {
        using var stdout = new SafeFileHandle((nint)111, false);
        using var stderr = new SafeFileHandle((nint)112, false);
        var error = Assert.Throws<EvidenceAdmissionException>(() => Recipe(stdout, stderr, Capabilities() with { MaximumTasks = tasks }));
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("HTTP/1.1 503 Unhealthy\r\nContent-Length: 17\r\n\r\nnative-http-ready")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\nnative-http-ready")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 17\r\nContent-Length: 17\r\n\r\nnative-http-ready")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 17\r\n\r\nnative-http-ready")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 17\r\n\r\nwrong-body-canary")]
    [InlineData("")]
    public void DetachedHealthBytesCannotPassMalformedOrUnhealthyResponses(string value)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxApplicationProcess.RequireHealth(System.Text.Encoding.ASCII.GetBytes(value)));
        Assert.Equal("ASEVD410", error.Code);
        Assert.DoesNotContain("canary", error.Message);
    }

    [Fact]
    public void ExactHealthGrammarAcceptsBytesButDoesNotIssueResourceReceipt()
    {
        LinuxApplicationProcess.RequireHealth("HTTP/1.1 200 OK\r\nContent-Length: 17\r\nContent-Type: text/plain\r\n\r\nnative-http-ready"u8);
        Assert.Throws<EvidenceAdmissionException>(() => LinuxApplicationProcess.RequireHealth(new byte[4097]));
    }

    private static LinuxApplicationLimits Capabilities() => new(1L << 28, 1L << 30, 128, 1048576, 120, 5);
    private static LinuxApplicationUnit Recipe(SafeFileHandle stdout, SafeFileHandle stderr,
        LinuxApplicationLimits? capabilities = null)
    {
        var generation = Guid.Parse("4523b7bf-72a3-4fc7-a367-86857556e574");
        return LinuxApplicationUnit.Create(LinuxUnitName.Create(LinuxUnitRole.Application, generation),
            LinuxUnitName.Create(LinuxUnitRole.Owner, generation), "/run/sdk/dotnet", "/run/sdk", "/run/tool",
            "/run/subject", "/run/worker/output", "/run/worker/control", "/run/producer", "/run/app/bundle",
            "/run/app/bundle/AppHost.dll", "/run/app/scratch", "/run/app/denied-output",
            "/run/app/bundle/proof-input/declared.txt", 65014, 65015, capabilities ?? Capabilities(),
            TimeSpan.FromSeconds(180), stdout, stderr);
    }
}
