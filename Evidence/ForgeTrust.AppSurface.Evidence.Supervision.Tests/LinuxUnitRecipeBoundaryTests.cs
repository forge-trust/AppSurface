using System.IO.Pipes;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Recipe validation with real borrowed pipe handles; no backend, process owner or admission is created.</summary>
public sealed class LinuxUnitRecipeBoundaryTests
{
    private static readonly Guid Generation = Guid.Parse("ee4fb587-3c78-42ce-ab72-f044c74bf15b");

    /// <summary>Supplies independently named invalid producer recipe boundaries.</summary>
    public static IEnumerable<object[]> ProducerBoundaries()
    {
        foreach (var name in new[] { "unit-null", "owner-null", "unit-role", "owner-role", "generation", "uid-zero", "uid-max", "gid-zero", "gid-max",
            "stop-zero", "stop-large", "remaining-equal", "remaining-large", "runtime-submicrosecond", "runtime-too-large",
            "stdout-null", "stderr-null", "stdout-closed", "stderr-closed",
            "host-outside", "solution-outside", "token-outside", "package-outside", "runtime-tool", "subject-original", "subject-raw", "subject-tool", "subject-runtime",
            "results-subject", "results-scratch", "scratch-tool", "scratch-runtime", "results-raw", "scratch-raw", "writable-runtime", "writable-denied", "runtime-denied", "denied-overlap" }) yield return [name];
        for (var i = 0; i < 13; i++) yield return ["path-null-" + i];
        foreach (var name in new[] { "relative", "empty", "double", "dot", "parent", "percent", "dollar", "control", "long", "utf8", "surrogate", "socket-long" }) yield return ["path-" + name];
    }

