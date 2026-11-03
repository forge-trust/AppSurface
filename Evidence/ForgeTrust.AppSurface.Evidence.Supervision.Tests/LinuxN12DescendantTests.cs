using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Private grammar, actual owned-stream sequencing and detached kernel data only; no native admission or descendant proof.</summary>
public sealed class LinuxN12DescendantTests
{
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1\n")]
    [InlineData("2147483648")]
    [InlineData("canary")]
    public void NoncanonicalPidDataRejectsWithoutEcho(string value)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxN12Descendant.ParsePid(value));
        Assert.Equal("ASEVD410", error.Code);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void SameImageLaunchDataHasClosedArgumentsAndInheritedOutput()
    {
        var start = LinuxN12Descendant.CreateStartInfo("/usr/share/dotnet/dotnet", "/run/tool/appsurface.dll", 4001);
        Assert.Equal("/usr/share/dotnet/dotnet", start.FileName);
        Assert.Equal(new[] { "/run/tool/appsurface.dll", "evidence", "n12-output-holder", "--parent", "4001" }, start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.False(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.False(start.RedirectStandardInput);
        Assert.Equal(2147483647, LinuxN12Descendant.ParsePid("2147483647"));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxN12Descendant.CreateStartInfo("relative", "/run/tool/appsurface.dll", 1));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxN12Descendant.CreateStartInfo("/usr/dotnet", "/run/./appsurface.dll", 1));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxN12Descendant.CreateStartInfo("/usr/dotnet", "/run/appsurface.dll", 0));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxN12Descendant.CreateStartInfo("/usr/dotnet", "/run/appsurface.exe", 1));
    }

    [Theory]
    [InlineData("N12-child-ready:123\n", true)]
    [InlineData("N12-child-ready:124\n", false)]
    [InlineData("N12-child-ready:0123\n", false)]
    [InlineData("N12-child-ready:123", false)]
    [InlineData("canary\n", false)]
    public async Task ChildAcknowledgementRequiresActualSelectedPidAndCompleteFrame(string text, bool valid)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));
        if (valid) await LinuxN12Descendant.ReadReadyAsync(stream, 123, default);
        else Assert.Equal("ASEVD410", (await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            LinuxN12Descendant.ReadReadyAsync(stream, 123, default))).Code);
    }

    [Fact]
    public async Task AckOversizeAndCancelledReadCannotSynthesizeChildStartup()
    {
        using var over = new MemoryStream(Enumerable.Repeat((byte)'x', LinuxN12Descendant.MaximumFrameBytes + 1).ToArray());
        await Assert.ThrowsAsync<EvidenceAdmissionException>(() => LinuxN12Descendant.ReadReadyAsync(over, 123, default));
        Assert.Equal(LinuxN12Descendant.MaximumFrameBytes, over.Position);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var valid = new MemoryStream(Encoding.ASCII.GetBytes("N12-child-ready:123\n"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LinuxN12Descendant.ReadReadyAsync(valid, 123, cancelled.Token));
    }

    [Fact]
    public async Task OriginalAckReadFailureIsClosedAndDoesNotEchoItsException()
    {
        using var stream = new BrokenRead();
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => LinuxN12Descendant.ReadReadyAsync(stream, 123, default));
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("N12-descendant:123\n", true)]
    [InlineData("N12-descendant:0\n", false)]
    [InlineData("N12-descendant:0123\n", false)]
    [InlineData("N12-descendant:-123\n", false)]
    [InlineData("N12-descendant:2147483648\n", false)]
    [InlineData("N12-descendant:123\r\n", false)]
    [InlineData("N12-descendant:123", false)]
    [InlineData("canary\nN12-descendant:123\n", false)]
    public async Task OriginalPumpFrameIsBoundedFirstLineDataOnly(string text, bool valid)
    {
        var observer = new SupervisionDescendantObservation();
        foreach (var value in Encoding.ASCII.GetBytes(text)) observer.Feed(new[] { value });
        observer.Complete();
        if (valid) Assert.Equal(123, await observer.Observed);
        else await Assert.ThrowsAsync<EvidenceAdmissionException>(() => observer.Observed);
    }

    [Fact]
    public async Task OversizeFrameAndEarlyEofStayRejectedAfterLaterValidBytes()
    {
        foreach (var oversize in new[] { true, false })
        {
            var observer = new SupervisionDescendantObservation();
            if (oversize) observer.Feed(Enumerable.Repeat((byte)'x', LinuxN12Descendant.MaximumFrameBytes + 1).ToArray());
            else observer.Complete();
            observer.Feed(Encoding.ASCII.GetBytes("N12-descendant:123\n"));
            await Assert.ThrowsAsync<EvidenceAdmissionException>(() => observer.Observed);
        }
    }

    [Fact]
    public async Task LeaderAndLocalWriteClosureCannotJoinInheritedWriterOrCloseReadEarly()
    {
        var held = new HeldRead();
        var frame = Encoding.ASCII.GetBytes("N12-descendant:123\n");
        var phase = new SupervisionDescendantObservation();
        await using var ownership = new SupervisionOutputPipeOwnership(held, new MemoryStream(frame), new CloseOnly(), new CloseOnly());
        var collection = ownership.BeginCollectAsync(default, descendant: phase);
        Task? disposal = null;
        try
        {
            Assert.Equal(123, await phase.Observed.WaitAsync(TimeSpan.FromSeconds(2)));
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            ownership.CloseWriteCopies();
            Assert.False(collection.IsCompleted);
            disposal = ownership.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(held.Closed);
        }
        finally
        {
            held.Release.TrySetResult();
            await collection.WaitAsync(TimeSpan.FromSeconds(2));
            await (disposal ?? ownership.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2));
        }
        var joined = await collection;
        Assert.True(joined.Successful);
        Assert.True(joined.Stdout.EndOfStream);
        Assert.True(joined.Stderr.EndOfStream);
        Assert.Equal(frame.Length, joined.ReceivedBytes); // One original charged reader, no duplicate accounting.
        Assert.True(held.Closed);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("wrong-leader")]
    [InlineData("signalled")]
    [InlineData("failed")]
    [InlineData("same-pid")]
    [InlineData("dead-child")]
    [InlineData("wrong-uid")]
    [InlineData("wrong-gid")]
    [InlineData("wrong-cgroup")]
    [InlineData("absent-group")]
    [InlineData("empty-group")]
    [InlineData("frozen-group")]
    [InlineData("joined-output")]
    public void BeforeStopKernelDataRequiresDistinctLiveChildAndUnfinishedOutput(string changed)
    {
        var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var terminal = Terminal(unit);
        var child = Child(unit);
        var group = Populated();
        LinuxN12LeaderExitObservation.RequireBeforeStop(terminal, 4001, child, 65010, 65011, unit, group, false);
        switch (changed)
        {
            case "running": terminal = terminal with { MainPid = 4001 }; break;
            case "wrong-leader": terminal = terminal with { ExecMainPid = 4002 }; break;
            case "signalled": terminal = terminal with { ExecMainCode = 2 }; break;
            case "failed": terminal = terminal with { ExecMainStatus = 1 }; break;
            case "same-pid": child = child with { Stat = child.Stat with { Pid = 4001 } }; break;
            case "dead-child": child = child with { Stat = child.Stat with { State = 'Z' } }; break;
            case "wrong-uid": child = child with { Uids = child.Uids with { Effective = 65012 } }; break;
            case "wrong-gid": child = child with { Gids = child.Gids with { FileSystem = 65012 } }; break;
            case "wrong-cgroup": child = child with { ControlGroup = "/system.slice/canary.service" }; break;
            case "absent-group": group = new(false, null, null, null, null, null); break;
            case "empty-group": group = group with { Populated = false }; break;
            case "frozen-group": group = group with { Frozen = true }; break;
        }
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxN12LeaderExitObservation.RequireBeforeStop(
            terminal, 4001, child, 65010, 65011, unit, group, changed == "joined-output"));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetachedReceiptRequiresActualCollectorEofAndRetainsSeparateCustodyRequirement()
    {
        var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, Guid.NewGuid());
        var value = new LinuxN12LeaderExitObservation(Guid.NewGuid(), 4001, Child(unit), Populated(), Terminal(unit));
        using var stdout = new MemoryStream(new byte[] { 1, 2 });
        using var stderr = new MemoryStream(new byte[] { 3 });
        var output = await new SupervisionOutputCollector().CollectAsync(stdout, stderr, default);
        var empty = Populated() with { Populated = false };
        var bytes = value.EncodeJoined(output, empty);
        using var json = JsonDocument.Parse(bytes);
        Assert.True(bytes.Length <= 4096);
        Assert.True(json.RootElement.GetProperty("custody_and_accounts_not_yet_closed").GetBoolean());
        Assert.False(json.RootElement.GetProperty("native_authority").GetBoolean());
        Assert.False(json.RootElement.GetProperty("native_acceptance").GetBoolean());
        Assert.Equal(3, json.RootElement.GetProperty("received_bytes").GetInt64());
        Assert.Throws<EvidenceAdmissionException>(() => value.EncodeJoined(output, Populated()));
        Assert.Throws<EvidenceAdmissionException>(() => value.EncodeJoined(output with { Stdout = output.Stdout with { EndOfStream = false } }, empty));
        Assert.Throws<EvidenceAdmissionException>(() => (value with { Generation = Guid.Empty }).EncodeJoined(output, empty));
    }

    [Fact]
    public void PrivateRoleCannotBecomeOrdinaryWorkerOrAcceptExtraOptions()
    {
        var arguments = new[] { "evidence", LinuxN12Descendant.Role, "--parent", "123" };
        Assert.True(EvidenceProcessRoleParser.IsReserved(arguments));
        Assert.True(EvidenceProcessRoleParser.IsReserved(new[] { "Evidence", "N12-output-holder" }));
#if EVIDENCE_PRIVATE_N12
        {
            var selection = EvidenceProcessRoleParser.Parse(arguments);
            Assert.Equal(EvidenceProcessRole.OutputHolder, selection.Role);
            Assert.Equal("123", selection.Path);
            Assert.False(selection.Help);
        }
#else
        Assert.Equal("ASEVD402", Assert.Throws<EvidenceAdmissionException>(() => EvidenceProcessRoleParser.Parse(arguments)).Code);
#endif
        Assert.Throws<EvidenceAdmissionException>(() => EvidenceProcessRoleParser.Parse(arguments.Concat(new[] { "canary" }).ToArray()));
        Assert.Throws<EvidenceAdmissionException>(() => EvidenceProcessRoleParser.Parse(new[] { "evidence", LinuxN12Descendant.Role, "--help" }));
        Assert.Equal("ASEVD402", Assert.Throws<EvidenceAdmissionException>(() => EvidenceProcessRoleParser.Parse(
            new[] { "evidence", LinuxN12Descendant.Role, "--parent", "canary" })).Code);
    }

    private static LinuxCgroupSample Populated() => new(true, true, false, 0, 28, 100);
    private static LinuxProcessSample Child(LinuxUnitName unit) => new(new(4002, 500, 'S'),
        new(65010, 65010, 65010, 65010), new(65011, 65011, 65011, 65011), "/system.slice/" + unit.Value);
    private static LinuxUnitProperties Terminal(LinuxUnitName unit) => new(unit.Value, "loaded", "active", "exited",
        "/system.slice/" + unit.Value, 0, 4001, 1, 0, "65010", "65011", "exec", "control-group", true, 1, 1);

    private sealed class CloseOnly : IDisposable { public void Dispose() { } }
    private sealed class BrokenRead : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("canary-private-path"));
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class HeldRead : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false); // Native owner stop must supply real EOF, never token cancellation.
            return 0;
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
