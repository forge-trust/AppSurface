using ForgeTrust.AppSurface.Evidence.Contracts;
using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

public sealed class LinuxOwnerActivationTests
{
    private static readonly Guid Run = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private const ulong Now = 2_000_000;
    private static void Require(LinuxOwnerFacts value) => value.Require(Run, 123, "/runtime/dotnet", "/tool/app.dll",
        "/run/request.json", Now, TimeSpan.FromSeconds(30));

    [Fact]
    public void CompleteSampleFitsOriginalDeadlineWithoutIssuingALease()
    {
        var (unit, service) = Properties();
        var sample = LinuxOwnerFacts.Parse(unit, service);
        Require(sample);
        Assert.Equal(LinuxOwnerFacts.GuardPath(Run), Assert.Single(sample.Conditions).Parameter);
        // Parser copies mutable wire arrays; later source replacement cannot change the sample.
        service["Environment"] = VariantValue.Array(new[] { "canary=secret" });
        Assert.Equal(5, sample.Environment.Count);
        Require(sample);
    }

    [Theory]
    [InlineData("Conditions", false)]
    [InlineData("ConditionResult", false)]
    [InlineData("ActiveEnterTimestampMonotonic", false)]
    [InlineData("Restart", true)]
    [InlineData("FinalKillSignal", true)]
    [InlineData("SendSIGKILL", true)]
    [InlineData("ExecStart", true)]
    [InlineData("EnvironmentFiles", true)]
    [InlineData("Environment", true)]
    [InlineData("UnsetEnvironment", true)]
    [InlineData("PassEnvironment", true)]
    public void MissingAndWrongTypedStartupFactsFailClosed(string key, bool inService)
    {
        var (unit, service) = Properties();
        var source = inService ? service : unit;
        source.Remove(key);
        AssertClosed(() => LinuxOwnerFacts.Parse(unit, service));
        source[key] = "canary-invalid-wire";
        AssertClosed(() => Require(LinuxOwnerFacts.Parse(unit, service)));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("trigger")]
    [InlineData("negate")]
    [InlineData("path")]
    [InlineData("untested")]
    [InlineData("failed")]
    [InlineData("extra")]
    [InlineData("empty")]
    [InlineData("result")]
    public void ConditionAlternativesCannotReplaceTheSingleSuccessfulPathGuard(string change)
    {
        var facts = Sample();
        var condition = Assert.Single(facts.Conditions);
        var altered = change switch
        {
            "kind" => condition with { Kind = "ConditionFileNotEmpty" },
            "trigger" => condition with { Trigger = true },
            "negate" => condition with { Negate = true },
            "path" => condition with { Parameter = "/run/canary/armed" },
            "untested" => condition with { Result = 0 },
            "failed" => condition with { Result = -1 },
            _ => condition
        };
        facts = facts with
        {
            Conditions = change == "empty" ? [] : change == "extra" ? [condition, condition] : [altered],
            ConditionResult = change != "result"
        };
        AssertClosed(() => Require(facts));
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("LoadState")]
    [InlineData("ActiveState")]
    [InlineData("SubState")]
    [InlineData("MainPID")]
    [InlineData("ExecMainPID")]
    [InlineData("User")]
    [InlineData("Group")]
    [InlineData("Type")]
    [InlineData("KillMode")]
    [InlineData("RemainAfterExit")]
    [InlineData("ControlGroup")]
    public void LiveOwnerShapeRejectsACollectedOrDifferentProcess(string key)
    {
        var (unit, service) = Properties();
        var source = unit.ContainsKey(key) ? unit : service;
        source[key] = key switch
        {
            "MainPID" or "ExecMainPID" => 456u,
            "RemainAfterExit" => true,
            _ => "canary-invalid-owner"
        };
        AssertClosed(() => Require(LinuxOwnerFacts.Parse(unit, service)));
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("kill")]
    [InlineData("signal")]
    [InlineData("ignore")]
    [InlineData("executable")]
    [InlineData("argv")]
    [InlineData("environment")]
    [InlineData("unset")]
    [InlineData("files")]
    [InlineData("pass")]
    public void PolicyMutationCannotBecomeAnArmedOwner(string change)
    {
        var sample = Sample();
        sample = change switch
        {
            "restart" => sample with { Restart = "always" },
            "kill" => sample with { SendSigKill = false },
            "signal" => sample with { FinalKillSignal = 15 },
            "ignore" => sample with { IgnoreFailure = true },
            "executable" => sample with { Executable = "/canary/runtime" },
            "argv" => sample with { Arguments = ["/runtime/dotnet", "/tool/app.dll", "evidence", "supervise", "--request", "/run/canary"] },
            "environment" => sample with { Environment = sample.Environment.Concat(["DOTNET_STARTUP_HOOKS=canary"]).ToArray() },
            "unset" => sample with { UnsetEnvironment = sample.UnsetEnvironment.Skip(1).ToArray() },
            "files" => sample with { EnvironmentFileCount = 1 },
            _ => sample with { PassEnvironmentCount = 1 }
        };
        AssertClosed(() => Require(sample));
    }

