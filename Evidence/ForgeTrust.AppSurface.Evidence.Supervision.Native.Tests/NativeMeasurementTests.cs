using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Checks current-run native measurements before the standard Coverlet collector merge consumes them.</summary>
/// <remarks>Files are measurements, not admission, process ownership, a registered proof, or a gate capability.</remarks>
public sealed class NativeMeasurementTests
{
    private const string Module = "ForgeTrust.AppSurface.Evidence.Supervision.dll";
    private static readonly string[] Modules = ["ForgeTrust.AppSurface.Cli.dll", "ForgeTrust.AppSurface.Evidence.Contracts.dll", "ForgeTrust.AppSurface.Evidence.Planner.dll",
        "ForgeTrust.AppSurface.Evidence.Coverage.dll", "ForgeTrust.AppSurface.Evidence.Aspire.dll", Module, "ForgeTrust.AppSurface.Evidence.Cli.dll"];
    private static readonly string[] Cases = ["normal", "sdk-normal", "readiness", "factory-stall", "cancel", "stuck-descendant", "disposal-failure"];

    [LinuxMeasurementFact]
    public async Task LinuxNativeMeasurementsMatchTheCurrentSourceAndAllSevenPhysicalControls()
    {
        var repo = FindRepository();
        var directory = TestPathUtils.PathUnder(repo, "TestResults", "issue779-native-measurement");
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("rev-parse"); info.ArgumentList.Add("HEAD");
        using var process = Process.Start(info)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string head;
        try
        {
            head = await process.StandardOutput.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        Assert.Equal(0, process.ExitCode);
        var bindingBytes = Read(directory, "measurement-binding.json");
        using var binding = JsonDocument.Parse(bindingBytes);
        var source = Read(directory, "source.json");
        var native = Read(directory, "native-receipt.json");
        var coverage = Read(directory, "native-supervision.json", 32L * 1024 * 1024);
        var sdkCoverage = Read(directory, "native-sdk.json", 32L * 1024 * 1024);
        var rootTrx = Read(directory, "root-files.trx");
        RootFilesystemMeasurement.Validate(binding.RootElement.GetProperty("root_file_tests"), rootTrx);
        RootSupplementMeasurement.Validate(binding.RootElement.GetProperty("root_supplement_tests"),
            Read(directory, "root-supplement.trx", 1024 * 1024));
        Validate(binding.RootElement, head.Trim(), source, native, coverage, sdkCoverage);
        const string producerPath = "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageProducer.cs";
        var producerSource = File.ReadAllBytes(TestPathUtils.PathUnder(repo, producerPath));
        using var sourceBinding = JsonDocument.Parse(source);
        Assert.Equal(sourceBinding.RootElement.GetProperty("files").GetProperty(producerPath).GetProperty("sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(producerSource)));
        SdkFailureMeasurement.Validate(binding.RootElement, head.Trim(), Read(directory, "sdk-test-failure.json"),
            Read(directory, "native-sdk-failure.json"), System.Text.Encoding.UTF8.GetString(producerSource));
        var creation = ReadAccount("accounts-creation", "C4Accounts");
        var rollback = ReadAccount("accounts-rollback", "C4AccountRollback");
        AccountMeasurementLineage.Validate(source, bindingBytes, coverage, native, rootTrx,
            creation.Binding, creation.Coverage, creation.Native, rollback.Binding, rollback.Coverage, rollback.Native);
        var producerDirectory = TestPathUtils.PathUnder(directory, "producer-roots");
        Assert.Equal(source, Read(producerDirectory, "source.json"));
        ProducerRootsMeasurement.Validate(source, Read(producerDirectory, "producer-roots-binding.json"),
            Read(producerDirectory, "native-producer-roots.json", 32L * 1024 * 1024),
            Read(producerDirectory, "native-producer-roots-c2.json", 32L * 1024 * 1024),
            Read(producerDirectory, "native-receipt.json"), rollback.Binding, rollback.Coverage, rollback.Native);

        (byte[] Binding, byte[] Coverage, byte[] Native) ReadAccount(string folder, string variant)
        {
            var accountDirectory = TestPathUtils.PathUnder(directory, folder);
            var accountSource = Read(accountDirectory, "source.json");
            Assert.Equal(source, accountSource); // The complete original source capture, not only a claimed commit.
            var accountBinding = Read(accountDirectory, "measurement-binding.json");
            var accountCoverage = Read(accountDirectory, "native-accounts.json", 32L * 1024 * 1024);
            var accountNative = Read(accountDirectory, "native-accounts-receipt.json");
            AccountMeasurement.ValidateMerged(head.Trim(), variant, accountSource, accountBinding, accountNative, accountCoverage);
            return (accountBinding, accountCoverage, accountNative);
        }
    }

    private static byte[] Read(string directory, string name, long maximum = 8L * 1024 * 1024)
    {
        var path = TestPathUtils.PathUnder(directory, name);
        Assert.True(File.Exists(path), "Prepare the current source-bound Linux native measurement before running the complete gate.");
        using var stream = File.OpenRead(path);
        Assert.InRange(stream.Length, 1L, maximum);
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        Assert.Equal(-1, stream.ReadByte());
        return data;
    }

    private static void Validate(JsonElement binding, string head, byte[] source, byte[] native, byte[] coverage, byte[] sdkCoverage)
    {
        Assert.Equal("issue779-native-instrumentation-v1", binding.GetProperty("schema").GetString());
        Assert.Equal(head, binding.GetProperty("source").GetString());
        Assert.Equal("coverlet.console/10.0.1", binding.GetProperty("collector").GetString());
        foreach (var item in new[] { ("source_capture", source), ("native_receipt", native), ("coverage", coverage), ("sdk_coverage", sdkCoverage) })
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(item.Item2)), binding.GetProperty(item.Item1 + "_sha256").GetString());
        var imageNames = Modules.SelectMany(static name => new[] { name, Path.ChangeExtension(name, ".pdb") }).Order(StringComparer.Ordinal);
        foreach (var prefix in new[] { string.Empty, "sdk_" })
        foreach (var kind in new[] { "original", "instrumented" })
            Assert.Equal(imageNames, binding.GetProperty(prefix + kind).EnumerateObject()
                .Select(static row => row.Name).Order(StringComparer.Ordinal));
        foreach (var prefix in new[] { string.Empty, "sdk_" })
        foreach (var name in Modules)
        {
            var original = binding.GetProperty(prefix + "original").GetProperty(name).GetString();
            var instrumented = binding.GetProperty(prefix + "instrumented").GetProperty(name).GetString();
            Assert.Matches("^[0-9a-f]{64}$", original!);
            Assert.Matches("^[0-9a-f]{64}$", instrumented!);
            Assert.NotEqual(original, instrumented);
            var pdb = Path.ChangeExtension(name, ".pdb");
            Assert.Matches("^[0-9a-f]{64}$", binding.GetProperty(prefix + "original").GetProperty(pdb).GetString()!);
            Assert.Matches("^[0-9a-f]{64}$", binding.GetProperty(prefix + "instrumented").GetProperty(pdb).GetString()!);
        }
        using var sourceDocument = JsonDocument.Parse(source);
        Assert.Equal(head, sourceDocument.RootElement.GetProperty("source").GetString());
        using var actual = JsonDocument.Parse(native);
        var root = actual.RootElement;
        Assert.Equal("issue779-csharp-checkpoint3-native-v1", root.GetProperty("schema").GetString());
        Assert.Equal(head, root.GetProperty("source").GetString());
        Assert.Equal(0, root.GetProperty("exit").GetInt32());
        Assert.False(root.GetProperty("trusted_eligibility").GetBoolean());
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(Cases, cases.Select(static row => row.GetProperty("case").GetString()));
        foreach (var row in cases)
        {
            Assert.True(row.GetProperty("passed").GetBoolean());
            Assert.True(row.GetProperty("groups_empty").GetBoolean());
            Assert.True(row.GetProperty("final_groups_empty").GetBoolean());
            var settled = Assert.Single(row.GetProperty("application_observations").EnumerateArray(),
                static value => value.GetProperty("phase").GetString() == "settled");
            foreach (var key in new[] { "group_empty_twice", "pumps_joined", "pending_start_joined" })
                Assert.True(settled.GetProperty(key).GetBoolean());
        }
        WorkspaceGuardMeasurement.Validate(root);
        var worker = Assert.Single(cases[0].GetProperty("worker_measurements").EnumerateArray());
        Assert.Equal("evidence-worker-measurement-v1", worker.GetProperty("schema").GetString());
        Assert.True(worker.GetProperty("original_pidfd_exit").GetBoolean());
        Assert.False(worker.GetProperty("native_acceptance").GetBoolean());
        Assert.True(worker.GetProperty("pid").GetUInt32() > 0);
        Assert.True(worker.GetProperty("start_time").GetUInt64() > 0);
        var workerModules = worker.GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(Modules.Order(StringComparer.Ordinal), workerModules.Select(static row => row.GetProperty("module").GetString()).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "ForgeTrust.AppSurface.Cli.dll", "ForgeTrust.AppSurface.Evidence.Contracts.dll", "ForgeTrust.AppSurface.Evidence.Coverage.dll", "ForgeTrust.AppSurface.Evidence.Cli.dll" })
        {
            var row = Assert.Single(workerModules, value => value.GetProperty("module").GetString() == name);
            Assert.Equal("unchanged-worker-handoff", row.GetProperty("outcome").GetString());
            Assert.Equal(binding.GetProperty("instrumented").GetProperty(name).GetString(), row.GetProperty("module_sha256").GetString());
            Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("worker_sha256").GetString()!);
            Assert.Equal(row.GetProperty("worker_sha256").GetString(), row.GetProperty("host_sha256").GetString());
        }
        var sdkWorker = Assert.Single(cases[1].GetProperty("worker_measurements").EnumerateArray());
        Assert.Equal("evidence-worker-measurement-v1", sdkWorker.GetProperty("schema").GetString());
        Assert.True(sdkWorker.GetProperty("original_pidfd_exit").GetBoolean());
        Assert.False(sdkWorker.GetProperty("native_acceptance").GetBoolean());
        var sdkModules = sdkWorker.GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(Modules.Order(StringComparer.Ordinal), sdkModules.Select(static row => row.GetProperty("module").GetString()).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "ForgeTrust.AppSurface.Cli.dll", "ForgeTrust.AppSurface.Evidence.Contracts.dll", "ForgeTrust.AppSurface.Evidence.Coverage.dll", "ForgeTrust.AppSurface.Evidence.Aspire.dll", "ForgeTrust.AppSurface.Evidence.Cli.dll" })
        {
            var row = Assert.Single(sdkModules, value => value.GetProperty("module").GetString() == name);
            Assert.Equal("unchanged-worker-handoff", row.GetProperty("outcome").GetString());
            Assert.Equal(binding.GetProperty("sdk_instrumented").GetProperty(name).GetString(), row.GetProperty("module_sha256").GetString());
            Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("worker_sha256").GetString()!);
            Assert.Equal(row.GetProperty("worker_sha256").GetString(), row.GetProperty("host_sha256").GetString());
        }
        using var sdkReport = JsonDocument.Parse(sdkCoverage);
        Assert.Equal(Modules.Order(StringComparer.Ordinal), sdkReport.RootElement.EnumerateObject()
            .Select(static value => Path.GetFileName(value.Name)).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "EvidenceHostBootstrap.cs", "EvidenceRestrictedAspireApplication.cs" })
            Assert.Contains(sdkReport.RootElement.EnumerateObject().SelectMany(static module => module.Value.EnumerateObject()),
                document => Path.GetFileName(document.Name) == name && HasHits(document.Value));
        RequireProtectedCliHits(sdkReport.RootElement);
        using var report = JsonDocument.Parse(coverage);
        RequireProtectedCliHits(report.RootElement);
        var modules = report.RootElement.EnumerateObject().ToArray();
        Assert.Equal(Modules.Order(StringComparer.Ordinal), modules.Select(static value => Path.GetFileName(value.Name)).Order(StringComparer.Ordinal));
        var observedNativeRoot = false;
        var observedNativeWorker = false;
        foreach (var module in modules)
        foreach (var document in module.Value.EnumerateObject())
        {
            if (Path.GetFileName(document.Name) == "LinuxEmptyObservationExecution.cs")
                observedNativeRoot |= HasHits(document.Value);
            if (Path.GetFileName(document.Name) == "EvidenceLinuxWorkerSupervisor.cs")
                observedNativeWorker |= document.Value.EnumerateObject().SelectMany(static type => type.Value.EnumerateObject())
                    .Where(static method => method.Name.Contains("ConnectAsync", StringComparison.Ordinal))
                    .SelectMany(static method => method.Value.GetProperty("Lines").EnumerateObject())
                    .Any(static line => line.Value.GetInt32() > 0);
        }
        Assert.True(observedNativeRoot, "The report must contain actual guarded native root execution hits.");
        Assert.True(observedNativeWorker, "The report must contain the actual authenticated worker connection, not presumed visibility across PrivateTmp.");
    }

    private static void RequireProtectedCliHits(JsonElement report)
    {
        var module = Assert.Single(report.EnumerateObject(), static row =>
            Path.GetFileName(row.Name) == "ForgeTrust.AppSurface.Evidence.Cli.dll");
        Assert.Contains(module.Value.EnumerateObject(), static document =>
            Path.GetFileName(document.Name) == "EvidenceProtectedCliExecution.cs" && HasHits(document.Value));
    }

    private static bool HasHits(JsonElement document) => document.EnumerateObject()
        .SelectMany(static type => type.Value.EnumerateObject())
        .SelectMany(static method => method.Value.GetProperty("Lines").EnumerateObject())
        .Any(static line => line.Value.GetInt32() > 0);

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(TestPathUtils.PathUnder(directory.FullName, "ForgeTrust.AppSurface.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The native measurement fixture requires its source checkout.");
    }
}

/// <summary>Native measurements require the prepared Linux lane; ordinary runs report an explicit skip.</summary>
public sealed class LinuxMeasurementFactAttribute : FactAttribute
{
    public LinuxMeasurementFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("EVIDENCEHOST_NATIVE_MEASUREMENT") != "1")
            Skip = "The source-bound root/systemd measurement requires the prepared Linux runtime workflow.";
    }
}
