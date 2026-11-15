using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

if (args.Length > 0 && args[0].StartsWith("settlement-", StringComparison.Ordinal))
{
    return args[0] switch
    {
        "settlement-holder" when args.Length == 6 => await RunHolderAsync(args[1], args[2], args[3], args[4], args[5]),
        "settlement-contender" when args.Length == 5 => await RunContenderAsync(args[1], args[2], args[3], args[4]),
        "settlement-fresh-domain" when args.Length == 5 => await RunFreshDomainAsync(args[1], args[2], args[3], args[4]),
        "settlement-child-client" when args.Length == 2 => await RunChildClientAsync(args[1]),
        "settlement-heartbeat-child" when args.Length == 2 => await RunHeartbeatChildAsync(args[1]),
        _ => Usage(),
    };
}

if (args.Length is not (3 or 4) || args[0] is not ("hold" or "try") || args[2] is not ("shared" or "exclusive"))
{
    Console.Error.WriteLine("Usage: <hold|try> <existing-output-directory> <shared|exclusive> [private-candidate-lock-domain]");
    return 64;
}

var exclusive = args[2] == "exclusive";
var candidateDomain = args.Length == 4 ? args[3] : null;
if (args[0] == "try")
{
    try
    {
        using var lease = CoverageArtifactOperationLease.Acquire(args[1], exclusive, windowsLockRoot: candidateDomain);
        Console.WriteLine("acquired");
        return 0;
    }
    catch (CoverageExecutionException)
    {
        Console.WriteLine("busy");
        return 2;
    }
}

using (CoverageArtifactOperationLease.Acquire(args[1], exclusive, windowsLockRoot: candidateDomain))
{
    Console.WriteLine("acquired");
    Console.Out.Flush();
    _ = await Console.In.ReadLineAsync();
}

return 0;

static int Usage()
{
    Console.Error.WriteLine("Invalid settlement probe arguments.");
    return 64;
}

static async Task<int> RunHolderAsync(string storeRoot, string repositoryNamespace, string key, string executionId, string recordRoot)
{
    Directory.CreateDirectory(recordRoot);
    var store = OpenStore(storeRoot, repositoryNamespace);
    var lease = AssertSingle(await store.AcquireAsync([key], CancellationToken.None));
    lease.EstablishAuthority(executionId);
    await using var lifetime = EvidenceChildLifetimeSession.Start(executionId);
    WriteJson(Path.Join(recordRoot, "holder-channel.json"), new ChannelRecord(executionId,
        lifetime.Environment[EvidenceChildLifetimeSession.NonceEnvironmentName]));

    var clientStart = StartProbe("settlement-child-client", recordRoot);
    foreach (var (name, value) in lifetime.Environment) clientStart.Environment[name] = value;
    using var childProcess = Process.Start(clientStart)
        ?? throw new InvalidOperationException("The settlement child client did not start.");
    Console.WriteLine($"holder-admitted execution={executionId} pid={Environment.ProcessId}");
    Console.Out.Flush();

    try
    {
        await childProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
        var exitCode = childProcess.ExitCode;
        var settled = await lifetime.WaitForSettlementAsync(TimeSpan.FromSeconds(4), CancellationToken.None);
        if (!settled) return 70;
        var state = lease.ReadState();
        lease.SettleExecution(executionId);
        state = lease.ReadState();
        WriteJson(Path.Join(recordRoot, "holder-settled.json"), new HolderSettlement(exitCode,
            lifetime.IsSettled, lifetime.IsUnknown, state?.InFlightExecutionId, state?.PayloadDirectoryName));
        Console.WriteLine($"holder-settled childExit={exitCode} sessionSettled={lifetime.IsSettled} unknown={lifetime.IsUnknown} flight={state?.InFlightExecutionId ?? "none"} payload={state?.PayloadDirectoryName ?? "none"}");
        return exitCode == 0 && lifetime.IsSettled ? 0 : 71;
    }
    finally
    {
        lease.Dispose();
    }
}

