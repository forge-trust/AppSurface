using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;
using Xunit;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReceiptSettlementProcessTests
{
    private static readonly TimeSpan ProcessDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task NormalCompletion_JoinsCooperativeDescendantThenClearsOnlyMatchingFlight()
    {
        var tree = SettlementTree.Create();
        using var holder = StartHolder(tree);
        Process? client = null;
        Process? heartbeat = null;
        string? executionId = null;
        string? nonce = null;
        try
        {
            await WaitForHolderAdmissionAsync(holder);
            var channel = await WaitForJsonAsync<ChannelRecord>(tree.ChannelPath);
            executionId = channel.ExecutionId;
            nonce = channel.Nonce;
            var launch = await WaitForJsonAsync<ClientLaunch>(tree.ClientPath);
            Assert.Equal(executionId, launch.ExecutionId);
            Assert.Equal(nonce, launch.Nonce);
            client = OpenRecordedProcess(launch.ProcessId);
            var firstHeartbeat = await WaitForHeartbeatAsync(tree, executionId, nonce);
            heartbeat = OpenRecordedProcess(firstHeartbeat.ProcessId);

            // Keep the controller's write handle open through a complete polling iteration.
            // The child must keep working until it sees the full execution ID and nonce.
            await using (var incompleteStop = new FileStream(tree.StopPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                await incompleteStop.WriteAsync(System.Text.Encoding.UTF8.GetBytes(executionId + "|"));
                await incompleteStop.FlushAsync();
                var latestSequence = ReadHeartbeats(tree, executionId, nonce).Max(static item => item.Sequence);
                var continued = await WaitForHeartbeatAfterAsync(tree, executionId, nonce, latestSequence + 1,
                    "heartbeat progress while the partial stop marker is open");
                Assert.Equal(firstHeartbeat.ProcessId, continued.ProcessId);
            }

            await RequestOwnedChildStopAsync(tree, executionId, nonce);
            await WaitForExitAsync(heartbeat, "cooperative heartbeat child");
            await WaitForExitAsync(client, "cooperative child client");
            await WaitForExitAsync(holder, "settlement holder");

            var completion = await WaitForJsonAsync<ClientCompletion>(tree.ClientCompletedPath);
            Assert.Equal(0, completion.HeartbeatExitCode);
            Assert.Null(completion.ChannelFailure);
            var settled = await WaitForJsonAsync<HolderSettlement>(tree.HolderSettledPath);
            Assert.Equal(0, settled.ChildExitCode);
            Assert.True(settled.SessionSettled);
            Assert.False(settled.Unknown);
            Assert.Null(settled.InFlightExecutionId);
            Assert.Null(settled.PayloadDirectoryName);

            var final = ReadAuthority(tree);
            Assert.Null(final?.InFlightExecutionId);
            Assert.Null(final?.PayloadDirectoryName);
            Assert.Null(final?.OriginManifestDigest);
        }
        finally
        {
            await CleanupOwnedProcessesAsync(tree, holder, client, heartbeat, executionId, nonce);
            tree.Dispose();
        }
    }

    [Fact]
    public async Task KilledHolder_LeavesLiveChildAndDurableInFlightThatRefusesContender_UntilSeparateDomainRecovery()
    {
        var tree = SettlementTree.Create();
        using var holder = StartHolder(tree);
        Process? client = null;
        Process? heartbeat = null;
        string? executionId = null;
        string? nonce = null;
        try
        {
            await WaitForHolderAdmissionAsync(holder);
            var channel = await WaitForJsonAsync<ChannelRecord>(tree.ChannelPath);
            executionId = channel.ExecutionId;
            nonce = channel.Nonce;
            var launch = await WaitForJsonAsync<ClientLaunch>(tree.ClientPath);
            Assert.Equal(executionId, launch.ExecutionId);
            Assert.Equal(nonce, launch.Nonce);
            client = OpenRecordedProcess(launch.ProcessId);
            var firstHeartbeat = await WaitForHeartbeatAsync(tree, executionId, nonce);
            heartbeat = OpenRecordedProcess(firstHeartbeat.ProcessId);
            holder.Kill(entireProcessTree: false);
            await WaitForExitAsync(holder, "holder root after non-tree kill");
            var afterExitSequence = ReadHeartbeats(tree, executionId, nonce).Max(static item => item.Sequence);
            var afterKill = await WaitForHeartbeatAfterAsync(tree, executionId, nonce, afterExitSequence,
                "heartbeat progress after holder termination");
            Assert.Equal(firstHeartbeat.ProcessId, afterKill.ProcessId);
            Assert.False(heartbeat.HasExited);
            Assert.False(client.HasExited);

            using var contender = StartProbe(true, "settlement-contender", tree.StoreRoot, tree.RepositoryNamespace,
                tree.Key, tree.FactorySentinel);
            var contenderOutput = await ReadProcessOutputAsync(contender, "receipt contender");
            Assert.Equal(0, contender.ExitCode);
            Assert.Equal($"contender-refused flight={executionId} reason=unresolved-execution factoryStarts=0", contenderOutput.Trim());
            Assert.False(File.Exists(tree.FactorySentinel));
            Assert.Equal(executionId, ReadAuthority(tree)?.InFlightExecutionId);

            await RequestOwnedChildStopAsync(tree, executionId, nonce);
            await WaitForExitAsync(heartbeat, "nonce-stopped heartbeat child");
            await WaitForExitAsync(client, "child client after holder loss");
            var completion = await WaitForJsonAsync<ClientCompletion>(tree.ClientCompletedPath);
            Assert.Equal(0, completion.HeartbeatExitCode);
            Assert.NotNull(completion.ChannelFailure);
            Assert.Equal(executionId, completion.ExecutionId);
            Assert.Equal(nonce, completion.Nonce);

            // Actual exit of both owned processes proves no further heartbeat can be produced.
            Assert.True(heartbeat.HasExited);
            Assert.True(client.HasExited);
            Assert.NotEmpty(ReadHeartbeats(tree, executionId, nonce));

            // Settlement of descendants does not confer ownership of the abandoned execution marker.
            Assert.Equal(executionId, ReadAuthority(tree)?.InFlightExecutionId);

            using var freshDomain = StartProbe(true, "settlement-fresh-domain", tree.FreshStoreRoot,
                tree.RepositoryNamespace, tree.Key, tree.FreshFactorySentinel);
            var freshOutput = await ReadProcessOutputAsync(freshDomain, "separate-store recovery control");
            Assert.Equal(0, freshDomain.ExitCode);
            Assert.Equal("fresh-domain-allowed factoryStarts=1 settled=true", freshOutput.Trim());
            Assert.Equal("factory-started", await File.ReadAllTextAsync(tree.FreshFactorySentinel));
            Assert.Null(EvidenceReceiptQuota.ReadAuthority(tree.FreshStoreRoot, tree.RepositoryNamespace, tree.Key)?.InFlightExecutionId);
            Assert.Equal(executionId, ReadAuthority(tree)?.InFlightExecutionId);
        }
        finally
        {
            await CleanupOwnedProcessesAsync(tree, holder, client, heartbeat, executionId, nonce);
            tree.Dispose();
        }
    }

    [Fact]
    public void ReadHeartbeats_ObservesOnlyCompleteRecordsWhileWriterRemainsOpen()
    {
        using var tree = SettlementTree.Create();
        const string nonce = "owned-heartbeat-nonce";
        using var stream = new FileStream(tree.HeartbeatPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        var first = new Heartbeat(Environment.ProcessId, tree.ExecutionId, nonce, 1, DateTimeOffset.UnixEpoch);
        var second = first with { Sequence = 2 };
        var serialized = JsonSerializer.Serialize(second);

        writer.WriteLine(JsonSerializer.Serialize(first));
        writer.Write(serialized[..^1]);
        Assert.Equal(first, Assert.Single(ReadHeartbeats(tree, tree.ExecutionId, nonce)));

        writer.WriteLine(serialized[^1..]);
        Assert.Equal(new[] { first, second }, ReadHeartbeats(tree, tree.ExecutionId, nonce));
        Assert.Empty(ReadHeartbeats(tree, "different-execution", nonce));
        Assert.Empty(ReadHeartbeats(tree, tree.ExecutionId, "different-nonce"));
    }

    [Fact]
    public void ReadHeartbeats_RejectsMalformedCompletedRecords()
    {
        using var tree = SettlementTree.Create();
        using var stream = new FileStream(tree.HeartbeatPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        writer.WriteLine("{malformed}");

        Assert.Throws<JsonException>(() => ReadHeartbeats(tree, tree.ExecutionId, "nonce"));
    }

    [Fact]
    public async Task WaitUntilAsync_ReportsReadinessDescriptionWhenDeadlineExpires()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() =>
            WaitUntilAsync<string>(static () => null, static value => value is not null,
                "partial stop marker readiness", deadline.Token));

        Assert.Contains("partial stop marker readiness", timeout.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<OperationCanceledException>(timeout.InnerException);
    }

    private static Process StartHolder(SettlementTree tree)
        => StartProbe(true, "settlement-holder", tree.StoreRoot, tree.RepositoryNamespace, tree.Key, tree.ExecutionId, tree.RecordRoot);

    private static async Task WaitForHolderAdmissionAsync(Process holder)
    {
        using var timeout = new CancellationTokenSource(ProcessDeadline);
        var line = await holder.StandardOutput.ReadLineAsync(timeout.Token);
        Assert.StartsWith("holder-admitted execution=", line, StringComparison.Ordinal);
    }

    private static async Task<Heartbeat> WaitForHeartbeatAsync(SettlementTree tree, string executionId, string nonce)
        => await WaitUntilAsync(() => ReadHeartbeats(tree, executionId, nonce).FirstOrDefault(),
            static heartbeat => heartbeat is not null, "first nonce-bound child heartbeat");

    private static async Task<Heartbeat> WaitForHeartbeatAfterAsync(
        SettlementTree tree, string executionId, string nonce, int sequence, string description)
        => await WaitUntilAsync(() => ReadHeartbeats(tree, executionId, nonce).LastOrDefault(),
            heartbeat => heartbeat is not null && heartbeat.Sequence > sequence, description);

    private static IReadOnlyList<Heartbeat> ReadHeartbeats(SettlementTree tree, string executionId, string nonce)
    {
        if (!File.Exists(tree.HeartbeatPath)) return [];
        using var stream = new FileStream(tree.HeartbeatPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var contents = reader.ReadToEnd();
        var completeRecords = contents[..(contents.LastIndexOf('\n') + 1)];
        return completeRecords.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => JsonSerializer.Deserialize<Heartbeat>(line))
            .Where(item => item is not null && item.ExecutionId == executionId && item.Nonce == nonce)
            .Select(static item => item!)
            .ToArray();
    }

    private static async Task RequestOwnedChildStopAsync(SettlementTree tree, string executionId, string nonce)
    {
        var channel = await WaitForJsonAsync<ChannelRecord>(tree.ChannelPath);
        Assert.Equal(executionId, channel.ExecutionId);
        Assert.Equal(nonce, channel.Nonce);
        var heartbeat = await WaitForHeartbeatAsync(tree, executionId, nonce);
        Assert.Equal(nonce, heartbeat.Nonce);
        await File.WriteAllTextAsync(tree.StopPath, executionId + "|" + nonce);
    }

    private static async Task<T> WaitForJsonAsync<T>(string path)
        => await WaitUntilAsync(() => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : default,
            static value => value is not null, Path.GetFileName(path));

    private static async Task<T> WaitUntilAsync<T>(Func<T?> read, Func<T?, bool> ready, string description)
    {
        using var timeout = new CancellationTokenSource(ProcessDeadline);
        return await WaitUntilAsync(read, ready, description, timeout.Token);
    }

    private static async Task<T> WaitUntilAsync<T>(
        Func<T?> read, Func<T?, bool> ready, string description, CancellationToken deadlineToken)
    {
        try
        {
            while (true)
            {
                deadlineToken.ThrowIfCancellationRequested();
                var value = read();
                if (ready(value)) return value!;
                await Task.Delay(PollInterval, deadlineToken);
            }
        }
        catch (OperationCanceledException exception) when (deadlineToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {description} within {ProcessDeadline}.", exception);
        }
    }

    private static Process OpenRecordedProcess(int processId)
        => Process.GetProcessById(processId);

    private static async Task WaitForExitAsync(Process process, string description)
    {
        using var timeout = new CancellationTokenSource(ProcessDeadline);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.HasExited, $"{description} did not exit within {ProcessDeadline}.");
    }

    private static async Task<string> ReadProcessOutputAsync(Process process, string description)
    {
        using var timeout = new CancellationTokenSource(ProcessDeadline);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"{description} failed with exit code {process.ExitCode}: {error}");
        Assert.Empty(error);
        return output;
    }

    private static Process StartProbe(params string[] arguments) => StartProbe(false, arguments);

    private static Process StartProbe(bool redirectOutput, params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };
        info.ArgumentList.Add(ProbeAssemblyPath());
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("The receipt settlement probe did not start.");
    }

    private static string ProbeAssemblyPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Parent?.Name ?? "Debug";
        var probe = Path.Join(FindRepositoryRoot(), "tests", "ForgeTrust.AppSurface.CoverageLeaseProbe", "bin",
            configuration, "net10.0", "ForgeTrust.AppSurface.CoverageLeaseProbe.dll");
        Assert.True(File.Exists(probe), $"The receipt settlement probe was not built: {probe}");
        return probe;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "Cli", "ForgeTrust.AppSurface.Cli.Tests", "ForgeTrust.AppSurface.Cli.Tests.csproj")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the receipt settlement probe.");
    }

    private static EvidenceReceiptState? ReadAuthority(SettlementTree tree)
        => EvidenceReceiptQuota.ReadAuthority(tree.StoreRoot, tree.RepositoryNamespace, tree.Key);

    private static async Task CleanupOwnedProcessesAsync(SettlementTree tree, Process holder, Process? client,
        Process? heartbeat, string? executionId, string? nonce)
    {
        if (!holder.HasExited)
        {
            holder.Kill(entireProcessTree: false);
            using var holderTimeout = new CancellationTokenSource(ProcessDeadline);
            await holder.WaitForExitAsync(holderTimeout.Token);
        }

        try
        {
            // The holder writes the channel before it starts the client. Recover that record
            // even when admission stdout or the first client observation did not arrive.
            if (File.Exists(tree.ChannelPath))
            {
                var channel = await WaitForJsonAsync<ChannelRecord>(tree.ChannelPath);
                Assert.Equal(tree.ExecutionId, channel.ExecutionId);
                Assert.Equal(executionId ?? channel.ExecutionId, channel.ExecutionId);
                Assert.Equal(nonce ?? channel.Nonce, channel.Nonce);
                executionId = channel.ExecutionId;
                nonce = channel.Nonce;

                // Write the stop request before waiting for the client so a late heartbeat can exit.
                await File.WriteAllTextAsync(tree.StopPath, executionId + "|" + nonce);
                var launch = await WaitForJsonAsync<ClientLaunch>(tree.ClientPath);
                Assert.Equal(executionId, launch.ExecutionId);
                Assert.Equal(nonce, launch.Nonce);
                client ??= TryOpenRecordedProcess(launch.ProcessId);
                if (client is { HasExited: false }) await WaitForExitAsync(client, "owned child client during cleanup");

                var ownedHeartbeat = ReadHeartbeats(tree, executionId, nonce).FirstOrDefault();
                if (ownedHeartbeat is not null) heartbeat ??= TryOpenRecordedProcess(ownedHeartbeat.ProcessId);
            }
            if (heartbeat is { HasExited: false }) await WaitForExitAsync(heartbeat, "owned heartbeat during cleanup");
            if (!holder.HasExited) throw new InvalidOperationException("Owned receipt holder remained alive after cleanup.");
        }
        finally
        {
            client?.Dispose();
            heartbeat?.Dispose();
        }
    }

    private static Process? TryOpenRecordedProcess(int processId)
    {
        try { return Process.GetProcessById(processId); }
        catch (ArgumentException) { return null; }
    }

    private sealed class SettlementTree : IDisposable
    {
        private SettlementTree(string root, string executionId)
        {
            Root = root;
            ExecutionId = executionId;
            StoreRoot = Path.Join(root, "store");
            FreshStoreRoot = Path.Join(root, "fresh-store");
            RecordRoot = Path.Join(root, "records");
            Directory.CreateDirectory(RecordRoot);
        }

        internal string Root { get; }
        internal string ExecutionId { get; }
        internal string StoreRoot { get; }
        internal string FreshStoreRoot { get; }
        internal string RecordRoot { get; }
        internal string RepositoryNamespace { get; } = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("settlement-repo"u8));
        internal string Key { get; } = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("settlement-key"u8));
        internal string ChannelPath => Path.Join(RecordRoot, "holder-channel.json");
        internal string ClientPath => Path.Join(RecordRoot, "child-client.json");
        internal string ClientCompletedPath => Path.Join(RecordRoot, "child-client-completed.json");
        internal string HeartbeatPath => Path.Join(RecordRoot, "heartbeat.jsonl");
        internal string StopPath => Path.Join(RecordRoot, "stop-child.txt");
        internal string HolderSettledPath => Path.Join(RecordRoot, "holder-settled.json");
        internal string FactorySentinel => Path.Join(RecordRoot, "factory-started.txt");
        internal string FreshFactorySentinel => Path.Join(RecordRoot, "fresh-factory-started.txt");

        internal static SettlementTree Create()
            => new(Path.Join(Path.GetTempPath(), "evidence-receipt-settlement-" + Guid.NewGuid().ToString("N")),
                "exec_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record ChannelRecord(string ExecutionId, string Nonce);
    private sealed record ClientLaunch(int ProcessId, string ExecutionId, string Nonce);
    private sealed record ClientCompletion(int ProcessId, string ExecutionId, string Nonce, int HeartbeatExitCode, string? ChannelFailure);
    private sealed record Heartbeat(int ProcessId, string ExecutionId, string Nonce, int Sequence, DateTimeOffset ObservedAt);
    private sealed record HolderSettlement(int ChildExitCode, bool SessionSettled, bool Unknown,
        string? InFlightExecutionId, string? PayloadDirectoryName);
}
