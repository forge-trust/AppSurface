using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

// A separately opted-in renamed apphost exercises SDK identity failures without replacing a real SDK.
if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
    && Environment.GetEnvironmentVariable("APPSURFACE_IDENTITY_SDK_PROBE_ROOT") is { Length: > 0 } sdkProbeRoot)
{
    var mode = Environment.GetEnvironmentVariable("APPSURFACE_IDENTITY_SDK_PROBE_MODE");
    await File.WriteAllTextAsync(Path.Join(sdkProbeRoot, "sdk-probe.observed"), mode);
    if (mode == "count")
        await File.AppendAllTextAsync(Path.Join(sdkProbeRoot, "sdk-probe.invocations"), "query\n");
    if (mode == "pipe-holder")
    {
        var pid = Path.Join(sdkProbeRoot, "sdk-probe.child-pid");
        await File.WriteAllTextAsync(pid + ".part", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(pid + ".part", pid);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!File.Exists(Path.Join(sdkProbeRoot, "stop-sdk-probe")))
        {
            // gstack-timing-ok: bounded independently owned probe, released and joined by its fixture.
            await Task.Delay(TimeSpan.FromMilliseconds(20), stop.Token);
        }
        return 0;
    }
    if (mode == "root-exits-with-pipe-child")
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.Environment["APPSURFACE_IDENTITY_SDK_PROBE_MODE"] = "pipe-holder";
        using var child = Process.Start(start) ?? throw new InvalidOperationException("The SDK pipe-holder probe did not start.");
        var rootPid = Path.Join(sdkProbeRoot, "sdk-probe.root-pid");
        await File.WriteAllTextAsync(rootPid + ".part",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(rootPid + ".part", rootPid);
        await Console.Out.WriteLineAsync("10.0.402");
        return 0;
    }
    if (mode == "deadline")
    {
        var attempted = Path.Join(sdkProbeRoot, "sdk-probe.deadline-started");
        // Model a timed-out initial discovery followed by an unavailable SDK, without repeating a long wait.
        if (File.Exists(attempted)) return 17;
        await File.WriteAllTextAsync(attempted, "started");
    }
    if (mode is "cancel" or "deadline")
    {
        var pid = Path.Join(sdkProbeRoot, "sdk-probe.pid");
        await File.WriteAllTextAsync(pid + ".part", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(pid + ".part", pid);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!File.Exists(Path.Join(sdkProbeRoot, "stop-sdk-probe")))
        {
            // gstack-timing-ok: independently bounded fixture shutdown, with actual process exit asserted.
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
        return 0;
    }
    if (mode == "nonzero")
    {
        await Console.Error.WriteAsync("private-sdk-probe-error-marker");
        return 17;
    }
    if (mode is "excessive-stdout" or "excessive-stderr")
    {
        var writer = mode == "excessive-stdout" ? Console.Out : Console.Error;
        var block = new string('x', 64 * 1024);
        for (var index = 0; index < 66; index++)
            await writer.WriteAsync(block);
        return 0;
    }
    if (mode == "blank") return 0;
    if (mode == "long")
    {
        await Console.Out.WriteLineAsync(new string('1', 129));
        return 0;
    }
    if (mode is "change" or "fail-after-first")
    {
        var marker = Path.Join(sdkProbeRoot, "sdk-probe.first-query");
        var first = !File.Exists(marker);
        if (first) await File.WriteAllTextAsync(marker, "queried");
        if (!first && mode == "fail-after-first") return 17;
        await Console.Out.WriteLineAsync(first ? "10.0.402" : "10.0.403");
        return 0;
    }
    await Console.Out.WriteLineAsync("10.0.402");
    return 0;
}

// A renamed copy of this test-only apphost stands in for Git, so identity tests can verify bounded
// process output and actual cancellation/exit without depending on a shell or a real repository's size.
if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "git", StringComparison.OrdinalIgnoreCase)
    && Environment.GetEnvironmentVariable("APPSURFACE_IDENTITY_GIT_PROBE_ROOT") is { Length: > 0 } gitProbeRoot)
{
    var mode = Environment.GetEnvironmentVariable("APPSURFACE_IDENTITY_GIT_PROBE_MODE");
    if (mode == "inventory-collision") Console.OutputEncoding = System.Text.Encoding.UTF8;
    if (mode is not null && (mode.StartsWith("inventory-", StringComparison.Ordinal)
        || mode.StartsWith("common-", StringComparison.Ordinal)))
    {
        if (args.Contains("--show-toplevel", StringComparer.Ordinal))
        {
            await Console.Out.WriteLineAsync(gitProbeRoot);
            return 0;
        }
        if (args.Contains("--git-common-dir", StringComparer.Ordinal))
        {
            var counter = Path.Join(gitProbeRoot, "git-probe-common-count");
            var count = File.Exists(counter) ? int.Parse(await File.ReadAllTextAsync(counter), System.Globalization.CultureInfo.InvariantCulture) : 0;
            await File.WriteAllTextAsync(counter, (count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var common = mode == "common-flip" && count > 0 ? "common-b"
                : mode == "common-loss" && count > 0 ? "missing-common" : "common-a";
            await Console.Out.WriteLineAsync(Path.Join(gitProbeRoot, common));
            return 0;
        }
        if (args.Contains("--stage", StringComparer.Ordinal))
        {
            if (mode == "inventory-delete") File.Delete(Path.Join(gitProbeRoot, "input.txt"));
            return 0;
        }
        var listingCounter = Path.Join(gitProbeRoot, "git-probe-listing-count");
        var listings = File.Exists(listingCounter) ? int.Parse(await File.ReadAllTextAsync(listingCounter), System.Globalization.CultureInfo.InvariantCulture) : 0;
        await File.WriteAllTextAsync(listingCounter, (listings + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var files = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Join(gitProbeRoot, "git-probe-files.json")))!;
        if (mode == "inventory-change" && listings > 0)
        {
            await File.WriteAllTextAsync(Path.Join(gitProbeRoot, "added.txt"), "added after input hashing");
            files = [.. files, "added.txt"];
        }
        await Console.Out.WriteAsync(string.Join('\0', files) + "\0");
        return 0;
    }
    if (mode is "namespace-missing" or "namespace-linked")
    {
        await Console.Out.WriteLineAsync(args.Contains("--show-toplevel", StringComparer.Ordinal)
            ? gitProbeRoot : Path.Join(gitProbeRoot, ".git"));
        return 0;
    }
    if (mode == "cancel")
    {
        var pid = Path.Join(gitProbeRoot, "git-probe.pid");
        await File.WriteAllTextAsync(pid + ".part", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(pid + ".part", pid);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(Path.Join(gitProbeRoot, "stop-git-probe")))
        {
            // gstack-timing-ok: independent bounded fixture shutdown; tests assert actual exit, not elapsed time.
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
        return 0;
    }
    if (mode == "invalid-utf8")
    {
        await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xff });
        return 0;
    }
    if (mode == "stderr-limit")
    {
        await Console.Error.WriteAsync(new string('e', 64 * 1024 + 1));
        await Console.Out.WriteLineAsync(gitProbeRoot);
        return 0;
    }
    if (mode is "stdout-limit" or "stdout-boundary")
    {
        await Console.Out.WriteAsync(new string('x', 16 * 1024 * 1024 + (mode == "stdout-limit" ? 2 : 1)));
        return 0;
    }
    await Console.Error.WriteAsync("private-git-probe-error-marker");
    return 17;
}

if (args.Length == 1 && args[0] == "environment-clear")
{
    const string sentinel = "APPSURFACE_EVIDENCE_CHILD_ENVIRONMENT_CLEAR_SENTINEL";
    return Environment.GetEnvironmentVariable(sentinel) is null ? 1 : 0;
}

if (args.Length > 0 && args[0].StartsWith("settlement-", StringComparison.Ordinal))
{
    return args[0] switch
    {
        "settlement-holder" when args.Length == 6 => await RunHolderAsync(args[1], args[2], args[3], args[4], args[5]),
        "settlement-contender" when args.Length == 5 => await RunContenderAsync(args[1], args[2], args[3], args[4]),
        "settlement-fresh-domain" when args.Length == 5 => await RunFreshDomainAsync(args[1], args[2], args[3], args[4]),
        "settlement-child-client" when args.Length == 2 => await RunChildClientAsync(args[1]),
        "settlement-independent-heartbeat-root" when args.Length == 2 => await RunIndependentHeartbeatRootAsync(args[1]),
        "settlement-independent-heartbeat-root-exits" when args.Length == 2 => await RunIndependentHeartbeatRootAsync(args[1], exitBeforeChild: true),
        "settlement-enrolled-heartbeat" when args.Length == 2 => await RunEnrolledHeartbeatAsync(args[1]),
        "settlement-heartbeat-child" when args.Length == 2 => await RunHeartbeatChildAsync(args[1]),
        _ => Usage(),
    };
}

if (args.Length > 0 && args[0] == "process")
{
    return await RunProcessProbeAsync(args[1..]);
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

static async Task<int> RunIndependentHeartbeatRootAsync(string recordRoot, bool exitBeforeChild = false)
{
    WriteJson(Path.Join(recordRoot, "root-process.json"), new { ProcessId = Environment.ProcessId });
    // The descendant owns its own enrollment connection, which survives this root being terminated.
    using var child = Process.Start(StartProbe("settlement-enrolled-heartbeat", recordRoot))
        ?? throw new InvalidOperationException("The independently enrolled descendant did not start.");
    if (exitBeforeChild) return 0;
    await child.WaitForExitAsync();
    return child.ExitCode;
}

static async Task<int> RunEnrolledHeartbeatAsync(string recordRoot)
{
    var exitCode = -1;
    await EvidenceOwnedChild.RunAsync(async _ => exitCode = await RunHeartbeatChildAsync(recordRoot), CancellationToken.None);
    return exitCode;
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

static async Task<int> RunProcessProbeAsync(string[] processArgs)
{
    const int maximumOutputBytes = 16 * 1024 * 1024;
    if (processArgs.Length == 0)
    {
        Console.Error.WriteLine("A process probe mode is required.");
        return 64;
    }

    switch (processArgs[0])
    {
        case "echo-env" when processArgs.Length == 3:
            {
                var prefix = string.Concat(
                    Environment.GetEnvironmentVariable(processArgs[1]) ?? "unset",
                    "|",
                    Environment.GetEnvironmentVariable(processArgs[2]) ?? "unset",
                    "|");
                var output = Console.OpenStandardOutput();
                await output.WriteAsync(Encoding.UTF8.GetBytes(prefix));
                await Console.OpenStandardInput().CopyToAsync(output);
                await output.FlushAsync();
                return 0;
            }
        case "streams" when processArgs.Length == 2 && TryReadOutputCount(processArgs[1], maximumOutputBytes, out var streamBytes):
            await Task.WhenAll(
                WritePatternAsync(Console.OpenStandardOutput(), (byte)'o', streamBytes),
                WritePatternAsync(Console.OpenStandardError(), (byte)'e', streamBytes));
            return 0;
        case "stdout" when processArgs.Length == 2 && TryReadOutputCount(processArgs[1], maximumOutputBytes, out var standardOutputBytes):
            await WritePatternAsync(Console.OpenStandardOutput(), (byte)'x', standardOutputBytes);
            return 0;
        case "flood" when processArgs.Length == 1:
            await WritePatternAsync(Console.OpenStandardOutput(), (byte)'f', maximumOutputBytes);
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        case "invalid-utf8" when processArgs.Length == 1:
            await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xFF });
            return 0;
        case "replacement" when processArgs.Length == 1:
            await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xEF, 0xBF, 0xBD });
            return 0;
        case "hold" when processArgs.Length == 2:
            await File.WriteAllTextAsync(processArgs[1], "ready");
            await Console.OpenStandardOutput().WriteAsync("ready"u8.ToArray());
            await Console.OpenStandardOutput().FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        default:
            Console.Error.WriteLine("Unsupported process probe mode or arguments.");
            return 64;
    }
}