    [Theory]
    [InlineData(0ul, 1ul, 25_000_000ul, 5_000_000ul, 30L)]
    [InlineData(2_000_001ul, Now, 25_000_000ul, 5_000_000ul, 30L)]
    [InlineData(1_000_000ul, Now, 0ul, 5_000_000ul, 30L)]
    [InlineData(1_000_000ul, Now, 3_600_000_001ul, 5_000_000ul, 3600L)]
    [InlineData(1_000_000ul, Now, 25_000_000ul, 0ul, 30L)]
    [InlineData(1_000_000ul, Now, 25_000_000ul, 30_000_001ul, 60L)]
    [InlineData(1_000_000ul, 26_000_000ul, 25_000_000ul, 5_000_000ul, 30L)]
    [InlineData(1_000_000ul, Now, 25_000_000ul, 5_000_000ul, 28L)]
    [InlineData(1_000_000ul, Now, 25_000_000ul, 5_000_000ul, 0L)]
    [InlineData(1_000_000ul, Now, 25_000_000ul, 5_000_000ul, 3601L)]
    public void DeadlineCannotBeMissingResetExpiredOrLargerThanTheJob(ulong since, ulong now, ulong runtime, ulong stop, long remaining)
    {
        var facts = Sample();
        facts = facts with { ActiveSinceMicroseconds = since,
            Unit = facts.Unit with { RuntimeMaxMicroseconds = runtime, TimeoutStopMicroseconds = stop } };
        AssertClosed(() => facts.RequireDeadline(now, TimeSpan.FromSeconds(remaining)));
    }

    [Fact]
    public void StopReserveAndElapsedRuntimeFitExactlyAtTheDeadline()
    {
        Sample().RequireDeadline(Now, TimeSpan.FromSeconds(29));
        AssertClosed(() => Sample().RequireDeadline(Now, TimeSpan.FromSeconds(29) - TimeSpan.FromTicks(10)));
    }

    [Fact]
    public void OriginalStopReserveAllowsCleanupButCannotReopenDispatch()
    {
        var facts = Sample();
        AssertClosed(() => facts.RequireDeadline(26_000_000, TimeSpan.FromSeconds(5)));
        facts.RequireDeadline(26_000_000, TimeSpan.FromSeconds(5), allowStopping: true);
        AssertClosed(() => facts.RequireDeadline(31_000_000, TimeSpan.FromSeconds(5), allowStopping: true));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("setid")]
    [InlineData("links")]
    [InlineData("length")]
    [InlineData("inode")]
    public void MarkerMetadataRequiresTheExactRootPrivateOrdinaryObject(string change)
    {
        var value = Marker();
        LinuxOwnerGuard.RequireMarker(value);
        value = change switch
        {
            "kind" => value with { Mode = 0x4180 },
            "uid" => value with { Uid = 123 },
            "gid" => value with { Gid = 123 },
            "write" => value with { Mode = 0x8190 },
            "read" => value with { Mode = 0x8184 },
            "setid" => value with { Mode = 0x8980 },
            "links" => value with { LinkCount = 2 },
            "length" => value with { Length = 32 },
            _ => value with { Inode = 0 }
        };
        AssertClosed(() => LinuxOwnerGuard.RequireMarker(value));
    }