    [Theory]
    [MemberData(nameof(ProducerBoundaries))]
    public void ProducerRejectsOneUnsafeBoundaryWhileAdjacentRecipeRemainsValid(string boundary)
    {
        using var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var error = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var paths = ProducerPaths();
        var unit = LinuxUnitName.Create(LinuxUnitRole.Producer, Generation);
        var owner = LinuxUnitName.Create(LinuxUnitRole.Owner, Generation);
        uint uid = 65012, gid = 65013;
        var remaining = TimeSpan.FromSeconds(120); var stopping = TimeSpan.FromSeconds(5);
        SafeHandle stdout = output.SafePipeHandle, stderr = error.SafePipeHandle;
        LinuxProducerUnit Create() => LinuxProducerUnit.Create(unit, owner, paths[0], paths[1], paths[2], paths[3], paths[4], paths[5], paths[6], paths[7], paths[8], paths[9], paths[10], paths[11], paths[12], uid, gid, remaining, stopping, stdout, stderr);
        Assert.Equal(115_000_000UL, Create().RuntimeMicroseconds);
        switch (boundary)
        {
            case "unit-null": unit = null!; break;
            case "owner-null": owner = null!; break;
            case "unit-role": unit = owner; break;
            case "owner-role": owner = unit; break;
            case "generation": owner = LinuxUnitName.Create(LinuxUnitRole.Owner, Guid.Parse("35a514e2-7d39-4023-8a25-111603541d0d")); break;
            case "uid-zero": uid = 0; break;
            case "uid-max": uid = uint.MaxValue; break;
            case "gid-zero": gid = 0; break;
            case "gid-max": gid = uint.MaxValue; break;
            case "stop-zero": stopping = TimeSpan.Zero; break;
            case "stop-large": stopping = TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1); break;
            case "remaining-equal": remaining = stopping; break;
            case "remaining-large": remaining = TimeSpan.FromSeconds(LinuxProducerUnit.MaximumRuntimeSeconds + 30) + TimeSpan.FromTicks(1); break;
            case "runtime-submicrosecond": remaining = stopping + TimeSpan.FromTicks(1); break;
            case "runtime-too-large": remaining = TimeSpan.FromSeconds(LinuxProducerUnit.MaximumRuntimeSeconds + 30); break;
            case "stdout-null": stdout = null!; break;
            case "stderr-null": stderr = null!; break;
            case "stdout-closed": stdout.Dispose(); break;
            case "stderr-closed": stderr.Dispose(); break;
            case "host-outside": paths[0] = "/other/dotnet"; break;
            case "solution-outside": paths[8] = "/other/Subject.csproj"; break;
            case "token-outside": paths[10] = "/other/token"; break;
            case "package-outside": paths[12] = "/other/packages"; break;
            case "runtime-tool": paths[1] = paths[2]; paths[0] = paths[1] + "/dotnet"; break;
            case "subject-original": SetSubject(paths[3]); break;
            case "subject-raw": SetSubject(paths[4]); break;
            case "subject-tool": SetSubject(paths[2]); break;
            case "subject-runtime": SetSubject(paths[1]); break;
            case "results-subject": SetResults(paths[7] + "/results"); break;
            case "results-scratch": SetResults(paths[11] + "/results"); break;
            case "scratch-tool": SetScratch(paths[2] + "/scratch"); break;
            case "scratch-runtime": SetScratch(paths[1] + "/scratch"); break;
            case "results-raw": SetResults(paths[4] + "/results"); break;
            case "scratch-raw": SetScratch(paths[4] + "/scratch"); break;
            case "writable-runtime": SetResults(paths[1] + "/results"); break;
            case "writable-denied": SetSubject(paths[6] + "/subject"); break;
            case "runtime-denied": paths[1] = paths[3] + "/sdk"; paths[0] = paths[1] + "/dotnet"; break;
            case "denied-overlap": paths[3] = paths[2] + "/source"; break;
            case "path-socket-long": paths[5] = "/" + new string('a', 100); break;
            default:
                if (boundary.StartsWith("path-null-", StringComparison.Ordinal)) paths[int.Parse(boundary[10..], System.Globalization.CultureInfo.InvariantCulture)] = null!;
                else paths[2] = BadPath(boundary[5..], producer: true);
                break;
        }
        var failure = Assert.Throws<EvidenceAdmissionException>(() => Create());
        Assert.Equal("ASEVD410", failure.Code);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("canary", failure.Message, StringComparison.Ordinal);
        void SetSubject(string value) { paths[7] = value; paths[8] = value + "/Subject.csproj"; }
        void SetResults(string value) { paths[9] = value; paths[10] = value + "/token"; }
        void SetScratch(string value) { paths[11] = value; paths[12] = value + "/nuget"; }
    }

    /// <summary>Supplies independently named invalid application recipe boundaries.</summary>
    public static IEnumerable<object[]> ApplicationBoundaries()
    {
        foreach (var name in new[] { "unit-null", "owner-null", "unit-role", "owner-role", "generation", "uid-zero", "uid-max", "gid-zero", "gid-max", "limits-null",
            "tasks-zero", "tasks-large", "memory-zero", "memory-large", "scratch-zero", "scratch-large", "output-zero", "output-large", "start-zero", "start-large", "stop-zero", "stop-large", "remaining-equal", "remaining-large",
            "stdout-null", "stderr-null", "stdout-closed", "stderr-closed", "host-outside", "apphost-outside", "apphost-nested", "input-outside", "bundle-scratch", "runtime-scratch", "runtime-bundle" }) yield return [name];
        for (var i = 0; i < 12; i++) yield return ["path-null-" + i];
        foreach (var name in new[] { "relative", "empty", "double", "dot", "parent", "percent", "dollar", "control", "long", "backslash" }) yield return ["path-" + name];
        foreach (var denied in new[] { 2, 3, 4, 5, 6, 10 })
        foreach (var target in new[] { 1, 7, 9 }) yield return [$"denied-{denied}-{target}"];
    }

    [Theory]
    [MemberData(nameof(ApplicationBoundaries))]
    public void ApplicationRejectsOneUnsafeBoundaryWhileAdjacentRecipeRemainsValid(string boundary)
    {
        using var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var error = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var paths = new[] { "/run/sdk/dotnet", "/run/sdk", "/run/tool", "/run/subject", "/run/worker/output", "/run/worker/control", "/run/producer", "/run/app/bundle", "/run/app/bundle/AppHost.dll", "/run/app/scratch", "/run/app/denied-output", "/run/app/bundle/proof-input/declared.txt" };
        var unit = LinuxUnitName.Create(LinuxUnitRole.Application, Generation);
        var owner = LinuxUnitName.Create(LinuxUnitRole.Owner, Generation);
        uint uid = 65014, gid = 65015;
        LinuxApplicationLimits limits = new(1L << 28, 1L << 30, 128, 1048576, 120, 5);
        var remaining = TimeSpan.FromSeconds(180);
        SafeHandle stdout = output.SafePipeHandle, stderr = error.SafePipeHandle;
        LinuxApplicationUnit Create() => LinuxApplicationUnit.Create(unit, owner, paths[0], paths[1], paths[2], paths[3], paths[4], paths[5], paths[6], paths[7], paths[8], paths[9], paths[10], paths[11], uid, gid, limits, remaining, stdout, stderr);
        Assert.Equal(128UL, Create().Properties["TasksMax"].GetUInt64());
        switch (boundary)
        {
            case "unit-null": unit = null!; break;
            case "owner-null": owner = null!; break;
            case "unit-role": unit = owner; break;
            case "owner-role": owner = unit; break;
            case "generation": owner = LinuxUnitName.Create(LinuxUnitRole.Owner, Guid.Parse("35a514e2-7d39-4023-8a25-111603541d0d")); break;
            case "uid-zero": uid = 0; break;
            case "uid-max": uid = uint.MaxValue; break;
            case "gid-zero": gid = 0; break;
            case "gid-max": gid = uint.MaxValue; break;
            case "limits-null": limits = null!; break;
            case "tasks-zero": limits = limits with { MaximumTasks = 0 }; break;
            case "tasks-large": limits = limits with { MaximumTasks = 129 }; break;
            case "memory-zero": limits = limits with { MemoryBytes = 0 }; break;
            case "memory-large": limits = limits with { MemoryBytes = (1L << 30) + 1 }; break;
            case "scratch-zero": limits = limits with { ScratchBytes = 0 }; break;
            case "scratch-large": limits = limits with { ScratchBytes = (1L << 30) + 1 }; break;
            case "output-zero": limits = limits with { MaximumOutputBytes = 0 }; break;
            case "output-large": limits = limits with { MaximumOutputBytes = 1048577 }; break;
            case "start-zero": limits = limits with { StartSeconds = 0 }; break;
            case "start-large": limits = limits with { StartSeconds = 121 }; break;
            case "stop-zero": limits = limits with { StoppingSeconds = 0 }; break;
            case "stop-large": limits = limits with { StoppingSeconds = 31 }; break;
            case "remaining-equal": remaining = TimeSpan.FromSeconds(limits.StoppingSeconds); break;
            case "remaining-large": remaining = TimeSpan.FromHours(1) + TimeSpan.FromTicks(1); break;
            case "stdout-null": stdout = null!; break;
            case "stderr-null": stderr = null!; break;
            case "stdout-closed": stdout.Dispose(); break;
            case "stderr-closed": stderr.Dispose(); break;
            case "host-outside": paths[0] = "/other/dotnet"; break;
            case "apphost-outside": paths[8] = "/other/AppHost.dll"; break;
            case "apphost-nested": paths[8] = paths[7] + "/nested/AppHost.dll"; break;
            case "input-outside": paths[11] = "/other/input.txt"; break;
            case "bundle-scratch": SetBundle(paths[9] + "/bundle"); break;
            case "runtime-scratch": SetRuntime(paths[9] + "/sdk"); break;
            case "runtime-bundle": SetRuntime(paths[7] + "/sdk"); break;
            default:
                if (boundary.StartsWith("path-null-", StringComparison.Ordinal)) paths[int.Parse(boundary[10..], System.Globalization.CultureInfo.InvariantCulture)] = null!;
                else if (boundary.StartsWith("denied-", StringComparison.Ordinal))
                {
                    var parts = boundary.Split('-');
                    paths[int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)] = paths[int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)] + "/denied";
                }
                else paths[2] = BadPath(boundary[5..], producer: false);
                break;
        }
        var failure = Assert.Throws<EvidenceAdmissionException>(() => Create());
        Assert.Equal("ASEVD410", failure.Code);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("canary", failure.Message, StringComparison.Ordinal);
        void SetBundle(string value) { paths[7] = value; paths[8] = value + "/AppHost.dll"; paths[11] = value + "/proof-input/declared.txt"; }
        void SetRuntime(string value) { paths[1] = value; paths[0] = value + "/dotnet"; }
    }

    [Fact]
    public void InclusiveProducerBudgetAndUtf8LimitsRemainRepresentableWithoutNativeDispatch()
    {
        using var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var error = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var paths = ProducerPaths();
        LinuxProducerUnit Create(TimeSpan stopping, TimeSpan remaining) => LinuxProducerUnit.Create(
            LinuxUnitName.Create(LinuxUnitRole.Producer, Generation), LinuxUnitName.Create(LinuxUnitRole.Owner, Generation),
            paths[0], paths[1], paths[2], paths[3], paths[4], paths[5], paths[6], paths[7], paths[8], paths[9], paths[10], paths[11], paths[12],
            1, uint.MaxValue - 1, remaining, stopping, output.SafePipeHandle, error.SafePipeHandle);
        var microsecond = Create(TimeSpan.FromTicks(10), TimeSpan.FromTicks(20));
        Assert.Equal(1UL, microsecond.RuntimeMicroseconds);
        Assert.Equal(1UL, microsecond.StoppingMicroseconds);
        var maximum = Create(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(LinuxProducerUnit.MaximumRuntimeSeconds + 30));
        Assert.Equal((ulong)LinuxProducerUnit.MaximumRuntimeSeconds * 1_000_000UL, maximum.RuntimeMicroseconds);
        paths[2] = "/" + new string('x', 4095);
        paths[5] = "/" + new string('é', 49) + "a"; // Exactly 100 UTF-8 bytes, not 100 UTF-16 code units.
        Assert.NotNull(Create(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(120)));
        paths[5] = "/" + new string('é', 50);
        Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(() => Create(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(120))).Code);
    }

    [Fact]
    public void InclusiveApplicationCapabilityAndPathLimitsRemainDataOnly()
    {
        using var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var error = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        LinuxApplicationUnit Create(LinuxApplicationLimits limits, TimeSpan remaining, string tool) => LinuxApplicationUnit.Create(
            LinuxUnitName.Create(LinuxUnitRole.Application, Generation), LinuxUnitName.Create(LinuxUnitRole.Owner, Generation),
            "/run/sdk/dotnet", "/run/sdk", tool, "/run/subject", "/run/worker/output", "/run/worker/control", "/run/producer",
            "/run/app/bundle", "/run/app/bundle/AppHost.dll", "/run/app/scratch", "/run/app/denied-output", "/run/app/bundle/input.txt",
            1, uint.MaxValue - 1, limits, remaining, output.SafePipeHandle, error.SafePipeHandle);
        var minimum = Create(new(1, 1, 1, 1, 1, 1), TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(10), "/run/tool");
        Assert.Equal(1UL, minimum.Properties["RuntimeMaxUSec"].GetUInt64());
        Assert.Equal(1UL, minimum.Properties["TasksMax"].GetUInt64());
        Assert.Equal(1UL, minimum.Properties["MemoryMax"].GetUInt64());
        var maximum = Create(new(1L << 30, 1L << 30, 128, 1048576, 120, 30), TimeSpan.FromHours(1), "/" + new string('x', 4094));
        Assert.Equal(128UL, maximum.Properties["TasksMax"].GetUInt64());
        Assert.Equal(30_000_000UL, maximum.Properties["TimeoutStopUSec"].GetUInt64());
    }

    private static string[] ProducerPaths() => ["/run/sdk/dotnet", "/run/sdk", "/run/tool", "/run/source", "/run/control/raw-results", "/run/control/worker/broker/control.sock", "/run/control/output", "/run/producer/subject", "/run/producer/subject/Subject.csproj", "/run/producer/results", "/run/producer/results/token", "/run/producer/scratch", "/run/producer/scratch/nuget"];

    private static string BadPath(string name, bool producer) => name switch
    {
        "relative" => "canary/relative", "empty" => "", "double" => "/canary//child", "dot" => "/canary/./child", "parent" => "/canary/../child",
        "percent" => "/canary/%name", "dollar" => "/canary/$name", "control" => "/canary/\nname", "backslash" => "/canary/\\child",
        "long" => "/" + new string('x', producer ? 4096 : 4095), "utf8" => "/" + new string('\u00e9', 2048), "surrogate" => "/canary/\ud800", _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