static bool TryReadOutputCount(string value, int maximum, out int count)
    => int.TryParse(value, out count) && count >= 0 && count <= maximum;

static async Task WritePatternAsync(Stream stream, byte value, int byteCount)
{
    var buffer = new byte[Math.Min(8192, Math.Max(1, byteCount))];
    Array.Fill(buffer, value);
    var remaining = byteCount;
    while (remaining > 0)
    {
        var writeCount = Math.Min(buffer.Length, remaining);
        await stream.WriteAsync(buffer.AsMemory(0, writeCount));
        remaining -= writeCount;
    }

    await stream.FlushAsync();
}

record ChannelRecord(string ExecutionId, string Nonce);
record HolderSettlement(int ChildExitCode, bool SessionSettled, bool Unknown, string? InFlightExecutionId, string? PayloadDirectoryName);
record ClientLaunch(int ProcessId, string ExecutionId, string Nonce);
record ClientCompletion(int ProcessId, string ExecutionId, string Nonce, int HeartbeatExitCode, string? ChannelFailure);
record Heartbeat(int ProcessId, string ExecutionId, string Nonce, int Sequence, DateTimeOffset ObservedAt);
record HeartbeatCompletion(int ProcessId, string ExecutionId, string Nonce, int FinalSequence, int ExitCode = 0);
