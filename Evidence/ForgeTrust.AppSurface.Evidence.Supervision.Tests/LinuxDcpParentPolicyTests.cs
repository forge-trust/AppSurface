using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ParentFact = ForgeTrust.AppSurface.Evidence.Supervision.LinuxApplicationDcpIdentity.LinuxApplicationParentFact;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached parent-graph and executable-policy controls; no live process identity or admission is created.</summary>
public sealed class LinuxDcpParentPolicyTests
{
    private const uint Uid = 65010;
    private const uint Gid = 65011;
    private static readonly LinuxUnitName Unit = LinuxUnitName.Create(LinuxUnitRole.Application,
        Guid.Parse("3133bf3e-7344-4428-b826-f5464a4d4991"));

    [Theory]
    [InlineData("empty")]
    [InlineData("wrong-first-pid")]
    [InlineData("wrong-first-start")]
    [InlineData("first-parent-zero")]
    [InlineData("first-parent-overflow")]
    [InlineData("self-parent")]
    [InlineData("skipped-link")]
    [InlineData("trailing-link")]
    [InlineData("no-dcp")]
    [InlineData("last-parent-zero")]
    [InlineData("last-parent-overflow")]
    [InlineData("intermediate-pid-mismatch")]
    [InlineData("intermediate-zombie")]
    [InlineData("intermediate-unit")]
    public void ParentGraphRejectsIncompleteDiscontinuousOrNonLiveLinks(string mutation)
    {
        var resource = Sample(300);
        var dcp = Sample(500);
        var chain = Chain();
        LinuxApplicationDcpIdentity.RequireParentChain(chain, resource, dcp, Uid, Gid, Unit);
        chain = mutation switch
        {
            "empty" => [],
            "wrong-first-pid" => [chain[0] with { Pid = 302, Process = Sample(302) }, chain[1]],
            "wrong-first-start" => [chain[0] with { Process = Sample(300, 999) }, chain[1]],
            "first-parent-zero" => [chain[0] with { ParentPid = 0 }, chain[1]],
            "first-parent-overflow" => [chain[0] with { ParentPid = uint.MaxValue }, chain[1]],
            "self-parent" => [chain[0] with { ParentPid = 300 }, chain[1]],
            "skipped-link" => [chain[0] with { ParentPid = 302 }, chain[1]],
            "trailing-link" => [chain[0] with { ParentPid = 500 }, chain[1]],
            "no-dcp" => [chain[0], chain[1] with { ParentPid = 502 }],
            "last-parent-zero" => [chain[0], chain[1] with { ParentPid = 0 }],
            "last-parent-overflow" => [chain[0], chain[1] with { ParentPid = uint.MaxValue }],
            "intermediate-pid-mismatch" => [chain[0], chain[1] with { Process = Sample(302) }],
            "intermediate-zombie" => [chain[0], chain[1] with { Process = Sample(301) with { Stat = new(301, 301, 'Z') } }],
            "intermediate-unit" => [chain[0], chain[1] with { Process = Sample(301) with { ControlGroup = "/system.slice/CANARY.service" } }],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Reject(() => LinuxApplicationDcpIdentity.RequireParentChain(chain, resource, dcp, Uid, Gid, Unit));
    }

    [Fact]
    public void SameProcessParentComparisonRequiresTheSameStartAndNoInventedLinks()
    {
        var process = Sample(300);
        LinuxApplicationDcpIdentity.RequireParentChain([], process, process, Uid, Gid, Unit);
        Reject(() => LinuxApplicationDcpIdentity.RequireParentChain([], process,
            Sample(300, 999), Uid, Gid, Unit));
        Reject(() => LinuxApplicationDcpIdentity.RequireParentChain(Chain(), process, process, Uid, Gid, Unit));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("pid")]
    [InlineData("parent")]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("unit")]
    public void RepeatedWalkCannotChangeItsLinksOrProcessCredentials(string mutation)
    {
        var first = Chain();
        var second = Chain();
        LinuxApplicationDcpIdentity.RequireSameParentWalk(first, second);
        switch (mutation)
        {
            case "length": second = [second[0]]; break;
            case "pid": second[1] = second[1] with { Pid = 302 }; break;
            case "parent": second[1] = second[1] with { ParentPid = 501 }; break;
            case "uid": second[1] = second[1] with { Process = Sample(301) with { Uids = new(Uid, Uid, Uid, 0) } }; break;
            case "gid": second[1] = second[1] with { Process = Sample(301) with { Gids = new(Gid, Gid, Gid, 0) } }; break;
            case "unit": second[1] = second[1] with { Process = Sample(301) with { ControlGroup = "/system.slice/CANARY.service" } }; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Reject(() => LinuxApplicationDcpIdentity.RequireSameParentWalk(first, second));
    }

    [Theory]
    [InlineData("empty-chain")]
    [InlineData("deep-chain")]
    [InlineData("empty-candidates")]
    [InlineData("excess-candidates")]
    [InlineData("candidate-zombie")]
    [InlineData("candidate-unit")]
    public void NearestAncestorComparisonRequiresBoundedLiveCandidateData(string mutation)
    {
        var resource = Sample(300);
        var chain = Chain();
        LinuxProcessSample[] candidates = [Sample(500)];
        Assert.Equal(500u, LinuxApplicationDcpIdentity.FindNearestDcpAncestor(chain, resource, candidates, Uid, Gid, Unit));
        switch (mutation)
        {
            case "empty-chain": chain = []; break;
            case "deep-chain": chain = Enumerable.Repeat(chain[0], 17).ToArray(); break;
            case "empty-candidates": candidates = []; break;
            case "excess-candidates": candidates = Enumerable.Range(500, 129).Select(id => Sample((uint)id)).ToArray(); break;
            case "candidate-zombie": candidates = [Sample(500) with { Stat = new(500, 500, 'Z') }]; break;
            case "candidate-unit": candidates = [Sample(500) with { ControlGroup = "/system.slice/CANARY.service" }]; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Reject(() => LinuxApplicationDcpIdentity.FindNearestDcpAncestor(chain, resource, candidates, Uid, Gid, Unit));
    }

    [Theory]
    [InlineData(0x80)] // Owner write.
    [InlineData(0x10)] // Group write.
    [InlineData(0x02)] // Other write.
    [InlineData(0x800)] // Set UID.
    [InlineData(0x400)] // Set GID.
    [InlineData(0x200)] // Sticky bit on a file.
    public void AuditedRuntimeExecutableRejectsEveryWriteAndSpecialPermission(int addedMode)
    {
        var readOnly = new LinuxDcpFileMetadata(8, 1, 42, 0x8000, 0, 0x816d, 8192);
        LinuxApplicationDcpIdentity.RequireAuditedRuntimeHost(readOnly);
        Reject(() => LinuxApplicationDcpIdentity.RequireAuditedRuntimeHost(
            readOnly with { Mode = (ushort)(readOnly.Mode | addedMode) }));
    }

    [Theory]
    [InlineData("4294967296\n")]
    [InlineData("10000000000\n")]
    [InlineData("1\n\n2\n")]
    public void ProcessListRejectsNumericOverflowAndEmptyInteriorRows(string text)
    {
        Assert.Equal(new uint[] { 1, 2 }, LinuxApplicationDcpIdentity.ParseProcessIds("1\n2\n"u8));
        Reject(() => LinuxApplicationDcpIdentity.ParseProcessIds(Encoding.ASCII.GetBytes(text)));
    }

    [Theory]
    [InlineData("runtime", "relative/dotnet")]
    [InlineData("runtime", "/run/./dotnet")]
    [InlineData("runtime", "/run//dotnet")]
    [InlineData("runtime", "/run/CANARY\0/dotnet")]
    [InlineData("resource", "/run/../resource/NativeHttpResource.dll")]
    [InlineData("resource", "/run//resource/NativeHttpResource.dll")]
    [InlineData("socket", "/run/./http.sock")]
    [InlineData("socket", "/run/other.sock")]
    public void ExpectedResourcePathsMustBeCanonicalBeforeCommandByteComparison(string field, string path)
    {
        const string runtime = "/run/runtime/dotnet";
        const string resource = "/run/bundle/resource/NativeHttpResource.dll";
        const string socket = "/run/scratch/http.sock";
        byte[] command = CommandBytes(runtime, resource, socket);
        LinuxApplicationDcpIdentity.RequireResourceCommandBytes(command, runtime, resource, socket);
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(command,
            field == "runtime" ? path : runtime,
            field == "resource" ? path : resource,
            field == "socket" ? path : socket));
    }

    [Fact]
    public void ExpectedResourcePathsRejectValuesOutsideTheLengthBounds()
    {
        const string runtime = "/run/runtime/dotnet";
        const string resource = "/run/bundle/resource/NativeHttpResource.dll";
        const string socket = "/run/scratch/http.sock";
        var command = CommandBytes(runtime, resource, socket);

        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(command, "/", resource, socket));
        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(
            command, "/" + new string('a', 4096), resource, socket));
    }

    [Fact]
    public void ExpectedResourcePathWithMalformedUtf16IsRejectedWithoutEchoingInput()
    {
        const string runtime = "/run/runtime/dotnet";
        const string resource = "/run/bundle/resource/NativeHttpResource.dll";
        const string socket = "/run/scratch/http.sock";
        var command = CommandBytes(runtime, resource, socket);
        var malformedRuntime = "/run/CANARY-\uD800/dotnet";

        Reject(() => LinuxApplicationDcpIdentity.RequireResourceCommandBytes(
            command, malformedRuntime, resource, socket));
    }

    private static byte[] CommandBytes(string runtime, string resource, string socket) =>
        [.. new[] { runtime, resource, "--socket", socket, "--case", "normal" }
            .SelectMany(static value => Encoding.UTF8.GetBytes(value + "\0"))];

    private static ParentFact[] Chain() => [new(300, 301, Sample(300)), new(301, 500, Sample(301))];

    private static LinuxProcessSample Sample(uint pid, ulong? start = null) => new(new(pid, start ?? pid, 'S'),
        new(Uid, Uid, Uid, Uid), new(Gid, Gid, Gid, Gid), "/system.slice/" + Unit.Value);

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("CANARY", error.Message, StringComparison.Ordinal);
    }
}