static async Task<int> RunContenderAsync(string storeRoot, string repositoryNamespace, string key, string factorySentinel)
{
    var store = OpenStore(storeRoot, repositoryNamespace);
    using var lease = AssertSingle(await store.AcquireAsync([key], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
    var state = lease.ReadState();
    var proposal = lease.ProposeReadReceipt();
    if (state?.InFlightExecutionId is { } inFlight && proposal.IsUnavailable && proposal.Reason == "unresolved-execution")
    {
        Console.WriteLine($"contender-refused flight={inFlight} reason={proposal.Reason} factoryStarts=0");
        return 0;
    }

    var executionId = "contender_" + Guid.NewGuid().ToString("N");
    lease.EstablishAuthority(executionId);
    File.WriteAllText(factorySentinel, "factory-started");
    lease.SettleExecution(executionId);
    Console.WriteLine($"contender-factory-started flight={state?.InFlightExecutionId ?? "none"}");
    return 2;
}

static async Task<int> RunFreshDomainAsync(string storeRoot, string repositoryNamespace, string key, string factorySentinel)
{
    var store = OpenStore(storeRoot, repositoryNamespace);
    using var lease = AssertSingle(await store.AcquireAsync([key], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
    if (lease.ReadState()?.InFlightExecutionId is not null) return 3;
    var executionId = "fresh_" + Guid.NewGuid().ToString("N");
    lease.EstablishAuthority(executionId);
    File.WriteAllText(factorySentinel, "factory-started");
    lease.SettleExecution(executionId);
    Console.WriteLine("fresh-domain-allowed factoryStarts=1 settled=true");
    return 0;
}

static async Task<int> RunChildClientAsync(string recordRoot)
{
    var executionId = Environment.GetEnvironmentVariable(EvidenceChildLifetimeSession.ExecutionEnvironmentName)
        ?? throw new InvalidOperationException("Missing child execution identity.");
    var nonce = Environment.GetEnvironmentVariable(EvidenceChildLifetimeSession.NonceEnvironmentName)
        ?? throw new InvalidOperationException("Missing child session nonce.");
    WriteJson(Path.Join(recordRoot, "child-client.json"), new ClientLaunch(Environment.ProcessId, executionId, nonce));
    var heartbeatExitCode = -1;
    string? channelFailure = null;
    try
    {
        heartbeatExitCode = await EvidenceOwnedChild.RunProcessAsync(
            StartProbe("settlement-heartbeat-child", recordRoot), CancellationToken.None);
    }
    catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
    {
        channelFailure = exception.GetType().Name;
        var completionPath = Path.Join(recordRoot, "heartbeat-completed.json");
        if (File.Exists(completionPath))
        {
            var completion = JsonSerializer.Deserialize<HeartbeatCompletion>(File.ReadAllBytes(completionPath));
            if (completion is { ExitCode: 0 } && completion.ExecutionId == executionId && completion.Nonce == nonce)
                heartbeatExitCode = completion.ExitCode;
        }
    }

    WriteJson(Path.Join(recordRoot, "child-client-completed.json"), new ClientCompletion(
        Environment.ProcessId, executionId, nonce, heartbeatExitCode, channelFailure));
    return heartbeatExitCode is 0 or -1 ? 0 : 4;
}

static async Task<int> RunHeartbeatChildAsync(string recordRoot)
{
    var executionId = Environment.GetEnvironmentVariable(EvidenceChildLifetimeSession.ExecutionEnvironmentName)
        ?? throw new InvalidOperationException("Missing child execution identity.");
    var nonce = Environment.GetEnvironmentVariable(EvidenceChildLifetimeSession.NonceEnvironmentName)
        ?? throw new InvalidOperationException("Missing child session nonce.");
    var stopPath = Path.Join(recordRoot, "stop-child.txt");
    var sequence = 0;
    while (true)
    {
        AppendJsonLine(Path.Join(recordRoot, "heartbeat.jsonl"), new Heartbeat(Environment.ProcessId,
            executionId, nonce, sequence++, DateTimeOffset.UtcNow));
        if (File.Exists(stopPath)
            && string.Equals(await File.ReadAllTextAsync(stopPath), executionId + "|" + nonce, StringComparison.Ordinal))
        {
            WriteJson(Path.Join(recordRoot, "heartbeat-completed.json"), new HeartbeatCompletion(
                Environment.ProcessId, executionId, nonce, sequence));
            return 0;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(150));
    }
}

static EvidenceReceiptStore OpenStore(string root, string repositoryNamespace)
    => EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
    {
        RootDirectory = root,
        RepositoryNamespace = repositoryNamespace,
        WaitTimeout = TimeSpan.FromSeconds(15),
    });

static EvidenceReceiptStore.EvidenceReceiptKeyLease AssertSingle(IReadOnlyList<EvidenceReceiptStore.EvidenceReceiptKeyLease> leases)
    => leases.Count == 1 ? leases[0] : throw new InvalidOperationException("Expected exactly one acquired receipt key.");

static ProcessStartInfo StartProbe(params string[] arguments)
{
    var info = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
    };
    info.ArgumentList.Add(ProbeAssemblyPath());
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    return info;
}

static string ProbeAssemblyPath() => Environment.ProcessPath is { } processPath
    && Path.GetFileNameWithoutExtension(processPath).Equals("ForgeTrust.AppSurface.CoverageLeaseProbe", StringComparison.OrdinalIgnoreCase)
        ? processPath
        : typeof(Program).Assembly.Location;

static void WriteJson<T>(string path, T value)
{
    var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
    File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value));
    File.Move(temporary, path, overwrite: true);
}

static void AppendJsonLine<T>(string path, T value)
    => File.AppendAllText(path, JsonSerializer.Serialize(value) + Environment.NewLine);

record ChannelRecord(string ExecutionId, string Nonce);
record HolderSettlement(int ChildExitCode, bool SessionSettled, bool Unknown, string? InFlightExecutionId, string? PayloadDirectoryName);
record ClientLaunch(int ProcessId, string ExecutionId, string Nonce);
record ClientCompletion(int ProcessId, string ExecutionId, string Nonce, int HeartbeatExitCode, string? ChannelFailure);
record Heartbeat(int ProcessId, string ExecutionId, string Nonce, int Sequence, DateTimeOffset ObservedAt);
record HeartbeatCompletion(int ProcessId, string ExecutionId, string Nonce, int FinalSequence, int ExitCode = 0);