    [Fact]
    public void RenameMayChangeCtimeButCannotReplaceBytesIdentityOrAccess()
    {
        var before = Marker();
        Assert.True(LinuxOwnerGuard.SameMarkerObject(before, before with { ChangeSeconds = 5 }));
        Assert.False(LinuxOwnerGuard.SameMarkerObject(before, before with { Inode = 999 }));
        Assert.False(LinuxOwnerGuard.SameMarkerObject(before, before with { ModifySeconds = 5 }));
        Assert.False(LinuxOwnerGuard.SameMarkerObject(before, before with { DeviceMinor = 2 }));
    }

    [Fact]
    public void MalformedCompoundVariantsDoNotAcceptLeadingValidFields()
    {
        var (unit, service) = Properties();
        var command = service["ExecStart"].GetItem(0);
        service["ExecStart"] = VariantValue.ArrayOfVariant(new[] { command, command });
        AssertClosed(() => LinuxOwnerFacts.Parse(unit, service));
        service["ExecStart"] = VariantValue.ArrayOfVariant(new[] { VariantValue.Struct("/runtime/dotnet") });
        AssertClosed(() => LinuxOwnerFacts.Parse(unit, service));
        (unit, service) = Properties();
        unit["Conditions"] = VariantValue.ArrayOfVariant(new[] { VariantValue.Struct("ConditionPathExists") });
        AssertClosed(() => LinuxOwnerFacts.Parse(unit, service));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void WrongCompoundArrayKindsCannotEnterTheNativeOwnerProjection(int kind)
    {
        var wrong = kind switch
        {
            0 => VariantValue.ArrayOfVariant(System.Array.Empty<VariantValue>()),
            1 => (VariantValue)"canary",
            _ => VariantValue.Array(System.Array.Empty<string>())
        };
        // Each control isolates the actual shared wire-kind guard; other malformed members cannot mask it.
        AssertClosed(() => LinuxSystemdBackend.RequireOwnerStructArray(wrong));
    }

    [Fact]
    public void BootstrapTupleIsCompleteCopiedReadOnlyDataWithoutNativeAuthority()
    {
        var expected = new[]
        {
            "/usr/bin/env", "-i", "PATH=/usr/bin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8",
            "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_CLI_HOME=/tmp", "/runtime/dotnet", "/tool/app.dll",
            "evidence", "supervise", "--request", "/run/request.json"
        };
        var arguments = LinuxOwnerFacts.BootstrapArguments("/runtime/dotnet", "/tool/app.dll", "/run/request.json");
        Assert.Equal(13, arguments.Count);
        Assert.Equal(expected, arguments);
        Assert.Throws<NotSupportedException>(() => { ((IList<string>)arguments)[1] = "canary-option"; });
        var (unit, service) = Properties();
        var wireArguments = expected.ToArray();
        service["ExecStart"] = VariantValue.ArrayOfVariant(new[] { VariantValue.Struct("/usr/bin/env",
            VariantValue.Array(wireArguments), false, 0ul, 0ul, 0ul, 0ul, 123u, 0, 0) });
        var facts = LinuxOwnerFacts.Parse(unit, service);
        wireArguments[1] = "canary-option";
        service["ExecStart"] = VariantValue.ArrayOfVariant(System.Array.Empty<VariantValue>());
        Assert.Equal("/usr/bin/env", facts.Executable);
        Assert.Equal(expected, facts.Arguments);
        Require(facts);
    }

    [Theory]
    [InlineData("/bin/env")]
    [InlineData("/runtime/dotnet")]
    [InlineData("/usr/bin/env-canary")]
    public void AnotherExecutableCannotReplaceTheEnvironmentClearingBootstrap(string executable)
    {
        AssertClosed(() => Require(Sample() with { Executable = executable }));
    }

    [Theory]
    [InlineData("missing-i")]
    [InlineData("wrong-i")]
    [InlineData("extra-argument")]
    [InlineData("extra-assignment")]
    [InlineData("changed-assignment")]
    [InlineData("reordered-assignments")]
    [InlineData("missing-assignment")]
    [InlineData("duplicate-assignment")]
    [InlineData("direct-dotnet")]
    [InlineData("argv-zero")]
    [InlineData("role")]
    public void BootstrapArgumentsRejectMissingOptionsAndCallerAdditions(string change)
    {
        var facts = Sample();
        var arguments = facts.Arguments.ToList();
        switch (change)
        {
            case "missing-i": arguments.RemoveAt(1); break;
            case "wrong-i": arguments[1] = "-u"; break;
            case "extra-argument": arguments.Add("canary-argument"); break;
            case "extra-assignment": arguments.Insert(7, "DOTNET_STARTUP_HOOKS=canary"); break;
            case "changed-assignment": arguments[2] = "PATH=/canary"; break;
            case "reordered-assignments": (arguments[2], arguments[3]) = (arguments[3], arguments[2]); break;
            case "missing-assignment": arguments.RemoveAt(2); break;
            case "duplicate-assignment": arguments[3] = arguments[2]; break;
            case "direct-dotnet": arguments.RemoveRange(0, 7); break;
            case "argv-zero": arguments[0] = "/bin/env"; break;
            case "role": arguments[10] = "worker"; break;
        }
        AssertClosed(() => Require(facts with { Arguments = arguments }));
    }

    [Fact]
    public void WireArgumentBoundRejectsMoreThanTheCompleteBootstrapTuple()
    {
        var (unit, service) = Properties();
        var arguments = Sample().Arguments.Concat(new[] { "canary-argument" }).ToArray();
        service["ExecStart"] = VariantValue.ArrayOfVariant(new[] { VariantValue.Struct("/usr/bin/env",
            VariantValue.Array(arguments), false, 0ul, 0ul, 0ul, 0ul, 123u, 0, 0) });
        AssertClosed(() => LinuxOwnerFacts.Parse(unit, service));
    }

    [Theory]
    [InlineData(7, "/runtime/${canary}/dotnet")]
    [InlineData(7, "/runtime/%n/dotnet")]
    [InlineData(8, "/tool/${canary}/app.dll")]
    [InlineData(8, "/tool/%n/app.dll")]
    [InlineData(12, "/run/${canary}/request.json")]
    [InlineData(12, "/run/%n/request.json")]
    public void MatchingMetadataCannotAuthorizeExpansionSyntaxInAnySuppliedBootstrapPath(int index, string path)
    {
        var facts = Sample();
        var arguments = facts.Arguments.ToArray();
        arguments[index] = path;
        facts = facts with { Arguments = arguments };
        // Expected inputs match argv exactly; rejection must be the path contract, not an argv mismatch.
        AssertClosed(() => facts.Require(Run, 123, arguments[7], arguments[8], arguments[12],
            Now, TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/dotnet")]
    [InlineData("/")]
    [InlineData("/runtime//dotnet")]
    [InlineData("/runtime/../dotnet")]
    [InlineData("/runtime/./dotnet")]
    [InlineData("/runtime\\dotnet")]
    [InlineData("/runtime/canary\ndotnet")]
    public void BootstrapDataRequiresCanonicalAbsolutePaths(string? path)
    {
        AssertClosed(() => LinuxOwnerFacts.BootstrapArguments(path!, "/tool/app.dll", "/run/request.json"));
    }

    [Fact]
    public void MatchingRuntimeMetadataCannotTurnTheCommandIntoAnEnvironmentAssignment()
    {
        var facts = Sample();
        var arguments = facts.Arguments.ToArray();
        arguments[7] = "/runtime/dot=net";
        facts = facts with { Arguments = arguments };
        AssertClosed(() => facts.Require(Run, 123, arguments[7], arguments[8], arguments[12],
            Now, TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData("=runtime")]
    [InlineData("/runtime/dot=net")]
    public void BootstrapRuntimeMustBeANonAssignmentCommand(string runtimeHost)
    {
        AssertClosed(() => LinuxOwnerFacts.BootstrapArguments(runtimeHost, "/tool/app.dll", "/run/request.json"));
    }

    [Theory]
    [InlineData("/tool/app=entry.dll", "/run/request.json")]
    [InlineData("/tool/app.dll", "/run/request=data.json")]
    [InlineData("/tool/app=entry.dll", "/run/request=data.json")]
    public void EqualsInPositionalEntryAndRequestPathsDoesNotChangeTheCommandBoundary(string entryPath, string requestPath)
    {
        var facts = Sample();
        var arguments = LinuxOwnerFacts.BootstrapArguments("/runtime/dotnet", entryPath, requestPath);
        Assert.Equal("/runtime/dotnet", arguments[7]);
        Assert.Equal(entryPath, arguments[8]);
        Assert.Equal(requestPath, arguments[12]);
        (facts with { Arguments = arguments }).Require(Run, 123, arguments[7], entryPath, requestPath,
            Now, TimeSpan.FromSeconds(30));
    }

    private static LinuxOwnerFacts Sample() { var (unit, service) = Properties(); return LinuxOwnerFacts.Parse(unit, service); }
    private static LinuxProtectedMetadata Marker() => new(0, 1, 7, 0, 0, 0x8180, 1, 33, 1, 0, 1, 0);
    private static void AssertClosed(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
    }
    private static (Dictionary<string, VariantValue> Unit, Dictionary<string, VariantValue> Service) Properties()
    {
        var name = LinuxUnitName.Create(LinuxUnitRole.Owner, Run).Value;
        var unit = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["Id"] = name, ["LoadState"] = "loaded", ["ActiveState"] = "active", ["SubState"] = "running",
            ["ActiveEnterTimestampMonotonic"] = 1_000_000ul, ["ConditionResult"] = true,
            ["Conditions"] = VariantValue.ArrayOfVariant(new[] { VariantValue.Struct("ConditionPathExists", false, false, LinuxOwnerFacts.GuardPath(Run), 1) })
        };
        var argv = new[]
        {
            "/usr/bin/env", "-i", "PATH=/usr/bin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8",
            "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_CLI_HOME=/tmp", "/runtime/dotnet", "/tool/app.dll",
            "evidence", "supervise", "--request", "/run/request.json"
        };
        var service = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["ControlGroup"] = "/system.slice/" + name, ["MainPID"] = 123u, ["ExecMainPID"] = 123u,
            ["ExecMainCode"] = 0, ["ExecMainStatus"] = 0, ["User"] = "0", ["Group"] = "0", ["Type"] = "exec",
            ["KillMode"] = "control-group", ["RemainAfterExit"] = false, ["RuntimeMaxUSec"] = 25_000_000ul,
            ["TimeoutStopUSec"] = 5_000_000ul, ["Restart"] = "no", ["FinalKillSignal"] = 9, ["SendSIGKILL"] = true,
            ["ExecStart"] = VariantValue.ArrayOfVariant(new[] { VariantValue.Struct("/usr/bin/env", VariantValue.Array(argv), false,
                0ul, 0ul, 0ul, 0ul, 123u, 0, 0) }),
            ["Environment"] = VariantValue.Array(LinuxOwnerFacts.FixedEnvironment.ToArray()),
            ["UnsetEnvironment"] = VariantValue.Array(LinuxOwnerFacts.UnsafeEnvironment.ToArray()),
            ["EnvironmentFiles"] = VariantValue.ArrayOfVariant(System.Array.Empty<VariantValue>()),
            ["PassEnvironment"] = VariantValue.Array(System.Array.Empty<string>())
        };
        return (unit, service);
    }
}
